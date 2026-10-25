using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Services.Albums;
using Lumora.Services.Database;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Library;
using Lumora.Services.Quality;
using Lumora.Services.Stories;

namespace Lumora.Services.Covers;

/// <summary>
/// Umsetzung von <see cref="ICoverService"/> (ADR-0068). Lesen über Raw-SQL ohne Transaktion (WAL); Personen, Alben und Storys
/// setzt/löst ihr eigener Dienst (deren Nachrichten aktualisieren die Seiten), das Ordner-Titelbild schreibt dieser Dienst
/// selbst (eigener Single-Writer-Lock, Warten auf die Katalog-Schreibsperre über <see cref="SqliteBusyRetry"/>). Ein
/// Ordner-Titelbild zählt nur, solange die Datei noch im Ordner liegt und nicht fehlt — gelöscht, verschoben oder „Neu aufbauen“
/// ohne Gegenstück heißt automatisch wieder „automatisch“ (kein Fremdschlüssel nötig).
/// </summary>
public sealed class CoverService : ICoverService, IDisposable
{
    /// <summary>Höchstens so viele Dateien eines Ordners gehen in die automatische Wahl (neueste zuerst).</summary>
    public const int MaxFolderCandidates = 400;

    private readonly ILibraryContextFactory _factory;
    private readonly ILibraryScope _scope;
    private readonly IHighlightSelector _selector;
    private readonly IFaceRecognitionService? _faces;
    private readonly IAlbumService? _albums;
    private readonly IStoryService? _stories;
    private readonly IMessenger _messenger;
    private readonly ILogger<CoverService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CoverService(
        ILibraryContextFactory factory,
        IHighlightSelector selector,
        ILogger<CoverService> logger,
        ILibraryScope? scope = null,
        IFaceRecognitionService? faces = null,
        IAlbumService? albums = null,
        IStoryService? stories = null,
        IMessenger? messenger = null)
    {
        _factory = factory;
        _selector = selector;
        _logger = logger;
        _scope = scope ?? new LibraryScope(factory);
        _faces = faces;
        _albums = albums;
        _stories = stories;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public async Task<IReadOnlyList<CoverTarget>> GetTargetsAsync(int fileId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (!scope.HasLibrary)
        {
            return [];
        }

        var rows = await Task.Run(() => ReadTargets(fileId, scope), ct).ConfigureAwait(false);
        if (rows is null)
        {
            return [];
        }

        var result = new List<CoverTarget>();
        foreach (var person in rows.Persons)
        {
            var current = person.ManualFaceId ?? person.AutoFaceId;
            result.Add(new CoverTarget(CoverTargetKind.Person, person.PersonId, person.Name, current == person.FaceId, person.ManualFaceId is not null, person.FaceId));
        }

        foreach (var album in rows.Albums)
        {
            int? current = album.ManualFileId;
            if (current is null && _albums is not null)
            {
                current = (await _albums.GetAsync(album.Id, ct).ConfigureAwait(false))?.Cover?.FileId;
            }

            result.Add(new CoverTarget(CoverTargetKind.Album, album.Id, album.Name, current == fileId, album.ManualFileId is not null));
        }

        if (_stories is not null)
        {
            foreach (var storyId in rows.StoryIds)
            {
                if (await _stories.GetAsync(storyId, ct).ConfigureAwait(false) is not { } card)
                {
                    continue;
                }

                result.Add(new CoverTarget(CoverTargetKind.Story, card.Id, card.Title, card.CoverFileId == fileId, card.CoverIsManual));
            }
        }

        if (rows.Folder is { } folder)
        {
            var info = await GetFolderCoverAsync(folder.Id, ct).ConfigureAwait(false);
            result.Add(new CoverTarget(CoverTargetKind.Folder, folder.Id, folder.Name, info.FileId == fileId, info.IsManual));
        }

        return result;
    }

    public async Task SetAsync(CoverTarget target, int fileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        switch (target.Kind)
        {
            case CoverTargetKind.Person:
                var faces = _faces ?? throw new InvalidOperationException("Personen sind nicht verfügbar.");
                var faceId = target.FaceId ?? throw new InvalidOperationException("Kein Gesicht der Person in diesem Foto.");
                await faces.SetCoverAsync(target.Id, faceId, ct).ConfigureAwait(false);
                break;
            case CoverTargetKind.Album:
                await (_albums ?? throw new InvalidOperationException("Alben sind nicht verfügbar.")).SetCoverAsync(target.Id, fileId, ct).ConfigureAwait(false);
                break;
            case CoverTargetKind.Story:
                await (_stories ?? throw new InvalidOperationException("Storys sind nicht verfügbar.")).SetCoverAsync(target.Id, fileId, ct).ConfigureAwait(false);
                break;
            case CoverTargetKind.Folder:
                await SetFolderCoverAsync(target.Id, fileId, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        _logger.LogInformation("Titelbild gesetzt: {Kind} {Id}", target.Kind, target.Id);
        _messenger.Send(new CoverChangedMessage(target.Kind, target.Id));
        Usage.UsageReport.Used([fileId], _messenger); // selbst gewähltes Titelbild = Verwendung (Nutzungs-Signale, ADR-0072)
    }

    public async Task ResetAsync(CoverTargetKind kind, int id, CancellationToken ct = default)
    {
        switch (kind)
        {
            case CoverTargetKind.Person:
                await (_faces ?? throw new InvalidOperationException("Personen sind nicht verfügbar.")).ClearCoverAsync(id, ct).ConfigureAwait(false);
                break;
            case CoverTargetKind.Album:
                await (_albums ?? throw new InvalidOperationException("Alben sind nicht verfügbar.")).SetCoverAsync(id, null, ct).ConfigureAwait(false);
                break;
            case CoverTargetKind.Story:
                await (_stories ?? throw new InvalidOperationException("Storys sind nicht verfügbar.")).SetCoverAsync(id, null, ct).ConfigureAwait(false);
                break;
            case CoverTargetKind.Folder:
                await WriteFolderCoverAsync(id, null, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        _logger.LogInformation("Titelbild wieder automatisch: {Kind} {Id}", kind, id);
        _messenger.Send(new CoverChangedMessage(kind, id));
    }

    public async Task<FolderCoverInfo> GetFolderCoverAsync(int folderId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var (manual, candidates) = await Task.Run(() => ReadFolderCandidates(folderId, scope), ct).ConfigureAwait(false);
        if (manual is not null)
        {
            return new FolderCoverInfo(folderId, manual, true);
        }

        var auto = candidates.Count == 0 ? null : await _selector.PickCoverAsync(candidates, ct).ConfigureAwait(false);
        return new FolderCoverInfo(folderId, auto, false);
    }

    public void Dispose() => _writeLock.Dispose();

    // ---- Ordner ------------------------------------------------------------------------------------------------------

    private async Task SetFolderCoverAsync(int folderId, int fileId, CancellationToken ct)
    {
        var updated = await WriteFolderCoverAsync(folderId, fileId, ct).ConfigureAwait(false);
        if (updated == 0)
        {
            throw new InvalidOperationException("Das Titelbild muss ein Foto dieses Ordners sein.");
        }
    }

    private async Task<int> WriteFolderCoverAsync(int folderId, int? fileId, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = fileId is null
                ? "UPDATE Folders SET CoverFileId = NULL WHERE Id = $d;"
                : "UPDATE Folders SET CoverFileId = $f WHERE Id = $d AND EXISTS (SELECT 1 FROM Files f WHERE f.Id = $f AND f.FolderId = $d);";
            cmd.Parameters.AddWithValue("$d", folderId);
            cmd.Parameters.AddWithValue("$f", (object?)fileId ?? DBNull.Value);
            var n = cmd.ExecuteNonQuery();
            tx.Commit();
            return n;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Gültiges selbst gewähltes Titelbild bzw. Kandidaten (direkt im Ordner, sonst darunter; neueste zuerst).</summary>
    private (int? Manual, List<int> Candidates) ReadFolderCandidates(int folderId, LibraryScopeInfo scope)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT d.CoverFileId FROM Folders d
            WHERE d.Id = $d AND d.LibraryId IN ({scope.SqlLibraryIdList})
              AND EXISTS (SELECT 1 FROM Files f WHERE f.Id = d.CoverFileId AND f.FolderId = d.Id AND f.Missing = 0);
            """;
        cmd.Parameters.AddWithValue("$d", folderId);
        if (cmd.ExecuteScalar() is long manual)
        {
            return ((int)manual, []);
        }

        var candidates = ReadIds(connection, $"""
            SELECT f.Id FROM Files f WHERE f.FolderId = $d AND f.Missing = 0 AND f.MediaType <> 2
              AND f.LibraryId IN ({scope.SqlLibraryIdList})
            ORDER BY f.Id DESC LIMIT {MaxFolderCandidates};
            """, folderId);
        if (candidates.Count == 0)
        {
            // Nur Unterordner: die Fotos darunter (Pfad-Präfix, wie „mit Unterordnern“; rtrim für Laufwerkswurzeln wie „E:\“)
            candidates = ReadIds(connection, $"""
                SELECT f.Id FROM Folders d JOIN Folders s ON s.LibraryId = d.LibraryId AND s.Id <> d.Id AND substr(s.Path, 1, length(rtrim(d.Path, char(92))) + 1) = rtrim(d.Path, char(92)) || char(92)
                CROSS JOIN Files f ON f.FolderId = s.Id
                WHERE d.Id = $d AND f.Missing = 0 AND f.MediaType <> 2 AND f.LibraryId IN ({scope.SqlLibraryIdList})
                ORDER BY f.Id DESC LIMIT {MaxFolderCandidates};
                """, folderId);
        }

        return (null, candidates);
    }

    private static List<int> ReadIds(SqliteConnection connection, string sql, int folderId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$d", folderId);
        var list = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }

        return list;
    }

    // ---- Ziele eines Fotos ---------------------------------------------------------------------------------------------

    private sealed record PersonRow(int PersonId, string Name, long FaceId, long? ManualFaceId, long? AutoFaceId);

    private sealed record AlbumRow(int Id, string Name, int? ManualFileId);

    private sealed record FolderRow(int Id, string Name);

    private sealed record TargetRows(List<PersonRow> Persons, List<AlbumRow> Albums, List<int> StoryIds, FolderRow? Folder);

    private TargetRows? ReadTargets(int fileId, LibraryScopeInfo scope)
    {
        using var connection = _factory.OpenSqliteConnection();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM Files WHERE Id = $f AND LibraryId IN ({scope.SqlLibraryIdList});";
            check.Parameters.AddWithValue("$f", fileId);
            if (Convert.ToInt64(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                return null; // nicht im Bereich (ADR-0048)
            }
        }

        FaceCoverScoring.Register(connection);
        var libs = scope.SqlLibraryIdList;
        var persons = new List<PersonRow>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT p.Id, p.Name, x.Id,
                    (SELECT c.Id FROM Faces c JOIN Files cf ON cf.Id = c.FileId
                        WHERE c.Id = p.CoverFaceId AND c.PersonId = p.Id AND c.State = 2 AND cf.LibraryId IN ({libs})),
                    (SELECT c.Id FROM Faces c JOIN Files cf ON cf.Id = c.FileId LEFT JOIN FileQuality q ON q.FileId = c.FileId
                        WHERE c.PersonId = p.Id AND c.State = 2 AND cf.LibraryId IN ({libs})
                        ORDER BY {FaceCoverScoring.SqlScore("c", "cf", "q")} DESC, c.Id LIMIT 1)
                FROM Faces x JOIN Persons p ON p.Id = x.PersonId
                WHERE x.FileId = $f AND x.State = 2 AND p.GroupId = $g
                ORDER BY p.Name COLLATE NOCASE, p.Id, x.Id;
                """;
            cmd.Parameters.AddWithValue("$f", fileId);
            cmd.Parameters.AddWithValue("$g", scope.GroupId);
            using var reader = cmd.ExecuteReader();
            var seen = new HashSet<int>();
            while (reader.Read())
            {
                var personId = reader.GetInt32(0);
                if (seen.Add(personId)) // je Person ein Eintrag (erstes Gesicht im Foto)
                {
                    persons.Add(new PersonRow(personId, reader.GetString(1), reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)));
                }
            }
        }

        var albums = new List<AlbumRow>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT a.Id, a.Name,
                       CASE WHEN EXISTS (SELECT 1 FROM Files cf WHERE cf.Id = a.CoverFileId AND cf.LibraryId IN ({libs})) THEN a.CoverFileId END
                FROM AlbumItems ai JOIN Albums a ON a.Id = ai.AlbumId
                WHERE ai.FileId = $f AND a.GroupId = $g
                ORDER BY a.Name COLLATE NOCASE, a.Id;
                """;
            cmd.Parameters.AddWithValue("$f", fileId);
            cmd.Parameters.AddWithValue("$g", scope.GroupId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                albums.Add(new AlbumRow(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
            }
        }

        var stories = new List<int>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT s.Id FROM StoryItems si JOIN Stories s ON s.Id = si.StoryId
                WHERE si.FileId = $f AND si.Origin >= 0 AND s.GroupId = $g AND s.IsHidden = 0
                ORDER BY s.StartAt DESC, s.Id;
                """;
            cmd.Parameters.AddWithValue("$f", fileId);
            cmd.Parameters.AddWithValue("$g", scope.GroupId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                stories.Add(reader.GetInt32(0));
            }
        }

        FolderRow? folder = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT d.Id, d.Name FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id = $f;";
            cmd.Parameters.AddWithValue("$f", fileId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                folder = new FolderRow(reader.GetInt32(0), reader.GetString(1));
            }
        }

        return new TargetRows(persons, albums, stories, folder);
    }
}
