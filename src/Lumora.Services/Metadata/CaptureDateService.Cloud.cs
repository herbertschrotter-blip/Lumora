using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Cloud;
using Lumora.Services.Messaging;
using Lumora.Services.Thumbnails;

namespace Lumora.Services.Metadata;

/// <summary>
/// OneDrive-Platzhalter beim Nachtragen (ADR-0077, Spike 19): Das Aufnahmedatum kommt ohne Download aus dem
/// Eigenschaftsspeicher der Shell (<see cref="ICloudPropertyReader"/>). Windows liefert es als UTC ohne den Zeitversatz der
/// Aufnahme (<c>OffsetTimeOriginal</c>) — es gilt deshalb nur vorläufig (Herkunft <see cref="CaptureDateSource.Cloud"/>,
/// Nachbesserung 0.5.41). Ist die Datei lokal geworden — durch Lumora (<see cref="CloudFilesChangedMessage"/>) oder
/// außerhalb (einmal je Sitzung geprüft, nur Attribute) —, gibt der nächste Durchgang das Datum zum Neulesen frei
/// (Herkunft NULL, das vorläufige Datum bleibt bis dahin stehen) und liest es aus der Datei selbst. Liefert die Shell kein
/// Datum, bleibt die Datei offen und wird in dieser Sitzung nicht erneut gefragt, solange sie nur online liegt. GPS liefert
/// die Shell für Platzhalter nicht — Orte bleiben leer, bis die Datei lokal ist.
/// </summary>
public sealed partial class CaptureDateService
{
    /// <summary>Platzhalter ohne Datum aus der Shell: FileId → Änderungszeit (Unix) der gefragten Version.</summary>
    private readonly ConcurrentDictionary<int, long> _cloudWithoutDate = new();

    /// <summary>Von Lumora lokal gemachte Dateien (Lightbox, „Immer behalten“): vorläufiges Datum beim nächsten Durchgang neu lesen.</summary>
    private readonly ConcurrentQueue<int> _becameLocal = new();

    private int _cloudMessagesRegistered;
    private int _cloudDatesChecked;

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
                // immer vorläufig, egal was der Leser meldet: ohne OffsetTimeOriginal, später aus der Datei (0.5.41)
                results.Add(new ReadResult(row.FileId, row.LibraryId, row.ModifiedAt, value with { Source = CaptureDateSource.Cloud }));
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

    /// <summary>
    /// Vor jedem Durchgang: vorläufige Daten (<see cref="CaptureDateSource.Cloud"/>) lokal gewordener Dateien zum Neulesen
    /// freigeben — die gemeldeten sofort, einmal je Sitzung außerdem alle, deren Attribute „lokal“ sagen (außerhalb von
    /// Lumora geladen, z. B. im Explorer). Nicht erreichbare Wurzeln werden dabei nicht angefasst.
    /// </summary>
    private async Task ReopenLocalCloudDatesAsync(CancellationToken token)
    {
        if (_cloudProperties is not null)
        {
            RegisterCloudMessages(); // auch wenn in dieser Sitzung kein Platzhalter mehr ohne Datum ist
        }

        var reported = new List<int>();
        while (_becameLocal.TryDequeue(out var id))
        {
            reported.Add(id);
        }

        try
        {
            var ids = new List<int>(reported);
            var checkSession = Volatile.Read(ref _cloudDatesChecked) == 0;
            if (checkSession)
            {
                var provisional = await Task.Run(LoadProvisionalCloudDates, token).ConfigureAwait(false);
                if (provisional.Count > 0)
                {
                    // Attribute vieler Dateien lesen: in der Datei-Spur des Planers (wartet beim Einlesen, bei Pause)
                    using var turn = _planner is null ? null : await _planner.AcquireAsync(Background.BackgroundJob.CaptureDates, token).ConfigureAwait(false);
                    var offlineRoots = await LoadOfflineRootsAsync(token).ConfigureAwait(false);
                    ids.AddRange(await Task.Run(
                        () => provisional
                            .Where(r => !IsUnderAny(r.Path, offlineRoots) && ThumbnailService.IsLocallyAvailable(r.Path))
                            .Select(r => r.FileId)
                            .ToList(),
                        token).ConfigureAwait(false));
                }
            }

            var reopened = ids.Count == 0 ? 0 : await ReopenAsync(ids.Distinct().ToList(), token).ConfigureAwait(false);
            if (checkSession)
            {
                Volatile.Write(ref _cloudDatesChecked, 1); // erst nach Erfolg (gesperrter Katalog: nächster Durchgang)
            }

            if (reopened > 0)
            {
                _logger.LogInformation("Aufnahmedatum: {Count} Dateien aus OneDrive liegen jetzt lokal — das Datum wird aus der Datei neu gelesen", reopened);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || IsCatalogUnavailable(ex))
        {
            foreach (var id in reported)
            {
                _becameLocal.Enqueue(id); // Katalog gesperrt oder Abbruch: beim nächsten Durchgang
            }

            throw;
        }
        catch (Exception ex)
        {
            // nur ein Zusatz — das Nachtragen der übrigen Dateien darf daran nie scheitern
            Volatile.Write(ref _cloudDatesChecked, 1);
            _logger.LogWarning(ex, "Aufnahmedatum: vorläufige OneDrive-Daten nicht geprüft — das Nachtragen läuft weiter");
        }
    }

    /// <summary>
    /// Ein zum Neulesen freigegebenes vorläufiges Datum (Herkunft NULL, Datum gesetzt) bleibt, wenn die Datei selbst keins
    /// liefert oder nur eins aus dem Dateinamen (z. B. ein Format, das Lumora nicht liest, ADR-0046) — sonst ginge ein
    /// Datum verloren, das Windows kannte. Liefert die Zeilen, deren Herkunft wieder „vorläufig“ ist.
    /// </summary>
    private async Task<List<ReadResult>> KeepProvisionalAsync(List<ReadResult> results, HashSet<int> provisional, CancellationToken token)
    {
        var keep = results
            .Where(r => provisional.Contains(r.FileId) && r.Value is not { Source: CaptureDateSource.Exif or CaptureDateSource.Video })
            .ToList();
        if (keep.Count == 0)
        {
            return keep;
        }

        results.RemoveAll(r => keep.Contains(r));
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(
                async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, token).ConfigureAwait(false);
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE Files SET CapturedAtSource = $cloud WHERE Id = $id AND ModifiedAtUtc = $modified "
                                      + "AND CapturedAtSource IS NULL AND CapturedAt IS NOT NULL;";
                    cmd.Parameters.AddWithValue("$cloud", (int)CaptureDateSource.Cloud);
                    var id = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                    var modified = cmd.Parameters.Add("$modified", Microsoft.Data.Sqlite.SqliteType.Integer);
                    foreach (var r in keep)
                    {
                        id.Value = r.FileId;
                        modified.Value = r.ModifiedAt;
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                },
                token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        return keep;
    }

    private List<(int FileId, string Path)> LoadProvisionalCloudDates()
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT f.Id, d.Path, f.Name FROM Files f JOIN Folders d ON d.Id = f.FolderId "
                          + "WHERE f.CapturedAtSource = $cloud AND f.Missing = 0;";
        cmd.Parameters.AddWithValue("$cloud", (int)CaptureDateSource.Cloud);
        var rows = new List<(int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt32(0), Path.Combine(reader.GetString(1), reader.GetString(2))));
        }

        return rows;
    }

    /// <summary>Herkunft „vorläufig“ → NULL (noch nicht gelesen); nur Zeilen, die noch vorläufig sind. Liefert die Zahl.</summary>
    private async Task<int> ReopenAsync(IReadOnlyList<int> ids, CancellationToken token)
    {
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, token).ConfigureAwait(false);
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE Files SET CapturedAtSource = NULL WHERE Id = $id AND CapturedAtSource = $cloud;";
                    var id = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                    cmd.Parameters.AddWithValue("$cloud", (int)CaptureDateSource.Cloud);
                    var changed = 0;
                    foreach (var fileId in ids)
                    {
                        id.Value = fileId;
                        changed += cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                    return changed;
                },
                token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
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
                    r._becameLocal.Enqueue(id); // vorläufiges Datum aus der Datei neu lesen
                }

                r.RequestBackfill();
            }
        });
    }
}
