using System.Net.Http.Headers;
using System.Text.Json;

namespace Lumora.Services.ModelPackages;

/// <summary>
/// Download über HTTPS (bzw. <c>file://</c> für Tests): Antwort-Header zuerst, dann gestreamt in die Zieldatei; mehr Bytes
/// als erwartet brechen ab. Sendet nur die Anfrage selbst (Kennung „Lumora“, keine Benutzerdaten).
/// </summary>
public sealed class HttpModelDownloader(HttpClient? http = null) : IModelDownloader
{
    private const int BufferSize = 1 << 16;

    public async Task DownloadAsync(Uri source, string target, long expectedSize, IProgress<long>? progress, CancellationToken ct)
    {
        Stream? input = null;
        HttpResponseMessage? response = null;
        HttpClient? own = null;
        try
        {
            if (source.IsFile)
            {
                input = new FileStream(source.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            }
            else
            {
                var client = http ?? (own = new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
                using var request = CreateRequest(source);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            }

            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            long done = 0;
            long lastReport = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (expectedSize > 0 && done > expectedSize)
                {
                    throw new ModelPackageException($"Die Datei ist größer als erwartet ({expectedSize:N0} Bytes) — Download abgebrochen.");
                }

                if (done - lastReport >= 1 << 20)
                {
                    lastReport = done;
                    progress?.Report(done);
                }
            }

            progress?.Report(done);
        }
        finally
        {
            if (input is not null)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }

            response?.Dispose();
            own?.Dispose();
        }
    }

    /// <summary>GET mit der Kennung „Lumora“ — sonst nichts (keine Kennungen, keine Parameter über den Benutzer).</summary>
    internal static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Lumora", null));
        return request;
    }
}

/// <summary>Metadaten für „Update suchen“: ein GET mit Zeitlimit, nur die Anfrage selbst.</summary>
public sealed class HttpModelMetadataClient(HttpClient? http = null) : IModelMetadataClient
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string> GetStringAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ModelPackageException("Update-Quellen werden nur über HTTPS abgefragt.");
        }

        using var request = HttpModelDownloader.CreateRequest(uri);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// „Update suchen“ (ADR-0070): nur auf Klick, nur Metadaten. Ohne Quelle „Version x, keine automatische Prüfung“. Bei
/// Hugging Face werden die LFS-Prüfsummen der großen Dateien aus der Dateiliste mit den hinterlegten verglichen.
/// </summary>
public static class ModelUpdateChecker
{
    public static string NoSourceText(ModelPackageDefinition definition)
        => $"Version {definition.Version}, keine automatische Prüfung.";

    public static async Task<ModelUpdateCheckResult> CheckAsync(ModelPackageDefinition definition, ModelPackageStatus status, IModelMetadataClient client, CancellationToken ct)
    {
        if (status.State == ModelPackageState.UpdateAvailable)
        {
            return new ModelUpdateCheckResult(ModelUpdateOutcome.UpdateReady,
                $"Lumora bringt Version {definition.Version} mit (geladen: {status.InstalledVersion}) — „Laden…“ holt sie.");
        }

        if (definition.UpdateSource is not { } source)
        {
            return new ModelUpdateCheckResult(ModelUpdateOutcome.NoSource, NoSourceText(definition));
        }

        try
        {
            return source.Kind switch
            {
                ModelUpdateSourceKind.HuggingFaceFiles => await CheckHuggingFaceAsync(definition, source.ManifestUri, client, ct).ConfigureAwait(false),
                _ => new ModelUpdateCheckResult(ModelUpdateOutcome.NoSource, NoSourceText(definition)),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ModelPackageException or InvalidOperationException or KeyNotFoundException)
        {
            return new ModelUpdateCheckResult(ModelUpdateOutcome.Failed, $"Der Hersteller war nicht erreichbar oder hat unerwartet geantwortet ({ex.GetType().Name}).");
        }
    }

    private static async Task<ModelUpdateCheckResult> CheckHuggingFaceAsync(ModelPackageDefinition definition, Uri manifest, IModelMetadataClient client, CancellationToken ct)
    {
        // Große Dateien liegen im LFS (oid = SHA-256); kleine Konfigurationsdateien haben eine Git-Prüfsumme und zählen nicht.
        var large = definition.FileList.Where(f => f.Size >= 1_000_000).ToList();
        var remote = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in large.Select(f => FolderOf(f.RelativePath)).Distinct(StringComparer.Ordinal))
        {
            var uri = folder.Length == 0 ? new Uri(manifest.AbsoluteUri.TrimEnd('/')) : new Uri(manifest, folder);
            var json = await client.GetStringAsync(uri, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("path", out var path) && entry.TryGetProperty("lfs", out var lfs) && lfs.TryGetProperty("oid", out var oid))
                {
                    remote[path.GetString() ?? string.Empty] = oid.GetString() ?? string.Empty;
                }
            }
        }

        var changed = large.Where(f => !remote.TryGetValue(f.RelativePath, out var sha) || !string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase)).ToList();
        return changed.Count == 0
            ? new ModelUpdateCheckResult(ModelUpdateOutcome.UpToDate, $"Aktuell — beim Hersteller gibt es keine neuere Fassung als Version {definition.Version}.")
            : new ModelUpdateCheckResult(ModelUpdateOutcome.NewerAtVendor,
                $"Der Hersteller hat geänderte Dateien veröffentlicht ({string.Join(", ", changed.Select(f => f.RelativePath))}). " +
                "Lumora lädt nur geprüfte Fassungen — sie kommt mit einem Lumora-Update.");
    }

    private static string FolderOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }
}
