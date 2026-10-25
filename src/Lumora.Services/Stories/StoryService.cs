using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Albums;
using Lumora.Services.Database;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Places;
using Lumora.Services.Quality;

namespace Lumora.Services.Stories;

/// <summary>
/// Storys über Raw-SQL (ADR-0058). Lesen ohne Transaktion (WAL), Schreiben mit Warten auf die Schreibsperre
/// (<see cref="SqliteBusyRetry"/>). Bereich und Gruppe kommen frisch aus <see cref="ILibraryScope"/> (ADR-0048): Storys
/// der Gruppe, Dateien und Zähler des Bereichs (verknüpfte Gruppe, einzeln geöffnet: Zähler nur dieser Bibliothek).
/// Ortsnamen über <see cref="IPlaceLookup"/> (GeoNames, ADR-0050), Highlights/Titelbilder über
/// <see cref="IHighlightSelector"/>.
/// </summary>
public sealed partial class StoryService : IStoryService, IDisposable
{
    private const int ChunkSize = 500;
    private const int NewListLimit = 12;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<StoryService> _logger;
    private readonly IMessenger _messenger;
    private readonly ILibraryScope _scope;
    private readonly IHighlightSelector _selector;
    private readonly IPlaceLookup? _places;
    private readonly IAlbumService? _albums;
    private readonly IStoryIndexService? _index;
    private readonly StoryClock _clock;
    private readonly StoryHomeCache _homes;
    private readonly Usage.IUsageService? _usage; // „Vergessene Schätze“ (ADR-0072, StoryService.Treasures.cs)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, IReadOnlyList<StoryMemory> Memories)> _memoryCache = new(StringComparer.Ordinal);

    public StoryService(
        ILibraryContextFactory factory,
        ILogger<StoryService> logger,
        IHighlightSelector selector,
        IMessenger? messenger = null,
        ILibraryScope? scope = null,
        IPlaceLookup? places = null,
        IAlbumService? albums = null,
        IStoryIndexService? index = null,
        StoryClock? clock = null,
        StoryHomeCache? homes = null,
        Usage.IUsageService? usage = null)
    {
        _usage = usage;
        _factory = factory;
        _logger = logger;
        _selector = selector;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _scope = scope ?? new LibraryScope(factory);
        _places = places;
        _albums = albums;
        _index = index;
        _clock = clock ?? new StoryClock();
        _homes = homes ?? new StoryHomeCache();

        // Rückblicke je Tag zwischenspeichern; jede Änderung an Storys, Dateien, Aufnahmedaten oder Gruppen verwirft sie.
        _messenger.Register<StoryService, StoriesChangedMessage>(this, static (r, _) => r._memoryCache.Clear());
        _messenger.Register<StoryService, FilesChangedMessage>(this, static (r, _) => r._memoryCache.Clear());
        _messenger.Register<StoryService, CaptureDatesChangedMessage>(this, static (r, _) => r._memoryCache.Clear());
        _messenger.Register<StoryService, StorySettingsChangedMessage>(this, static (r, _) => r._memoryCache.Clear());
        _messenger.Register<StoryService, LibraryGroupsChangedMessage>(this, static (r, _) => r._memoryCache.Clear());
        _messenger.Register<StoryService, UsageChangedMessage>(this, static (r, _) =>
        {
            r._memoryCache.Clear();
            r._forgottenCache.Clear(); // ADR-0072: Lernen/„Vergessene Schätze“ umgeschaltet oder gelöscht
        });
    }

    public void Dispose() => _messenger.UnregisterAll(this);

    // ---- Lesen -----------------------------------------------------------------------------------------------------

    public async Task<StoryOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (!scope.HasLibrary)
        {
            return StoryOverview.Empty;
        }

        var (cards, settings, totalFiles) = await ReadAsync(connection =>
        {
            var s = StorySettingsStore.Load(connection);
            var all = ReadCards(connection, scope, s, where: null);
            // Nur der Index (LibraryId, MediaType) — Größe der Bibliothek an Fotos und Videos.
            var total = (int)StorySql.Scalar(connection, null,
                $"SELECT COUNT(*) FROM Files f WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.MediaType IN (0, 1);");
            return (all, s, total);
        }, ct).ConfigureAwait(false);

        var memories = settings.MemoriesEnabled ? await GetMemoriesAsync(null, ct).ConfigureAwait(false) : [];
        var visible = cards.Where(c => !c.IsHidden).ToList();
        var events = visible.Where(c => c.Kind is StoryKind.Event or StoryKind.Trip).OrderByDescending(c => c.StartAt).ToList();
        var unseen = visible.Where(c => !c.IsSeen && c.Kind != StoryKind.Memory).OrderByDescending(c => c.StartAt).ToList();
        return new StoryOverview(
            memories,
            visible.Where(c => c.IsPinned).OrderByDescending(c => c.StartAt).ToList(),
            unseen.Take(NewListLimit).ToList(),
            unseen.Count,
            events,
            events.GroupBy(c => c.Year).OrderByDescending(g => g.Key).Select(g => new StoryYearCount(g.Key, g.Count())).ToList(),
            visible.Where(c => c.Kind == StoryKind.Person).OrderByDescending(c => c.LastDay).ThenBy(c => c.Title, StringComparer.CurrentCulture).ToList(),
            visible.Where(c => c.Kind == StoryKind.Place).OrderByDescending(c => c.FileCount).ToList(),
            visible.Where(c => c.Kind == StoryKind.Custom).OrderByDescending(c => c.CreatedAtUtc).ToList(),
            cards.Count(c => c.IsHidden),
            new StoryKindCounts(
                visible.Count(c => c.Kind != StoryKind.Memory) + memories.Count,
                events.Count,
                memories.Count,
                visible.Count(c => c.Kind == StoryKind.Person),
                visible.Count(c => c.Kind == StoryKind.Place),
                visible.Count(c => c.Kind == StoryKind.Custom)),
            totalFiles);
    }

    public async Task<IReadOnlyList<StoryCard>> GetHiddenAsync(CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await ReadAsync(connection => (IReadOnlyList<StoryCard>)ReadCards(connection, scope, StorySettingsStore.Load(connection), "s.IsHidden = 1")
            .OrderByDescending(c => c.StartAt).ToList(), ct).ConfigureAwait(false);
    }

    public async Task<StoryCard?> GetAsync(int storyId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await ReadAsync(connection => ReadCards(connection, scope, StorySettingsStore.Load(connection), "s.Id = " + Inv(storyId), includeDisabled: true).FirstOrDefault(), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoryCard>> GetStoriesForFileAsync(int fileId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await ReadAsync(connection => (IReadOnlyList<StoryCard>)ReadCards(connection, scope, StorySettingsStore.Load(connection),
            $"s.Id IN (SELECT StoryId FROM StoryItems WHERE FileId = {Inv(fileId)} AND Origin >= 0)").OrderByDescending(c => c.StartAt).ToList(), ct).ConfigureAwait(false);
    }

    public async Task<StoryDetails?> GetDetailsAsync(int storyId, CancellationToken ct = default)
    {
        var card = await GetAsync(storyId, ct).ConfigureAwait(false);
        if (card is null)
        {
            return null;
        }

        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await ReadAsync(connection =>
        {
            var items = new List<(int FileId, int Day, bool IsVideo, int? PlaceId)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT f.Id, si.Day, f.MediaType, f.PlaceId FROM StoryItems si CROSS JOIN Files f ON f.Id = si.FileId
                    WHERE si.StoryId = $s AND si.Origin >= 0 AND f.LibraryId IN ({scope.SqlLibraryIdList})
                    ORDER BY {StorySql.TimeKey}, f.Id;
                    """;
                cmd.Parameters.AddWithValue("$s", storyId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    items.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) == 1, reader.IsDBNull(3) ? null : reader.GetInt32(3)));
                }
            }

            var persons = new List<StoryPerson>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT p.Id, p.Name, COUNT(DISTINCT fa.FileId) AS n
                    FROM StoryItems si CROSS JOIN Files f ON f.Id = si.FileId
                    CROSS JOIN Faces fa ON fa.FileId = si.FileId AND fa.State = 2
                    CROSS JOIN Persons p ON p.Id = fa.PersonId
                    WHERE si.StoryId = $s AND si.Origin >= 0 AND f.LibraryId IN ({scope.SqlLibraryIdList}) AND p.IsHidden = 0
                    GROUP BY p.Id ORDER BY n DESC, p.Name;
                    """;
                cmd.Parameters.AddWithValue("$s", storyId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    persons.Add(new StoryPerson(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
                }
            }

            var placeIds = StoryPlanWriter.ParsePlaceList(StorySql.ExecuteText(connection, "SELECT PlaceIds FROM Stories WHERE Id = $s;", storyId));
            var places = placeIds.Select(id => _places?.GetPlace(id)).OfType<PlaceInfo>().Select(p => new StoryPlace(p.PlaceId, p.Name, p.Country)).ToList();
            var multiYear = card.FirstDay.Year != card.LastDay.Year;
            var singleDay = card.FirstDay == card.LastDay;
            var days = items.GroupBy(i => i.Day).OrderBy(g => g.Key).Select((g, index) =>
            {
                var date = StoryDetector.DateOf(g.Key);
                var dayPlaces = g.Where(i => i.PlaceId is not null).GroupBy(i => i.PlaceId!.Value).OrderByDescending(p => p.Count()).Take(2)
                    .Select(p => _places?.GetPlace(p.Key)?.Name).OfType<string>().ToList();
                var videos = g.Count(i => i.IsVideo);
                return new StoryDay(index + 1, date, StoryTitleBuilder.Day(date, withYear: singleDay || multiYear), StoryTitleBuilder.ShortDay(date),
                    StoryTitleBuilder.Places(dayPlaces), g.Select(i => i.FileId).ToList(), g.Count() - videos, videos);
            }).ToList();

            return new StoryDetails(card, Kicker(card), places, persons, days, items.Select(i => i.FileId).ToList());
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HighlightPick>> GetHighlightsAsync(int storyId, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var (files, share) = await ReadAsync(connection =>
        {
            if (!InGroup(connection, storyId, scope.GroupId))
            {
                return (new List<int>(), 0.0);
            }

            return (StorySql.ReadStoryFileIds(connection, storyId, scope.LibraryIds), StorySettingsStore.Load(connection).HighlightShare);
        }, ct).ConfigureAwait(false);
        return files.Count == 0 ? [] : await _selector.SelectAsync(files, new HighlightOptions(share), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetPreviewFileIdsAsync(IReadOnlyCollection<int> storyIds, int perStory = 3, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storyIds);
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await ReadAsync(connection =>
        {
            var map = new Dictionary<int, IReadOnlyList<int>>();
            foreach (var id in storyIds.Distinct())
            {
                if (!InGroup(connection, id, scope.GroupId))
                {
                    continue;
                }

                var files = StorySql.ReadStoryFileIds(connection, id, scope.LibraryIds);
                var cover = StorySql.ExecuteText(connection, "SELECT CoverFileId FROM Stories WHERE Id = $s;", id) is { } c
                    && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cv) && files.Contains(cv) ? cv : (int?)null;
                map[id] = Spread(files, cover, perStory);
            }

            return (IReadOnlyDictionary<int, IReadOnlyList<int>>)map;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Titelbild zuerst, dann gleichmäßig über die Story verteilt.</summary>
    internal static List<int> Spread(IReadOnlyList<int> files, int? first, int count)
    {
        var result = new List<int>(count);
        if (first is { } f)
        {
            result.Add(f);
        }

        if (files.Count == 0 || count <= result.Count)
        {
            return result;
        }

        var need = Math.Min(count, files.Count);
        for (var i = 0; i < files.Count && result.Count < need; i++)
        {
            var index = (int)((long)(i * 2 + 1) * files.Count / (need * 2L));
            var candidate = files[Math.Clamp(index, 0, files.Count - 1)];
            if (!result.Contains(candidate))
            {
                result.Add(candidate);
            }
        }

        foreach (var candidate in files)
        {
            if (result.Count >= need)
            {
                break;
            }

            if (!result.Contains(candidate))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    // ---- Karten ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Karten der Gruppe des Bereichs. Deckt der Bereich die ganze Gruppe ab, gelten die gespeicherten Zähler; sonst werden
    /// sie im Bereich gezählt und Storys ohne Datei im Bereich fallen weg.
    /// </summary>
    private List<StoryCard> ReadCards(SqliteConnection connection, LibraryScopeInfo scope, StorySettings settings, string? where, bool includeDisabled = false)
    {
        if (!scope.HasLibrary)
        {
            return [];
        }

        var rows = new List<Row>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT s.Id, s.Kind, s.Signature, s.StartAt, s.EndAt, s.FirstDay, s.LastDay, s.DatePrecision, s.PlaceId, s.PersonId,
                       s.PlaceIds, s.VisitCount, s.IsAway, s.CustomTitle, s.CoverFileId, s.CoverIsManual, s.IsPinned, s.IsHidden,
                       s.IsSeen, s.FileCount, s.VideoCount, s.CreatedAtUtc, p.Name, COALESCE(p.IsHidden, 0)
                FROM Stories s LEFT JOIN Persons p ON p.Id = s.PersonId
                WHERE s.GroupId = $g{(where is null ? string.Empty : " AND (" + where + ")")};
                """;
            cmd.Parameters.AddWithValue("$g", scope.GroupId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Row(
                    reader.GetInt32(0), (StoryKind)reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt32(5), reader.GetInt32(6), (CaptureDatePrecision)reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    reader.GetBoolean(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetInt32(14),
                    reader.GetBoolean(15), reader.GetBoolean(16), reader.GetBoolean(17), reader.GetBoolean(18), reader.GetInt32(19),
                    reader.GetInt32(20), reader.GetInt64(21), reader.IsDBNull(22) ? null : reader.GetString(22), reader.GetBoolean(23)));
            }
        }

        Dictionary<int, (int Files, int Videos)>? scoped = null;
        if (scope.LibraryIds.Count < scope.GroupLibraryIds.Count && rows.Count > 0)
        {
            scoped = new Dictionary<int, (int, int)>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT si.StoryId, COUNT(*), SUM(f.MediaType = 1)
                FROM Stories s CROSS JOIN StoryItems si ON si.StoryId = s.Id CROSS JOIN Files f ON f.Id = si.FileId
                WHERE s.GroupId = $g{(where is null ? string.Empty : " AND (" + where + ")")} AND si.Origin >= 0 AND f.LibraryId IN ({scope.SqlLibraryIdList})
                GROUP BY si.StoryId;
                """;
            cmd.Parameters.AddWithValue("$g", scope.GroupId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                scoped[reader.GetInt32(0)] = (reader.GetInt32(1), reader.GetInt32(2));
            }
        }

        var homeCountry = HomeCountry(connection, scope);
        var cards = new List<StoryCard>(rows.Count);
        foreach (var row in rows)
        {
            if (!includeDisabled && !Enabled(row.Kind, settings))
            {
                continue;
            }

            if (row.Kind == StoryKind.Person && row.PersonHidden && !includeDisabled)
            {
                continue; // ausgeblendete Personen bekommen keine Storys (ADR-0045)
            }

            var (files, videos) = (row.FileCount, row.VideoCount);
            if (scoped is not null)
            {
                (files, videos) = scoped.GetValueOrDefault(row.Id);
            }

            if (files == 0)
            {
                continue;
            }

            cards.Add(BuildCard(row, files, videos, homeCountry));
        }

        return cards;
    }

    private static bool Enabled(StoryKind kind, StorySettings settings) => kind switch
    {
        StoryKind.Event or StoryKind.Trip => settings.EventsEnabled,
        StoryKind.Memory => settings.MemoriesEnabled,
        StoryKind.Person => settings.PersonsEnabled,
        StoryKind.Place => settings.PlacesEnabled,
        _ => true,
    };

    private sealed record Row(
        int Id, StoryKind Kind, string Signature, long StartAt, long EndAt, int FirstDay, int LastDay, CaptureDatePrecision Precision,
        int? PlaceId, int? PersonId, string? PlaceIds, int? VisitCount, bool IsAway, string? CustomTitle, int? CoverFileId, bool CoverIsManual,
        bool IsPinned, bool IsHidden, bool IsSeen, int FileCount, int VideoCount, long CreatedAt, string? PersonName, bool PersonHidden);

    private StoryCard BuildCard(Row row, int files, int videos, string? homeCountry)
    {
        var first = StoryDetector.DateOf(row.FirstDay);
        var last = StoryDetector.DateOf(row.LastDay);
        string autoTitle;
        string? subtitle;
        switch (row.Kind)
        {
            case StoryKind.Person:
                var name = row.PersonName ?? "Person";
                autoTitle = StoryTitleBuilder.Person(name, first.Year, last.Year);
                subtitle = (files - videos).ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + (files - videos == 1 ? " Foto mit " : " Fotos mit ") + name;
                break;
            case StoryKind.Place:
                var place = row.PlaceId is { } pid ? _places?.GetPlace(pid) : null;
                autoTitle = StoryTitleBuilder.Place(place?.Name ?? "Ort", first.Year, last.Year);
                var visits = row.VisitCount ?? 0;
                var visitText = visits.ToString(CultureInfo.InvariantCulture) + (visits == 1 ? " Besuch" : " Besuche");
                subtitle = place is not null && homeCountry is not null && !string.Equals(place.Country, homeCountry, StringComparison.Ordinal)
                    ? visitText + StoryTitleBuilder.Dot + place.Country
                    : visitText;
                break;
            case StoryKind.Memory when IsForgottenKey(row.Signature): // „Vergessene Schätze“ (ADR-0072)
                autoTitle = ForgottenKicker;
                subtitle = ForgottenSubtitle(first.Year, last.Year);
                break;
            case StoryKind.Memory when row.Signature.StartsWith("R:M:", StringComparison.Ordinal):
                autoTitle = StoryTitleBuilder.MonthYears(first.Month, first.Year, last.Year);
                subtitle = PlacesLine(row.PlaceIds, homeCountry);
                break;
            default:
                autoTitle = StoryTitleBuilder.Range(first, last, row.Precision);
                subtitle = PlacesLine(row.PlaceIds, homeCountry);
                break;
        }

        return new StoryCard(
            row.Id,
            row.Kind,
            StoryTitleBuilder.KindLabel(row.Kind, row.IsAway),
            row.CustomTitle ?? autoTitle,
            autoTitle,
            subtitle,
            StoryTitleBuilder.Counts(files, videos),
            files,
            videos,
            DateTimeOffset.FromUnixTimeSeconds(row.StartAt).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(row.EndAt).UtcDateTime,
            first,
            last,
            first.Year,
            row.LastDay - row.FirstDay + 1,
            row.CoverFileId,
            row.CoverIsManual,
            row.IsPinned,
            row.IsHidden,
            row.IsSeen,
            row.IsAway,
            row.CustomTitle is not null,
            row.PersonId,
            row.PlaceId,
            DateTimeOffset.FromUnixTimeSeconds(row.CreatedAt).UtcDateTime);
    }

    /// <summary>Ortsnamen der Story; Land nur, wenn alle Orte in EINEM Land außerhalb von zu Hause liegen.</summary>
    private string? PlacesLine(string? placeIds, string? homeCountry)
    {
        if (_places is null)
        {
            return null;
        }

        var places = StoryPlanWriter.ParsePlaceList(placeIds).Select(_places.GetPlace).OfType<PlaceInfo>().ToList();
        var countries = places.Select(p => p.Country).Distinct(StringComparer.Ordinal).ToList();
        var country = countries.Count == 1 && homeCountry is not null && !string.Equals(countries[0], homeCountry, StringComparison.Ordinal) ? countries[0] : null;
        return StoryTitleBuilder.Places(places.Select(p => p.Name).ToList(), country);
    }

    private string? HomeCountry(SqliteConnection connection, LibraryScopeInfo scope)
    {
        if (_places is null)
        {
            return null;
        }

        if (!_homes.TryGet(scope.GroupId, out var home))
        {
            home = StorySql.ReadHome(connection, scope.GroupLibraryIds, StorySettingsStore.Load(connection).HomeOrAutomatic);
            _homes.Set(scope.GroupId, home);
        }

        return home?.PlaceId is { } id ? _places.GetPlace(id)?.Country : null;
    }

    private string Kicker(StoryCard card)
    {
        if (card.Kind == StoryKind.Memory)
        {
            var years = _clock.Today.Year - card.FirstDay.Year;
            return card.FirstDay.Month == _clock.Today.Month && card.FirstDay.Day == _clock.Today.Day && card.FirstDay == card.LastDay && years > 0
                ? StoryTitleBuilder.YearsAgo(years)
                : card.KindLabel;
        }

        return card.DayCount > 1 && card.Kind is StoryKind.Trip or StoryKind.Event or StoryKind.Custom
            ? card.KindLabel + StoryTitleBuilder.Dot + card.DayCount.ToString(CultureInfo.InvariantCulture) + " Tage"
            : card.KindLabel;
    }

    // ---- Hilfen ----------------------------------------------------------------------------------------------------

    private static bool InGroup(SqliteConnection connection, int storyId, int groupId)
        => StorySql.Scalar(connection, null, "SELECT COUNT(*) FROM Stories WHERE Id = $s AND GroupId = $g;", ("$s", storyId), ("$g", groupId)) > 0;

    private Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken ct)
        => Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            return read(connection);
        }, ct);

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken ct)
        => await Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            var result = write(connection, tx);
            tx.Commit();
            return result;
        }, ct).ConfigureAwait(false);

    private void Publish(int groupId, StoriesChangeKind kind, params int[] storyIds)
    {
        try
        {
            _messenger.Send(new StoriesChangedMessage(groupId, kind, storyIds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der StoriesChangedMessage warf eine Ausnahme");
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);
}
