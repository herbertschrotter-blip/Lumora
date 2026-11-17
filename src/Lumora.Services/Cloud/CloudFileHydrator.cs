namespace Lumora.Services.Cloud;

/// <summary>
/// Das Original eines Platzhalters ließ sich nicht laden (keine Internetverbindung, OneDrive nicht gestartet, abgelehnt) —
/// die Datei gilt deshalb nie als „fehlt“ (PLAN Kap. 12, ADR-0077).
/// </summary>
public sealed class CloudFileUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Lädt das Original eines Platzhalters (Download durch Windows) — austauschbar für Tests.</summary>
public interface ICloudFileHydrator
{
    /// <summary>
    /// Liest die Datei einmal ganz, damit Windows sie herunterlädt; <paramref name="progress"/> meldet 0…1. Abbrechen über
    /// <paramref name="ct"/> schließt die Datei sofort. Wirft <see cref="CloudFileUnavailableException"/>, wenn der Inhalt
    /// nicht zu bekommen ist.
    /// </summary>
    Task HydrateAsync(string path, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>
/// Lightbox (ADR-0077): liest einen Platzhalter in Blöcken zu 1 MB und verwirft die Daten — Windows lädt dabei den Inhalt
/// von OneDrive; danach liegt die Datei lokal und wird wie jede andere dekodiert. Zwischen den Blöcken (und im asynchronen
/// Lesen) greift der Abbruch. Fehler beim Öffnen oder Lesen werden zu <see cref="CloudFileUnavailableException"/>.
/// </summary>
public sealed class CloudFileHydrator : ICloudFileHydrator
{
    private const int BlockSize = 1 << 20;

    public async Task HydrateAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ct.ThrowIfCancellationRequested();
        try
        {
            await Task.Run(
                async () =>
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 1,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var length = Math.Max(1L, stream.Length);
                    var buffer = new byte[BlockSize];
                    long total = 0;
                    progress?.Report(0);
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        progress?.Report(Math.Min(1.0, (double)total / length));
                    }

                    progress?.Report(1);
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CloudFileUnavailableException(
                "Das Original liegt nur in OneDrive und konnte gerade nicht geladen werden — Internetverbindung und OneDrive prüfen.", ex);
        }
    }
}
