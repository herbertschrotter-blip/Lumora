using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Activity;

namespace Lumora.Services.ModelPackages;

/// <summary>
/// Update-Manager „Modelle &amp; Daten“ (ADR-0070): eine Liste aller Modelle und Datenpakete, Laden nur mit bestätigter
/// <see cref="ModelDownloadRequest"/>, SHA-256 nach dem Laden, Aufräumen nach Abbruch, Fortschritt in der Aktivitäts-Anzeige
/// (ADR-0067), „Update suchen“ nur auf Klick. Protokolliert nur die Id des Eintrags, nie Pfade.
/// </summary>
public interface IModelPackageManager
{
    /// <summary>Alle Einträge in Anzeigereihenfolge (Modelle, Daten, Extern).</summary>
    IReadOnlyList<IModelPackageSource> Packages { get; }

    IModelPackageSource? Find(string id);

    Task<ModelPackageStatus> GetStatusAsync(string id, CancellationToken ct = default);

    /// <summary>Angaben für den Bestätigungsdialog; geladen wird erst nach <see cref="ModelDownloadRequest.Confirm"/>.</summary>
    ModelDownloadRequest DescribeDownload(string id);

    /// <summary>
    /// Lädt nach Bestätigung, prüft SHA-256 und schreibt den Prüf-Vermerk. Ohne Bestätigung
    /// <see cref="ModelDownloadNotConfirmedException"/> (es wird nichts geladen). Abbruch → angefangene Dateien weg.
    /// </summary>
    Task<ModelPackageStatus> InstallAsync(ModelDownloadRequest request, IProgress<ModelPackageProgress>? progress = null, CancellationToken ct = default);

    Task<ModelVerifyResult> VerifyAsync(string id, IProgress<ModelPackageProgress>? progress = null, CancellationToken ct = default);

    Task RemoveAsync(string id, CancellationToken ct = default);

    /// <summary>Nur auf Klick; nur Metadaten beim Hersteller.</summary>
    Task<ModelUpdateCheckResult> CheckForUpdateAsync(string id, CancellationToken ct = default);

    /// <summary>true, solange für den Eintrag Laden, Prüfen oder Entfernen läuft.</summary>
    bool IsBusy(string id);

    /// <summary>Nach Laden, Prüfen oder Entfernen (Id; von beliebigen Threads).</summary>
    event Action<string>? PackageChanged;
}

/// <inheritdoc />
public sealed class ModelPackageManager : IModelPackageManager
{
    private const long Mb = 1_048_576;

    private readonly IModelMetadataClient _metadata;
    private readonly IActivityCenter? _activities;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.Ordinal);

    public ModelPackageManager(
        IEnumerable<IModelPackageSource> sources,
        IModelMetadataClient metadata,
        IActivityCenter? activities = null,
        ILogger<ModelPackageManager>? logger = null,
        TimeProvider? time = null)
    {
        // Doppelte Ids: der erste gewinnt (TryAdd-Muster; Rauchtest/Tests registrieren vorab)
        Packages = sources
            .GroupBy(s => s.Definition.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => (int)s.Definition.Group)
            .ToList();
        _metadata = metadata;
        _activities = activities;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<IModelPackageSource> Packages { get; }

    public event Action<string>? PackageChanged;

    public IModelPackageSource? Find(string id) => Packages.FirstOrDefault(p => string.Equals(p.Definition.Id, id, StringComparison.Ordinal));

    public bool IsBusy(string id) => _busy.ContainsKey(id);

    public async Task<ModelPackageStatus> GetStatusAsync(string id, CancellationToken ct = default)
    {
        var source = Require(id);
        try
        {
            return await source.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            _logger.LogWarning("Modelle: Zustand von {Id} nicht lesbar ({Error})", id, ex.GetType().Name);
            return new ModelPackageStatus(ModelPackageState.Broken, "nicht lesbar", ex.Message);
        }
    }

    public ModelDownloadRequest DescribeDownload(string id)
    {
        var source = Require(id);
        EnsureCanDownload(source);
        return new ModelDownloadRequest(source.Definition, source.DownloadUri, source.Folder);
    }

    public async Task<ModelPackageStatus> InstallAsync(ModelDownloadRequest request, IProgress<ModelPackageProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsConfirmed)
        {
            _logger.LogWarning("Modelle: Laden von {Id} ohne Bestätigung abgelehnt", request.Id);
            throw new ModelDownloadNotConfirmedException(request.Id);
        }

        var source = Require(request.Id);
        EnsureCanDownload(source);
        var definition = source.Definition;
        Enter(definition.Id);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activity = _activities?.Start(new ActivityStartInfo(
            ActivityKind.ModelDownload, "Modell laden (MB)", definition.Name, UserInitiated: true, Key: "model:" + definition.Id, Cancel: cts.Cancel));
        var file = source as FileModelPackageSource;
        var total = Math.Max(definition.DownloadSize, 1);
        var reporter = new ActionProgress<ModelPackageProgress>(p =>
        {
            progress?.Report(p);
            var t = p.Total > 0 ? p.Total : total;
            activity?.Report((int)Math.Min(int.MaxValue, p.Done / Mb), (int)Math.Min(int.MaxValue, t / Mb), p.Stage);
        });
        try
        {
            _logger.LogInformation("Modelle: Laden von {Id} gestartet ({MB:N0} MB, Version {Version})", definition.Id, definition.DownloadSize / (double)Mb, definition.Version);
            file?.PrepareDownload();
            await source.DownloadAsync(reporter, cts.Token).ConfigureAwait(false);

            if (file is not null)
            {
                var verify = await file.VerifyAsync(reporter, cts.Token).ConfigureAwait(false);
                file.WriteManifest(verify.Ok, _time.GetUtcNow());
                if (!verify.Ok)
                {
                    throw new ModelPackageException($"Nach dem Laden stimmt die Prüfsumme nicht: {string.Join(", ", verify.BadFiles)}. Das Modell ist als defekt markiert.");
                }
            }

            var status = await source.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Modelle: {Id} geladen und geprüft", definition.Id);
            activity?.Complete(new ActivityCompletion($"{definition.Title} geladen und geprüft"));
            return status;
        }
        catch (OperationCanceledException)
        {
            file?.CleanupPartial();
            _logger.LogInformation("Modelle: Laden von {Id} abgebrochen", definition.Id);
            activity?.Canceled("Abgebrochen — angefangene Dateien wurden verworfen.");
            throw;
        }
        catch (ModelPackageException ex)
        {
            Failed(ex);
            throw;
        }
        catch (Exception ex)
        {
            Failed(ex);
            throw new ModelPackageException(UserMessage(ex), ex);
        }
        finally
        {
            Leave(definition.Id);
        }

        void Failed(Exception ex)
        {
            file?.CleanupPartial();
            _logger.LogWarning("Modelle: Laden von {Id} gescheitert ({Error})", definition.Id, ex.GetType().Name);
            activity?.Fail($"{definition.Title} konnte nicht geladen werden", ex is ModelPackageException ? ex.Message : UserMessage(ex));
        }
    }

    public async Task<ModelVerifyResult> VerifyAsync(string id, IProgress<ModelPackageProgress>? progress = null, CancellationToken ct = default)
    {
        var source = Require(id);
        Enter(id);
        try
        {
            var result = await source.VerifyAsync(progress, ct).ConfigureAwait(false);
            if (source is FileModelPackageSource file && (file.ComputeStatus().State != ModelPackageState.NotInstalled))
            {
                file.WriteManifest(result.Ok, _time.GetUtcNow());
            }

            _logger.LogInformation("Modelle: {Id} geprüft — {Result}", id, result.Ok ? "in Ordnung" : $"{result.BadFiles.Count} Abweichung(en)");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Modelle: Prüfen von {Id} gescheitert ({Error})", id, ex.GetType().Name);
            return new ModelVerifyResult(false, "Die Dateien konnten nicht gelesen werden (in Benutzung oder gesperrt).", []);
        }
        finally
        {
            Leave(id);
        }
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        var source = Require(id);
        if (!source.Capabilities.HasFlag(ModelPackageCapabilities.Remove))
        {
            throw new ModelPackageException($"„{source.Definition.Title}“ kann hier nicht entfernt werden.");
        }

        Enter(id);
        try
        {
            await source.RemoveAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Modelle: {Id} entfernt", id);
        }
        finally
        {
            Leave(id);
        }
    }

    public async Task<ModelUpdateCheckResult> CheckForUpdateAsync(string id, CancellationToken ct = default)
    {
        var source = Require(id);
        var status = await GetStatusAsync(id, ct).ConfigureAwait(false);
        var result = await ModelUpdateChecker.CheckAsync(source.Definition, status, _metadata, ct).ConfigureAwait(false);
        _logger.LogInformation("Modelle: Update-Suche für {Id} — {Outcome}", id, result.Outcome);
        return result;
    }

    private IModelPackageSource Require(string id)
        => Find(id) ?? throw new ModelPackageException($"Unbekannter Eintrag „{id}“.");

    private static void EnsureCanDownload(IModelPackageSource source)
    {
        if (!source.Capabilities.HasFlag(ModelPackageCapabilities.Download))
        {
            throw new ModelPackageException(source.IsOverridden
                ? "Der Modellordner ist über eine Umgebungsvariable vorgegeben — Lumora lädt dorthin nichts."
                : $"„{source.Definition.Title}“ wird nicht von Lumora geladen.");
        }
    }

    private void Enter(string id)
    {
        if (!_busy.TryAdd(id, 0))
        {
            throw new ModelPackageException("Für diesen Eintrag läuft schon ein Vorgang.");
        }
    }

    private void Leave(string id)
    {
        _busy.TryRemove(id, out _);
        PackageChanged?.Invoke(id);
    }

    private static string UserMessage(Exception ex) => ex switch
    {
        HttpRequestException http => $"Der Download ist gescheitert ({(http.StatusCode is { } code ? $"HTTP {(int)code}" : "keine Verbindung")}). Es wurde nichts gespeichert.",
        IOException or UnauthorizedAccessException => "Die Dateien konnten nicht geschrieben werden (Platz, Rechte oder in Benutzung). Es wurde nichts gespeichert.",
        _ => ex.Message,
    };
}
