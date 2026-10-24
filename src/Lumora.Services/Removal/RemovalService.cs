using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;

namespace Lumora.Services.Removal;

/// <summary>
/// „Ordner in den Papierkorb“ und „Fehlende aus der Bibliothek entfernen“ (ADR-0065) für die drei Oberflächen (Reiter
/// „Ordner“, Galerie, Einstellungen › Speicher &amp; Wartung): Vorschau mit Zahlen für die Bestätigung, Ausführung über die
/// protokollierten Datei-Operationen (<see cref="IFileOperationService.RecycleFolderAsync"/>,
/// <see cref="IFileOperationService.RemoveMissingAsync"/>), Rückgängig über <see cref="IFileOperationService.UndoBatchAsync"/>.
/// Arbeitet nur mit den übergebenen Bibliotheken bzw. Dateien — den Bereich (ADR-0048) und die Sperre in „Alle verknüpften“
/// legt die Oberfläche fest; in einer versteckten Bibliothek ist der Katalog der Sitzung ohnehin der eigene (ADR-0049).
/// </summary>
public interface IRemovalService
{
    /// <summary>Vorschau „Ordner in den Papierkorb…“: Zahlen für die Bestätigung, Sperrgrund, Papierkorb vorhanden?</summary>
    Task<FolderRecyclePreview?> PreviewFolderRecycleAsync(int folderId, CancellationToken ct = default);

    /// <summary>Ordner in den Papierkorb (bzw. mit Zustimmung endgültig löschen, wenn das Laufwerk keinen hat).</summary>
    Task<FolderRecycleResult> RecycleFolderAsync(int folderId, bool allowPermanentDelete, CancellationToken ct = default);

    /// <summary>Vorschau „Fehlende entfernen“: entfernbare Dateien (Wurzel erreichbar), offline, nicht fehlend, Benutzerdaten.</summary>
    Task<MissingSummary> SummarizeMissingAsync(MissingTarget target, CancellationToken ct = default);

    /// <summary>Fehlende entfernen (prüft erneut: Guard, „wieder da“).</summary>
    Task<MissingRemovalBatchResult> RemoveMissingAsync(MissingTarget target, CancellationToken ct = default);

    /// <summary>Fehlende je Ordner (direkt) in den Bibliotheken — für die Zähler im Ordnerbaum.</summary>
    Task<IReadOnlyDictionary<int, int>> CountMissingByFolderAsync(IReadOnlyList<int> libraryIds, CancellationToken ct = default);

    /// <summary>Rückgängig eines RecycleFolder- oder RemoveFromCatalog-Batches.</summary>
    Task<FileOperationBatchResult?> UndoAsync(int batchId, CancellationToken ct = default);
}

/// <inheritdoc cref="IRemovalService"/>
public sealed class RemovalService : IRemovalService
{
    private const int Chunk = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IFileOperationService _fileOps;
    private readonly IRecycleBin _recycleBin;
    private readonly ILogger<RemovalService> _logger;

    public RemovalService(ILibraryContextFactory factory, IFileOperationService fileOps, IRecycleBin recycleBin, ILogger<RemovalService> logger)
    {
        _factory = factory;
        _fileOps = fileOps;
        _recycleBin = recycleBin;
        _logger = logger;
    }

    public async Task<FolderRecyclePreview?> PreviewFolderRecycleAsync(int folderId, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var folder = await ctx.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId, ct).ConfigureAwait(false);
        if (folder is null)
        {
            return null;
        }

        var path = FolderRemovalRules.Normalize(folder.Path);
        var blocked = await FolderRemovalRules.CheckAsync(ctx, folder.LibraryId, path, ct).ConfigureAwait(false);
        var fileIds = await FileIdsUnderAsync(ctx, folder.LibraryId, path, missingOnly: false, ct).ConfigureAwait(false);
        var userData = await CountUserDataAsync(fileIds, ct).ConfigureAwait(false);
        var (files, folders) = blocked is null ? await Task.Run(() => CountOnDisk(path), ct).ConfigureAwait(false) : (0, 0);
        var noBin = blocked is null ? _recycleBin.GetUnavailableReasonFor(path) : null;
        return new FolderRecyclePreview(folder.Id, folder.LibraryId, folder.Name, path, blocked, fileIds.Count, files, folders, userData, noBin);
    }

    public Task<FolderRecycleResult> RecycleFolderAsync(int folderId, bool allowPermanentDelete, CancellationToken ct = default)
        => _fileOps.RecycleFolderAsync(folderId, allowPermanentDelete, ct);

    public async Task<MissingSummary> SummarizeMissingAsync(MissingTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var ctx = _factory.Create();
        List<(int Id, bool Missing, int FolderId, int LibraryId)> rows;
        switch (target)
        {
            case MissingTarget.InFolder inFolder:
            {
                var folder = await ctx.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == inFolder.FolderId, ct).ConfigureAwait(false);
                if (folder is null)
                {
                    return MissingSummary.Empty;
                }

                var ids = await FileIdsUnderAsync(ctx, folder.LibraryId, FolderRemovalRules.Normalize(folder.Path), missingOnly: true, ct).ConfigureAwait(false);
                rows = await LoadRowsAsync(ctx, ids, ct).ConfigureAwait(false);
                break;
            }

            case MissingTarget.Selection selection:
                rows = await LoadRowsAsync(ctx, selection.FileIds.Distinct().ToList(), ct).ConfigureAwait(false);
                break;

            case MissingTarget.InLibraries inLibraries:
            {
                var libraryIds = inLibraries.LibraryIds.ToList();
                rows = (await ctx.Files.AsNoTracking()
                        .Where(f => f.Missing && libraryIds.Contains(f.LibraryId))
                        .Select(f => new { f.Id, f.FolderId, f.LibraryId })
                        .ToListAsync(ct)
                        .ConfigureAwait(false))
                    .Select(r => (r.Id, true, r.FolderId, r.LibraryId))
                    .ToList();
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        var present = rows.Count(r => !r.Missing);
        var missing = rows.Where(r => r.Missing).ToList();
        if (missing.Count == 0)
        {
            return new MissingSummary([], present, 0, [], UserDataCounts.None);
        }

        // Offline-vs-Missing-Guard: je Wurzel einmal prüfen; fehlende unter einer nicht erreichbaren Wurzel bleiben außen vor
        var folderIds = missing.Select(r => r.FolderId).Distinct().ToList();
        var folderPaths = new Dictionary<int, string>();
        foreach (var chunk in folderIds.Chunk(Chunk))
        {
            foreach (var f in await ctx.Folders.AsNoTracking().Where(f => chunk.Contains(f.Id)).Select(f => new { f.Id, f.Path }).ToListAsync(ct).ConfigureAwait(false))
            {
                folderPaths[f.Id] = FolderRemovalRules.Normalize(f.Path);
            }
        }

        var libraries = missing.Select(r => r.LibraryId).Distinct().ToList();
        var roots = await ctx.LibraryRoots.AsNoTracking().Where(r => libraries.Contains(r.LibraryId)).ToListAsync(ct).ConfigureAwait(false);
        var availability = new Dictionary<int, bool>();
        var offlineRoots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var removable = new List<int>();
        var offline = 0;
        foreach (var row in missing)
        {
            var root = folderPaths.TryGetValue(row.FolderId, out var folderPath)
                ? roots.Where(r => r.LibraryId == row.LibraryId && FolderRemovalRules.IsInside(folderPath, FolderRemovalRules.Normalize(r.Path)))
                    .OrderByDescending(r => r.Path.Length)
                    .FirstOrDefault()
                : null;
            if (root is null)
            {
                offline++;
                continue;
            }

            if (!availability.TryGetValue(root.Id, out var available))
            {
                available = await RootAvailabilityProbe.IsAvailableAsync(root.Path, ct).ConfigureAwait(false);
                availability[root.Id] = available;
                if (!available)
                {
                    offlineRoots.Add(root.Path);
                }
            }

            if (available)
            {
                removable.Add(row.Id);
            }
            else
            {
                offline++;
            }
        }

        var userData = await CountUserDataAsync(removable, ct).ConfigureAwait(false);
        return new MissingSummary(removable, present, offline, offlineRoots.ToList(), userData);
    }

    public async Task<MissingRemovalBatchResult> RemoveMissingAsync(MissingTarget target, CancellationToken ct = default)
    {
        var summary = await SummarizeMissingAsync(target, ct).ConfigureAwait(false);
        if (summary.Count == 0)
        {
            _logger.LogInformation("Fehlende entfernen: nichts entfernbar ({Offline} offline, {Present} nicht fehlend)", summary.Offline, summary.Present);
            return new MissingRemovalBatchResult(0, 0, 0, summary.Offline, summary.Present, 0, 0, summary.OfflineRoots);
        }

        var prune = target is MissingTarget.InFolder f ? f.FolderId : (int?)null;
        var result = await _fileOps.RemoveMissingAsync(summary.FileIds, prune, ct).ConfigureAwait(false);
        return result with
        {
            SkippedOffline = result.SkippedOffline + summary.Offline,
            SkippedNotMissing = result.SkippedNotMissing + summary.Present,
            OfflineRoots = result.OfflineRoots.Concat(summary.OfflineRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    public async Task<IReadOnlyDictionary<int, int>> CountMissingByFolderAsync(IReadOnlyList<int> libraryIds, CancellationToken ct = default)
    {
        var ids = libraryIds.ToList();
        await using var ctx = _factory.Create();
        return await ctx.Files.AsNoTracking()
            .Where(f => f.Missing && ids.Contains(f.LibraryId))
            .GroupBy(f => f.FolderId)
            .Select(g => new { FolderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FolderId, x => x.Count, ct)
            .ConfigureAwait(false);
    }

    public Task<FileOperationBatchResult?> UndoAsync(int batchId, CancellationToken ct = default)
        => _fileOps.UndoBatchAsync(batchId, ct);

    // ------------------------------------------------------------------ Helfer

    /// <summary>FileIds im Ordner samt Unterordnern (gleiche Bibliothek, Pfad-Präfix), optional nur fehlende.</summary>
    private static async Task<List<int>> FileIdsUnderAsync(Data.LibraryContext ctx, int libraryId, string path, bool missingOnly, CancellationToken ct)
    {
        var lower = AsciiLower(path);
        var lowerPrefix = AsciiLower(path + Path.DirectorySeparatorChar);
        var folderIds = await ctx.Folders.AsNoTracking()
            .Where(f => f.LibraryId == libraryId && (f.Path.ToLower() == lower || f.Path.ToLower().StartsWith(lowerPrefix)))
            .Select(f => f.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var result = new List<int>();
        foreach (var chunk in folderIds.Chunk(Chunk))
        {
            result.AddRange(await ctx.Files.AsNoTracking()
                .Where(f => chunk.Contains(f.FolderId) && (!missingOnly || f.Missing))
                .Select(f => f.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        return result;
    }

    private static async Task<List<(int Id, bool Missing, int FolderId, int LibraryId)>> LoadRowsAsync(Data.LibraryContext ctx, IReadOnlyList<int> ids, CancellationToken ct)
    {
        var rows = new List<(int, bool, int, int)>(ids.Count);
        foreach (var chunk in ids.Chunk(Chunk))
        {
            rows.AddRange((await ctx.Files.AsNoTracking()
                    .Where(f => chunk.Contains(f.Id))
                    .Select(f => new { f.Id, f.Missing, f.FolderId, f.LibraryId })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .Select(r => (r.Id, r.Missing, r.FolderId, r.LibraryId)));
        }

        return rows;
    }

    /// <summary>Benutzerdaten an den Dateien (je Merkmal Anzahl Dateien).</summary>
    private Task<UserDataCounts> CountUserDataAsync(IReadOnlyList<int> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return Task.FromResult(UserDataCounts.None);
        }

        return Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                var total = UserDataCounts.None;
                foreach (var chunk in fileIds.Chunk(Chunk))
                {
                    ct.ThrowIfCancellationRequested();
                    var list = string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)));
                    total = total.Add(new UserDataCounts(
                        chunk.Length,
                        Count(connection, $"SELECT COUNT(*) FROM Files WHERE Id IN ({list}) AND (IsFavorite = 1 OR Rating > 0);"),
                        Count(connection, $"SELECT COUNT(DISTINCT f.FileId) FROM Faces f JOIN Persons p ON p.Id = f.PersonId WHERE f.FileId IN ({list});"),
                        Count(connection, $"SELECT COUNT(DISTINCT FileId) FROM AlbumItems WHERE FileId IN ({list});"),
                        Count(connection, $"SELECT COUNT(*) FROM (SELECT FileId FROM FileTags WHERE FileId IN ({list}) UNION SELECT FileId FROM FileCategoryValues WHERE FileId IN ({list}));")));
                }

                return total;
            },
            ct);
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Dateien und Unterordner auf der Platte (unlesbare Teile übersprungen).</summary>
    private static (int Files, int Folders) CountOnDisk(string path)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            return (Directory.EnumerateFiles(path, "*", options).Count(), Directory.EnumerateDirectories(path, "*", options).Count());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static string AsciiLower(string value)
        => string.Create(value.Length, value, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
}
