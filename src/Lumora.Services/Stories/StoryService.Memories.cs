using System.Globalization;
using Microsoft.Data.Sqlite;
using Lumora.Domain.Enums;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Places;

namespace Lumora.Services.Stories;

/// <summary>
/// Rückblicke (ADR-0058): werden je Tag dynamisch berechnet — „Heute vor N Jahren“ (derselbe Kalendertag, mindestens
/// <see cref="MinMemoryFiles"/> Fotos) und „dieser Monat in früheren Jahren“ (mindestens die Mindestgröße). Gespeichert
/// werden sie erst, wenn der Benutzer einen öffnet (<see cref="OpenMemoryAsync"/>, Signatur = Schlüssel) — dann tragen sie
/// Benutzer-Zustand wie jede Story. Jeder Tag hat andere Rückblicke; dauerhaft gespeicherte müssten täglich neu
/// geschrieben werden.
/// </summary>
public sealed partial class StoryService
{
    /// <summary>Mindestzahl Fotos/Videos eines „Heute vor N Jahren“.</summary>
    public const int MinMemoryFiles = 6;

    /// <summary>Ortszeit kann bis zu 14 h von UTC abweichen: so weit um den Tag herum lesen, dann nach Kalendertag filtern.</summary>
    private const long ZoneSlackSeconds = 14 * 3600;

    /// <summary>Höchstens so viele Kandidaten für das Titelbild eines Rückblicks.</summary>
    private const int MaxCoverCandidates = 400;

    /// <summary>Rückblicke eines Tages bleiben so lange zwischengespeichert (Änderungen verwerfen sie sofort).</summary>
    private static readonly TimeSpan MemoryCacheLifetime = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<StoryMemory>> GetMemoriesAsync(DateOnly? day = null, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (!scope.HasLibrary)
        {
            return [];
        }

        var today = day ?? _clock.Today;
        var zone = _clock.Zone;
        var cacheKey = scope.Key + "|" + today.DayNumber.ToString(CultureInfo.InvariantCulture);
        if (_memoryCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < MemoryCacheLifetime)
        {
            return cached.Memories;
        }

        var found = await ReadAsync(connection =>
        {
            var settings = StorySettingsStore.Load(connection);
            if (!settings.MemoriesEnabled)
            {
                return [];
            }

            var list = new List<(string Key, StoryMemoryKind Kind, int YearsAgo, DateOnly Date, List<StoryTimelineItem> Items, int? StoryId, bool Pinned)>();
            var earliest = StorySql.Scalar(connection, null, $"SELECT COALESCE(MIN({StorySql.TimeKey}), 0) FROM Files f WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND {StorySql.Eligible};");
            if (earliest == 0)
            {
                return list;
            }

            var firstYear = StoryDetector.DateOf(StorySql.LocalDay(earliest, null, zone)).Year - 1;
            for (var years = 1; today.Year - years >= firstYear; years++)
            {
                var date = today.AddYears(-years);
                var items = ReadDays(connection, scope.LibraryIds, zone, date, date);
                if (items.Count >= MinMemoryFiles)
                {
                    list.Add((DayKey(date), StoryMemoryKind.OnThisDay, years, date, items, null, false));
                }
            }

            var month = new List<StoryTimelineItem>();
            for (var year = today.Year - 1; year >= firstYear; year--)
            {
                var first = new DateOnly(year, today.Month, 1);
                month.AddRange(ReadDays(connection, scope.LibraryIds, zone, first, first.AddMonths(1).AddDays(-1)));
            }

            if (month.Count >= settings.MinFiles && month.Count > 0)
            {
                month.Sort(static (x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.FileId.CompareTo(y.FileId));
                list.Add((MonthKey(today), StoryMemoryKind.ThisMonth, 0, new DateOnly(today.Year, today.Month, 1), month, null, false));
            }

            // Schon gespeicherte (geöffnete/angeheftete) Rückblicke.
            for (var i = 0; i < list.Count; i++)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Id, IsPinned, IsHidden FROM Stories WHERE GroupId = $g AND Signature = $sig;";
                cmd.Parameters.AddWithValue("$g", scope.GroupId);
                cmd.Parameters.AddWithValue("$sig", list[i].Key);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetBoolean(2))
                    {
                        list.RemoveAt(i--); // ausgeblendet
                        continue;
                    }

                    list[i] = list[i] with { StoryId = reader.GetInt32(0), Pinned = reader.GetBoolean(1) };
                }
            }

            return list;
        }, ct).ConfigureAwait(false);

        var homeCountry = found.Count == 0 ? null : await ReadAsync(c => HomeCountry(c, scope), ct).ConfigureAwait(false);
        var forgotten = await ForgottenMemoryAsync(scope, today, ct).ConfigureAwait(false); // ADR-0072, StoryService.Treasures.cs
        var result = new List<StoryMemory>(found.Count);
        foreach (var m in found)
        {
            var ids = m.Items.Select(i => i.FileId).ToList();
            var cover = await _selector.PickCoverAsync(Sample(ids, MaxCoverCandidates), ct).ConfigureAwait(false);
            var videos = m.Items.Count(i => i.IsVideo);
            var places = StoryDetector.SignificantPlaces(m.Items, new StoryDetectionOptions());
            var years = m.Items.Select(i => StoryDetector.YearOf(i.Day)).ToList();
            result.Add(new StoryMemory(
                m.Key,
                m.Kind,
                m.YearsAgo,
                m.Date,
                m.Kind == StoryMemoryKind.OnThisDay ? StoryTitleBuilder.YearsAgo(m.YearsAgo) : StoryTitleBuilder.MonthInEarlierYears(m.Date.Month),
                m.Kind == StoryMemoryKind.OnThisDay ? StoryTitleBuilder.Day(m.Date) : StoryTitleBuilder.MonthYears(m.Date.Month, years.Min(), years.Max()),
                PlacesLine(StoryPlanWriter.PlaceList(places), homeCountry),
                StoryTitleBuilder.Counts(m.Items.Count, videos),
                m.Items.Count,
                videos,
                cover,
                Spread(ids, cover, 5),
                m.StoryId,
                m.Pinned));
        }

        if (forgotten is not null)
        {
            result.Add(forgotten);
        }

        _memoryCache[cacheKey] = (DateTime.UtcNow, result);
        return result;
    }

    /// <summary>Gleichmäßige Stichprobe (Titelbild eines Monats-Rückblicks mit Zehntausenden Fotos).</summary>
    private static List<int> Sample(List<int> ids, int max)
    {
        if (ids.Count <= max)
        {
            return ids;
        }

        var sample = new List<int>(max);
        for (var i = 0; i < max; i++)
        {
            sample.Add(ids[(int)((long)i * ids.Count / max)]);
        }

        return sample;
    }

    public async Task<int> OpenMemoryAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (!scope.HasLibrary)
        {
            throw new InvalidOperationException("Ohne Bibliothek gibt es keine Rückblicke.");
        }

        if (IsForgottenKey(key))
        {
            var forgottenId = await OpenForgottenAsync(key, scope, ct).ConfigureAwait(false); // ADR-0072
            await EnsureCoverAsync(forgottenId, ct).ConfigureAwait(false);
            Publish(scope.GroupId, StoriesChangeKind.Created, forgottenId);
            return forgottenId;
        }

        var zone = _clock.Zone;
        var today = _clock.Today;
        var items = await ReadAsync(c =>
        {
            List<StoryTimelineItem> items;
            if (TryParseDayKey(key, out var date) && date < today)
            {
                items = ReadDays(c, scope.GroupLibraryIds, zone, date, date);
            }
            else if (TryParseMonthKey(key, out var year, out var month))
            {
                items = [];
                for (var y = year - 1; y >= 1900 && y >= year - 200; y--)
                {
                    var first = new DateOnly(y, month, 1);
                    var part = ReadDays(c, scope.GroupLibraryIds, zone, first, first.AddMonths(1).AddDays(-1));
                    items.AddRange(part);
                    if (part.Count == 0 && StorySql.Scalar(c, null,
                            $"SELECT COUNT(*) FROM Files f WHERE f.LibraryId IN ({StorySql.IdList(scope.GroupLibraryIds)}) AND {StorySql.TimeKey} < $t;",
                            ("$t", (long)StoryDetector.DayOf(first) * 86400)) == 0)
                    {
                        break; // nichts Älteres mehr
                    }
                }
            }
            else
            {
                throw new ArgumentException($"Unbekannter Rückblick „{key}“.", nameof(key));
            }

            if (items.Count == 0)
            {
                throw new InvalidOperationException("Für diesen Rückblick gibt es keine Fotos.");
            }

            items.Sort(static (x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.FileId.CompareTo(y.FileId));
            return items;
        }, ct).ConfigureAwait(false);

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

        await EnsureCoverAsync(id, ct).ConfigureAwait(false);
        Publish(scope.GroupId, StoriesChangeKind.Created, id);
        return id;
    }

    /// <summary>Dateien der Kalendertage [<paramref name="first"/>, <paramref name="last"/>] (Ortszeit der Aufnahme).</summary>
    private static List<StoryTimelineItem> ReadDays(SqliteConnection connection, IReadOnlyCollection<int> libraryIds, TimeZoneInfo zone, DateOnly first, DateOnly last)
    {
        var firstDay = StoryDetector.DayOf(first);
        var lastDay = StoryDetector.DayOf(last);
        var from = (long)firstDay * 86400 - ZoneSlackSeconds;
        var to = ((long)lastDay + 1) * 86400 + ZoneSlackSeconds;
        return StorySql.ReadTimeline(connection, libraryIds, zone, from, to).Where(i => i.Day >= firstDay && i.Day <= lastDay).ToList();
    }

    private static string DayKey(DateOnly date) => "R:D:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string MonthKey(DateOnly today) => "R:M:" + today.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static bool TryParseDayKey(string key, out DateOnly date)
    {
        date = default;
        return key.StartsWith("R:D:", StringComparison.Ordinal)
               && DateOnly.TryParseExact(key[4..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryParseMonthKey(string key, out int year, out int month)
    {
        year = month = 0;
        if (!key.StartsWith("R:M:", StringComparison.Ordinal)
            || !DateOnly.TryParseExact(key[4..] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return false;
        }

        year = date.Year;
        month = date.Month;
        return true;
    }
}
