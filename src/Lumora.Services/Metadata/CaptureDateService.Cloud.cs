using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Cloud;
using Lumora.Services.Messaging;

namespace Lumora.Services.Metadata;

/// <summary>
/// OneDrive-Platzhalter beim Nachtragen (ADR-0077, Spike 19): Das Aufnahmedatum kommt ohne Download aus dem
/// Eigenschaftsspeicher der Shell (<see cref="ICloudPropertyReader"/>, Herkunft EXIF). Liefert er keins, bleibt die Datei
/// offen und wird in dieser Sitzung nicht erneut gefragt, solange sie nur online liegt; sobald sie lokal ist
/// (<see cref="CloudFilesChangedMessage"/>), liest das normale Nachtragen die Datei selbst. GPS liefert die Shell für
/// Platzhalter nicht — Orte bleiben leer, bis die Datei lokal ist.
/// </summary>
public sealed partial class CaptureDateService
{
    /// <summary>Platzhalter ohne Datum aus der Shell: FileId → Änderungszeit (Unix) der gefragten Version.</summary>
    private readonly ConcurrentDictionary<int, long> _cloudWithoutDate = new();

    private int _cloudMessagesRegistered;

    /// <summary>Platzhalter, für den sich die Shell-Abfrage lohnt (nur online, Dienst vorhanden, nicht schon ohne Ergebnis gefragt).</summary>
    private bool IsCloudCandidate(int fileId, string path, long modifiedAt)
    {
        if (_cloudProperties is null || !CloudFiles.IsOnlineOnly(path))
        {
            return false;
        }

        RegisterCloudMessages();
        return !(_cloudWithoutDate.TryGetValue(fileId, out var asked) && asked == modifiedAt);
    }

    /// <summary>
    /// Fragt die Shell je Platzhalter; Treffer kommen zu <paramref name="results"/>. Liefert die Zahl ohne Datum. Braucht eine
    /// Antwort länger als <see cref="SlowCloudRead"/> (OneDrive hängt, keine Verbindung), werden die übrigen Platzhalter
    /// dieses Stapels nicht mehr gefragt — der Stapel hält die Datei-Spur des Planers sonst minutenlang.
    /// </summary>
    private int ReadPlaceholders(IReadOnlyList<PendingRow> placeholders, List<ReadResult> results, CancellationToken token)
    {
        if (placeholders.Count == 0 || _cloudProperties is not { } reader)
        {
            return 0;
        }

        var withoutDate = 0;
        for (var i = 0; i < placeholders.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var row = placeholders[i];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (reader.ReadCaptureDate(row.Path) is { } value)
            {
                results.Add(new ReadResult(row.FileId, row.LibraryId, row.ModifiedAt, value));
            }
            else
            {
                _cloudWithoutDate[row.FileId] = row.ModifiedAt;
                withoutDate++;
            }

            if (clock.Elapsed > SlowCloudRead)
            {
                _logger.LogInformation("Aufnahmedatum: OneDrive antwortet langsam — {Count} Platzhalter folgen in einem späteren Durchgang", placeholders.Count - i - 1);
                return withoutDate + (placeholders.Count - i - 1);
            }
        }

        return withoutDate;
    }

    /// <summary>Ab dieser Antwortzeit gilt die Shell für den Rest des Stapels als zu langsam.</summary>
    internal static readonly TimeSpan SlowCloudRead = TimeSpan.FromSeconds(5);

    /// <summary>Erst beim ersten Platzhalter: lokal gewordene Dateien lösen einen Durchgang aus (das Datum aus der Datei selbst).</summary>
    private void RegisterCloudMessages()
    {
        if (Interlocked.Exchange(ref _cloudMessagesRegistered, 1) != 0)
        {
            return;
        }

        _messenger.Register<CaptureDateService, CloudFilesChangedMessage>(this, static (r, m) =>
        {
            if (m.BecameLocal)
            {
                foreach (var id in m.FileIds)
                {
                    r._cloudWithoutDate.TryRemove(id, out _);
                }

                r.RequestBackfill();
            }
        });
    }
}
