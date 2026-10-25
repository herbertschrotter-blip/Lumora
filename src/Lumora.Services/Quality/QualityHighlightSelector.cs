using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Duplicates;

namespace Lumora.Services.Quality;

/// <summary>
/// Highlight-Auswahl aus dem Katalog (ADR-0057): liest je Datei Güte (<c>FileQuality</c>, nur gültige Werte),
/// Fingerabdrücke (<c>FileFingerprints</c>: pHash und Prüfsumme, nur gültige), bestätigte Gesichter, Favorit, Bewertung
/// und Aufnahmezeit in Stücken zu <see cref="ChunkSize"/> Ids und überlässt die Auswahl <see cref="HighlightPlanner"/>.
/// Fehlende (<c>Files.Missing</c>) und unbekannte Ids fallen heraus. Die Serien-Schwelle ist die „ähnlich“-Schwelle der
/// Doppelten (Einstellung, Standard 10). Dateien ohne Güte-Wert (noch nicht bewertet) zählen neutral. Die gelernte Nutzung
/// (ADR-0072, <see cref="Usage.IUsageService"/>) kommt als kleiner Zuschlag dazu — nur wenn „Nutzung lernen“ an ist.
/// </summary>
public sealed class QualityHighlightSelector : IHighlightSelector
{
    /// <summary>Ids je Abfrage (eingebettete Ganzzahlen, kein Parameter-Limit).</summary>
    public const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IDuplicateSettingsStore? _duplicateSettings;
    private readonly Usage.IUsageService? _usage;
    private readonly ILogger _logger;

    public QualityHighlightSelector(
        ILibraryContextFactory factory,
        IDuplicateSettingsStore? duplicateSettings = null,
        ILogger<QualityHighlightSelector>? logger = null,
        Usage.IUsageService? usage = null)
    {
        _factory = factory;
        _duplicateSettings = duplicateSettings;
        _usage = usage;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public async Task<IReadOnlyList<HighlightPick>> SelectAsync(IReadOnlyList<int> fileIds, HighlightOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentNullException.ThrowIfNull(options);
        if (fileIds.Count == 0)
        {
            return [];
        }

        var threshold = await SimilarThresholdAsync(ct).ConfigureAwait(false);
        var candidates = await Task.Run(() => Load(fileIds, ct), ct).ConfigureAwait(false);
        candidates = await WithUsageAsync(candidates, ct).ConfigureAwait(false);
        return HighlightPlanner.Select(candidates, options, threshold);
    }

    public async Task<int?> PickCoverAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return null;
        }

        var candidates = await Task.Run(() => Load(fileIds, ct), ct).ConfigureAwait(false);
        candidates = await WithUsageAsync(candidates, ct).ConfigureAwait(false);
        return HighlightPlanner.PickCover(candidates);
    }

    /// <summary>Gelernte Nutzung je Kandidat (ADR-0072); ohne Dienst, ohne Nutzung oder bei Lesefehlern unverändert.</summary>
    private async Task<List<HighlightCandidate>> WithUsageAsync(List<HighlightCandidate> candidates, CancellationToken ct)
    {
        if (_usage is null || candidates.Count == 0)
        {
            return candidates;
        }

        try
        {
            var totals = await _usage.GetTotalsAsync(candidates.Select(c => c.FileId).ToList(), ct).ConfigureAwait(false);
            return totals.Count == 0
                ? candidates
                : candidates.Select(c => totals.TryGetValue(c.FileId, out var t) ? c with { Usage = t } : c).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Highlights: Nutzung nicht lesbar — ohne Zuschlag");
            return candidates;
        }
    }

    private async Task<int> SimilarThresholdAsync(CancellationToken ct)
    {
        if (_duplicateSettings is null)
        {
            return DuplicateSettings.DefaultSimilarThreshold;
        }

        try
        {
            return (await _duplicateSettings.LoadAsync(ct).ConfigureAwait(false)).SimilarThreshold;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Highlights: Schwelle der Doppelten nicht lesbar — Standard");
            return DuplicateSettings.DefaultSimilarThreshold;
        }
    }

    /// <summary>Kandidaten in Eingabe-Reihenfolge (doppelte Ids nur beim ersten Vorkommen).</summary>
    private List<HighlightCandidate> Load(IReadOnlyList<int> fileIds, CancellationToken ct)
    {
        var position = new Dictionary<int, int>(fileIds.Count);
        for (var i = 0; i < fileIds.Count; i++)
        {
            position.TryAdd(fileIds[i], i);
        }

        var found = new List<HighlightCandidate>(position.Count);
        using var connection = _factory.OpenSqliteConnection();
        foreach (var chunk in position.Keys.Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = Sql(string.Join(",", chunk.Select(id => id.ToString(CultureInfo.InvariantCulture))));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                found.Add(new HighlightCandidate(
                    id,
                    position[id],
                    reader.GetInt64(2),
                    reader.GetInt32(1) == 1,
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.GetInt32(11),
                    reader.IsDBNull(12) ? 0 : reader.GetDouble(12),
                    reader.GetInt64(3) != 0,
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    reader.IsDBNull(8) ? null : PerceptualHash.FromStored(reader.GetInt64(8)),
                    !reader.IsDBNull(9) && reader.GetInt64(9) != 0,
                    reader.IsDBNull(10) ? null : (byte[])reader.GetValue(10)));
            }
        }

        found.Sort((a, b) => a.Position.CompareTo(b.Position));
        return found;
    }

    private static string Sql(string idList)
    {
        var validImage = $"h.ImageHash IS NOT NULL AND h.ImageHashVersion = {PerceptualHash.Version.ToString(CultureInfo.InvariantCulture)} AND h.ImageModifiedAt = f.ModifiedAtUtc";
        var validQuality = FileQualityIndexService.ValidQuality("q", "f") + " AND q.ErrorCode IS NULL";
        return $"""
            SELECT f.Id, f.MediaType, COALESCE(f.CapturedAt, f.ModifiedAtUtc), f.IsFavorite, f.Rating,
                   CASE WHEN {validQuality} THEN q.Sharpness END,
                   CASE WHEN {validQuality} THEN q.Exposure END,
                   CASE WHEN {validQuality} THEN q.Aesthetic END,
                   CASE WHEN {validImage} THEN h.ImageHash END,
                   CASE WHEN {validImage} THEN h.ImageLowDetail END,
                   CASE WHEN {DuplicateSql.ValidContent("h", "f")} THEN h.ContentHash END,
                   (SELECT COUNT(*) FROM Faces x WHERE x.FileId = f.Id AND x.State = 2),
                   (SELECT AVG(x.Quality) FROM Faces x WHERE x.FileId = f.Id AND x.State = 2)
            FROM Files f
            LEFT JOIN FileQuality q ON q.FileId = f.Id
            LEFT JOIN FileFingerprints h ON h.FileId = f.Id
            WHERE f.Id IN ({idList}) AND f.Missing = 0;
            """;
    }
}
