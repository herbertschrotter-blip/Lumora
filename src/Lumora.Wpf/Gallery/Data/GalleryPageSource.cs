using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Data.RawSql;
using Lumora.Services.Database;

namespace Lumora.Gallery.Data;

/// <summary>
/// Abfrage-Parameter der Galerie. <paramref name="FolderId"/> null = ganze Library;
/// <paramref name="IncludeSubfolders"/> steuert, ob der Ordnerfilter rekursiv (Pfad-Prefix) wirkt.
/// <paramref name="SearchTerm"/> ist freier Benutzertext; den FTS5-MATCH-Ausdruck baut
/// <see cref="FtsQueryBuilder.FromUserInput"/> (eine Implementierung im Repo) — Text ohne suchbaren Inhalt
/// (leer, nur Sonderzeichen wie <c>---</c>) gilt als „keine Suche“. Die Suche findet außerdem Dateien, deren
/// Tag-Name (Tags, ADR-0021), Kategorie-Wert oder Kategorie-Name (ADR-0027) den Suchtext enthält. <paramref name="TagIds"/> +
/// <paramref name="TagMode"/> filtern nach Tags (ODER / UND / ohne Tag), <paramref name="CategoryFilters"/> nach
/// Kategorie-Werten. <paramref name="CategorySort"/> (nicht null) sortiert die ganze Trefferliste nach dem Wert einer
/// Kategorie und ersetzt dann <paramref name="Sort"/>. <paramref name="Filter"/> = Datei-Teil (Dateiart, Favoriten,
/// Ablage, Mindest-Bewertung, ADR-0041); alle Teile wirken mit UND.
/// </summary>
public sealed partial record GalleryQuery(
    int LibraryId,
    GalleryFilterState Filter,
    string? SearchTerm,
    int? FolderId = null,
    bool IncludeSubfolders = true,
    GallerySort Sort = GallerySort.ModifiedDesc,
    IReadOnlyList<int>? TagIds = null,
    GalleryTagMode TagMode = GalleryTagMode.Any,
    IReadOnlyList<GalleryCategoryFilter>? CategoryFilters = null,
    GalleryCategorySort? CategorySort = null,
    GalleryPersonFilter? PersonFilter = null)
{
    /// <summary>
    /// Bibliotheken des Bereichs (ADR-0048, <c>GalleryPageSource.Scope.cs</c>): null bzw. eine = nur <see cref="LibraryId"/>;
    /// mehrere = Ansicht „Alle verknüpften“.
    /// </summary>
    public IReadOnlyList<int>? LibraryIds { get; init; }

    /// <summary>Wirksame Bereichs-Bibliotheken (nie leer).</summary>
    public IReadOnlyList<int> ScopeLibraryIds => LibraryIds is { Count: > 1 } ids ? ids : [LibraryId];

    /// <summary>True, wenn ein Personen-Filter wirkt (0.2.38, <c>GalleryPageSource.Persons.cs</c>).</summary>
    public bool HasPersonFilter => PersonFilter is { IsActive: true };

    /// <summary>Sicherer FTS5-MATCH-Ausdruck (quotierte Präfix-Phrasen) oder null ohne suchbaren Inhalt.</summary>
    public string? MatchExpression => FtsQueryBuilder.FromUserInput(SearchTerm);

    public bool HasSearch => MatchExpression is not null;
    public bool HasFolder => FolderId is not null;

    /// <summary>True, wenn ein Tag-Filter wirkt (Tags gewählt oder Modus „ohne Tag“).</summary>
    public bool HasTagFilter => TagMode == GalleryTagMode.None || TagIds is { Count: > 0 };

    /// <summary>Wirksame Kategorie-Filter (mindestens ein Wert oder „ohne Wert“ gewählt).</summary>
    public IReadOnlyList<GalleryCategoryFilter> ActiveCategoryFilters
        => CategoryFilters?.Where(c => c.IsActive).ToList() ?? (IReadOnlyList<GalleryCategoryFilter>)[];
}

/// <summary>
/// Filter einer Kategorie (ADR-0027): Dateien mit einem der Werte (ODER) und/oder ohne Wert in dieser Kategorie.
/// Mehrere Kategorien verknüpft die Galerie mit UND.
/// </summary>
public sealed record GalleryCategoryFilter(int CategoryId, IReadOnlyList<int> ValueIds, bool IncludeNone)
{
    public bool IsActive => IncludeNone || ValueIds.Count > 0;
}

/// <summary>
/// Serverseitige Sortierung nach dem Wert einer Kategorie (ADR-0027, 0.2.23): Werte in ihrer Reihenfolge aus der
/// Verwaltung (SortOrder), absteigend umgekehrt; Dateien ohne Wert stehen in beiden Richtungen am Ende. Gleiche Werte
/// nach Datum neu → alt, dann Id. Gelesen wird segmentweise je Wert (ADR-0031, <c>GalleryPageSource.CategorySort.cs</c>).
/// </summary>
public sealed record GalleryCategorySort(int CategoryId, bool Descending);

/// <summary>
/// Raw-SQLite-Lesepfad der Galerie (ADR-0004 / ADR-0010): eine Verbindung, alle Queries laufen
/// serialisiert auf dem Thread-Pool, damit der UI-Thread nie blockiert. Liefert ausschließlich
/// kleine DTOs — nie mehr als eine Page (≤ 1000 Zeilen) pro Aufruf.
/// </summary>
public sealed partial class GalleryPageSource : IDisposable
{
    private const string FileColumns =
        "f.Id, f.FolderId, f.Name, f.Extension, f.Size, f.ModifiedAtUtc, f.MediaType, f.IsFavorite, f.Rating, f.Missing, fo.Path, lr.Role, " + CaptureDateColumns
        + ", f.Width AS MediaWidth, f.Height AS MediaHeight" // Collage (ADR-0044): gelesen über den Spaltennamen
        + ", f.LibraryId AS FileLibraryId"; // Bibliothek je Datei (ADR-0048: Vorschaubild-Cache in „Alle verknüpften“)

    /// <summary>Rolle der Wurzel je Datei (Badge „Eingang“, ADR-0020); LEFT JOIN, weil Altzeilen ohne RootId denkbar sind.</summary>
    private const string RootJoin = "LEFT JOIN LibraryRoots lr ON lr.Id = fo.RootId";

    /// <summary>Chunk-Größe für IN-Listen (SQLite-Variablenlimit bleibt weit darunter).</summary>
    private const int InChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<GalleryPageSource> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public GalleryPageSource(ILibraryContextFactory factory, ILogger<GalleryPageSource>? logger = null)
    {
        _factory = factory;
        _logger = logger ?? NullLogger<GalleryPageSource>.Instance;
    }

    public bool IsOpen => _connection is not null;
    public string DatabasePath => _factory.DatabasePath;

    public void Open()
    {
        if (_connection is not null)
        {
            return;
        }

        _connection = _factory.OpenSqliteConnection();
        ResetCountCaches();
    }

    /// <summary>Anzahl der Treffer für die Query (Filter, Suche, Ordner).</summary>
    public Task<int> GetTotalCountAsync(GalleryQuery query, CancellationToken ct)
        => RunAsync(conn =>
        {
            var folder = ResolveFolderClause(conn, query);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildCountSql(query, folder.Clause);
            BindFilterParams(cmd, query, folder);
            if (query.CategorySort is null)
            {
                return Convert.ToInt32(cmd.ExecuteScalar());
            }

            // Kategorie-Sortierung (ADR-0031): die erste Seite braucht dieselbe Zahl — merken statt doppelt zählen.
            var dataVersion = ReadDataVersion(conn, null, startSnapshot: false);
            var total = Convert.ToInt64(cmd.ExecuteScalar());
            RememberTotal(dataVersion, cmd, total);
            return (int)total;
        }, ct);

    /// <summary>
    /// Lädt eine Page. Liegt die Page näher am Ende als am Anfang der Trefferliste, wird intern
    /// umgekehrt sortiert, vom Ende her mit kleinem OFFSET gelesen und clientseitig gedreht
    /// (Reverse-Trick aus Spike #02, Folge-Erkenntnis 1 — ⏭ bleibt auch bei 500k unter 150 ms).
    /// <paramref name="totalCount"/> ist der zuletzt gezählte Gesamtwert (0 = unbekannt → kein Reverse). Mit
    /// Kategorie-Sortierung zählt die Methode selbst je Wert-Segment und ignoriert <paramref name="totalCount"/> (ADR-0031).
    /// </summary>
    public Task<IReadOnlyList<GalleryFileDto>> LoadPageAsync(
        GalleryQuery query,
        int pageIndex,
        int pageSize,
        int totalCount,
        CancellationToken ct)
        => RunAsync<IReadOnlyList<GalleryFileDto>>(conn =>
        {
            if (query.CategorySort is { } categorySort)
            {
                // Segmentweise je Wert statt einer Sortierung über alle Dateien (ADR-0031); zählt selbst.
                return LoadCategorySortedPage(conn, query, categorySort, pageIndex, pageSize, ct);
            }

            var offsetFromStart = (long)pageIndex * pageSize;
            var remaining = Math.Max(0L, totalCount - offsetFromStart);
            var limitFromEnd = (int)Math.Min(pageSize, remaining);
            var offsetFromEnd = totalCount - offsetFromStart - limitFromEnd;
            var useReverse = totalCount > 0 && limitFromEnd > 0 && offsetFromEnd < offsetFromStart;

            var folder = ResolveFolderClause(conn, query);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildPageSql(query, folder.Clause, reverse: useReverse);
            BindFilterParams(cmd, query, folder);
            cmd.Parameters.AddWithValue("$limit", useReverse ? limitFromEnd : pageSize);
            cmd.Parameters.AddWithValue("$offset", useReverse ? offsetFromEnd : offsetFromStart);

            var sw = Stopwatch.StartNew();
            var rows = new List<GalleryFileDto>(pageSize);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(ReadRow(reader));
            }

            if (useReverse)
            {
                rows.Reverse();
            }

            sw.Stop();
            _logger.LogDebug(
                "Page-SQL: Idx={Idx} Size={Size} Rows={Rows} Reverse={Reverse} Offset={Offset} Sort={Sort} Folder={Folder} Rek={Rek} Suche={Search} {Ms:N1} ms",
                pageIndex, pageSize, rows.Count, useReverse, useReverse ? offsetFromEnd : offsetFromStart, query.Sort,
                query.FolderId?.ToString() ?? "-", query.IncludeSubfolders, query.SearchTerm ?? "-",
                sw.Elapsed.TotalMilliseconds);
            return rows;
        }, ct);

    /// <summary>Stammdaten eines Ordners oder null, wenn er nicht (mehr) existiert.</summary>
    public Task<GalleryFolderInfo?> GetFolderInfoAsync(int folderId, CancellationToken ct)
        => RunAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, Path, ParentId FROM Folders WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", folderId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new GalleryFolderInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3));
        }, ct);

    /// <summary>
    /// Nächste Unterordner eines Ordners mit rekursiver Dateianzahl (Container-State, PLAN Kap. 16).
    /// Pfad-basiert statt rein über ParentId, weil der Scanner nur Ordner mit Dateien anlegt und
    /// ParentId-Ketten damit Lücken haben können; ein Unterordner ohne eigene Zeile erscheint dann
    /// über seinen nächsten erfassten Nachfahren (Name = relativer Pfad). Zwei Index-Queries
    /// (Files GROUP BY FolderId, Folders per Prefix) und Aggregation im Speicher — kein LIKE-Scan
    /// über 500k Files-Zeilen pro Unterordner.
    /// </summary>
    public Task<IReadOnlyList<GallerySubfolderDto>> GetSubfoldersAsync(int libraryId, int folderId, CancellationToken ct)
        => RunAsync<IReadOnlyList<GallerySubfolderDto>>(conn =>
        {
            if (ReadDescendants(conn, folderId, ct) is not { } found)
            {
                return Array.Empty<GallerySubfolderDto>();
            }

            var (parentTrimmed, descendants, directCounts) = found;

            var prefix = parentTrimmed + Path.DirectorySeparatorChar;
            var known = new HashSet<string>(descendants.Select(d => TrimSeparators(d.Path)), StringComparer.OrdinalIgnoreCase);
            var result = new List<GallerySubfolderDto>();
            foreach (var (id, path) in descendants)
            {
                var trimmed = TrimSeparators(path);

                // Direkter Unterordner = zwischen ihm und dem Elternordner liegt kein weiterer erfasster Ordner.
                var isDirect = true;
                var ancestor = Path.GetDirectoryName(trimmed);
                while (!string.IsNullOrEmpty(ancestor) && !ancestor.Equals(parentTrimmed, StringComparison.OrdinalIgnoreCase))
                {
                    if (known.Contains(ancestor))
                    {
                        isDirect = false;
                        break;
                    }

                    ancestor = Path.GetDirectoryName(ancestor);
                }

                if (!isDirect)
                {
                    continue;
                }

                var childPrefix = trimmed + Path.DirectorySeparatorChar;
                var count = 0;
                foreach (var (otherId, otherPath) in descendants)
                {
                    if (otherId == id
                        || otherPath.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        count += directCounts.GetValueOrDefault(otherId);
                    }
                }

                var displayName = trimmed.Length > prefix.Length ? trimmed[prefix.Length..] : trimmed;
                result.Add(new GallerySubfolderDto(id, displayName, path, count));
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }, ct);

    /// <summary>
    /// Erfasste Nachfahren eines Ordners (per Pfad-Präfix) und die Anzahl der Dateien direkt in jedem Ordner der
    /// Bibliothek; null = Ordner unbekannt oder ohne Nachfahren. Unterordner gehören immer der Bibliothek des Ordners
    /// (in „Alle verknüpften“ nicht zwingend der aktiven, ADR-0048).
    /// </summary>
    private static (string ParentTrimmed, List<(int Id, string Path)> Descendants, Dictionary<int, int> DirectCounts)? ReadDescendants(
        SqliteConnection conn, int folderId, CancellationToken ct)
    {
        string? parentPath = null;
        var libraryId = 0;
        using (var pathCmd = conn.CreateCommand())
        {
            pathCmd.CommandText = "SELECT Path, LibraryId FROM Folders WHERE Id = $id;";
            pathCmd.Parameters.AddWithValue("$id", folderId);
            using var pathReader = pathCmd.ExecuteReader();
            if (pathReader.Read())
            {
                parentPath = pathReader.GetString(0);
                libraryId = pathReader.GetInt32(1);
            }
        }

        if (parentPath is null)
        {
            return null;
        }

        var parentTrimmed = TrimSeparators(parentPath);
        var descendants = new List<(int Id, string Path)>();
        using (var folderCmd = conn.CreateCommand())
        {
            folderCmd.CommandText = """
                SELECT Id, Path FROM Folders
                WHERE LibraryId = $libId AND Path LIKE $prefix ESCAPE '\';
                """;
            folderCmd.Parameters.AddWithValue("$libId", libraryId);
            folderCmd.Parameters.AddWithValue("$prefix", EscapeLike(parentTrimmed + Path.DirectorySeparatorChar) + "%");
            using var reader = folderCmd.ExecuteReader();
            while (reader.Read())
            {
                descendants.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }

        if (descendants.Count == 0)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        var directCounts = new Dictionary<int, int>();
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT FolderId, COUNT(*) FROM Files WHERE LibraryId = $libId GROUP BY FolderId;";
            countCmd.Parameters.AddWithValue("$libId", libraryId);
            using var reader = countCmd.ExecuteReader();
            while (reader.Read())
            {
                directCounts[reader.GetInt32(0)] = reader.GetInt32(1);
            }
        }

        return (parentTrimmed, descendants, directCounts);
    }

    private static string TrimSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Favorit/Bewertung/Missing für konkrete Dateien (nach FilesChangedMessage Kind=Metadata).</summary>
    public Task<IReadOnlyList<GalleryMetadataDto>> GetMetadataAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => RunAsync<IReadOnlyList<GalleryMetadataDto>>(conn =>
        {
            var result = new List<GalleryMetadataDto>(fileIds.Count);
            foreach (var chunk in fileIds.Chunk(InChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                var sb = new StringBuilder("SELECT Id, IsFavorite, Rating, Missing FROM Files WHERE Id IN (");
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    var name = "$p" + i;
                    sb.Append(name);
                    cmd.Parameters.AddWithValue(name, chunk[i]);
                }

                sb.Append(");");
                cmd.CommandText = sb.ToString();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new GalleryMetadataDto(
                        reader.GetInt32(0),
                        reader.GetInt32(1) == 1,
                        reader.IsDBNull(2) ? null : (byte)reader.GetInt32(2),
                        reader.GetInt32(3) == 1));
                }
            }

            return result;
        }, ct);

    /// <summary>
    /// Abfrageplan (EXPLAIN QUERY PLAN, Spalte „detail“) der Page- bzw. Count-SQL — für Tests, die sichern, dass die
    /// Suche über Indizes läuft und nicht alle Dateien oder Zuordnungen durchläuft (ADR-0010/0011). Mit
    /// Kategorie-Sortierung (und <paramref name="count"/> = false) die Pläne von Segment-Zählung, Wert- und
    /// „ohne Wert“-Segment hintereinander (ADR-0031).
    /// </summary>
    internal Task<IReadOnlyList<string>> ExplainAsync(GalleryQuery query, bool count, CancellationToken ct)
        => RunAsync<IReadOnlyList<string>>(conn =>
        {
            if (!count && query.CategorySort is { } categorySort)
            {
                return ExplainCategorySort(conn, query, categorySort);
            }

            var folder = ResolveFolderClause(conn, query);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + (count ? BuildCountSql(query, folder.Clause) : BuildPageSql(query, folder.Clause, reverse: false));
            BindFilterParams(cmd, query, folder);
            if (!count)
            {
                cmd.Parameters.AddWithValue("$limit", 100);
                cmd.Parameters.AddWithValue("$offset", 0);
            }

            var lines = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                lines.Add(reader.GetString(3));
            }

            return lines;
        }, ct);

    // ---- SQL-Aufbau -------------------------------------------------------------------------

    private readonly record struct FolderClause(string Clause, int? FolderId, string? Path, string? Prefix);

    /// <summary>
    /// Ordnerfilter: direkt über FolderId; rekursiv über den Ordnerpfad als Prefix auf Folders
    /// (<c>Path = $folderPath OR Path LIKE $folderPrefix ESCAPE '\'</c>). Der Prefix-Vergleich
    /// läuft als IN-Subquery über die (kleine) Folders-Tabelle statt pro Files-Zeile.
    /// </summary>
    private static FolderClause ResolveFolderClauseCore(SqliteConnection conn, GalleryQuery query) // erweitert um Verknüpfungen/Album: GalleryPageSource.Albums.cs (ADR-0054)
    {
        if (query.FolderId is not { } folderId)
        {
            return new FolderClause(string.Empty, null, null, null);
        }

        if (!query.IncludeSubfolders)
        {
            return new FolderClause("AND f.FolderId = $folderId", folderId, null, null);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Path FROM Folders WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", folderId);
        var path = cmd.ExecuteScalar() as string;
        if (path is null)
        {
            // Ordner existiert nicht mehr → direkte FolderId-Bedingung liefert 0 Treffer (kein Absturz).
            return new FolderClause("AND f.FolderId = $folderId", folderId, null, null);
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = EscapeLike(trimmed + Path.DirectorySeparatorChar) + "%";
        var clause = $"""
            AND f.FolderId IN (
                SELECT sf.Id FROM Folders sf
                WHERE {ScopeCondition(query, "sf.LibraryId")}
                  AND (sf.Path = $folderPath OR sf.Path LIKE $folderPrefix ESCAPE '\'))
            """;
        return new FolderClause(clause, folderId, path, prefix);
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string BuildPageSql(GalleryQuery query, string folderClause, bool reverse)
    {
        var orderBy = BuildOrderBy(query.Sort, reverse);
        return $"""
            SELECT {FileColumns}
            FROM {BuildFilesSource(query)}
            JOIN Folders fo ON fo.Id = f.FolderId
            {RootJoin}
            WHERE {ScopeCondition(query, "f.LibraryId")}
              {BuildFilterClause(query.Filter)}
              {BuildTagClause(query)}
              {BuildCategoryClause(query)}
              {BuildPersonClause(query)}
              {BuildMotifClause(query)}
              {folderClause}
            ORDER BY {orderBy}
            LIMIT $limit OFFSET $offset;
            """;
    }

    private static string BuildCountSql(GalleryQuery query, string folderClause)
    {
        return $"""
            SELECT COUNT(*)
            FROM {BuildFilesSource(query)}
            WHERE {ScopeCondition(query, "f.LibraryId")}
              {BuildFilterClause(query.Filter)}
              {BuildTagClause(query)}
              {BuildCategoryClause(query)}
              {BuildPersonClause(query)}
              {BuildMotifClause(query)}
              {folderClause};
            """;
    }

    /// <summary>
    /// Quelle der Files-Zeilen. Mit Suche treiben die Treffer die Abfrage wie vor 0.2.6 (damals
    /// <c>FROM FilesFts JOIN Files</c>): FTS5-Treffer (Name/Endung, Präfix-Phrasen) vereinigt mit den Dateien, deren
    /// Tag-Name (ADR-0021), Kategorie-Wert oder Kategorie-Name (ADR-0027) den Suchtext enthält (NOCASE-Spalten, LIKE),
    /// einmal materialisiert und per <c>CROSS JOIN</c> (feste Join-Reihenfolge) über die Rowid an Files gebunden — kein
    /// Durchlauf aller Dateien der Library. Tags, Werte und Kategorien laufen alle als <c>… IN (Ids aus der kleinen
    /// Namenstabelle)</c>, damit SQLite die Zuordnungen über ihren Index liest (<c>IX_FileTags_Tag</c>,
    /// <c>IX_FileCategoryValues_Value</c>, <c>IX_FileCategoryValues_Category_Value_File</c>) statt alle Zuordnungen zu
    /// durchlaufen (vor 0.2.30 las der Tag-Zweig per Join alle <c>FileTags</c>-Zeilen, Plan <c>SCAN ft</c>). Seit 0.2.38
    /// auch Personennamen (bestätigte Gesichter, <see cref="PersonSearchUnion"/>). Ohne Suche einfach <c>Files f</c>.
    /// </summary>
    private static string BuildFilesSource(GalleryQuery query) => query.HasSearch
        ? query.MotifOnlySearchTerm is not null ? MotifOnlySource : """
          (SELECT rowid AS Id FROM FilesFts WHERE FilesFts MATCH $term
           UNION
           SELECT ft.FileId FROM FileTags ft
           WHERE ft.TagId IN (SELECT t.Id FROM Tags t WHERE t.Name LIKE $tagLike ESCAPE '\')
           UNION
           SELECT fcv.FileId FROM FileCategoryValues fcv
           WHERE fcv.ValueId IN (SELECT v.Id FROM CategoryValues v WHERE v.Name LIKE $tagLike ESCAPE '\')
           UNION
           SELECT fcn.FileId FROM FileCategoryValues fcn
           WHERE fcn.CategoryId IN (SELECT k.Id FROM Categories k WHERE k.Name LIKE $tagLike ESCAPE '\')
          """ + PersonSearchUnion + "\n" + MotifSearchUnion /* Motive (ADR-0052) */ + """
          ) hit
          CROSS JOIN Files f ON f.Id = hit.Id
          """
        : "Files f";

    /// <summary>Tag-Filter: ODER (mindestens eines), UND (alle gewählten) oder „ohne Tag“ (ADR-0021).</summary>
    private static string BuildTagClause(GalleryQuery query)
    {
        if (query.TagMode == GalleryTagMode.None)
        {
            return "AND NOT EXISTS (SELECT 1 FROM FileTags ft WHERE ft.FileId = f.Id)";
        }

        if (query.TagIds is not { Count: > 0 } tagIds)
        {
            return string.Empty;
        }

        var inList = string.Join(",", Enumerable.Range(0, tagIds.Count).Select(i => "$tag" + i));
        return query.TagMode == GalleryTagMode.All
            ? $"AND f.Id IN (SELECT ft.FileId FROM FileTags ft WHERE ft.TagId IN ({inList}) GROUP BY ft.FileId HAVING COUNT(DISTINCT ft.TagId) = $tagCount)"
            : $"AND f.Id IN (SELECT ft.FileId FROM FileTags ft WHERE ft.TagId IN ({inList}))";
    }

    /// <summary>
    /// Kategorie-Filter (ADR-0027): je Kategorie „Wert ∈ gewählte Werte“ ODER „kein Wert“ (falls gewählt); die
    /// Kategorien untereinander UND. Parameter $catC{i} (Kategorie) und $catV{i}_{j} (Werte).
    /// </summary>
    private static string BuildCategoryClause(GalleryQuery query)
    {
        var filters = query.ActiveCategoryFilters;
        if (filters.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            var parts = new List<string>(2);
            if (filter.ValueIds.Count > 0)
            {
                var inList = string.Join(",", Enumerable.Range(0, filter.ValueIds.Count).Select(j => $"$catV{i}_{j}"));
                parts.Add($"f.Id IN (SELECT cv.FileId FROM FileCategoryValues cv WHERE cv.CategoryId = $catC{i} AND cv.ValueId IN ({inList}))");
            }

            if (filter.IncludeNone)
            {
                parts.Add($"NOT EXISTS (SELECT 1 FROM FileCategoryValues cv WHERE cv.FileId = f.Id AND cv.CategoryId = $catC{i})");
            }

            sb.Append(" AND (").Append(string.Join(" OR ", parts)).Append(')');
        }

        return sb.ToString();
    }

    /// <summary>LIKE-Muster „%text%“ für die Tag-Namenssuche (Sonderzeichen von LIKE escaped).</summary>
    internal static string BuildTagLikePattern(string searchTerm) => "%" + EscapeLike(searchTerm.Trim()) + "%";

    /// <summary>Sortierspalte + Richtung; Id als stabile Zweitsortierung in derselben Richtung.</summary>
    private static string BuildOrderBy(GallerySort sort, bool reverse)
    {
        var (column, descending) = sort switch
        {
            GallerySort.ModifiedAsc => ("f.ModifiedAtUtc", false),
            GallerySort.NameAsc => ("f.Name COLLATE NOCASE", false),
            GallerySort.NameDesc => ("f.Name COLLATE NOCASE", true),
            GallerySort.SizeDesc => ("f.Size", true),
            GallerySort.SizeAsc => ("f.Size", false),
            GallerySort.CapturedDesc => (CaptureDateSortSql.SortKey, true), // ADR-0046, Ausdrucks-Index IX_Files_Library_Captured
            GallerySort.CapturedAsc => (CaptureDateSortSql.SortKey, false),
            _ => ("f.ModifiedAtUtc", true),
        };

        if (reverse)
        {
            descending = !descending;
        }

        var direction = descending ? "DESC" : "ASC";
        return $"{column} {direction}, f.Id {direction}";
    }

    private static void BindFilterParams(SqliteCommand cmd, GalleryQuery query, FolderClause folder)
    {
        cmd.Parameters.AddWithValue("$libId", query.LibraryId);
        BindFileFilterParams(cmd, query.Filter); // Datei-Filter (ADR-0041, GalleryPageSource.FileFilter.cs)
        if (query.MatchExpression is { } matchExpression)
        {
            cmd.Parameters.AddWithValue("$term", matchExpression);
            cmd.Parameters.AddWithValue("$tagLike", BuildTagLikePattern(query.SearchTerm!));
        }

        if (query.TagMode != GalleryTagMode.None && query.TagIds is { Count: > 0 } tagIds)
        {
            for (var i = 0; i < tagIds.Count; i++)
            {
                cmd.Parameters.AddWithValue("$tag" + i, tagIds[i]);
            }

            if (query.TagMode == GalleryTagMode.All)
            {
                cmd.Parameters.AddWithValue("$tagCount", tagIds.Distinct().Count());
            }
        }

        BindPersonParams(cmd, query); // Personen-Filter (0.2.38)
        BindMotifParams(cmd, query); // Motiv-Filter und „Motiv: …“ (ADR-0052)
        BindAlbumParams(cmd, query); // Filter „Album“ (ADR-0054)

        var categoryFilters = query.ActiveCategoryFilters;
        for (var i = 0; i < categoryFilters.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$catC{i}", categoryFilters[i].CategoryId);
            for (var j = 0; j < categoryFilters[i].ValueIds.Count; j++)
            {
                cmd.Parameters.AddWithValue($"$catV{i}_{j}", categoryFilters[i].ValueIds[j]);
            }
        }

        if (folder.FolderId is { } folderId)
        {
            if (folder.Path is null)
            {
                cmd.Parameters.AddWithValue("$folderId", folderId);
            }
            else
            {
                cmd.Parameters.AddWithValue("$folderPath", folder.Path);
                cmd.Parameters.AddWithValue("$folderPrefix", folder.Prefix!);
            }
        }
    }

    private static GalleryFileDto ReadRow(SqliteDataReader reader) => new(
        Id: reader.GetInt32(0),
        FolderId: reader.GetInt32(1),
        Name: reader.GetString(2),
        Extension: reader.GetString(3),
        Size: reader.GetInt64(4),
        ModifiedAtUnix: reader.GetInt64(5),
        MediaType: reader.GetInt32(6),
        IsFavorite: reader.GetInt32(7) == 1,
        Rating: reader.IsDBNull(8) ? null : (byte)reader.GetInt32(8),
        Missing: reader.GetInt32(9) == 1,
        FolderPath: reader.GetString(10),
        RootRole: reader.IsDBNull(11) ? null : reader.GetInt32(11),
        CaptureDate: ReadCaptureDate(reader)) // GalleryPageSource.CaptureDate.cs (ADR-0046)
    {
        Width = ReadOptionalInt(reader, "MediaWidth"),
        Height = ReadOptionalInt(reader, "MediaHeight"),
        LibraryId = ReadOptionalInt(reader, "FileLibraryId") ?? 0,
    };

    /// <summary>Optionale Spalte über ihren Namen (unabhängig von der Position in <see cref="FileColumns"/>).</summary>
    private static int? ReadOptionalInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    // ---- Ausführung -------------------------------------------------------------------------

    /// <summary>Serialisiert alle Zugriffe auf die eine Verbindung und hält sie vom UI-Thread fern.</summary>
    private async Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
    {
        var conn = EnsureOpen();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => work(conn), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection EnsureOpen()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("GalleryPageSource ist nicht geöffnet. Open() vor dem ersten Query aufrufen.");
        }

        return _connection;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        _gate.Dispose();
    }
}
