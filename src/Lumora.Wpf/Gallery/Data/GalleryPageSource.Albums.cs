using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Lumora.Data.RawSql;
using Lumora.Services.Albums;

namespace Lumora.Gallery.Data;

/// <summary>Album- und Verknüpfungs-Teil der Galerie-Abfrage (ADR-0054); eigener Teil des Records (Positionsparameter bleiben).</summary>
public sealed partial record GalleryQuery
{
    /// <summary>Filter „Album“: nur Dateien dieses eigenen Albums; null = keiner.</summary>
    public int? AlbumId { get; init; }

    /// <summary>Ordneransicht „mit Verknüpfungen“ (Standard) bzw. „nur echte Dateien“.</summary>
    public bool IncludeFolderLinks { get; init; } = true;
}

/// <summary>
/// Alben und Ordner-Verknüpfungen in der Galerie-SQL (ADR-0054). Der Ordnerfilter (<see cref="ResolveFolderClauseCore"/>)
/// wird hier erweitert und wirkt damit in Zählung, Seiten und Kategorie-Segmenten gleich:
/// <list type="bullet">
/// <item>„mit Verknüpfungen“: <c>(f.FolderId ∈ Ordner[+Unterordner] ODER f.Id ∈ FolderLinks dieser Ordner)</c> — nur, wenn
/// es dort überhaupt Verknüpfungen gibt; sonst bleibt die Bedingung unverändert (gleiche Abfragepläne).</item>
/// <item>Filter „Album“: <c>f.Id IN (AlbumItems des Albums)</c> über den Primärschlüssel (AlbumId, FileId).</item>
/// </list>
/// Dazu: Zähler „n verknüpft“, Kennzeichen „verknüpft“ je Kachel, Ids einer Abfrage (intelligente Alben) und die
/// Auflösung eines gespeicherten Filters (<see cref="SmartAlbumFilter"/>, Begriffe über ihre Namen) in eine Abfrage.
/// </summary>
public sealed partial class GalleryPageSource
{
    private const string AlbumClause = "AND f.Id IN (SELECT ai.FileId FROM AlbumItems ai WHERE ai.AlbumId = $albumId)";

    /// <summary>Ordnerfilter samt Verknüpfungen und Album-Filter (alle Lesewege der Galerie gehen hier durch).</summary>
    private static FolderClause ResolveFolderClause(SqliteConnection conn, GalleryQuery query)
    {
        var folder = ResolveFolderClauseCore(conn, query);
        var clause = folder.Clause;
        if (folder.FolderId is not null && query.IncludeFolderLinks && HasFolderLinks(conn, query, folder))
        {
            clause = folder.Path is null
                ? "AND (f.FolderId = $folderId OR f.Id IN (SELECT fl.FileId FROM FolderLinks fl WHERE fl.FolderId = $folderId))"
                : $"AND (f.FolderId IN ({SubtreeFoldersSql(query, "sf")}) OR f.Id IN ({SubtreeLinksSql(query)}))";
        }

        if (query.AlbumId is not null)
        {
            clause += "\n  " + AlbumClause;
        }

        clause += VariantClause(query); // „beste Fassung zeigen“ (ADR-0071, GalleryPageSource.Variants.cs)
        return folder with { Clause = clause };
    }

    private static void BindAlbumParams(SqliteCommand cmd, GalleryQuery query)
    {
        if (query.AlbumId is { } albumId)
        {
            cmd.Parameters.AddWithValue("$albumId", albumId);
        }
    }

    private static string SubtreeFoldersSql(GalleryQuery query, string alias)
        => $"SELECT {alias}.Id FROM Folders {alias} WHERE {ScopeCondition(query, alias + ".LibraryId")} AND ({alias}.Path = $folderPath OR {alias}.Path LIKE $folderPrefix ESCAPE '\\')";

    private static string SubtreeLinksSql(GalleryQuery query)
        => $"SELECT fl.FileId FROM FolderLinks fl WHERE fl.FolderId IN ({SubtreeFoldersSql(query, "lf")})";

    /// <summary>Gibt es im Ordner (bzw. seinem Teilbaum) überhaupt Verknüpfungen? Billig über den Primärschlüssel.</summary>
    private static bool HasFolderLinks(SqliteConnection conn, GalleryQuery query, FolderClause folder)
    {
        using var cmd = conn.CreateCommand();
        if (folder.Path is null)
        {
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM FolderLinks WHERE FolderId = $folderId);";
            cmd.Parameters.AddWithValue("$folderId", folder.FolderId!.Value);
        }
        else
        {
            cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM FolderLinks fl WHERE fl.FolderId IN ({SubtreeFoldersSql(query, "lf")}));";
            cmd.Parameters.AddWithValue("$libId", query.LibraryId);
            cmd.Parameters.AddWithValue("$folderPath", folder.Path);
            cmd.Parameters.AddWithValue("$folderPrefix", folder.Prefix!);
        }

        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Bedingung „verknüpft, aber nicht physisch im Ordner(-Teilbaum)“.</summary>
    private static string LinkedOnlyClause(GalleryQuery query, FolderClause folder)
        => folder.Path is null
            ? "AND f.Id IN (SELECT fl.FileId FROM FolderLinks fl WHERE fl.FolderId = $folderId) AND f.FolderId <> $folderId"
            : $"AND f.Id IN ({SubtreeLinksSql(query)}) AND f.FolderId NOT IN ({SubtreeFoldersSql(query, "sf")})";

    /// <summary>
    /// Zähler „n verknüpft“ der Ordneransicht: Treffer der Abfrage (alle Filter), die nur über eine Verknüpfung im Ordner
    /// erscheinen. 0 ohne Ordnerfilter, bei „nur echte Dateien“ oder ohne Verknüpfungen.
    /// </summary>
    public Task<int> GetFolderLinkedCountAsync(GalleryQuery query, CancellationToken ct)
        => RunAsync(conn =>
        {
            if (!query.HasFolder || !query.IncludeFolderLinks)
            {
                return 0;
            }

            var folder = ResolveFolderClauseCore(conn, query);
            if (folder.FolderId is null || !HasFolderLinks(conn, query, folder))
            {
                return 0;
            }

            using var cmd = conn.CreateCommand();
            var clause = LinkedOnlyClause(query, folder) + (query.AlbumId is null ? string.Empty : "\n  " + AlbumClause);
            cmd.CommandText = BuildCountSql(query, clause);
            BindFilterParams(cmd, query, folder);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, ct);

    /// <summary>Hat der Ordner der Abfrage (samt Unterordnern, falls gewählt) überhaupt Verknüpfungen? Unabhängig von den Filtern.</summary>
    public Task<bool> FolderHasLinksAsync(GalleryQuery query, CancellationToken ct)
        => RunAsync(conn =>
        {
            if (!query.HasFolder)
            {
                return false;
            }

            var folder = ResolveFolderClauseCore(conn, query);
            return folder.FolderId is not null && HasFolderLinks(conn, query, folder);
        }, ct);

    /// <summary>Welche der Dateien erscheinen in der Ordneransicht nur über eine Verknüpfung (Ketten-Symbol)?</summary>
    public Task<IReadOnlySet<int>> GetLinkedHereAsync(GalleryQuery query, IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => RunAsync<IReadOnlySet<int>>(conn =>
        {
            var result = new HashSet<int>();
            if (!query.HasFolder || !query.IncludeFolderLinks || fileIds.Count == 0)
            {
                return result;
            }

            var folder = ResolveFolderClauseCore(conn, query);
            if (folder.FolderId is null || !HasFolderLinks(conn, query, folder))
            {
                return result;
            }

            foreach (var chunk in fileIds.Chunk(InChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                var inList = new StringBuilder();
                for (var i = 0; i < chunk.Length; i++)
                {
                    inList.Append(i == 0 ? string.Empty : ",").Append("$p").Append(i.ToString(CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), chunk[i]);
                }

                cmd.CommandText = $"SELECT f.Id FROM Files f WHERE f.Id IN ({inList}) {LinkedOnlyClause(query, folder)};";
                cmd.Parameters.AddWithValue("$libId", query.LibraryId);
                cmd.Parameters.AddWithValue("$folderId", folder.FolderId.Value);
                if (folder.Path is not null)
                {
                    cmd.Parameters.AddWithValue("$folderPath", folder.Path);
                    cmd.Parameters.AddWithValue("$folderPrefix", folder.Prefix!);
                }

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetInt32(0));
                }
            }

            return result;
        }, ct);

    /// <summary>Alle Treffer-Ids der Abfrage, neueste Aufnahme zuerst (intelligente Alben); <paramref name="limit"/> begrenzt.</summary>
    public Task<IReadOnlyList<int>> GetFileIdsAsync(GalleryQuery query, int? limit, CancellationToken ct)
        => RunAsync<IReadOnlyList<int>>(conn =>
        {
            // Dieselbe Klausel-Erzeugung wie Karte und Zählung (GalleryPageSource.Shared.cs, ADR-0055)
            using var cmd = CreateFilteredCommand(
                conn,
                query,
                "f.Id",
                $"ORDER BY {CaptureDateSortSql.SortKey} DESC, f.Id DESC{(limit is { } n ? $" LIMIT {n.ToString(CultureInfo.InvariantCulture)}" : string.Empty)}");
            var ids = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                ids.Add(reader.GetInt32(0));
            }

            return ids;
        }, ct);

    /// <summary>Katalog-Ordner mit diesem Pfad (ohne Groß-/Kleinschreibung) in den Bibliotheken des Bereichs; null, wenn keiner.</summary>
    public Task<(int Id, int LibraryId)?> FindFolderAsync(string path, IReadOnlyList<int> libraryIds, CancellationToken ct)
        => RunAsync<(int Id, int LibraryId)?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            var scope = string.Join(",", libraryIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            cmd.CommandText = $"SELECT Id, LibraryId FROM Folders WHERE LibraryId IN ({scope}) AND Path = $p COLLATE NOCASE LIMIT 1;";
            cmd.Parameters.AddWithValue("$p", path.TrimEnd('\\', '/'));
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : null;
        }, ct);

    /// <summary>
    /// Gespeicherten Filter eines intelligenten Albums in eine Abfrage übersetzen: Begriffe über ihren Namen in der Gruppe
    /// der Bibliothek <paramref name="libraryId"/>; ein Name, den es nicht (mehr) gibt, wird zur Id -1 und trifft nichts
    /// (ein gelöschtes Tag weitet das Album nie auf alles aus).
    /// </summary>
    public Task<GalleryQuery> BuildSmartQueryAsync(SmartAlbumFilter filter, int libraryId, IReadOnlyList<int> scopeLibraryIds, CancellationToken ct)
        => RunAsync(conn => BuildSmartQuery(conn, filter, libraryId, scopeLibraryIds), ct);

    private static GalleryQuery BuildSmartQuery(SqliteConnection conn, SmartAlbumFilter filter, int libraryId, IReadOnlyList<int> scopeLibraryIds)
    {
        int? Lookup(string sql, params (string Name, object Value)[] parameters)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$lib", libraryId);
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            return cmd.ExecuteScalar() is long id ? (int)id : null;
        }

        const string Group = "(SELECT GroupId FROM Libraries WHERE Id = $lib)";
        List<int> Names(IReadOnlyList<string> names, string table)
            => names.Select(n => Lookup($"SELECT Id FROM {table} WHERE GroupId = {Group} AND Name = $n COLLATE NOCASE LIMIT 1;", ("$n", n)) ?? -1).ToList();

        var media = Enum.TryParse<GalleryMediaKind>(filter.Media, out var m) ? m : GalleryMediaKind.All;
        var location = Enum.TryParse<GalleryLocation>(filter.Location, out var l) ? l : GalleryLocation.Anywhere;
        var state = new GalleryFilterState(media, filter.FavoritesOnly, location, Math.Clamp(filter.MinRating, 0, GalleryFilterState.MaxRating))
        {
            Place = filter.Place is { } p ? new GalleryPlaceFilter(p.PlaceIds, p.IncludeWithoutLocation, p.IncludeWithoutPlaceName, p.Label) : null,
        };

        var tagMode = Enum.TryParse<GalleryTagMode>(filter.TagMode, out var tm) ? tm : GalleryTagMode.Any;
        var tagIds = filter.Tags.Count > 0 ? Names(filter.Tags, "Tags") : null;

        var personMode = Enum.TryParse<GalleryPersonMode>(filter.PersonMode, out var pm) ? pm : GalleryPersonMode.Any;
        var personFilter = new GalleryPersonFilter(Names(filter.Persons, "Persons"), personMode, filter.PersonIncludeSuggestions,
            filter.Relations.Count > 0 ? Names(filter.Relations, "PersonRelations") : null);

        var categories = new List<GalleryCategoryFilter>();
        foreach (var category in filter.Categories)
        {
            var categoryId = Lookup($"SELECT Id FROM Categories WHERE GroupId = {Group} AND Name = $n COLLATE NOCASE LIMIT 1;", ("$n", category.Name));
            if (categoryId is not { } cid)
            {
                categories.Add(new GalleryCategoryFilter(-1, [-1], false)); // Kategorie gibt es nicht mehr → trifft nichts
                continue;
            }

            var values = category.Values
                .Select(v => Lookup("SELECT Id FROM CategoryValues WHERE CategoryId = $c AND Name = $n COLLATE NOCASE LIMIT 1;", ("$c", cid), ("$n", v)) ?? -1)
                .ToList();
            categories.Add(new GalleryCategoryFilter(cid, values, category.IncludeNone));
        }

        int? folderId = null;
        if (filter.FolderPath is { } path)
        {
            var scope = string.Join(",", scopeLibraryIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            folderId = Lookup($"SELECT Id FROM Folders WHERE LibraryId IN ({scope}) AND Path = $p COLLATE NOCASE LIMIT 1;", ("$p", path)) ?? -1;
        }

        int? albumId = filter.AlbumName is { } album
            ? Lookup($"SELECT Id FROM Albums WHERE GroupId = {Group} AND Kind = 0 AND Name = $n LIMIT 1;", ("$n", album)) ?? -1
            : null;

        var motifMode = Enum.TryParse<GalleryMotifMode>(filter.MotifMode, out var mm) ? mm : GalleryMotifMode.Any;
        return new GalleryQuery(
            libraryId,
            state,
            string.IsNullOrWhiteSpace(filter.SearchTerm) ? null : filter.SearchTerm.Trim(),
            folderId,
            filter.IncludeSubfolders ?? true,
            GallerySort.CapturedDesc,
            tagIds,
            tagMode,
            categories,
            null,
            personFilter.IsActive ? personFilter : null)
        {
            LibraryIds = scopeLibraryIds.Count > 1 ? scopeLibraryIds : null,
            MotifFilter = filter.Motifs.Count > 0 ? new GalleryMotifFilter(Names(filter.Motifs, "MotifTerms"), motifMode) : null,
            AlbumId = albumId,
        };
    }
}
