using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Catalog;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;

namespace Lumora.Services.Metadata;

/// <summary>
/// Aufnahmedatum im Katalog (ADR-0046): lesen, „Datum setzen…“ (genau, ungefähr, verschieben, zurücksetzen) mit
/// Protokoll und Rückgängig, Nachtragen im Hintergrund (<c>CaptureDateService.Backfill.cs</c>). Schreibt nur den Katalog,
/// nie die Datei.
/// </summary>
public interface ICaptureDateService
{
    /// <summary>Zustand des Nachtragens (Statusleiste).</summary>
    CaptureDateBackfillStatus BackfillStatus { get; }

    /// <summary>Nach der DB-Initialisierung: Nachrichten abonnieren, ersten Durchgang nach <c>StartDelay</c> planen.</summary>
    void Start();

    /// <summary>Stößt einen Durchgang des Nachtragens an (z. B. nach einem Scan); kehrt sofort zurück.</summary>
    void RequestBackfill();

    /// <summary>Ein Durchgang über alle noch nicht gelesenen Dateien (öffentlich für Tests).</summary>
    Task<CaptureDateBackfillReport> RunBackfillPassAsync(CancellationToken ct = default);

    Task<CaptureDateInfo?> GetAsync(int fileId, CancellationToken ct = default);

    /// <summary>FileIds im Ordner (<paramref name="includeSubfolders"/>: samt allen Unterordnern, über den Ordnerpfad).</summary>
    Task<IReadOnlyList<int>> GetFileIdsInFolderAsync(int folderId, bool includeSubfolders, CancellationToken ct = default);

    /// <summary>
    /// Setzt das Aufnahmedatum der Dateien (Herkunft „manuell“; <see cref="CaptureDateEdit.Reset"/>: wieder lesen).
    /// Ein <c>OperationBatch</c> (<see cref="OperationType.SetCaptureDate"/>) mit einer <c>FileOperation</c> und den alten
    /// Werten je geänderter Datei, alles in einer Transaktion. Unveränderte Dateien zählen nicht.
    /// </summary>
    Task<CaptureDateChangeResult> ApplyAsync(IReadOnlyList<int> fileIds, CaptureDateEdit edit, CancellationToken ct = default);

    /// <summary>Jüngster noch nicht zurückgenommener „Datum setzen“-Batch oder null.</summary>
    Task<int?> GetUndoableBatchIdAsync(CancellationToken ct = default);

    /// <summary>Nimmt den Batch zurück (alte Werte zurück); null, wenn er nicht (mehr) rückgängig zu machen ist.</summary>
    Task<CaptureDateChangeResult?> UndoAsync(int batchId, CancellationToken ct = default);
}

/// <inheritdoc cref="ICaptureDateService"/>
public sealed partial class CaptureDateService : ICaptureDateService, IDisposable
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ICaptureDateReader _reader;
    private readonly IScanGate? _scanGate;
    private readonly Background.IBackgroundPlanner? _planner;
    private readonly ILogger<CaptureDateService> _logger;
    private readonly IMessenger _messenger;

    /// <summary>Single-Writer für Nachtragen, „Datum setzen“ und Rückgängig.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <param name="scanGate">Optional (Tests); produktiv der gemeinsame Singleton — das Nachtragen ruht, solange er die Bibliothek belegt.</param>
    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    public CaptureDateService(
        ILibraryContextFactory factory,
        ICaptureDateReader reader,
        ILogger<CaptureDateService> logger,
        IScanGate? scanGate = null,
        IMessenger? messenger = null,
        Background.IBackgroundPlanner? planner = null)
    {
        _factory = factory;
        _reader = reader;
        _logger = logger;
        _scanGate = scanGate;
        _planner = planner;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public async Task<CaptureDateInfo?> GetAsync(int fileId, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var row = await ctx.Files.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { f.CapturedAt, f.CapturedAtSource, f.CapturedAtPrecision, f.CapturedAtOffset })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        long? unix = row.CapturedAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeSeconds() : null;
        return CaptureDateInfo.FromColumns(fileId, unix, (int?)row.CapturedAtSource, (int)row.CapturedAtPrecision, row.CapturedAtOffset);
    }

    public async Task<IReadOnlyList<int>> GetFileIdsInFolderAsync(int folderId, bool includeSubfolders, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        if (!includeSubfolders)
        {
            return await ctx.Files.AsNoTracking().Where(f => f.FolderId == folderId).OrderBy(f => f.Id).Select(f => f.Id).ToListAsync(ct).ConfigureAwait(false);
        }

        var folder = await ctx.Folders.AsNoTracking()
            .Where(f => f.Id == folderId)
            .Select(f => new { f.LibraryId, f.Path })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (folder is null)
        {
            return Array.Empty<int>();
        }

        // Wie FolderMetadataService: Ordnerpfad und alles darunter (Pfadvergleich ohne Groß-/Kleinschreibung).
        var path = Path.TrimEndingDirectorySeparator(folder.Path);
        var lower = path.ToLowerInvariant();
        var prefix = (path + Path.DirectorySeparatorChar).ToLowerInvariant();
        var libraryId = folder.LibraryId;
        var folderIds = (await ctx.Folders.AsNoTracking()
                .Where(d => d.LibraryId == libraryId)
                .Select(d => new { d.Id, d.Path })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .Where(d =>
            {
                var p = Path.TrimEndingDirectorySeparator(d.Path).ToLowerInvariant();
                return p == lower || p.StartsWith(prefix, StringComparison.Ordinal);
            })
            .Select(d => d.Id)
            .ToList();
        var result = new List<int>();
        foreach (var chunk in folderIds.Chunk(ChunkSize))
        {
            result.AddRange(await ctx.Files.AsNoTracking().Where(f => chunk.Contains(f.FolderId)).Select(f => f.Id).ToListAsync(ct).ConfigureAwait(false));
        }

        result.Sort();
        return result;
    }

    // ---- Datum setzen -------------------------------------------------------------------------------

    private sealed record CurrentRow(int Id, int LibraryId, string Path, long? CapturedAt, int? Source, int Precision, int? Offset, long ModifiedAt);

    private sealed record NewValues(long? CapturedAt, int? Source, int Precision, int? Offset);

    public async Task<CaptureDateChangeResult> ApplyAsync(IReadOnlyList<int> fileIds, CaptureDateEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentNullException.ThrowIfNull(edit);
        Validate(edit);
        var ids = fileIds.Distinct().ToArray();
        var description = edit.Describe();
        if (ids.Length == 0)
        {
            return new CaptureDateChangeResult(0, 0, 0, description);
        }

        // Verschieben noch nicht gelesener Dateien: erst aus der Datei lesen (außerhalb der Schreibtransaktion), sonst
        // würde vom Änderungsdatum aus verschoben und das echte Datum nie mehr gelesen (Herkunft danach „manuell“).
        var unread = edit is CaptureDateEdit.Shift
            ? await ReadUnreadAsync(ids, ct).ConfigureAwait(false)
            : new Dictionary<int, CaptureDateValue>();

        var changedByLibrary = new Dictionary<int, List<int>>();
        CaptureDateChangeResult result;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await CatalogFileTargets.WriteAsync(_factory, ids, _logger, (connection, tx, targets) =>
            {
                var rows = LoadRows(connection, tx, targets.Select(t => t.FileId).ToArray());
                var changes = new List<(CurrentRow Row, NewValues Values)>(rows.Count);
                foreach (var row in rows)
                {
                    var values = Compute(row, edit, unread.TryGetValue(row.Id, out var read) ? read : null);
                    if (values != new NewValues(row.CapturedAt, row.Source, row.Precision, row.Offset))
                    {
                        changes.Add((row, values));
                    }
                }

                if (changes.Count == 0)
                {
                    return new CaptureDateChangeResult(0, 0, rows.Count, description);
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var batchId = InsertBatch(connection, tx, now, $"{description} ({changes.Count} Datei(en))");
                WriteChanges(connection, tx, batchId, now, changes);
                foreach (var (row, _) in changes)
                {
                    if (!changedByLibrary.TryGetValue(row.LibraryId, out var list))
                    {
                        changedByLibrary[row.LibraryId] = list = new List<int>();
                    }

                    list.Add(row.Id);
                }

                return new CaptureDateChangeResult(batchId, changes.Count, rows.Count - changes.Count, description);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Datum setzen: {Description} — {Changed} geändert, {Unchanged} unverändert (Batch {BatchId})", description, result.Changed, result.Unchanged, result.BatchId);
        Publish(changedByLibrary);
        if (edit is CaptureDateEdit.Reset && result.Changed > 0)
        {
            RequestBackfill(); // zurückgesetzte Dateien gleich wieder aus der Datei lesen
        }

        return result;
    }

    private static void Validate(CaptureDateEdit edit)
    {
        switch (edit)
        {
            case CaptureDateEdit.Exact e when e.LocalWallClock.Year < CaptureDateValue.MinPlausible.Year || e.LocalWallClock > DateTime.Now.AddDays(1):
                throw new ArgumentOutOfRangeException(nameof(edit), e.LocalWallClock, "Das Datum liegt außerhalb des gültigen Bereichs.");
            case CaptureDateEdit.Approximate a when a.Year < CaptureDateValue.MinPlausible.Year || a.Year > DateTime.Now.Year || a.Month is < 1 or > 12:
                throw new ArgumentOutOfRangeException(nameof(edit), a.Year, "Jahr oder Monat liegt außerhalb des gültigen Bereichs.");
            case CaptureDateEdit.Shift s when s.Delta == TimeSpan.Zero || s.Delta.Duration() > TimeSpan.FromDays(36_500):
                throw new ArgumentOutOfRangeException(nameof(edit), s.Delta, "Die Verschiebung muss ungleich 0 und kleiner als 100 Jahre sein.");
        }
    }

    /// <summary>Neue Werte einer Datei; ungefähre Angaben liegen in der Mitte ihres Zeitraums (Monat: 15., Jahr: 1. Juli).</summary>
    /// <param name="readNow">Für Verschieben: gerade aus der Datei gelesenes Datum einer noch nicht gelesenen Datei.</param>
    private static NewValues Compute(CurrentRow row, CaptureDateEdit edit, CaptureDateValue? readNow = null)
    {
        switch (edit)
        {
            case CaptureDateEdit.Exact e:
                return From(CaptureDateValue.FromWallClock(e.LocalWallClock, null, CaptureDateSource.Manual, CaptureDatePrecision.Exact));
            case CaptureDateEdit.Approximate { Month: null } a:
                return From(CaptureDateValue.FromWallClock(new DateTime(a.Year, 7, 1, 12, 0, 0), null, CaptureDateSource.Manual, CaptureDatePrecision.Year));
            case CaptureDateEdit.Approximate a:
                return From(CaptureDateValue.FromWallClock(new DateTime(a.Year, a.Month!.Value, 15, 12, 0, 0), null, CaptureDateSource.Manual, CaptureDatePrecision.Month));
            case CaptureDateEdit.Shift s when row.Source is null && readNow is { } read:
                var readShifted = new DateTimeOffset(read.Utc).ToUnixTimeSeconds() + (long)s.Delta.TotalSeconds;
                return new NewValues(readShifted, (int)CaptureDateSource.Manual, (int)read.Precision, read.OffsetMinutes);
            case CaptureDateEdit.Shift s:
                var has = row.CapturedAt is not null && row.Source is not null;
                var baseUnix = has ? row.CapturedAt!.Value : row.ModifiedAt;
                var shifted = baseUnix + (long)s.Delta.TotalSeconds;
                var utc = DateTimeOffset.FromUnixTimeSeconds(shifted).UtcDateTime;
                var offset = has && row.Offset is { } o ? o : (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(utc).TotalMinutes);
                return new NewValues(shifted, (int)CaptureDateSource.Manual, has ? row.Precision : (int)CaptureDatePrecision.Exact, offset);
            default:
                return new NewValues(null, null, (int)CaptureDatePrecision.Exact, null);
        }

        static NewValues From(CaptureDateValue v)
            => new(new DateTimeOffset(v.Utc).ToUnixTimeSeconds(), (int)v.Source, (int)v.Precision, v.OffsetMinutes);
    }

    /// <summary>Datum aus der Datei für noch nicht gelesene, lokal vorliegende Dateien (wie das Nachtragen).</summary>
    private async Task<Dictionary<int, CaptureDateValue>> ReadUnreadAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        var paths = new List<(int Id, string Path)>();
        await using (var ctx = _factory.Create())
        {
            foreach (var chunk in ids.Chunk(ChunkSize))
            {
                var rows = await ctx.Files.AsNoTracking()
                    .Where(f => chunk.Contains(f.Id) && f.CapturedAtSource == null && !f.Missing)
                    .Join(ctx.Folders.AsNoTracking(), f => f.FolderId, d => d.Id, (f, d) => new { f.Id, d.Path, f.Name })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                paths.AddRange(rows.Select(r => (r.Id, Path.Combine(r.Path, r.Name))));
            }
        }

        return await Task.Run(
            () =>
            {
                var result = new Dictionary<int, CaptureDateValue>();
                foreach (var (id, path) in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    if (ThumbnailService.IsLocallyAvailable(path) && _reader.Read(path) is { } value)
                    {
                        result[id] = value;
                    }
                }

                return result;
            },
            ct).ConfigureAwait(false);
    }

    private static List<CurrentRow> LoadRows(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<int> ids)
    {
        var rows = new List<CurrentRow>(ids.Count);
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "SELECT f.Id, f.LibraryId, d.Path, f.Name, f.CapturedAt, f.CapturedAtSource, f.CapturedAtPrecision, f.CapturedAtOffset, f.ModifiedAtUtc " +
                $"FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({CatalogFileTargets.BindIds(cmd, chunk, "$f")});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new CurrentRow(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    Path.Combine(reader.GetString(2), reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.GetInt64(8)));
            }
        }

        return rows;
    }

    private static int InsertBatch(SqliteConnection connection, SqliteTransaction tx, long now, string description)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO OperationBatches (OperationType, Status, CreatedAtUtc, CompletedAtUtc, PlanId, Description)
            VALUES ($type, $status, $now, $now, NULL, $description);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$type", (int)OperationType.SetCaptureDate);
        cmd.Parameters.AddWithValue("$status", (int)BatchStatus.Applied);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$description", description);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Je Datei: FileOperation (Applied) + alte Werte in CaptureDateUndo + neue Werte in Files.</summary>
    private static void WriteChanges(SqliteConnection connection, SqliteTransaction tx, int batchId, long now, IReadOnlyList<(CurrentRow Row, NewValues Values)> changes)
    {
        using var op = connection.CreateCommand();
        op.Transaction = tx;
        op.CommandText = """
            INSERT INTO FileOperations (BatchId, FileId, Action, SourcePath, DestPath, Status, Error, ExecutedAtUtc, UndoneAtUtc)
            VALUES ($batch, $file, $action, $path, NULL, $status, NULL, $now, NULL);
            SELECT last_insert_rowid();
            """;
        op.Parameters.AddWithValue("$batch", batchId);
        var pFile = op.Parameters.Add("$file", SqliteType.Integer);
        op.Parameters.AddWithValue("$action", (int)OperationType.SetCaptureDate);
        var pPath = op.Parameters.Add("$path", SqliteType.Text);
        op.Parameters.AddWithValue("$status", (int)FileOperationStatus.Applied);
        op.Parameters.AddWithValue("$now", now);

        using var undo = connection.CreateCommand();
        undo.Transaction = tx;
        undo.CommandText = """
            INSERT INTO CaptureDateUndo (FileOperationId, OldCapturedAt, OldSource, OldPrecision, OldOffset)
            VALUES ($op, $at, $source, $precision, $offset);
            """;
        var uOp = undo.Parameters.Add("$op", SqliteType.Integer);
        var uAt = undo.Parameters.Add("$at", SqliteType.Integer);
        var uSource = undo.Parameters.Add("$source", SqliteType.Integer);
        var uPrecision = undo.Parameters.Add("$precision", SqliteType.Integer);
        var uOffset = undo.Parameters.Add("$offset", SqliteType.Integer);

        using var update = CreateUpdateCommand(connection, tx, onlyIfModifiedAt: false);

        foreach (var (row, values) in changes)
        {
            pFile.Value = row.Id;
            pPath.Value = row.Path;
            var opId = Convert.ToInt64(op.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

            uOp.Value = opId;
            uAt.Value = (object?)row.CapturedAt ?? DBNull.Value;
            uSource.Value = (object?)row.Source ?? DBNull.Value;
            uPrecision.Value = row.Precision;
            uOffset.Value = (object?)row.Offset ?? DBNull.Value;
            undo.ExecuteNonQuery();

            SetUpdate(update, row.Id, values);
            update.ExecuteNonQuery();
        }
    }

    /// <summary>UPDATE der vier Spalten; mit <paramref name="onlyIfModifiedAt"/> nur für die gelesene Dateiversion und nur, solange nichts gesetzt ist.</summary>
    private static SqliteCommand CreateUpdateCommand(SqliteConnection connection, SqliteTransaction tx, bool onlyIfModifiedAt)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Files SET CapturedAt = $at, CapturedAtSource = $source, CapturedAtPrecision = $precision, CapturedAtOffset = $offset WHERE Id = $id"
                          + (onlyIfModifiedAt ? " AND ModifiedAtUtc = $modified AND CapturedAtSource IS NULL;" : ";");
        cmd.Parameters.Add("$at", SqliteType.Integer);
        cmd.Parameters.Add("$source", SqliteType.Integer);
        cmd.Parameters.Add("$precision", SqliteType.Integer);
        cmd.Parameters.Add("$offset", SqliteType.Integer);
        cmd.Parameters.Add("$id", SqliteType.Integer);
        if (onlyIfModifiedAt)
        {
            cmd.Parameters.Add("$modified", SqliteType.Integer);
        }

        return cmd;
    }

    private static void SetUpdate(SqliteCommand cmd, int fileId, NewValues values)
    {
        cmd.Parameters["$at"].Value = (object?)values.CapturedAt ?? DBNull.Value;
        cmd.Parameters["$source"].Value = (object?)values.Source ?? DBNull.Value;
        cmd.Parameters["$precision"].Value = values.Precision;
        cmd.Parameters["$offset"].Value = (object?)values.Offset ?? DBNull.Value;
        cmd.Parameters["$id"].Value = fileId;
    }

    // ---- Rückgängig ---------------------------------------------------------------------------------

    public async Task<int?> GetUndoableBatchIdAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        return await ctx.OperationBatches.AsNoTracking()
            .Where(b => b.OperationType == OperationType.SetCaptureDate && b.Status == BatchStatus.Applied)
            .OrderByDescending(b => b.Id)
            .Select(b => (int?)b.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<CaptureDateChangeResult?> UndoAsync(int batchId, CancellationToken ct = default)
    {
        var changedByLibrary = new Dictionary<int, List<int>>();
        CaptureDateChangeResult? result;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await Task.Run(
                async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                    var undone = UndoCore(connection, tx, batchId, changedByLibrary);
                    tx.Commit();
                    return undone;
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        if (result is not null)
        {
            _logger.LogInformation("Datum setzen rückgängig: Batch {BatchId} — {Changed} Datei(en) zurückgesetzt, {Missing} nicht mehr im Katalog", batchId, result.Changed, result.Unchanged);
            Publish(changedByLibrary);
            RequestBackfill(); // zurückgenommen auf „noch nicht gelesen“ → gleich lesen
        }

        return result;
    }

    private static CaptureDateChangeResult? UndoCore(SqliteConnection connection, SqliteTransaction tx, int batchId, Dictionary<int, List<int>> changedByLibrary)
    {
        string? description;
        using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT Description FROM OperationBatches WHERE Id = $b AND OperationType = $type AND Status = $applied;";
            check.Parameters.AddWithValue("$b", batchId);
            check.Parameters.AddWithValue("$type", (int)OperationType.SetCaptureDate);
            check.Parameters.AddWithValue("$applied", (int)BatchStatus.Applied);
            var value = check.ExecuteScalar();
            if (value is null)
            {
                return null;
            }

            description = value as string;
        }

        var entries = new List<(long OpId, int? FileId, string Path, NewValues Old)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT o.Id, o.FileId, o.SourcePath, u.OldCapturedAt, u.OldSource, u.OldPrecision, u.OldOffset
                FROM FileOperations o JOIN CaptureDateUndo u ON u.FileOperationId = o.Id
                WHERE o.BatchId = $b AND o.Status = $applied
                ORDER BY o.Id DESC;
                """;
            read.Parameters.AddWithValue("$b", batchId);
            read.Parameters.AddWithValue("$applied", (int)FileOperationStatus.Applied);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                entries.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.GetString(2),
                    new NewValues(
                        reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6))));
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var update = CreateUpdateCommand(connection, tx, onlyIfModifiedAt: false);
        using var mark = connection.CreateCommand();
        mark.Transaction = tx;
        mark.CommandText = "UPDATE FileOperations SET Status = $undone, UndoneAtUtc = $now WHERE Id = $op;";
        mark.Parameters.AddWithValue("$undone", (int)FileOperationStatus.Undone);
        mark.Parameters.AddWithValue("$now", now);
        var mOp = mark.Parameters.Add("$op", SqliteType.Integer);

        var restored = 0;
        var missing = 0;
        foreach (var (opId, fileId, path, old) in entries)
        {
            // Nach „Neu aufbauen“ hat die Datei eine neue FileId — dann über Ordnerpfad + Name finden.
            var target = ResolveFile(connection, tx, fileId, path);
            if (target is { } t)
            {
                SetUpdate(update, t.FileId, old);
                update.ExecuteNonQuery();
                restored++;
                if (!changedByLibrary.TryGetValue(t.LibraryId, out var list))
                {
                    changedByLibrary[t.LibraryId] = list = new List<int>();
                }

                list.Add(t.FileId);
            }
            else
            {
                missing++;
            }

            mOp.Value = opId;
            mark.ExecuteNonQuery();
        }

        using (var batch = connection.CreateCommand())
        {
            batch.Transaction = tx;
            batch.CommandText = "UPDATE OperationBatches SET Status = $undone, CompletedAtUtc = $now WHERE Id = $b;";
            batch.Parameters.AddWithValue("$undone", (int)BatchStatus.Undone);
            batch.Parameters.AddWithValue("$now", now);
            batch.Parameters.AddWithValue("$b", batchId);
            batch.ExecuteNonQuery();
        }

        return new CaptureDateChangeResult(batchId, restored, missing, description ?? "Aufnahmedatum");
    }

    private static CatalogFileTarget? ResolveFile(SqliteConnection connection, SqliteTransaction tx, int? fileId, string path)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        if (fileId is { } id)
        {
            cmd.CommandText = "SELECT f.Id, f.LibraryId FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id = $id AND d.Path = $dir AND f.Name = $name;";
            cmd.Parameters.AddWithValue("$id", id);
        }
        else
        {
            cmd.CommandText = "SELECT f.Id, f.LibraryId FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE d.Path = $dir AND f.Name = $name;";
        }

        cmd.Parameters.AddWithValue("$dir", Path.GetDirectoryName(path) ?? string.Empty);
        cmd.Parameters.AddWithValue("$name", Path.GetFileName(path));
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                return new CatalogFileTarget(reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        if (fileId is not null)
        {
            // Id gibt es nicht mehr oder die Datei wurde verschoben: zuerst die Id allein, dann der Pfad.
            using var byId = connection.CreateCommand();
            byId.Transaction = tx;
            byId.CommandText = "SELECT Id, LibraryId FROM Files WHERE Id = $id;";
            byId.Parameters.AddWithValue("$id", fileId.Value);
            using (var reader = byId.ExecuteReader())
            {
                if (reader.Read())
                {
                    return new CatalogFileTarget(reader.GetInt32(0), reader.GetInt32(1));
                }
            }

            return ResolveFile(connection, tx, null, path);
        }

        return null;
    }

    private void Publish(Dictionary<int, List<int>> byLibrary)
    {
        foreach (var (libraryId, list) in byLibrary)
        {
            try
            {
                _messenger.Send(new CaptureDatesChangedMessage(libraryId, list));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der CaptureDatesChangedMessage warf eine Ausnahme");
            }
        }
    }

    public void Dispose()
    {
        StopBackfill();
        _writeLock.Dispose();
    }
}
