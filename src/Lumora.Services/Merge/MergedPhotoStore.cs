using Lumora.Domain.Enums;
using Lumora.Services.Database;

namespace Lumora.Services.Merge;

/// <summary>Ergebnis-Marke einer Datei (Galerie „Panorama · aus 26 Fotos“, ADR-0078 Punkt 9/10).</summary>
/// <param name="SourceCount">Quellen beim Anlegen.</param>
/// <param name="PresentSources">Davon noch im Katalog (nicht „fehlt“).</param>
public sealed record MergedPhotoMark(int FileId, MergeMethod Method, PanoramaProjection? Projection, int SourceCount, int PresentSources);

/// <summary>Eine Quelle eines Ergebnisses in Positionsreihenfolge.</summary>
public sealed record MergedSourceEntry(int FileId, int Position, bool Missing);

/// <summary>Alles für die Lightbox-Zeile „Zusammengefügt aus 26 Fotos · Panorama, Kugel 360° · Quellen zeigen · Neu berechnen…“ und den Abschnitt „Zusammengefügt“ im Eigenschaften-Fenster.</summary>
public sealed record MergedPhotoInfo(
    int FileId,
    MergeMethod Method,
    PanoramaProjection? Projection,
    int SourceCount,
    IReadOnlyList<MergedSourceEntry> Sources,
    string Tools,
    string RecipeJson,
    DateTime CreatedAtUtc,
    int BatchId,
    int? SeriesId)
{
    /// <summary>Quellen, die nicht mehr in der Bibliothek sind (entfernt oder „fehlt“).</summary>
    public int MissingSources => Math.Max(0, SourceCount - Sources.Count(s => !s.Missing));
}

/// <summary>Liest zusammengefügte Ergebnisse und ihre Quellen (ADR-0078) für Marke, Lightbox und „Suchen ▸“.</summary>
public interface IMergedPhotoStore
{
    /// <summary>Marken der Dateien, die Ergebnisse sind (übrige fehlen im Ergebnis).</summary>
    Task<IReadOnlyDictionary<int, MergedPhotoMark>> GetMarksAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);

    /// <summary>Welche der Dateien Quelle mindestens eines Ergebnisses sind.</summary>
    Task<IReadOnlySet<int>> GetSourceFileIdsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);

    /// <summary>Details eines Ergebnisses oder null, wenn die Datei kein Ergebnis ist.</summary>
    Task<MergedPhotoInfo?> GetInfoAsync(int resultFileId, CancellationToken ct = default);

    /// <summary>„Suchen ▸ Quellen“: Quellen eines Ergebnisses in Positionsreihenfolge (nur noch vorhandene Katalogzeilen).</summary>
    Task<IReadOnlyList<int>> GetSourcesAsync(int resultFileId, CancellationToken ct = default);

    /// <summary>„Suchen ▸ Zusammengefügtes Ergebnis“: Ergebnisse, in die die Datei eingeflossen ist, neueste zuerst.</summary>
    Task<IReadOnlyList<int>> GetResultsOfSourceAsync(int sourceFileId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class MergedPhotoStore(ILibraryContextFactory factory) : IMergedPhotoStore
{
    public Task<IReadOnlyDictionary<int, MergedPhotoMark>> GetMarksAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
        => Task.Run<IReadOnlyDictionary<int, MergedPhotoMark>>(() =>
        {
            var result = new Dictionary<int, MergedPhotoMark>();
            if (fileIds.Count == 0)
            {
                return result;
            }

            using var connection = factory.OpenSqliteConnection();
            foreach (var chunk in fileIds.Distinct().Chunk(400))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT m.FileId, m.Method, m.Projection, m.SourceCount,
                           (SELECT COUNT(*) FROM MergedPhotoSources s JOIN Files f ON f.Id = s.SourceFileId WHERE s.ResultFileId = m.FileId AND f.Missing = 0)
                    FROM MergedPhotos m WHERE m.FileId IN ({string.Join(',', chunk)});
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    result[id] = new MergedPhotoMark(id, (MergeMethod)reader.GetInt32(1), reader.IsDBNull(2) ? null : (PanoramaProjection)reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
                }
            }

            return result;
        }, ct);

    public Task<IReadOnlySet<int>> GetSourceFileIdsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
        => Task.Run<IReadOnlySet<int>>(() =>
        {
            var result = new HashSet<int>();
            if (fileIds.Count == 0)
            {
                return result;
            }

            using var connection = factory.OpenSqliteConnection();
            foreach (var chunk in fileIds.Distinct().Chunk(400))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT DISTINCT SourceFileId FROM MergedPhotoSources WHERE SourceFileId IN ({string.Join(',', chunk)});";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetInt32(0));
                }
            }

            return result;
        }, ct);

    public Task<MergedPhotoInfo?> GetInfoAsync(int resultFileId, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Method, Projection, SourceCount, Tools, RecipeJson, CreatedAtUtc, BatchId, SeriesId FROM MergedPhotos WHERE FileId = $id;";
            cmd.Parameters.AddWithValue("$id", resultFileId);
            MergeMethod method;
            PanoramaProjection? projection;
            int count, batch;
            int? series;
            string tools, recipe;
            DateTime created;
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                method = (MergeMethod)reader.GetInt32(0);
                projection = reader.IsDBNull(1) ? null : (PanoramaProjection)reader.GetInt32(1);
                count = reader.GetInt32(2);
                tools = reader.GetString(3);
                recipe = reader.GetString(4);
                created = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).UtcDateTime;
                batch = reader.GetInt32(6);
                series = reader.IsDBNull(7) ? null : reader.GetInt32(7);
            }

            ct.ThrowIfCancellationRequested();
            var sources = new List<MergedSourceEntry>();
            using var src = connection.CreateCommand();
            src.CommandText = "SELECT s.SourceFileId, s.Position, f.Missing FROM MergedPhotoSources s JOIN Files f ON f.Id = s.SourceFileId WHERE s.ResultFileId = $id ORDER BY s.Position, s.SourceFileId;";
            src.Parameters.AddWithValue("$id", resultFileId);
            using (var reader = src.ExecuteReader())
            {
                while (reader.Read())
                {
                    sources.Add(new MergedSourceEntry(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2) != 0));
                }
            }

            return new MergedPhotoInfo(resultFileId, method, projection, count, sources, tools, recipe, created, batch, series);
        }, ct);

    public Task<IReadOnlyList<int>> GetSourcesAsync(int resultFileId, CancellationToken ct = default)
        => Query("SELECT SourceFileId FROM MergedPhotoSources WHERE ResultFileId = $id ORDER BY Position, SourceFileId;", resultFileId, ct);

    public Task<IReadOnlyList<int>> GetResultsOfSourceAsync(int sourceFileId, CancellationToken ct = default)
        => Query("SELECT s.ResultFileId FROM MergedPhotoSources s JOIN MergedPhotos m ON m.FileId = s.ResultFileId WHERE s.SourceFileId = $id ORDER BY m.CreatedAtUtc DESC, s.ResultFileId DESC;", sourceFileId, ct);

    private Task<IReadOnlyList<int>> Query(string sql, int id, CancellationToken ct)
        => Task.Run<IReadOnlyList<int>>(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            var list = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetInt32(0));
            }

            return list;
        }, ct);
}
