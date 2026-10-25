using System.Globalization;
using Microsoft.Data.Sqlite;
using Lumora.Services.Database;

namespace Lumora.Services.Marks;

/// <summary>Häufigkeit eines Tags (<see cref="Id"/> = TagId) bzw. Kategorie-Werts (<see cref="Id"/> = ValueId) in einem Ordner.</summary>
public readonly record struct MarkUsage(int Id, int Count);

/// <summary>
/// Tag-/Kategorie-Zahlen eines Ordners — nur Dateien DIREKT im Ordner, nicht in Unterordnern (Mockup 16).
/// <paramref name="TaggedFiles"/> = Dateien mit mindestens einem Tag, <paramref name="CategorizedFiles"/> = Dateien mit
/// mindestens einem Kategorie-Wert; <paramref name="TopTags"/>/<paramref name="TopValues"/> die häufigsten (absteigend).
/// </summary>
public sealed record FolderMarkCount(int TaggedFiles, int CategorizedFiles, IReadOnlyList<MarkUsage> TopTags, IReadOnlyList<MarkUsage> TopValues)
{
    public static FolderMarkCount None { get; } = new(0, 0, [], []);

    public bool IsEmpty => TaggedFiles == 0 && CategorizedFiles == 0;
}

/// <summary>Ergebnis von <see cref="IFolderMarkCounter.CountAsync"/>: Ordner ohne Tag und ohne Kategorie fehlen.</summary>
public sealed class FolderMarkCounts
{
    public static FolderMarkCounts Empty { get; } = new(new Dictionary<int, FolderMarkCount>());

    public FolderMarkCounts(IReadOnlyDictionary<int, FolderMarkCount> byFolder)
    {
        ByFolder = byFolder ?? throw new ArgumentNullException(nameof(byFolder));
    }

    public IReadOnlyDictionary<int, FolderMarkCount> ByFolder { get; }

    /// <summary>Zahlen des Ordners oder null, wenn darin keine Datei Tags oder Kategorien hat.</summary>
    public FolderMarkCount? Get(int folderId) => ByFolder.TryGetValue(folderId, out var count) ? count : null;
}

/// <summary>
/// Zählt für den Ordnerbaum (Mockup 16), wie viele Dateien je Ordner Tags bzw. Kategorie-Werte haben, samt der häufigsten.
/// Alles in vier gruppierten Abfragen über die Zuordnungstabellen (kein N+1 je Ordner), beschränkt auf die Bibliotheken
/// des aktuellen Bereichs (ADR-0048) — Dateien anderer Bibliotheken zählen nie mit.
/// </summary>
public interface IFolderMarkCounter
{
    /// <param name="libraryIds">Bibliotheken des Bereichs (eine bzw. alle verknüpften); leer → leeres Ergebnis.</param>
    /// <param name="top">Wie viele der häufigsten Tags/Werte je Ordner mitkommen.</param>
    Task<FolderMarkCounts> CountAsync(IReadOnlyCollection<int> libraryIds, int top = 3, CancellationToken ct = default);
}

/// <inheritdoc cref="IFolderMarkCounter"/>
public sealed class FolderMarkCounter(ILibraryContextFactory factory) : IFolderMarkCounter
{
    public Task<FolderMarkCounts> CountAsync(IReadOnlyCollection<int> libraryIds, int top = 3, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        if (libraryIds.Count == 0)
        {
            return Task.FromResult(FolderMarkCounts.Empty);
        }

        // Ids sind int → als Literal-Liste sicher (wie LibraryScopeInfo.SqlLibraryIdList); Microsoft.Data.Sqlite ist synchron → Task.Run
        var idList = string.Join(",", libraryIds.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));
        return Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            var taggedFiles = ReadPairs(connection, $"""
                SELECT f.FolderId, COUNT(DISTINCT ft.FileId) FROM FileTags ft JOIN Files f ON f.Id = ft.FileId
                WHERE f.LibraryId IN ({idList}) GROUP BY f.FolderId;
                """, ct);
            var tagUsage = ReadUsage(connection, $"""
                SELECT f.FolderId, ft.TagId, COUNT(*) FROM FileTags ft JOIN Files f ON f.Id = ft.FileId
                WHERE f.LibraryId IN ({idList}) GROUP BY f.FolderId, ft.TagId;
                """, ct);
            var categorizedFiles = ReadPairs(connection, $"""
                SELECT f.FolderId, COUNT(DISTINCT v.FileId) FROM FileCategoryValues v JOIN Files f ON f.Id = v.FileId
                WHERE f.LibraryId IN ({idList}) GROUP BY f.FolderId;
                """, ct);
            var valueUsage = ReadUsage(connection, $"""
                SELECT f.FolderId, v.ValueId, COUNT(*) FROM FileCategoryValues v JOIN Files f ON f.Id = v.FileId
                WHERE f.LibraryId IN ({idList}) GROUP BY f.FolderId, v.ValueId;
                """, ct);

            var result = new Dictionary<int, FolderMarkCount>();
            foreach (var folderId in taggedFiles.Keys.Union(categorizedFiles.Keys))
            {
                result[folderId] = new FolderMarkCount(
                    taggedFiles.GetValueOrDefault(folderId),
                    categorizedFiles.GetValueOrDefault(folderId),
                    Top(tagUsage, folderId, top),
                    Top(valueUsage, folderId, top));
            }

            return new FolderMarkCounts(result);
        }, ct);
    }

    /// <summary>Häufigste zuerst, bei Gleichstand die kleinere Id (stabil).</summary>
    private static IReadOnlyList<MarkUsage> Top(Dictionary<int, List<MarkUsage>> usage, int folderId, int top)
        => usage.TryGetValue(folderId, out var list)
            ? list.OrderByDescending(u => u.Count).ThenBy(u => u.Id).Take(Math.Max(0, top)).ToArray()
            : [];

    private static Dictionary<int, int> ReadPairs(SqliteConnection connection, string sql, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = new Dictionary<int, int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetInt32(0)] = reader.GetInt32(1);
        }

        return result;
    }

    private static Dictionary<int, List<MarkUsage>> ReadUsage(SqliteConnection connection, string sql, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = new Dictionary<int, List<MarkUsage>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var folderId = reader.GetInt32(0);
            if (!result.TryGetValue(folderId, out var list))
            {
                list = new List<MarkUsage>();
                result[folderId] = list;
            }

            list.Add(new MarkUsage(reader.GetInt32(1), reader.GetInt32(2)));
        }

        return result;
    }
}
