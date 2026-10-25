using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Library;

namespace Lumora.Services.Motifs;

/// <summary>Eine Datei, die der Motiv-Index (neu) analysieren soll; Videos nur mit fertigem Vorschaubild.</summary>
public sealed record MotifCandidate(int FileId, int MediaType, string SourcePath, string? ThumbnailPath);

/// <summary>Ergebnis einer Datei: Bild-Vektor oder Fehlercode.</summary>
public sealed record MotifFileResult(int FileId, string ModelVersion, float[]? Vector, string? ErrorCode = null);

/// <summary>Ein Begriff zum Vergleichen: Text-Vektor und wirksame Schwelle.</summary>
public sealed record MotifTermVector(int TermId, float[] Vector, double Threshold);

/// <summary>Fehlercodes in <c>FileEmbeddings.ErrorCode</c>.</summary>
public static class MotifErrorCodes
{
    public const string DecodeFailed = "decode_failed";
    public const string AnalyzeFailed = "analyze_failed";
}

/// <summary>
/// Vergleich Bild-Vektor ↔ Begriffe (ADR-0052): Treffer = SigLIP-Wahrscheinlichkeit ≥ Schwelle des Begriffs.
/// </summary>
public static class MotifMatcher
{
    public static List<(int TermId, float Score)> Match(ReadOnlySpan<float> image, IReadOnlyList<MotifTermVector> terms, MotifScoring scoring)
    {
        var hits = new List<(int, float)>();
        foreach (var term in terms)
        {
            var p = scoring.Probability(MotifVectors.Dot(term.Vector, image));
            if (p >= term.Threshold)
            {
                hits.Add((term.TermId, p));
            }
        }

        return hits;
    }
}

/// <summary>
/// SQL der Motiv-Erkennung (ADR-0052) für Index, Begriffe und Suche. Alle Schreibvorgänge laufen nacheinander
/// (<see cref="_writeLock"/>, Single-Writer) in Transaktionen mit stiller Warte-Logik (ADR-0030); lange Neuberechnungen
/// in Abschnitten zu je 2 000 Dateien, damit andere Schreiber dazwischen drankommen. Bibliotheks-Trennung (ADR-0048,
/// seit 0.2.56): Begriffe gehören der Gruppe der Bibliothek (<c>MotifTerms.GroupId</c>), Dateien werden über
/// <c>Files.LibraryId</c> des Bereichs gelesen; eine Begriffsänderung rechnet die Treffer aller Bibliotheken der Gruppe neu.
/// Schreibwege legen eine fehlende Gruppe an (<c>LibraryGroupStore.EnsureGroup</c>), damit nie zwei Bibliotheken ohne
/// Gruppe dieselben Begriffe sehen. <c>MotifTerms.LibraryId</c> ist nur der Anker des Fremdschlüssels.
/// </summary>
public sealed class MotifStore
{
    private const int RecomputeChunk = 2000;
    private const int InChunk = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger _logger;
    private readonly SqliteBusyRetry _retry;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public MotifStore(ILibraryContextFactory factory, ILogger<MotifStore>? logger = null, SqliteBusyRetryOptions? retryOptions = null)
    {
        _factory = factory;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _retry = new SqliteBusyRetry(retryOptions, new WeakReferenceMessenger(), _logger);
    }

    // ---- Index -----------------------------------------------------------------------------------

    /// <summary>Dateien aller Bibliotheken der Gruppe <c>$grp</c> (Begriffe gelten für die ganze Gruppe).</summary>
    private const string GroupFilesCondition = "f.LibraryId IN (SELECT gl.Id FROM Libraries gl WHERE gl.GroupId = $grp)";

    private const string AnalyzableCondition =
        "f.LibraryId = $lib AND f.Missing = 0 AND (f.MediaType = 0 OR (f.MediaType = 1 AND EXISTS (SELECT 1 FROM Thumbnails tv WHERE tv.FileId = f.Id AND tv.Status = 2)))";

    /// <summary>Analysierte (aktuelle Version) und analysierbare Dateien der Bibliothek (Fotos, Videos mit Vorschaubild).</summary>
    public Task<(int Done, int Total)> CountAsync(int libraryId, string modelVersion, CancellationToken ct)
        => Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                var total = Scalar(connection, null, $"SELECT COUNT(*) FROM Files f WHERE {AnalyzableCondition};", ("$lib", libraryId));
                var done = Scalar(connection, null,
                    $"SELECT COUNT(*) FROM Files f WHERE {AnalyzableCondition} AND EXISTS (SELECT 1 FROM FileEmbeddings e WHERE e.FileId = f.Id AND e.ModelVersion = $ver);",
                    ("$lib", libraryId), ("$ver", modelVersion));
                return ((int)done, (int)total);
            },
            ct);

    /// <summary>Nächste offene Dateien (ohne Vektor der aktuellen Version), neueste zuerst, unterhalb von <paramref name="beforeId"/>.</summary>
    public Task<IReadOnlyList<MotifCandidate>> ReadPendingAsync(int libraryId, string modelVersion, int? beforeId, int limit, CancellationToken ct)
        => Task.Run<IReadOnlyList<MotifCandidate>>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT f.Id, f.MediaType, d.Path, f.Name,
                           (SELECT t.CachePath FROM Thumbnails t WHERE t.FileId = f.Id AND t.Status = 2 ORDER BY t.Size DESC LIMIT 1)
                    FROM Files f
                    JOIN Folders d ON d.Id = f.FolderId
                    LEFT JOIN FileEmbeddings e ON e.FileId = f.Id
                    WHERE {AnalyzableCondition} AND f.Id < $before
                      AND (e.FileId IS NULL OR e.ModelVersion <> $ver)
                    ORDER BY f.Id DESC
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$lib", libraryId);
                cmd.Parameters.AddWithValue("$ver", modelVersion);
                cmd.Parameters.AddWithValue("$before", beforeId ?? int.MaxValue);
                cmd.Parameters.AddWithValue("$limit", limit);
                var list = new List<MotifCandidate>(limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new MotifCandidate(
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        Path.Combine(reader.GetString(2), reader.GetString(3)),
                        reader.IsDBNull(4) ? null : reader.GetString(4)));
                }

                return list;
            },
            ct);

    /// <summary>
    /// Schreibt Vektoren und Treffer in einer Transaktion: Vektor setzen, alte Treffer der Datei löschen, neue gegen die
    /// Begriffe der Bibliothek (Text-Vektoren der Version <paramref name="textVersion"/>) einfügen. Dateien, die nicht
    /// mehr im Katalog stehen, fallen still weg. Liefert die geschriebenen FileIds.
    /// </summary>
    public Task<IReadOnlyList<int>> WriteAsync(int libraryId, IReadOnlyList<MotifFileResult> results, MotifScoring scoring, double defaultThreshold, string textVersion, CancellationToken ct)
        => Task.Run<IReadOnlyList<int>>(
            async () =>
            {
                if (results.Count == 0)
                {
                    return [];
                }

                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _retry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                    var terms = ReadTermVectors(connection, tx, LibraryGroupStore.EnsureGroup(connection, tx, libraryId), textVersion, defaultThreshold);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var written = new List<int>(results.Count);
                    foreach (var result in results)
                    {
                        if (Scalar(connection, tx, "SELECT COUNT(*) FROM Files WHERE Id = $id AND LibraryId = $lib;", ("$id", result.FileId), ("$lib", libraryId)) == 0)
                        {
                            continue;
                        }

                        Execute(connection, tx, """
                            INSERT INTO FileEmbeddings (FileId, ModelVersion, ErrorCode, CreatedAtUtc, Embedding)
                            VALUES ($file, $ver, $err, $now, $emb)
                            ON CONFLICT(FileId) DO UPDATE SET ModelVersion = excluded.ModelVersion, ErrorCode = excluded.ErrorCode,
                                CreatedAtUtc = excluded.CreatedAtUtc, Embedding = excluded.Embedding;
                            """,
                            ("$file", result.FileId), ("$ver", result.ModelVersion), ("$err", (object?)result.ErrorCode ?? DBNull.Value),
                            ("$now", now), ("$emb", result.Vector is null ? DBNull.Value : MotifVectors.EncodeHalf(result.Vector)));
                        Execute(connection, tx, "DELETE FROM FileMotifs WHERE FileId = $file;", ("$file", result.FileId));
                        if (result.Vector is { } vector)
                        {
                            InsertHits(connection, tx, result.FileId, MotifMatcher.Match(vector, terms, scoring));
                        }

                        written.Add(result.FileId);
                    }

                    tx.Commit();
                    return written;
                }
                finally
                {
                    _writeLock.Release();
                }
            },
            ct);

    /// <summary>„Neu analysieren“: Vektoren und Treffer der Bibliothek löschen; liefert die Zahl gelöschter Vektoren.</summary>
    public Task<int> ResetAsync(int libraryId, CancellationToken ct)
        => Task.Run(
            async () =>
            {
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _retry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                    Execute(connection, tx, "DELETE FROM FileMotifs WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib);", ("$lib", libraryId));
                    var count = Execute(connection, tx, "DELETE FROM FileEmbeddings WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib);", ("$lib", libraryId));
                    tx.Commit();
                    return count;
                }
                finally
                {
                    _writeLock.Release();
                }
            },
            ct);

    // ---- Begriffe -------------------------------------------------------------------------------

    /// <summary>Begriffe der Bibliothek (ihrer Gruppe) mit Anzahl Treffer nur in dieser Bibliothek.</summary>
    public Task<IReadOnlyList<MotifTermInfo>> GetTermsAsync(int libraryId, string textVersion, CancellationToken ct)
        => GetTermsAsync(libraryId, [libraryId], textVersion, ct);

    /// <summary>
    /// Begriffe der Gruppe von <paramref name="libraryId"/> mit Anzahl Treffer in den Bibliotheken des Bereichs
    /// (<paramref name="scopeLibraryIds"/>), in Anzeigereihenfolge. Deckt der Bereich die ganze Gruppe ab, zählt der Index
    /// <c>IX_FileMotifs_Term_File</c> allein; sonst nur Treffer auf Dateien des Bereichs (verknüpfte Bibliothek einzeln).
    /// </summary>
    public Task<IReadOnlyList<MotifTermInfo>> GetTermsAsync(int libraryId, IReadOnlyCollection<int> scopeLibraryIds, string textVersion, CancellationToken ct)
        => Task.Run<IReadOnlyList<MotifTermInfo>>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                var group = ReadGroup(connection, libraryId);
                if (group == 0)
                {
                    return [];
                }

                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT t.Id, t.LibraryId, t.Name, t.Prompt, t.Threshold,
                           (t.TextEmbedding IS NOT NULL AND t.TextModelVersion = $ver),
                           {HitCountSql(connection, group, scopeLibraryIds)}
                    FROM MotifTerms t
                    WHERE t.GroupId = $grp
                    ORDER BY t.SortOrder, t.Name COLLATE NOCASE, t.Id;
                    """;
                cmd.Parameters.AddWithValue("$grp", group);
                cmd.Parameters.AddWithValue("$ver", textVersion);
                var list = new List<MotifTermInfo>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new MotifTermInfo(
                        reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetDouble(4),
                        reader.GetInt32(6),
                        !reader.IsDBNull(5) && reader.GetInt64(5) == 1));
                }

                return list;
            },
            ct);

    /// <summary>
    /// Legt die Standard-Begriffe einmal je Bibliotheks-Gruppe an (Merker <c>motifs.seeded.g{GroupId}</c> in Settings —
    /// gelöschte Standard-Begriffe kommen nicht wieder, auch nicht über eine andere Bibliothek der Gruppe). Legt eine
    /// fehlende Gruppe an. Liefert die Zahl neu angelegter Begriffe.
    /// </summary>
    public Task<int> EnsureDefaultsAsync(int libraryId, CancellationToken ct)
        => Task.Run(
            async () =>
            {
                using (var probe = _factory.OpenSqliteConnection())
                {
                    var known = LibraryGroupStore.ReadGroupOf(probe, null, libraryId);
                    if (known is null)
                    {
                        return 0; // Bibliothek gibt es nicht
                    }

                    if (LibraryGroupStore.GroupExists(probe, null, known.Value)
                        && Scalar(probe, null, "SELECT COUNT(*) FROM Settings WHERE Key = $key;", ("$key", SeededKey(known.Value))) > 0)
                    {
                        return 0;
                    }
                }

                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _retry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                    var group = LibraryGroupStore.EnsureGroup(connection, tx, libraryId);
                    var key = SeededKey(group);
                    if (group == 0 || Scalar(connection, tx, "SELECT COUNT(*) FROM Settings WHERE Key = $key;", ("$key", key)) > 0)
                    {
                        tx.Commit(); // ggf. neu angelegte Gruppe behalten
                        return 0;
                    }

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var added = 0;
                    for (var i = 0; i < MotifDefaults.Terms.Count; i++)
                    {
                        var term = MotifDefaults.Terms[i];
                        added += Execute(connection, tx, """
                            INSERT OR IGNORE INTO MotifTerms (LibraryId, GroupId, Name, Prompt, Threshold, SortOrder, TextEmbedding, TextModelVersion, CreatedAtUtc)
                            VALUES ($lib, $grp, $name, $prompt, NULL, $sort, NULL, NULL, $now);
                            """,
                            ("$lib", libraryId), ("$grp", group), ("$name", term.Name), ("$prompt", term.Prompt), ("$sort", i), ("$now", now));
                    }

                    Execute(connection, tx, "INSERT INTO Settings (Key, Value) VALUES ($key, 'true');", ("$key", key));
                    tx.Commit();
                    return added;
                }
                finally
                {
                    _writeLock.Release();
                }
            },
            ct);

    /// <summary>Merker „Start-Liste angelegt“ der Gruppe.</summary>
    public static string SeededKey(int groupId) => LibraryGroupStore.MotifSeededKey(groupId);

    /// <summary>Neuer Begriff am Ende der Liste; wirft <see cref="MotifTermException"/>, wenn der Name im Bereich schon existiert.</summary>
    public Task<int> AddTermAsync(int libraryId, string name, string? prompt, CancellationToken ct)
        => WriteTermAsync(ct, (connection, tx) =>
        {
            var group = LibraryGroupStore.EnsureGroup(connection, tx, libraryId);
            if (group == 0)
            {
                throw new MotifTermException("Die Bibliothek gibt es nicht mehr.");
            }

            if (Scalar(connection, tx, "SELECT COUNT(*) FROM MotifTerms WHERE GroupId = $grp AND Name = $name;", ("$grp", group), ("$name", name)) > 0)
            {
                throw new MotifTermException($"Den Begriff „{name}“ gibt es schon.");
            }

            var sort = Scalar(connection, tx, "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM MotifTerms WHERE GroupId = $grp;", ("$grp", group));
            Execute(connection, tx, """
                INSERT INTO MotifTerms (LibraryId, GroupId, Name, Prompt, Threshold, SortOrder, TextEmbedding, TextModelVersion, CreatedAtUtc)
                VALUES ($lib, $grp, $name, $prompt, NULL, $sort, NULL, NULL, $now);
                """,
                ("$lib", libraryId), ("$grp", group), ("$name", name), ("$prompt", (object?)prompt ?? DBNull.Value), ("$sort", sort),
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            return (int)Scalar(connection, tx, "SELECT last_insert_rowid();");
        });

    /// <summary>
    /// Name/Beschreibung ändern: Text-Vektor verfällt (wird neu gerechnet). Gleicher Name in anderer Schreibweise ist
    /// erlaubt. Liefert die Anker-Bibliothek des Begriffs (eine der Gruppe) bzw. -1.
    /// </summary>
    public Task<int> UpdateTermTextAsync(int termId, string name, string? prompt, CancellationToken ct)
        => WriteTermAsync(ct, (connection, tx) =>
        {
            var libraryId = (int)Scalar(connection, tx, "SELECT COALESCE((SELECT LibraryId FROM MotifTerms WHERE Id = $id), -1);", ("$id", termId));
            if (libraryId < 0)
            {
                throw new MotifTermException("Den Begriff gibt es nicht mehr.");
            }

            var group = Scalar(connection, tx, "SELECT GroupId FROM MotifTerms WHERE Id = $id;", ("$id", termId));
            if (Scalar(connection, tx, "SELECT COUNT(*) FROM MotifTerms WHERE GroupId = $grp AND Name = $name AND Id <> $id;", ("$grp", group), ("$name", name), ("$id", termId)) > 0)
            {
                throw new MotifTermException($"Den Begriff „{name}“ gibt es schon.");
            }

            Execute(connection, tx, "UPDATE MotifTerms SET Name = $name, Prompt = $prompt, TextEmbedding = NULL, TextModelVersion = NULL WHERE Id = $id;",
                ("$name", name), ("$prompt", (object?)prompt ?? DBNull.Value), ("$id", termId));
            return libraryId;
        });

    public Task<int> SetThresholdAsync(int termId, double? threshold, CancellationToken ct)
        => WriteTermAsync(ct, (connection, tx) =>
        {
            var libraryId = (int)Scalar(connection, tx, "SELECT COALESCE((SELECT LibraryId FROM MotifTerms WHERE Id = $id), -1);", ("$id", termId));
            Execute(connection, tx, "UPDATE MotifTerms SET Threshold = $t WHERE Id = $id;", ("$t", (object?)threshold ?? DBNull.Value), ("$id", termId));
            return libraryId;
        });

    /// <summary>Löscht den Begriff samt Treffern (Kaskade); liefert die Bibliothek oder -1.</summary>
    public Task<int> DeleteTermAsync(int termId, CancellationToken ct)
        => WriteTermAsync(ct, (connection, tx) =>
        {
            var libraryId = (int)Scalar(connection, tx, "SELECT COALESCE((SELECT LibraryId FROM MotifTerms WHERE Id = $id), -1);", ("$id", termId));
            Execute(connection, tx, "DELETE FROM MotifTerms WHERE Id = $id;", ("$id", termId));
            return libraryId;
        });

    /// <summary>Begriffe der Gruppe ohne Text-Vektor der Version (Id, Name, Beschreibung).</summary>
    public Task<IReadOnlyList<(int Id, string Name, string? Prompt)>> GetTermsWithoutVectorAsync(int libraryId, string textVersion, CancellationToken ct)
        => Task.Run<IReadOnlyList<(int, string, string?)>>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                var group = ReadGroup(connection, libraryId);
                if (group == 0)
                {
                    return [];
                }

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Id, Name, Prompt FROM MotifTerms WHERE GroupId = $grp AND (TextEmbedding IS NULL OR TextModelVersion IS NULL OR TextModelVersion <> $ver) ORDER BY Id;";
                cmd.Parameters.AddWithValue("$grp", group);
                cmd.Parameters.AddWithValue("$ver", textVersion);
                var list = new List<(int, string, string?)>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }

                return list;
            },
            ct);

    /// <summary>Text-Vektoren speichern — nur, wenn Name und Beschreibung inzwischen nicht geändert wurden.</summary>
    public Task SetTextVectorsAsync(IReadOnlyList<(int Id, string Name, string? Prompt, float[] Vector)> vectors, string textVersion, CancellationToken ct)
        => WriteTermAsync(ct, (connection, tx) =>
        {
            foreach (var (id, name, prompt, vector) in vectors)
            {
                Execute(connection, tx, """
                    UPDATE MotifTerms SET TextEmbedding = $emb, TextModelVersion = $ver
                    WHERE Id = $id AND Name = $name COLLATE BINARY AND COALESCE(Prompt, '') = $prompt;
                    """,
                    ("$emb", MotifVectors.Encode(vector)), ("$ver", textVersion), ("$id", id), ("$name", name), ("$prompt", prompt ?? string.Empty));
            }

            return 0;
        });

    /// <summary>
    /// Treffer neu berechnen (Begriffe geändert, Schwelle geändert): je Abschnitt von 2 000 Dateien eine Transaktion —
    /// alte Treffer der betroffenen Begriffe löschen, neue aus den gespeicherten Bild-Vektoren einfügen. Die Bild-Vektoren
    /// bleiben unverändert. Begriffe gelten für die ganze Gruppe — gerechnet werden die Dateien ALLER Bibliotheken der
    /// Gruppe von <paramref name="libraryId"/>. <paramref name="termIds"/> null = alle Begriffe der Gruppe.
    /// </summary>
    public async Task<int> RecomputeAsync(int libraryId, IReadOnlyCollection<int>? termIds, MotifScoring scoring, double defaultThreshold, string modelVersion, CancellationToken ct)
    {
        var after = 0;
        var files = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = await Task.Run(
                async () =>
                {
                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        using var connection = _factory.OpenSqliteConnection();
                        using var tx = await _retry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                        var group = LibraryGroupStore.EnsureGroup(connection, tx, libraryId);
                        var terms = ReadTermVectors(connection, tx, group, modelVersion, defaultThreshold)
                            .Where(t => termIds is null || termIds.Contains(t.TermId)).ToList();
                        var rows = new List<(int FileId, float[] Vector)>(RecomputeChunk);
                        using (var cmd = connection.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = $"""
                                SELECT e.FileId, e.Embedding FROM FileEmbeddings e JOIN Files f ON f.Id = e.FileId
                                WHERE {GroupFilesCondition} AND e.FileId > $after AND e.ModelVersion = $ver AND e.Embedding IS NOT NULL
                                ORDER BY e.FileId LIMIT $limit;
                                """;
                            cmd.Parameters.AddWithValue("$grp", group);
                            cmd.Parameters.AddWithValue("$after", after);
                            cmd.Parameters.AddWithValue("$ver", modelVersion);
                            cmd.Parameters.AddWithValue("$limit", RecomputeChunk);
                            using var reader = cmd.ExecuteReader();
                            while (reader.Read())
                            {
                                rows.Add((reader.GetInt32(0), MotifVectors.DecodeHalf((byte[])reader[1])));
                            }
                        }

                        if (rows.Count == 0)
                        {
                            return rows;
                        }

                        var termFilter = termIds is null ? string.Empty : " AND TermId IN (" + string.Join(",", termIds.Select(i => i.ToString(CultureInfo.InvariantCulture))) + ")";
                        Execute(connection, tx, $"DELETE FROM FileMotifs WHERE FileId IN (SELECT e.FileId FROM FileEmbeddings e JOIN Files f ON f.Id = e.FileId WHERE {GroupFilesCondition} AND e.FileId > $after AND e.FileId <= $last){termFilter};",
                            ("$grp", group), ("$after", after), ("$last", rows[^1].FileId));
                        foreach (var (fileId, vector) in rows)
                        {
                            InsertHits(connection, tx, fileId, MotifMatcher.Match(vector, terms, scoring));
                        }

                        tx.Commit();
                        return rows;
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                },
                ct).ConfigureAwait(false);
            if (chunk.Count == 0)
            {
                break;
            }

            files += chunk.Count;
            after = chunk[^1].FileId;
        }

        return files;
    }

    // ---- Lesen für Oberfläche und Suche ----------------------------------------------------------

    /// <summary>Treffer der Dateien (höchste Wahrscheinlichkeit zuerst).</summary>
    public Task<IReadOnlyList<FileMotifInfo>> GetMotifsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => Task.Run<IReadOnlyList<FileMotifInfo>>(
            () =>
            {
                var result = new List<FileMotifInfo>();
                using var connection = _factory.OpenSqliteConnection();
                foreach (var chunk in fileIds.Distinct().Chunk(InChunk))
                {
                    using var cmd = connection.CreateCommand();
                    var sb = new StringBuilder();
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        sb.Append(i == 0 ? string.Empty : ",").Append("$f").Append(i);
                        cmd.Parameters.AddWithValue("$f" + i, chunk[i]);
                    }

                    cmd.CommandText = $"SELECT m.FileId, m.TermId, t.Name, m.Score FROM FileMotifs m JOIN MotifTerms t ON t.Id = m.TermId WHERE m.FileId IN ({sb}) ORDER BY m.FileId, m.Score DESC;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        result.Add(new FileMotifInfo(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetFloat(3)));
                    }
                }

                return result;
            },
            ct);

    /// <summary>Freitext-Bildsuche: die <paramref name="limit"/> ähnlichsten Bild-Vektoren der Bibliothek (Kosinus).</summary>
    public Task<IReadOnlyList<MotifSearchHit>> SearchAsync(int libraryId, float[] query, string modelVersion, int limit, CancellationToken ct)
        => SearchAsync([libraryId], query, modelVersion, limit, ct);

    /// <summary>Wie oben über alle Bibliotheken des Bereichs (Ansicht „Alle verknüpften“).</summary>
    public Task<IReadOnlyList<MotifSearchHit>> SearchAsync(IReadOnlyCollection<int> libraryIds, float[] query, string modelVersion, int limit, CancellationToken ct)
        => Task.Run<IReadOnlyList<MotifSearchHit>>(
            () =>
            {
                var best = new PriorityQueue<int, float>();
                if (libraryIds.Count == 0)
                {
                    return [];
                }

                using var connection = _factory.OpenSqliteConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT e.FileId, e.Embedding FROM FileEmbeddings e JOIN Files f ON f.Id = e.FileId
                    WHERE f.LibraryId IN ({IdList(libraryIds)}) AND e.ModelVersion = $ver AND e.Embedding IS NOT NULL;
                    """;
                cmd.Parameters.AddWithValue("$ver", modelVersion);
                using var reader = cmd.ExecuteReader();
                var scores = new Dictionary<int, float>();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var fileId = reader.GetInt32(0);
                    var score = MotifVectors.Dot(query, MotifVectors.DecodeHalf((byte[])reader[1]));
                    if (best.Count < limit)
                    {
                        best.Enqueue(fileId, score);
                        scores[fileId] = score;
                    }
                    else if (best.TryPeek(out _, out var lowest) && score > lowest)
                    {
                        scores.Remove(best.Dequeue());
                        best.Enqueue(fileId, score);
                        scores[fileId] = score;
                    }
                }

                return scores.Select(kv => new MotifSearchHit(kv.Key, kv.Value)).OrderByDescending(h => h.Score).ThenBy(h => h.FileId).ToList();
            },
            ct);

    /// <summary>ADR-0068: Bild-Vektor einer Datei (für „Ähnliche Fotos“) mit seiner Modell-Version; null ohne Vektor.</summary>
    public Task<(string ModelVersion, float[] Vector)?> GetImageVectorAsync(int fileId, CancellationToken ct)
        => Task.Run<(string, float[])?>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT ModelVersion, Embedding FROM FileEmbeddings WHERE FileId = $f AND Embedding IS NOT NULL;";
                cmd.Parameters.AddWithValue("$f", fileId);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? (reader.GetString(0), MotifVectors.DecodeHalf((byte[])reader[1])) : null;
            },
            ct);

    // ---- Hilfen ----------------------------------------------------------------------------------

    private async Task<int> WriteTermAsync(CancellationToken ct, Func<SqliteConnection, SqliteTransaction, int> work)
        => await Task.Run(
            async () =>
            {
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _retry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                    var result = work(connection, tx);
                    tx.Commit();
                    return result;
                }
                finally
                {
                    _writeLock.Release();
                }
            },
            ct).ConfigureAwait(false);

    /// <summary>Gruppe der Bibliothek ohne Schreiben (0 = keine bzw. Bibliothek fehlt — dann gibt es keine Begriffe).</summary>
    private static int ReadGroup(SqliteConnection connection, int libraryId)
        => LibraryGroupStore.ReadGroupOf(connection, null, libraryId) is { } g && g != 0 && LibraryGroupStore.GroupExists(connection, null, g) ? g : 0;

    /// <summary>
    /// Zähl-Ausdruck „Treffer des Begriffs t im Bereich“: deckt der Bereich alle Bibliotheken der Gruppe ab, liegen alle
    /// Treffer des Begriffs im Bereich (Treffer entstehen nur für Dateien der Gruppe) → nur der Index; sonst über Files.
    /// </summary>
    private static string HitCountSql(SqliteConnection connection, int group, IReadOnlyCollection<int> scopeLibraryIds)
    {
        var groupLibraries = LibraryGroupStore.ReadGroupLibraries(connection, null, group);
        return groupLibraries.All(scopeLibraryIds.Contains)
            ? "(SELECT COUNT(*) FROM FileMotifs m WHERE m.TermId = t.Id)"
            : $"(SELECT COUNT(*) FROM FileMotifs m JOIN Files f ON f.Id = m.FileId WHERE m.TermId = t.Id AND f.LibraryId IN ({IdList(scopeLibraryIds)}))";
    }

    /// <summary>Ganzzahlige Ids als SQL-Liste (nur Katalog-Ids, daher sicher einzubetten); leer → -1 (trifft nichts).</summary>
    private static string IdList(IEnumerable<int> ids)
    {
        var list = string.Join(",", ids.Distinct().Select(i => i.ToString(CultureInfo.InvariantCulture)));
        return list.Length == 0 ? "-1" : list;
    }

    private static List<MotifTermVector> ReadTermVectors(SqliteConnection connection, SqliteTransaction? tx, int groupId, string textVersion, double defaultThreshold)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id, TextEmbedding, Threshold FROM MotifTerms WHERE GroupId = $grp AND TextEmbedding IS NOT NULL AND TextModelVersion = $ver;";
        cmd.Parameters.AddWithValue("$grp", groupId);
        cmd.Parameters.AddWithValue("$ver", textVersion);
        var list = new List<MotifTermVector>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MotifTermVector(reader.GetInt32(0), MotifVectors.Decode((byte[])reader[1]), reader.IsDBNull(2) ? defaultThreshold : reader.GetDouble(2)));
        }

        return list;
    }

    private static void InsertHits(SqliteConnection connection, SqliteTransaction tx, int fileId, List<(int TermId, float Score)> hits)
    {
        foreach (var (termId, score) in hits)
        {
            Execute(connection, tx, "INSERT OR REPLACE INTO FileMotifs (FileId, TermId, Score) VALUES ($file, $term, $score);",
                ("$file", fileId), ("$term", termId), ("$score", score));
        }
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd.ExecuteNonQuery();
    }
}
