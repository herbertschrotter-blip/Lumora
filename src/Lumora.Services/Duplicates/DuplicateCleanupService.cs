using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;

namespace Lumora.Services.Duplicates;

/// <summary>
/// „Übrige in den Papierkorb“ und „Rückgängig“ der Seite „Doppelte“ (ADR-0053) über den bestehenden Weg der
/// Datei-Operationen (ADR-0012): ein Recycle-Batch von <see cref="IFileOperationService.RecycleAsync"/> mit Protokoll,
/// Papierkorb statt Löschen, Katalog/Thumbnails/FTS konsistent. Vorher (unter der Scan-Sperre der Bibliotheken) je
/// gelöschter Datei ein <see cref="CatalogRowSnapshot"/> und — mit „Angaben übertragen“ — die <see cref="MetadataTransfer"/>
/// auf die behaltene Datei; beides landet nach dem Batch in <c>DuplicateCleanupUndo</c>. „Rückgängig“ holt die Dateien
/// über <see cref="IFileOperationService.RestoreRecycledAsync"/> mit derselben FileId und allen Angaben zurück und nimmt die
/// Übertragung an den behaltenen Dateien zurück.
/// </summary>
public interface IDuplicateCleanupService
{
    Task<DuplicateCleanupResult> CleanUpAsync(IReadOnlyList<DuplicateCleanupGroup> groups, bool transferMetadata, CancellationToken ct = default);

    /// <summary>Jüngste Aufräumaktion, die sich noch zurücknehmen lässt (oder null).</summary>
    Task<DuplicateUndoInfo?> GetUndoableAsync(CancellationToken ct = default);

    Task<DuplicateUndoResult?> UndoAsync(int batchId, CancellationToken ct = default);
}

/// <inheritdoc cref="IDuplicateCleanupService"/>
public sealed class DuplicateCleanupService : IDuplicateCleanupService
{
    private readonly ILibraryContextFactory _factory;
    private readonly IFileOperationService _fileOperations;
    private readonly IScanGate? _scanGate;
    private readonly ILogger<DuplicateCleanupService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DuplicateCleanupService(
        ILibraryContextFactory factory,
        IFileOperationService fileOperations,
        ILogger<DuplicateCleanupService> logger,
        IScanGate? scanGate = null,
        IMessenger? messenger = null)
    {
        _factory = factory;
        _fileOperations = fileOperations;
        _logger = logger;
        _scanGate = scanGate;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    private sealed record Prepared(int FileId, int KeepFileId, string Snapshot, TransferLog Log);

    public async Task<DuplicateCleanupResult> CleanUpAsync(IReadOnlyList<DuplicateCleanupGroup> groups, bool transferMetadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var plan = Validate(groups);
        if (plan.Count == 0)
        {
            return new DuplicateCleanupResult(0, 0, 0, 0, 0, []);
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Abzug + Übertragung unter der Scan-Sperre (vor dem Papierkorb: der löscht per Kaskade Tags & Co. mit)
            var libraries = await LibrariesOfAsync(plan.SelectMany(p => new[] { p.Keep, p.Delete }), ct).ConfigureAwait(false);
            var prepared = new List<Prepared>(plan.Count);
            var missed = 0;
            using (await AcquireAsync(libraries.Values.Distinct(), ct).ConfigureAwait(false))
            {
                await Task.Run(
                    async () =>
                    {
                        using var connection = _factory.OpenSqliteConnection();
                        using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                        foreach (var (keep, delete, exact) in plan)
                        {
                            var snapshot = CatalogRowSnapshot.Capture(connection, tx, delete);
                            var log = new TransferLog();
                            if (transferMetadata)
                            {
                                (log, var personsMissed) = MetadataTransfer.Transfer(connection, tx, keep, delete, exact);
                                missed += personsMissed;
                            }

                            prepared.Add(new Prepared(delete, keep, snapshot, log));
                        }

                        tx.Commit();
                    },
                    ct).ConfigureAwait(false);
            }

            // 2. Papierkorb über die Datei-Operationen (eigener Batch, eigene Scan-Sperre)
            FileOperationBatchResult result;
            try
            {
                result = await _fileOperations.RecycleAsync(prepared.Select(p => p.FileId).ToList(), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await RevertTransfersAsync(prepared).ConfigureAwait(false); // nichts gelöscht: Übertragung zurück
                throw;
            }

            var succeeded = result.Items.Where(i => i.Success).Select(i => i.FileId).ToHashSet();

            // Dateien, die bleiben mussten (gesperrt, kein Papierkorb …): ihre Übertragung gleich zurücknehmen — sie haben
            // keine Rückgängig-Zeile, und ihre Angaben liegen ja weiter an ihnen selbst.
            await RevertTransfersAsync(prepared.Where(p => !succeeded.Contains(p.FileId)).ToList()).ConfigureAwait(false);
            prepared = prepared.Where(p => succeeded.Contains(p.FileId)).ToList();
            var errors = result.Items.Where(i => !i.Success).Select(i => $"{Path.GetFileName(i.SourcePath)}: {i.Error}").ToList();

            // 3. Rückgängig-Daten der gelöschten Dateien
            if (result.BatchId > 0 && succeeded.Count > 0)
            {
                await using var ctx = _factory.Create();
                var now = DateTime.UtcNow;
                foreach (var p in prepared.Where(p => succeeded.Contains(p.FileId)))
                {
                    ctx.DuplicateCleanupUndo.Add(new DuplicateCleanupUndo
                    {
                        BatchId = result.BatchId,
                        FileId = p.FileId,
                        KeptFileId = p.KeepFileId,
                        Snapshot = p.Snapshot,
                        Transfer = p.Log.IsEmpty ? null : p.Log.ToJson(),
                        CreatedAtUtc = now,
                    });
                }

                var description = $"Doppelte aufgeräumt: {succeeded.Count} Datei(en) in den Papierkorb";
                var batchId = result.BatchId;
                await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                await ctx.OperationBatches.Where(b => b.Id == batchId)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Description, description), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var transferred = prepared.Sum(p => p.Log.Count);
            PublishKeptChanged(prepared, libraries);
            _logger.LogInformation(
                "Doppelte aufgeräumt: {Recycled} im Papierkorb, {Failed} fehlgeschlagen, {Transferred} Angaben übertragen, {Missed} Personen nicht übertragbar (Batch {BatchId})",
                result.Succeeded, result.Failed, transferred, missed, result.BatchId);
            return new DuplicateCleanupResult(result.BatchId, result.Succeeded, result.Failed, transferred, missed, errors);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DuplicateUndoInfo?> GetUndoableAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var latest = await ctx.DuplicateCleanupUndo.AsNoTracking()
            .Where(u => u.UndoneAtUtc == null && u.KeptFileId != 0) // KeptFileId 0 = Abzüge von „Ordner in den Papierkorb“/„Fehlende entfernen“ (ADR-0065)
            .Join(ctx.OperationBatches.AsNoTracking(), u => u.BatchId, b => b.Id, (u, b) => new { u.BatchId, b.Status, b.CreatedAtUtc, b.Description })
            .Where(x => x.Status == BatchStatus.Applied || x.Status == BatchStatus.Undone) // Undone: teilweise zurückgeholt, Rest noch offen
            .OrderByDescending(x => x.BatchId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (latest is null)
        {
            return null;
        }

        var count = await ctx.DuplicateCleanupUndo.CountAsync(u => u.BatchId == latest.BatchId && u.UndoneAtUtc == null && u.KeptFileId != 0, ct).ConfigureAwait(false);
        return new DuplicateUndoInfo(latest.BatchId, count, latest.CreatedAtUtc, latest.Description);
    }

    public async Task<DuplicateUndoResult?> UndoAsync(int batchId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<DuplicateCleanupUndo> rows;
            await using (var ctx = _factory.Create())
            {
                rows = await ctx.DuplicateCleanupUndo.AsNoTracking()
                    .Where(u => u.BatchId == batchId && u.UndoneAtUtc == null && u.KeptFileId != 0) // ADR-0065
                    .OrderByDescending(u => u.Id)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }

            if (rows.Count == 0)
            {
                return null;
            }

            var byFileId = rows.ToDictionary(r => r.FileId);
            var restoredIds = new HashSet<int>();
            var result = await _fileOperations.RestoreRecycledAsync(
                batchId,
                (connection, transaction, file) =>
                {
                    if (file.OldFileId is not { } old || !byFileId.TryGetValue(old, out var row))
                    {
                        return null;
                    }

                    var snapshot = CatalogRowSnapshot.Parse(row.Snapshot);
                    var info = new FileInfo(file.Path);
                    if (!CatalogRowSnapshot.RestoreFileRow(connection, transaction, snapshot, file.FolderId, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()))
                    {
                        return null;
                    }

                    CatalogRowSnapshot.RestoreDependents(connection, transaction, snapshot);
                    restoredIds.Add(old);
                    return old;
                },
                ct).ConfigureAwait(false);
            if (result is null)
            {
                return null;
            }

            // Übertragung an den behaltenen Dateien zurücknehmen — nur, wo die Datei mit ihren Angaben zurück ist (sonst
            // blieben die Angaben nirgends); als erledigt gilt jede zurückgeholte Datei.
            // erst nach dem Commit sicher: das Item muss gelungen sein und die alte FileId tragen
            restoredIds.IntersectWith(result.Items.Where(i => i.Success).Select(i => i.FileId));
            var back = rows.Where(r => restoredIds.Contains(r.FileId)).ToList();
            var successPaths = result.Items.Where(i => i.Success).Select(i => i.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var done = rows.Where(r => restoredIds.Contains(r.FileId) || (PathOf(r) is { } p && successPaths.Contains(p))).ToList();
            var libraries = await LibrariesOfAsync(back.Select(r => r.KeptFileId), ct).ConfigureAwait(false);
            await Task.Run(
                async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, CancellationToken.None).ConfigureAwait(false);
                    foreach (var row in back)
                    {
                        MetadataTransfer.Revert(connection, tx, row.KeptFileId, TransferLog.FromJson(row.Transfer));
                    }

                    using var mark = connection.CreateCommand();
                    mark.Transaction = tx;
                    mark.CommandText = $"UPDATE DuplicateCleanupUndo SET UndoneAtUtc = $now WHERE Id IN ({DuplicateSql.IdList(done.Select(r => r.Id))})";
                    mark.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    mark.ExecuteNonQuery();
                    tx.Commit();
                },
                CancellationToken.None).ConfigureAwait(false);

            PublishKeptChanged(back.Select(r => new Prepared(r.FileId, r.KeptFileId, r.Snapshot, TransferLog.FromJson(r.Transfer))).ToList(), libraries);
            var errors = result.Items.Where(i => !i.Success).Select(i => $"{Path.GetFileName(i.SourcePath)}: {i.Error}").ToList();
            _logger.LogInformation("Doppelte: Aufräumen {BatchId} rückgängig — {Ok} zurückgeholt, {Failed} nicht", batchId, result.Succeeded, result.Failed);
            return new DuplicateUndoResult(batchId, result.Succeeded, result.Failed, errors);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Übertragungen zurücknehmen (jüngste zuerst) — für Dateien, die nicht gelöscht wurden.</summary>
    private Task RevertTransfersAsync(IReadOnlyList<Prepared> items)
    {
        var withTransfer = items.Where(p => !p.Log.IsEmpty).Reverse().ToList();
        if (withTransfer.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, CancellationToken.None).ConfigureAwait(false);
            foreach (var p in withTransfer)
            {
                MetadataTransfer.Revert(connection, tx, p.KeepFileId, p.Log);
            }

            tx.Commit();
            _logger.LogInformation("Doppelte: Übertragung für {Count} nicht gelöschte Datei(en) zurückgenommen", withTransfer.Count);
        });
    }

    private static string? PathOf(DuplicateCleanupUndo row)
    {
        try
        {
            var snapshot = CatalogRowSnapshot.Parse(row.Snapshot);
            return Path.Combine(snapshot.FolderPath, snapshot.Name);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Prüft die Gruppen: behaltene Datei nie gelöscht, keine Datei doppelt; liefert (behalten, löschen, exakt).</summary>
    private static List<(int Keep, int Delete, bool Exact)> Validate(IReadOnlyList<DuplicateCleanupGroup> groups)
    {
        var keeps = groups.Select(g => g.KeepFileId).ToHashSet();
        var seen = new HashSet<int>();
        var plan = new List<(int, int, bool)>();
        foreach (var group in groups)
        {
            foreach (var delete in group.DeleteFileIds)
            {
                if (delete == group.KeepFileId || keeps.Contains(delete))
                {
                    throw new ArgumentException($"Datei {delete} soll behalten und gelöscht werden.", nameof(groups));
                }

                if (seen.Add(delete))
                {
                    plan.Add((group.KeepFileId, delete, group.IsExact));
                }
            }
        }

        return plan;
    }

    private async Task<Dictionary<int, int>> LibrariesOfAsync(IEnumerable<int> fileIds, CancellationToken ct)
    {
        var ids = fileIds.Distinct().ToList();
        var map = new Dictionary<int, int>();
        await using var ctx = _factory.Create();
        foreach (var chunk in ids.Chunk(500))
        {
            foreach (var row in await ctx.Files.AsNoTracking().Where(f => chunk.Contains(f.Id)).Select(f => new { f.Id, f.LibraryId }).ToListAsync(ct).ConfigureAwait(false))
            {
                map[row.Id] = row.LibraryId;
            }
        }

        return map;
    }

    private async Task<IDisposable> AcquireAsync(IEnumerable<int> libraryIds, CancellationToken ct)
    {
        var leases = new List<IDisposable>();
        if (_scanGate is null)
        {
            return new Leases(leases);
        }

        try
        {
            foreach (var id in libraryIds.Distinct().Order())
            {
                leases.Add(await _scanGate.AcquireForFileOperationAsync(id, ct).ConfigureAwait(false));
            }
        }
        catch
        {
            new Leases(leases).Dispose();
            throw;
        }

        return new Leases(leases);
    }

    private sealed class Leases(List<IDisposable> leases) : IDisposable
    {
        public void Dispose()
        {
            foreach (var lease in Enumerable.Reverse(leases))
            {
                lease.Dispose();
            }
        }
    }

    private void PublishKeptChanged(IReadOnlyList<Prepared> prepared, IReadOnlyDictionary<int, int> libraries)
    {
        var byLibrary = prepared
            .Where(p => libraries.ContainsKey(p.KeepFileId))
            .GroupBy(p => libraries[p.KeepFileId]);
        foreach (var group in byLibrary)
        {
            var kept = group.Select(p => p.KeepFileId).Distinct().ToList();
            Send(new FilesChangedMessage(group.Key, FilesChangeKind.Metadata, kept));
            if (group.Any(p => p.Log.TouchesFaces))
            {
                Send(new FacesChangedMessage(group.Key, kept));
                Send(new PersonsChangedMessage(group.Key));
            }
        }

        Send(new DuplicatesChangedMessage(libraries.Values.Distinct().ToList()));
    }

    private void Send<TMessage>(TMessage message)
        where TMessage : class
    {
        try
        {
            _messenger.Send(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der {Message} warf eine Ausnahme", typeof(TMessage).Name);
        }
    }
}
