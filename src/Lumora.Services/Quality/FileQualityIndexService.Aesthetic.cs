using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lumora.Services.Quality;

/// <summary>
/// Bildwirkung-Phase (ADR-0057): für bewertete Dateien mit einem Motiv-Vektor des aktuellen Modells
/// (<c>FileEmbeddings</c>, ADR-0052) die Bildwirkung aus den gecachten Text-Vektoren — kein Bild wird dafür geladen, kein
/// Bildmodell läuft (nur einmal je Modell das Sprachmodell für die Text-Vektoren, und nur wenn es überhaupt etwas zu
/// rechnen gibt). Ohne Modell bzw. ohne Vektor bleibt sie NULL.
/// </summary>
public sealed partial class FileQualityIndexService
{
    private readonly record struct AestheticRow(int FileId, int LibraryId, byte[] Embedding);

    private async Task<int> RunAestheticPhaseAsync(string scope, HashSet<int> touched, CancellationToken token)
    {
        if (_aesthetics is null)
        {
            return 0;
        }

        var embeddingModel = _aesthetics.EmbeddingModelVersion;
        var version = AestheticPrompts.VersionFor(embeddingModel);
        using var reader = _factory.OpenSqliteConnection();
        var total = await Task.Run(() => CountAesthetic(reader, scope, version, embeddingModel), token).ConfigureAwait(false);
        if (total == 0)
        {
            return 0;
        }

        if (await _aesthetics.GetModelAsync(token).ConfigureAwait(false) is not { } model || model.Version != version)
        {
            return 0;
        }

        var cursor = 0;
        var done = 0;
        PublishStatus(new FileQualityIndexStatus(true, FileQualityIndexStatus.AestheticPhase, 0, total), force: true);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await WaitWhileSuspendedAsync(token).ConfigureAwait(false);
            using var turn = await TurnAsync(token).ConfigureAwait(false); // Pause und „Alle pausieren“ gelten auch hier
            var batch = await Task.Run(() => LoadAestheticBatch(reader, scope, version, embeddingModel, cursor, AestheticBatchSize), token).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            if (_planner is null && AnyBusy(batch.Select(r => r.LibraryId)))
            {
                await Task.Delay(BusyDelay, token).ConfigureAwait(false);
                continue;
            }

            var scores = batch.Select(r => (r.FileId, r.LibraryId, Score: model.DecodeImage(r.Embedding) is { } v ? model.Score(v) : (double?)null)).ToList();
            cursor = batch[^1].FileId;
            var written = await WriteAestheticAsync(scores, version, token).ConfigureAwait(false);
            done += written.Count;
            foreach (var libraryId in written.Distinct())
            {
                touched.Add(libraryId);
            }

            PublishStatus(new FileQualityIndexStatus(true, FileQualityIndexStatus.AestheticPhase, Math.Min(total, done), total));
        }

        return done;
    }

    private static string AestheticPending(string scope)
        => $"""
            FROM Files f
            JOIN FileQuality q ON q.FileId = f.Id
            JOIN FileEmbeddings e ON e.FileId = f.Id
            WHERE {scope} AND f.Missing = 0 AND {ValidQuality("q", "f")} AND q.ErrorCode IS NULL
              AND q.AestheticVersion IS NOT $av AND e.ModelVersion = $mv AND e.Embedding IS NOT NULL
            """;

    private static int CountAesthetic(SqliteConnection connection, string scope, string version, string embeddingModel)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) " + AestheticPending(scope) + ";";
        cmd.Parameters.AddWithValue("$av", version);
        cmd.Parameters.AddWithValue("$mv", embeddingModel);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<AestheticRow> LoadAestheticBatch(SqliteConnection connection, string scope, string version, string embeddingModel, int afterFileId, int take)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT f.Id, f.LibraryId, e.Embedding " + AestheticPending(scope) + " AND f.Id > $after ORDER BY f.Id LIMIT $take;";
        cmd.Parameters.AddWithValue("$av", version);
        cmd.Parameters.AddWithValue("$mv", embeddingModel);
        cmd.Parameters.AddWithValue("$after", afterFileId);
        cmd.Parameters.AddWithValue("$take", take);
        var rows = new List<AestheticRow>(take);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AestheticRow(reader.GetInt32(0), reader.GetInt32(1), (byte[])reader.GetValue(2)));
        }

        return rows;
    }

    /// <summary>Schreibt die Bildwirkung; liefert die Bibliothek je geschriebener Zeile.</summary>
    private async Task<List<int>> WriteAestheticAsync(IReadOnlyList<(int FileId, int LibraryId, double? Score)> scores, string version, CancellationToken token)
    {
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, token).ConfigureAwait(false);
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE FileQuality SET Aesthetic = $a, AestheticVersion = $av WHERE FileId = $id AND Version = $ver;";
                    var id = cmd.Parameters.Add("$id", SqliteType.Integer);
                    var a = cmd.Parameters.Add("$a", SqliteType.Real);
                    cmd.Parameters.AddWithValue("$av", version);
                    cmd.Parameters.AddWithValue("$ver", ImageQuality.Version);
                    var written = new List<int>(scores.Count);
                    foreach (var s in scores)
                    {
                        id.Value = s.FileId;
                        a.Value = s.Score is { } value ? value : DBNull.Value;
                        if (cmd.ExecuteNonQuery() > 0)
                        {
                            written.Add(s.LibraryId);
                        }
                    }

                    tx.Commit();
                    return written;
                },
                token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
