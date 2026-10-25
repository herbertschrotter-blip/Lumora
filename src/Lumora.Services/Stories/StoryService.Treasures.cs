using System.Globalization;
using Lumora.Domain.Enums;
using Lumora.Services.Library;
using Lumora.Services.Quality;

namespace Lumora.Services.Stories;

/// <summary>
/// Rückblick „Vergessene Schätze“ (ADR-0072): gute Fotos des Bereichs, die vor über einem Jahr aufgenommen und seit einem
/// Jahr nicht mehr angesehen wurden (Kandidaten aus den Nutzungs-Signalen, <see cref="Usage.IUsageService.FindForgottenAsync"/>),
/// davon die besten über <see cref="IHighlightSelector"/> (Serien nur einmal). Je Monat eine neue Auswahl (Schlüssel
/// „R:F:yyyy-MM“); wie jeder Rückblick erst beim Öffnen gespeichert — danach bleibt die geöffnete Auswahl, auch wenn ihre
/// Fotos dadurch „angesehen“ sind. Ohne Nutzungs-Signale, mit „Nutzung lernen“ oder „Vergessene Schätze“ aus: keiner.
/// </summary>
public sealed partial class StoryService
{
    /// <summary>So viele Fotos zeigt ein Rückblick „Vergessene Schätze“ höchstens.</summary>
    public const int ForgottenCount = 24;

    /// <summary>Stichprobe der Kandidaten, aus der die Auswahl die besten nimmt.</summary>
    private const int ForgottenCandidates = 2000;

    private const string ForgottenPrefix = "R:F:";

    /// <summary>So lange gilt die berechnete Auswahl eines Monats (Übersicht neu aufgebaut, Öffnen derselben Karte).</summary>
    private static readonly TimeSpan ForgottenCacheLifetime = TimeSpan.FromHours(1);

    /// <summary>Auswahl je Bereich und Monat: die Karte zeigt und das Öffnen speichert genau diese Fotos.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, List<StoryTimelineItem> Items)> _forgottenCache = new(StringComparer.Ordinal);

    private static string ForgottenKey(DateOnly today) => ForgottenPrefix + today.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    internal static bool IsForgottenKey(string key) => key.StartsWith(ForgottenPrefix, StringComparison.Ordinal);

    /// <summary>Fotos der „Vergessenen Schätze“ dieses Monats in Aufnahme-Reihenfolge; leer, wenn es (noch) keine gibt.</summary>
    private async Task<List<StoryTimelineItem>> ForgottenItemsAsync(LibraryScopeInfo scope, DateOnly today, CancellationToken ct)
    {
        if (_usage is null)
        {
            return [];
        }

        var cacheKey = scope.Key + "|" + ForgottenKey(today);
        if (_forgottenCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < ForgottenCacheLifetime)
        {
            return cached.Items;
        }

        var items = await ComputeForgottenItemsAsync(scope, today, ct).ConfigureAwait(false);
        _forgottenCache[cacheKey] = (DateTime.UtcNow, items);
        return items;
    }

    private async Task<List<StoryTimelineItem>> ComputeForgottenItemsAsync(LibraryScopeInfo scope, DateOnly today, CancellationToken ct)
    {
        var candidates = await _usage!.FindForgottenAsync(scope.LibraryIds, today, ForgottenCandidates, ct).ConfigureAwait(false);
        if (candidates.Count < MinMemoryFiles)
        {
            return [];
        }

        var picks = await _selector.SelectAsync(
            candidates,
            new HighlightOptions(Math.Min(1, (double)ForgottenCount / candidates.Count), MinMemoryFiles, ForgottenCount),
            ct).ConfigureAwait(false);
        if (picks.Count < MinMemoryFiles)
        {
            return [];
        }

        var zone = _clock.Zone;
        return await ReadAsync(c => StorySql.ReadByIds(c, scope.LibraryIds, picks.Select(p => p.FileId).ToList(), zone), ct).ConfigureAwait(false);
    }

    /// <summary>Karte „Vergessene Schätze“ für die Übersicht (Rückblicke von heute) oder null.</summary>
    private async Task<StoryMemory?> ForgottenMemoryAsync(LibraryScopeInfo scope, DateOnly today, CancellationToken ct)
    {
        var enabled = await ReadAsync(c => StorySettingsStore.Load(c).MemoriesEnabled, ct).ConfigureAwait(false);
        if (!enabled || _usage is null)
        {
            return null;
        }

        var usageSettings = await _usage.GetSettingsAsync(ct).ConfigureAwait(false);
        if (!usageSettings.Enabled || !usageSettings.ForgottenEnabled)
        {
            return null; // abgeschaltet: auch eine schon geöffnete nicht mehr unter den Rückblicken (sie bleibt als Story)
        }

        var key = ForgottenKey(today);
        var stored = await ReadAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Id, IsPinned, IsHidden FROM Stories WHERE GroupId = $g AND Signature = $sig;";
            cmd.Parameters.AddWithValue("$g", scope.GroupId);
            cmd.Parameters.AddWithValue("$sig", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (Id: reader.GetInt32(0), Pinned: reader.GetBoolean(1), Hidden: reader.GetBoolean(2)) : ((int Id, bool Pinned, bool Hidden)?)null;
        }, ct).ConfigureAwait(false);
        if (stored is { Hidden: true })
        {
            return null; // ausgeblendet
        }

        List<StoryTimelineItem> items;
        if (stored is { } existing)
        {
            // Schon geöffnet: die gespeicherte Auswahl zeigen (ihre Fotos gelten inzwischen als angesehen).
            var zone = _clock.Zone;
            items = await ReadAsync(c => StorySql.ReadByIds(c, scope.LibraryIds, StorySql.ReadStoryFileIds(c, existing.Id, scope.LibraryIds), zone), ct).ConfigureAwait(false);
        }
        else
        {
            var picked = await ForgottenItemsAsync(scope, today, ct).ConfigureAwait(false);
            var pickedIds = picked.Select(i => i.FileId).ToList();
            var zone = _clock.Zone;
            items = await ReadAsync(c => StorySql.ReadByIds(c, scope.LibraryIds, pickedIds, zone), ct).ConfigureAwait(false); // seither gelöschte fallen heraus
        }

        if (items.Count == 0 || (items.Count < MinMemoryFiles && stored is null))
        {
            return null;
        }

        var ids = items.Select(i => i.FileId).ToList();
        var cover = await _selector.PickCoverAsync(ids, ct).ConfigureAwait(false);
        var videos = items.Count(i => i.IsVideo);
        var years = items.Select(i => StoryDetector.YearOf(i.Day)).ToList();
        return new StoryMemory(
            key,
            StoryMemoryKind.ForgottenTreasures,
            0,
            new DateOnly(today.Year, today.Month, 1),
            ForgottenKicker,
            ForgottenTitle(items.Count),
            years.Count == 0 ? null : ForgottenSubtitle(years.Min(), years.Max()),
            StoryTitleBuilder.Counts(items.Count, videos),
            items.Count,
            videos,
            cover,
            Spread(ids, cover, 5),
            stored?.Id,
            stored?.Pinned ?? false);
    }

    /// <summary>Speichert die „Vergessenen Schätze“ beim Öffnen (eine schon gespeicherte bleibt unverändert).</summary>
    private async Task<int> OpenForgottenAsync(string key, LibraryScopeInfo scope, CancellationToken ct)
    {
        var existingId = await ReadAsync(c => StorySql.Scalar(c, null, "SELECT COALESCE(MAX(Id), 0) FROM Stories WHERE GroupId = $g AND Signature = $sig;", ("$g", scope.GroupId), ("$sig", key)), ct).ConfigureAwait(false);
        if (existingId > 0)
        {
            await MarkSeenAsync([(int)existingId], ct).ConfigureAwait(false);
            return (int)existingId;
        }

        // Dieselbe Auswahl wie die Karte (Zwischenspeicher je Monat); Schlüssel eines vergangenen Monats → dessen letzter Tag
        var today = _clock.Today;
        var month = DateOnly.TryParseExact(key[ForgottenPrefix.Length..] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            ? first
            : new DateOnly(today.Year, today.Month, 1);
        var date = month.Year == today.Year && month.Month == today.Month ? today : month.AddMonths(1).AddDays(-1);
        var zone = _clock.Zone;
        var picked = await ForgottenItemsAsync(scope, date, ct).ConfigureAwait(false);
        var ids = picked.Select(i => i.FileId).ToList();
        var items = await ReadAsync(c => StorySql.ReadByIds(c, scope.LibraryIds, ids, zone), ct).ConfigureAwait(false); // inzwischen gelöschte fallen heraus
        if (items.Count == 0)
        {
            throw new InvalidOperationException("Für „Vergessene Schätze“ gibt es gerade keine Fotos.");
        }

        var id = await WriteAsync((c, tx) =>
        {
            var detected = new DetectedStory
            {
                Kind = StoryKind.Memory,
                Signature = key,
                Items = items,
                PlaceIds = StoryDetector.SignificantPlaces(items, new StoryDetectionOptions()),
            };
            var existing = StorySql.ReadExisting(c, tx, scope.GroupId, $"s.Kind = {(int)StoryKind.Memory} AND s.Signature = '{key.Replace("'", "''", StringComparison.Ordinal)}'");
            var now = Now();
            StoryPlanWriter.Apply(c, tx, scope.GroupId, StoryReconciler.Reconcile(existing, [detected]), now);
            var storyId = (int)StorySql.Scalar(c, tx, "SELECT Id FROM Stories WHERE GroupId = $g AND Signature = $sig;", ("$g", scope.GroupId), ("$sig", key));
            StorySql.Execute(c, tx, "UPDATE Stories SET IsSeen = 1, SeenAtUtc = COALESCE(SeenAtUtc, $now) WHERE Id = $id;", ("$now", now), ("$id", storyId));
            return storyId;
        }, ct).ConfigureAwait(false);
        return id;
    }

    internal const string ForgottenKicker = "Vergessene Schätze";

    internal static string ForgottenTitle(int count)
        => count.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " schöne Fotos, lange nicht angesehen";

    internal static string ForgottenSubtitle(int fromYear, int toYear)
        => (fromYear == toYear ? fromYear.ToString(CultureInfo.InvariantCulture) : fromYear.ToString(CultureInfo.InvariantCulture) + "–" + toYear.ToString(CultureInfo.InvariantCulture))
           + StoryTitleBuilder.Dot + "seit über einem Jahr nicht geöffnet";
}
