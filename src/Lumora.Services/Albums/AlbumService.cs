using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Data.RawSql;
using Lumora.Domain.Enums;
using Lumora.Services.Catalog;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Messaging;

namespace Lumora.Services.Albums;

/// <summary>
/// Alben über Raw-SQL (ADR-0054). Lesen ohne Transaktion (WAL), Schreiben mit asynchronem Warten auf die Schreibsperre
/// (<see cref="SqliteBusyRetry"/>, ADR-0030); Zuweisungen an Dateien über <see cref="CatalogFileTargets"/> (FileIds nach
/// „Neu aufbauen“ umgesetzt). Eigener Single-Writer-Lock. Bereich und Gruppe kommen frisch aus <see cref="ILibraryScope"/>
/// (ADR-0048): Alben der Gruppe, Dateien des Bereichs. Intelligente Alben wertet <see cref="ISmartAlbumEvaluator"/> aus
/// (Wpf-Projekt). Kehrt eine entfernte Datei an ihren alten Pfad zurück (Windows-Papierkorb „Wiederherstellen“ + Scan),
/// stellt der Dienst ihre gesicherten Verknüpfungen wieder her (<see cref="FileLinkUndoStore"/>).
/// </summary>
public sealed class AlbumService : IAlbumService, IDisposable
{
    public const int MaxNameLength = 100;
    private const int ChunkSize = 500;
    private const int CopyBufferSize = 1 << 20;

    private readonly ILibraryContextFactory _factory;
    private readonly ILibraryScope _scope;
    private readonly ILogger<AlbumService> _logger;
    private readonly IMessenger _messenger;
    private readonly ISmartAlbumEvaluator? _evaluator;
    private readonly ICatalogStoragePaths? _catalog;
    private readonly Quality.IHighlightSelector? _highlights; // ADR-0068: automatisches Titelbild = höchste Foto-Güte
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Höchstens so viele Fotos eines Albums gehen in die automatische Titelbild-Wahl (neueste zuerst, ADR-0068).</summary>
    public const int MaxCoverCandidates = 400;

    /// <param name="evaluator">Auswerter der intelligenten Alben (Wpf); ohne kennen sie keine Dateien.</param>
    /// <param name="catalog">Katalog-Sitzung: in einer versteckten Bibliothek ist Exportieren gesperrt (ADR-0049).</param>
    public AlbumService(
        ILibraryContextFactory factory,
        ILogger<AlbumService> logger,
        IMessenger? messenger = null,
        ILibraryScope? scope = null,
        ISmartAlbumEvaluator? evaluator = null,
        ICatalogStoragePaths? catalog = null,
        Quality.IHighlightSelector? highlights = null)
    {
        _highlights = highlights;
        _factory = factory;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _scope = scope ?? new LibraryScope(factory);
        _evaluator = evaluator;
        _catalog = catalog;
        _messenger.Register<AlbumService, FilesChangedMessage>(this, static (r, m) => r.OnFilesChanged(m));
    }

    /// <summary>Wird nach dem Wiederherstellen von Verknüpfungen zurückgekehrter Dateien fertig (Tests).</summary>
    internal Task LastRestore { get; private set; } = Task.CompletedTask;

    // ---- Lesen -----------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AlbumInfo>> GetAllAsync(bool evaluateSmart = true, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var rows = await ReadAsync(connection => ReadAlbums(connection, scope.GroupId, albumId: null), ct).ConfigureAwait(false);
        var result = new List<AlbumInfo>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await ToInfoAsync(row, scope, ct, evaluateSmart).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<AlbumInfo?> GetAsync(int albumId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var row = (await ReadAsync(connection => ReadAlbums(connection, scope.GroupId, albumId), ct).ConfigureAwait(false)).FirstOrDefault();
        return row is null ? null : await ToInfoAsync(row, scope, ct).ConfigureAwait(false);
    }

    public async Task<AlbumInfo?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var row = (await ReadAsync(connection => ReadAlbums(connection, scope.GroupId, albumId: null, name: trimmed), ct).ConfigureAwait(false)).FirstOrDefault();
        return row is null ? null : await ToInfoAsync(row, scope, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<int>> GetFileIdsAsync(int albumId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var row = (await ReadAsync(connection => ReadAlbums(connection, scope.GroupId, albumId), ct).ConfigureAwait(false)).FirstOrDefault();
        if (row is null || !scope.HasLibrary)
        {
            return [];
        }

        if (row.Kind == AlbumKind.Smart)
        {
            return SmartAlbumFilter.FromJson(row.FilterJson) is { } filter && _evaluator is not null
                ? await _evaluator.GetFileIdsAsync(filter, scope, ct).ConfigureAwait(false)
                : [];
        }

        return await ReadAsync(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.Id FROM AlbumItems ai CROSS JOIN Files f ON f.Id = ai.FileId
                WHERE ai.AlbumId = $a AND f.LibraryId IN ({scope.SqlLibraryIdList})
                ORDER BY {CaptureDateSortSql.SortKey} DESC, f.Id DESC;
                """;
            cmd.Parameters.AddWithValue("$a", albumId);
            var ids = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt32(0));
            }

            return (IReadOnlyList<int>)ids;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetAlbumIdsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<int>>();
        }

        var groupId = (await _scope.GetCurrentAsync(ct).ConfigureAwait(false)).GroupId;
        return await ReadAsync(connection =>
        {
            var map = new Dictionary<int, List<int>>();
            foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT ai.FileId, ai.AlbumId FROM AlbumItems ai JOIN Albums a ON a.Id = ai.AlbumId
                    WHERE a.GroupId = $g AND ai.FileId IN ({CatalogFileTargets.BindIds(cmd, chunk, "$f")});
                    """;
                cmd.Parameters.AddWithValue("$g", groupId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!map.TryGetValue(reader.GetInt32(0), out var list))
                    {
                        list = new List<int>();
                        map[reader.GetInt32(0)] = list;
                    }

                    list.Add(reader.GetInt32(1));
                }
            }

            return (IReadOnlyDictionary<int, IReadOnlyList<int>>)map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
        }, ct).ConfigureAwait(false);
    }

    // ---- Stammdaten ------------------------------------------------------------------------------------------------

    public async Task<AlbumInfo> CreateAsync(string name, CancellationToken ct = default)
    {
        var normalized = NormalizeName(name);
        var id = await InsertAsync(normalized, AlbumKind.Manual, filterJson: null, ct).ConfigureAwait(false);
        _logger.LogInformation("Album angelegt: Id {Id}", id);
        Publish(new AlbumsChangedMessage(AlbumsChangeKind.Created, id));
        return await GetAsync(id, ct).ConfigureAwait(false) ?? throw new AlbumNotFoundException(id);
    }

    public async Task<AlbumInfo> SaveSmartAsync(string name, SmartAlbumFilter filter, bool replaceExisting = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var normalized = NormalizeName(name);
        if (filter.IsEmpty)
        {
            throw new ArgumentException("Ein intelligentes Album braucht mindestens einen Filter — sonst zeigt es alles.", nameof(filter));
        }

        var json = filter.ToJson();
        var existing = await FindByNameAsync(normalized, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.IsSmart)
            {
                throw new InvalidOperationException($"Es gibt schon ein eigenes Album „{existing.Name}“ — wähle einen anderen Namen.");
            }

            if (!replaceExisting)
            {
                throw new InvalidOperationException($"Ein Album „{existing.Name}“ existiert bereits.");
            }

            await WriteAsync(
                (connection, tx) => LibraryGroupStore.Execute(connection, tx,
                    "UPDATE Albums SET FilterJson = $j, UpdatedAtUtc = $now WHERE Id = $id AND Kind = 1;",
                    ("$j", json), ("$now", Now()), ("$id", existing.Id)),
                ct).ConfigureAwait(false);
            _logger.LogInformation("Intelligentes Album neu gespeichert: Id {Id} ({Parts} Bedingungen)", existing.Id, filter.Describe().Count);
            Publish(new AlbumsChangedMessage(AlbumsChangeKind.FilterChanged, existing.Id));
            return await GetAsync(existing.Id, ct).ConfigureAwait(false) ?? throw new AlbumNotFoundException(existing.Id);
        }

        var id = await InsertAsync(normalized, AlbumKind.Smart, json, ct).ConfigureAwait(false);
        _logger.LogInformation("Intelligentes Album angelegt: Id {Id} ({Parts} Bedingungen)", id, filter.Describe().Count);
        Publish(new AlbumsChangedMessage(AlbumsChangeKind.Created, id));
        return await GetAsync(id, ct).ConfigureAwait(false) ?? throw new AlbumNotFoundException(id);
    }

    public async Task RenameAsync(int albumId, string newName, CancellationToken ct = default)
    {
        var normalized = NormalizeName(newName);
        var album = await GetAsync(albumId, ct).ConfigureAwait(false) ?? throw new AlbumNotFoundException(albumId);
        if (string.Equals(album.Name, normalized, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await WriteAsync(
                (connection, tx) => LibraryGroupStore.Execute(connection, tx,
                    "UPDATE Albums SET Name = $n, UpdatedAtUtc = $now WHERE Id = $id;", ("$n", normalized), ("$now", Now()), ("$id", albumId)),
                ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"Ein Album „{normalized}“ existiert bereits.", ex);
        }

        Publish(new AlbumsChangedMessage(AlbumsChangeKind.Renamed, albumId));
    }

    public async Task DeleteAsync(int albumId, CancellationToken ct = default)
    {
        _ = await GetAsync(albumId, ct).ConfigureAwait(false) ?? throw new AlbumNotFoundException(albumId);
        var removed = await WriteAsync((connection, tx) =>
        {
            var items = (int)LibraryGroupStore.Scalar(connection, tx, "SELECT COUNT(*) FROM AlbumItems WHERE AlbumId = $id;", ("$id", albumId));
            LibraryGroupStore.Execute(connection, tx, "DELETE FROM AlbumItems WHERE AlbumId = $id; DELETE FROM FileLinkUndo WHERE Kind = 0 AND TargetId = $id; DELETE FROM Albums WHERE Id = $id;", ("$id", albumId));
            return items;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Album gelöscht: Id {Id}, {Count} Verknüpfungen entfernt (Fotos bleiben)", albumId, removed);
        Publish(new AlbumsChangedMessage(AlbumsChangeKind.Deleted, albumId));
    }

    public async Task SetCoverAsync(int albumId, int? fileId, CancellationToken ct = default)
    {
        _ = await GetAsync(albumId, ct).ConfigureAwait(false) ?? throw new AlbumNotFoundException(albumId);
        await WriteAsync((connection, tx) =>
        {
            if (fileId is { } id)
            {
                // Nur eine Datei aus einer Bibliothek der Gruppe des Albums.
                return LibraryGroupStore.Execute(connection, tx, """
                    UPDATE Albums SET CoverFileId = $f, UpdatedAtUtc = $now
                    WHERE Id = $id AND EXISTS (SELECT 1 FROM Files f JOIN Libraries l ON l.Id = f.LibraryId WHERE f.Id = $f AND l.GroupId = Albums.GroupId);
                    """, ("$f", id), ("$now", Now()), ("$id", albumId));
            }

            return LibraryGroupStore.Execute(connection, tx, "UPDATE Albums SET CoverFileId = NULL, UpdatedAtUtc = $now WHERE Id = $id;", ("$now", Now()), ("$id", albumId));
        }, ct).ConfigureAwait(false);
        Publish(new AlbumsChangedMessage(AlbumsChangeKind.CoverChanged, albumId));
    }

    // ---- Fotos -----------------------------------------------------------------------------------------------------

    public Task<int> AddFilesAsync(int albumId, IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => WriteItemsAsync(albumId, fileIds, add: true, ct);

    public Task<int> RemoveFilesAsync(int albumId, IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => WriteItemsAsync(albumId, fileIds, add: false, ct);

    private async Task<int> WriteItemsAsync(int albumId, IReadOnlyList<int> fileIds, bool add, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var album = await GetAsync(albumId, ct).ConfigureAwait(false) ?? throw new AlbumNotFoundException(albumId);
        if (album.IsSmart)
        {
            throw new InvalidOperationException("Ein intelligentes Album füllt sich selbst über seinen Filter — Fotos lassen sich nicht einzeln hinzufügen.");
        }

        int changed;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Now();
            changed = await CatalogFileTargets.WriteAsync(_factory, ids, _logger, (connection, tx, files) =>
            {
                var count = 0;
                foreach (var chunk in files.Select(f => f.FileId).Chunk(ChunkSize))
                {
                    ct.ThrowIfCancellationRequested();
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.Parameters.AddWithValue("$a", albumId);
                    cmd.Parameters.AddWithValue("$now", now);
                    var inList = CatalogFileTargets.BindIds(cmd, chunk, "$f");
                    cmd.CommandText = add
                        // Nur Dateien aus Bibliotheken der Gruppe des Albums (ADR-0048) — nie ein Foto aus einer fremden Bibliothek.
                        ? $"""
                          INSERT OR IGNORE INTO AlbumItems (AlbumId, FileId, AddedAtUtc)
                          SELECT $a, f.Id, $now FROM Files f
                          WHERE f.Id IN ({inList})
                            AND EXISTS (SELECT 1 FROM Albums a JOIN Libraries l ON l.GroupId = a.GroupId WHERE a.Id = $a AND a.Kind = 0 AND l.Id = f.LibraryId);
                          """
                        : $"DELETE FROM AlbumItems WHERE AlbumId = $a AND FileId IN ({inList});";
                    count += cmd.ExecuteNonQuery();
                }

                if (count > 0)
                {
                    LibraryGroupStore.Execute(connection, tx, "UPDATE Albums SET UpdatedAtUtc = $now WHERE Id = $a;", ("$now", now), ("$a", albumId));
                }

                return count;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Album {Id}: {Count} Foto(s) {Action}", albumId, changed, add ? "hinzugefügt" : "entfernt");
        if (changed > 0)
        {
            Publish(new AlbumsChangedMessage(AlbumsChangeKind.ItemsChanged, albumId));
        }

        return changed;
    }

    // ---- Exportieren -----------------------------------------------------------------------------------------------

    public async Task<AlbumExportResult> ExportAsync(int albumId, string targetFolder, IProgress<AlbumExportProgress>? progress = null, CancellationToken ct = default)
    {
        if (_catalog?.IsHidden == true)
        {
            throw new InvalidOperationException("In einer versteckten Bibliothek lässt sich kein Album exportieren — die Kopien lägen außerhalb des Containers.");
        }

        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            throw new ArgumentException("Kein Zielordner gewählt.", nameof(targetFolder));
        }

        var target = Path.GetFullPath(targetFolder);
        // Lesen ohne Abbruch (kurz); Abbrechen wirkt zwischen und während der Kopien.
        var ids = await GetFileIdsAsync(albumId, CancellationToken.None).ConfigureAwait(false);
        var sources = await ReadAsync(connection => ReadPaths(connection, ids), CancellationToken.None).ConfigureAwait(false);
        Directory.CreateDirectory(target);

        var copied = 0;
        var renamed = 0;
        var missing = 0;
        var failed = 0;
        var errors = new List<string>();
        var canceled = false;
        var done = 0;
        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            var name = Path.GetFileName(source);
            progress?.Report(new AlbumExportProgress(done, sources.Count, name));
            if (!File.Exists(source))
            {
                missing++;
                done++;
                continue;
            }

            var dest = UniqueTarget(target, name, out var wasRenamed);
            try
            {
                await CopyFileAsync(source, dest, ct).ConfigureAwait(false);
                copied++;
                renamed += wasRenamed ? 1 : 0;
            }
            catch (OperationCanceledException)
            {
                TryDelete(dest);
                canceled = true;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(dest);
                failed++;
                errors.Add($"{name}: {ex.Message}");
                _logger.LogWarning(ex, "Album exportieren: Datei {Index} nicht kopiert", done + 1);
            }

            done++;
        }

        progress?.Report(new AlbumExportProgress(done, sources.Count, null));
        _logger.LogInformation(
            "Album {Id} exportiert: {Copied} kopiert ({Renamed} umbenannt), {Missing} fehlen, {Failed} Fehler, abgebrochen={Canceled}",
            albumId, copied, renamed, missing, failed, canceled);
        return new AlbumExportResult(copied, renamed, missing, failed, canceled, target, errors);
    }

    // ---- Rückkehr entfernter Dateien (ADR-0054) -------------------------------------------------------------------

    /// <summary>
    /// Neue Dateien im Katalog (Scan, Rückgängig): liegt eine davon wieder am Pfad einer Datei, deren Verknüpfungen beim
    /// Entfernen gesichert wurden (Windows-Papierkorb „Wiederherstellen“), bekommen sie ihre Verknüpfungen zurück.
    /// </summary>
    private void OnFilesChanged(FilesChangedMessage message)
    {
        if (message.Kind != FilesChangeKind.Added || message.FileIds.Count == 0)
        {
            return;
        }

        var ids = message.FileIds.ToArray();
        LastRestore = Task.Run(async () =>
        {
            try
            {
                await RestoreReturnedAsync(ids).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Verknüpfungen zurückgekehrter Dateien konnten nicht wiederhergestellt werden");
            }
        });
    }

    private async Task RestoreReturnedAsync(int[] fileIds)
    {
        using var connection = _factory.OpenSqliteConnection();
        var pending = FileLinkUndoStore.ReadPending(connection);
        if (pending.Count == 0)
        {
            return;
        }

        // Nur dieselbe Datei: gleicher Pfad UND gleiche Größe und Änderungszeit (Windows „Wiederherstellen“ behält beides) —
        // ein neuer Import mit demselben Namen erbt keine Verknüpfungen.
        var byPath = pending.GroupBy(p => NormalizePath(p.SourcePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var matches = new List<(int OperationId, int FileId)>();
        foreach (var (id, path, size, modified) in ReadIdentities(connection, fileIds))
        {
            if (byPath.TryGetValue(NormalizePath(path), out var operations))
            {
                matches.AddRange(operations.Where(p => p.Size == size && p.ModifiedUnix == modified).Select(p => (p.OperationId, id)));
            }
        }

        if (matches.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        int restored;
        try
        {
            using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, CancellationToken.None).ConfigureAwait(false);
            restored = matches.Sum(m => FileLinkUndoStore.Restore(connection, tx, m.OperationId, m.FileId));
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Verknüpfungen zurückgekehrter Dateien wiederhergestellt: {Files} Datei(en), {Links} Verknüpfung(en)", matches.Count, restored);
        if (restored > 0)
        {
            Publish(new AlbumsChangedMessage(AlbumsChangeKind.ItemsChanged, null));
        }
    }

    // ---- Hilfen ----------------------------------------------------------------------------------------------------

    /// <summary>Trimmt den Namen und prüft Länge/Leer; wirft <see cref="ArgumentException"/>.</summary>
    public static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Der Albumname darf nicht leer sein.", nameof(name));
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"Der Albumname darf höchstens {MaxNameLength} Zeichen lang sein.", nameof(name));
        }

        return trimmed;
    }

    private sealed record AlbumRow(int Id, string Name, AlbumKind Kind, string? FilterJson, int? CoverFileId, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

    private async Task<int> InsertAsync(string name, AlbumKind kind, string? filterJson, CancellationToken ct)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        int id;
        try
        {
            id = await WriteAsync((connection, tx) =>
            {
                var order = LibraryGroupStore.Scalar(connection, tx, "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Albums WHERE GroupId = $g;", ("$g", scope.GroupId));
                using var cmd = LibraryGroupStore.Command(connection, tx, """
                    INSERT INTO Albums (GroupId, Name, Kind, FilterJson, CoverFileId, SortOrder, CreatedAtUtc, UpdatedAtUtc)
                    VALUES ($g, $n, $k, $j, NULL, $o, $now, $now);
                    SELECT last_insert_rowid();
                    """,
                    ("$g", scope.GroupId), ("$n", name), ("$k", (int)kind), ("$j", (object?)filterJson ?? DBNull.Value), ("$o", order), ("$now", Now()));
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"Ein Album „{name}“ existiert bereits.", ex);
        }

        if (!await LibraryGroupStore.RehomeIfGroupGoneAsync(_factory, "Albums", id, scope.LibraryId, ct).ConfigureAwait(false))
        {
            await WriteAsync((connection, tx) => LibraryGroupStore.Execute(connection, tx, "DELETE FROM Albums WHERE Id = $id;", ("$id", id)), ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Ein Album „{name}“ existiert bereits.");
        }

        return id;
    }

    private static List<AlbumRow> ReadAlbums(SqliteConnection connection, int groupId, int? albumId, string? name = null)
    {
        using var cmd = connection.CreateCommand();
        var where = new StringBuilder("GroupId = $g");
        cmd.Parameters.AddWithValue("$g", groupId);
        if (albumId is { } id)
        {
            where.Append(" AND Id = $id");
            cmd.Parameters.AddWithValue("$id", id);
        }

        if (name is not null)
        {
            where.Append(" AND Name = $n");
            cmd.Parameters.AddWithValue("$n", name);
        }

        cmd.CommandText = $"SELECT Id, Name, Kind, FilterJson, CoverFileId, CreatedAtUtc, UpdatedAtUtc FROM Albums WHERE {where} ORDER BY SortOrder, Name COLLATE NOCASE, Id;";
        var list = new List<AlbumRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AlbumRow(
                reader.GetInt32(0),
                reader.GetString(1),
                (AlbumKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                FromUnix(reader.GetInt64(5)),
                FromUnix(reader.GetInt64(6))));
        }

        return list;
    }

    private async Task<AlbumInfo> ToInfoAsync(AlbumRow row, LibraryScopeInfo scope, CancellationToken ct, bool evaluateSmart = true)
    {
        var filter = row.Kind == AlbumKind.Smart ? SmartAlbumFilter.FromJson(row.FilterJson) : null;
        int? count = null;
        int? firstFileId = null;
        IReadOnlyList<int> candidates = [];
        if (!scope.HasLibrary)
        {
            count = 0;
        }
        else if (row.Kind == AlbumKind.Manual)
        {
            (count, firstFileId) = await ReadAsync(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT COUNT(*) FROM AlbumItems ai CROSS JOIN Files f ON f.Id = ai.FileId
                    WHERE ai.AlbumId = $a AND f.LibraryId IN ({scope.SqlLibraryIdList});
                    SELECT f.Id FROM AlbumItems ai CROSS JOIN Files f ON f.Id = ai.FileId
                    WHERE ai.AlbumId = $a AND f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.MediaType <> 2
                    ORDER BY {CaptureDateSortSql.SortKey} DESC, f.Id DESC LIMIT {MaxCoverCandidates};
                    """;
                cmd.Parameters.AddWithValue("$a", row.Id);
                using var reader = cmd.ExecuteReader();
                var n = reader.Read() ? reader.GetInt32(0) : 0;
                var ids = new List<int>();
                if (reader.NextResult())
                {
                    while (reader.Read())
                    {
                        ids.Add(reader.GetInt32(0));
                    }
                }

                candidates = ids;
                return ((int?)n, ids.Count > 0 ? ids[0] : (int?)null);
            }, ct).ConfigureAwait(false);

            // ADR-0068: ohne eigenes Titelbild die höchste Foto-Güte (bevorzugt mit Personen), sonst das neueste Foto
            if (row.CoverFileId is null && _highlights is not null && candidates.Count > 1)
            {
                try
                {
                    firstFileId = await _highlights.PickCoverAsync(candidates, ct).ConfigureAwait(false) ?? firstFileId;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Album {Id}: automatisches Titelbild nicht wählbar — neuestes Foto", row.Id);
                }
            }
        }
        else if (filter is not null && _evaluator is not null && evaluateSmart)
        {
            try
            {
                var summary = await _evaluator.SummarizeAsync(filter, scope, ct).ConfigureAwait(false);
                count = summary.Count;
                firstFileId = summary.FirstFileId;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Intelligentes Album {Id} konnte nicht ausgewertet werden", row.Id);
            }
        }

        var coverId = row.CoverFileId ?? firstFileId;
        var cover = coverId is { } c
            ? await ReadAsync(connection => ReadCover(connection, c, scope), ct).ConfigureAwait(false)
              ?? (firstFileId is { } f && f != c ? await ReadAsync(connection => ReadCover(connection, f, scope), ct).ConfigureAwait(false) : null)
            : null;
        return new AlbumInfo(row.Id, row.Name, row.Kind, count, cover, filter, row.CreatedAtUtc, row.UpdatedAtUtc);
    }

    /// <summary>Titelbild nur, wenn die Datei im Bereich liegt (ein Titelbild aus einer anderen Bibliothek wäre ein Leck).</summary>
    private static AlbumCover? ReadCover(SqliteConnection connection, int fileId, LibraryScopeInfo scope)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.Id, f.LibraryId, d.Path, f.Name, f.MediaType, f.ModifiedAtUtc
            FROM Files f JOIN Folders d ON d.Id = f.FolderId
            WHERE f.Id = $id AND f.LibraryId IN ({scope.SqlLibraryIdList});
            """;
        cmd.Parameters.AddWithValue("$id", fileId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new AlbumCover(reader.GetInt32(0), reader.GetInt32(1), Path.Combine(reader.GetString(2), reader.GetString(3)),
                (MediaType)reader.GetInt32(4), FromUnix(reader.GetInt64(5)))
            : null;
    }

    /// <summary>Volle Pfade der Dateien in der Reihenfolge von <paramref name="ids"/>.</summary>
    private static List<string> ReadPaths(SqliteConnection connection, IReadOnlyList<int> ids)
    {
        var byId = ReadPathsWithIds(connection, ids).ToDictionary(x => x.Id, x => x.Path);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private static List<(int Id, string Path)> ReadPathsWithIds(SqliteConnection connection, IReadOnlyCollection<int> ids)
    {
        var list = new List<(int, string)>(ids.Count);
        foreach (var chunk in ids.Distinct().Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT f.Id, d.Path, f.Name FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({CatalogFileTargets.BindIds(cmd, chunk, "$f")});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add((reader.GetInt32(0), Path.Combine(reader.GetString(1), reader.GetString(2))));
            }
        }

        return list;
    }

    private static List<(int Id, string Path, long Size, long ModifiedUnix)> ReadIdentities(SqliteConnection connection, IReadOnlyCollection<int> ids)
    {
        var list = new List<(int, string, long, long)>(ids.Count);
        foreach (var chunk in ids.Distinct().Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT f.Id, d.Path, f.Name, f.Size, f.ModifiedAtUtc FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({CatalogFileTargets.BindIds(cmd, chunk, "$f")});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add((reader.GetInt32(0), Path.Combine(reader.GetString(1), reader.GetString(2)), reader.GetInt64(3), reader.GetInt64(4)));
            }
        }

        return list;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string UniqueTarget(string folder, string name, out bool renamed)
    {
        var dest = Path.Combine(folder, name);
        renamed = false;
        if (!File.Exists(dest))
        {
            return dest;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            dest = Path.Combine(folder, $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){ext}");
            if (!File.Exists(dest))
            {
                renamed = true;
                return dest;
            }
        }
    }

    private static async Task CopyFileAsync(string source, string dest, CancellationToken ct)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
        {
            await input.CopyToAsync(output, CopyBufferSize, ct).ConfigureAwait(false);
        }

        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // halb kopierte Datei bleibt liegen — der Benutzer sieht sie im Zielordner
        }
    }

    private Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken ct)
        => Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            return read(connection);
        }, ct);

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                var result = write(connection, tx);
                tx.Commit();
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    private void Publish<TMessage>(TMessage message)
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

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        _writeLock.Dispose();
    }
}
