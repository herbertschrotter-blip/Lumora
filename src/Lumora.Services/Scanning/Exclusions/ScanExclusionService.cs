using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Operations;
using Lumora.Services.Removal;

namespace Lumora.Services.Scanning.Exclusions;

/// <summary>Wofür eine Vorschau „bereits katalogisiert“ gilt (ADR-0066).</summary>
public abstract record ExclusionScope
{
    private ExclusionScope()
    {
    }

    /// <summary>Ein Ordner einer Bibliothek samt Unterordnern (vor „Ordner ausschließen…“).</summary>
    public sealed record Folder(int LibraryId, string FolderPath) : ExclusionScope;

    /// <summary>Ordner mit diesem Namen in jeder Tiefe (vor dem Hinzufügen zur allgemeinen Liste); null = alle Bibliotheken.</summary>
    public sealed record Name(string FolderName, IReadOnlyList<int>? LibraryIds = null) : ExclusionScope;

    /// <summary>Was die gespeicherten Ausschlüsse der Bibliotheken schon ausschließen (Einstellungen: „bereits katalogisiert“).</summary>
    public sealed record Current(IReadOnlyList<int> LibraryIds) : ExclusionScope;
}

/// <summary>Katalogisierte Dateien in (künftig) ausgeschlossenen Ordnern samt Benutzerdaten — für die Rückfrage.</summary>
public sealed record ExcludedFilesSummary(IReadOnlyList<int> FileIds, UserDataCounts UserData)
{
    public static ExcludedFilesSummary Empty { get; } = new([], UserDataCounts.None);

    public int Count => FileIds.Count;
}

/// <summary>
/// Ausschlüsse beim Einlesen (ADR-0066) für die Oberfläche: Regeln speichern (<see cref="Store"/>), Vorschau „schon katalogisiert“
/// mit Zahlen wie bei „Fehlende entfernen“, Entfernen aus dem Katalog über die protokollierten Datei-Operationen
/// (<see cref="IFileOperationService.RemoveExcludedAsync"/>, Rückgängig über <see cref="IFileOperationService.UndoBatchAsync"/>) und
/// erneutes Einlesen nach „Ausschluss aufheben“ (Incremental-Scan). Den Bereich (ADR-0048) legt die Oberfläche fest.
/// </summary>
public interface IScanExclusionService
{
    IScanExclusionStore Store { get; }

    Task<ExcludedFilesSummary> SummarizeAsync(ExclusionScope scope, CancellationToken ct = default);

    Task<ExclusionRemovalResult> RemoveFromCatalogAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);

    Task<FileOperationBatchResult?> UndoRemovalAsync(int batchId, CancellationToken ct = default);

    /// <summary>Bibliothek neu einlesen (Incremental) — nach „Ausschluss aufheben“.</summary>
    Task<ScanResult> RescanAsync(int libraryId, CancellationToken ct = default);
}

/// <inheritdoc cref="IScanExclusionService"/>
public sealed class ScanExclusionService : IScanExclusionService
{
    private const int Chunk = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IFileOperationService _fileOps;
    private readonly IScanService _scans;
    private readonly ILogger<ScanExclusionService> _logger;

    public ScanExclusionService(ILibraryContextFactory factory, IScanExclusionStore store, IFileOperationService fileOps, IScanService scans, ILogger<ScanExclusionService> logger)
    {
        _factory = factory;
        Store = store;
        _fileOps = fileOps;
        _scans = scans;
        _logger = logger;
    }

    public IScanExclusionStore Store { get; }

    public async Task<ExcludedFilesSummary> SummarizeAsync(ExclusionScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var ctx = _factory.Create();
        IReadOnlyList<int> libraryIds = scope switch
        {
            ExclusionScope.Folder f => [f.LibraryId],
            ExclusionScope.Name { LibraryIds: { } ids } => ids,
            ExclusionScope.Name => await ctx.Libraries.AsNoTracking().Select(l => l.Id).ToListAsync(ct).ConfigureAwait(false),
            ExclusionScope.Current c => c.LibraryIds,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        if (libraryIds.Count == 0)
        {
            return ExcludedFilesSummary.Empty;
        }

        var idList = libraryIds.ToList();
        var roots = await ctx.LibraryRoots.AsNoTracking().Where(r => idList.Contains(r.LibraryId)).ToListAsync(ct).ConfigureAwait(false);
        var predicates = new Dictionary<int, Func<string, bool>>();
        foreach (var libraryId in idList)
        {
            var libraryRoots = roots.Where(r => r.LibraryId == libraryId).Select(r => r.Path).ToList();
            switch (scope)
            {
                case ExclusionScope.Folder folder:
                {
                    var top = ScanExclusionSet.Normalize(folder.FolderPath);
                    predicates[libraryId] = path => ScanExclusionSet.IsInside(ScanExclusionSet.Normalize(path), top);
                    break;
                }

                case ExclusionScope.Name name:
                {
                    var set = new ScanExclusionSet([ScanExclusionStore.NormalizeName(name.FolderName)], [], libraryRoots);
                    predicates[libraryId] = set.IsExcludedPath;
                    break;
                }

                default:
                {
                    var set = await Store.LoadSetAsync(libraryId, ct).ConfigureAwait(false);
                    predicates[libraryId] = set.IsExcludedPath;
                    break;
                }
            }
        }

        var folders = await ctx.Folders.AsNoTracking()
            .Where(f => idList.Contains(f.LibraryId))
            .Select(f => new { f.Id, f.Path, f.LibraryId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var folderIds = folders.Where(f => predicates[f.LibraryId](f.Path)).Select(f => f.Id).ToList();
        var fileIds = new List<int>();
        foreach (var chunk in folderIds.Chunk(Chunk))
        {
            fileIds.AddRange(await ctx.Files.AsNoTracking().Where(f => chunk.Contains(f.FolderId)).Select(f => f.Id).ToListAsync(ct).ConfigureAwait(false));
        }

        if (fileIds.Count == 0)
        {
            return ExcludedFilesSummary.Empty;
        }

        var userData = await Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return UserDataCounter.Count(connection, fileIds, ct);
            },
            ct).ConfigureAwait(false);
        return new ExcludedFilesSummary(fileIds, userData);
    }

    public Task<ExclusionRemovalResult> RemoveFromCatalogAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => _fileOps.RemoveExcludedAsync(fileIds, ct);

    public Task<FileOperationBatchResult?> UndoRemovalAsync(int batchId, CancellationToken ct = default)
        => _fileOps.UndoBatchAsync(batchId, ct);

    public async Task<ScanResult> RescanAsync(int libraryId, CancellationToken ct = default)
    {
        var result = await _scans.ScanAsync(libraryId, ScanMode.Incremental, null, ct).ConfigureAwait(false);
        _logger.LogInformation("Nach „Ausschluss aufheben“ neu eingelesen: {Added} neu, {Outcome}", result.Added, result.Outcome);
        return result;
    }
}
