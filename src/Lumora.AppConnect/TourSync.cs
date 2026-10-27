using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Places.Tracks;

namespace Lumora.AppConnect;

/// <summary>Ergebnis eines Abgleichs mit einer App; <paramref name="Problem"/> = warum nicht (ohne Fehlertext der Gegenseite).</summary>
public sealed record TourSyncResult(string AppId, int Tours, int NewTracks, int RemovedTracks, string? Problem = null);

/// <summary>Stand der Touren einer App für die Karte im Reiter („412 Touren · 2016–2026“).</summary>
public sealed record TourSummary(int Count, int? FirstYear, int? LastYear)
{
    public static TourSummary Empty { get; } = new(0, null, null);
}

/// <summary>
/// Touren verbundener Apps übernehmen (App-Verbindung Stufe 7, ADR-0073; Vertrag 1.1 Abschnitt 8.2): alle Touren mit Recht
/// <c>tours.read</c> in <c>AppTours</c> (auch ohne Spur — für den späteren Galerie-Filter „Tour“), ihre Spuren mit Recht
/// <c>tracks.read</c> je Bibliotheks-Gruppe in <c>GpsTracks</c> (Herkunft <see cref="GpsTrackSource.App"/>, Herbert Frage 5) —
/// so nutzt „Ungenaue Orte“ sie wie eine Garmin-Spur, bei derselben Aktivität mit Vorrang (Frage 6). Verschwundene oder
/// geänderte Touren, entzogene Rechte und getrennte Apps räumt der Abgleich wieder ab. Läuft die App nicht, startet er sie
/// nur, wenn er darf (<c>mayStartApps</c>) und die App Lumora <c>background.start</c> gibt, hält dann eine Zusage und gibt sie
/// am Ende frei. Protokoll nur mit Anzahlen.
/// </summary>
public sealed class TourSync
{
    /// <summary>Halte-Zusage während eines Abgleichs mit einer selbst gestarteten App (Sekunden).</summary>
    public const int LeaseSeconds = 120;

    /// <summary>Nach so vielen Spuren die Zusage verlängern.</summary>
    private const int RenewEvery = 25;

    private static readonly JsonSerializerOptions SummitJson = new(JsonSerializerDefaults.Web);

    private readonly AppConnectClient _client;
    private readonly ConnectionStore _connections;
    private readonly ILibraryContextFactory _factory;
    private readonly IGpsTrackStore _tracks;
    private readonly IMessenger _messenger;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TourSync(AppConnectClient client, ConnectionStore connections, ILibraryContextFactory factory, IGpsTrackStore tracks,
        IMessenger? messenger = null, ILogger? logger = null, TimeProvider? time = null)
    {
        _client = client;
        _connections = connections;
        _factory = factory;
        _tracks = tracks;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Ein Abgleich ist fertig (Karte im Reiter neu zeichnen); vom Thread des Abgleichs.</summary>
    public event EventHandler? Changed;

    /// <summary>Alle Apps mit <c>tours.read</c> abgleichen und Touren nicht mehr berechtigter Apps entfernen.</summary>
    public async Task<IReadOnlyList<TourSyncResult>> SyncAllAsync(bool mayStartApps, CancellationToken ct = default)
    {
        var apps = _connections.All.Where(c => c.GrantedByPeer.Contains(Rights.ToursRead, StringComparer.Ordinal)).Select(c => c.AppId).ToList();
        var results = new List<TourSyncResult>();
        foreach (var appId in apps)
        {
            results.Add(await SyncAsync(appId, mayStartApps, ct).ConfigureAwait(false));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var removed = await Task.Run(() => RemoveOtherApps(apps), ct).ConfigureAwait(false);
            if (removed.Groups.Count > 0)
            {
                Announce(removed.Groups);
            }
        }
        finally
        {
            _gate.Release();
        }

        return results;
    }

    /// <summary>Eine App abgleichen (auch für den Knopf „Touren abgleichen“).</summary>
    public async Task<TourSyncResult> SyncAsync(string appId, bool mayStartApps, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await SyncCoreAsync(appId, mayStartApps, ct).ConfigureAwait(false);
            if (result.Problem is null)
            {
                _logger.LogInformation("App-Verbindung: {AppId}: {Tours} Touren, {New} Spuren neu, {Removed} entfernt",
                    appId, result.Tours, result.NewTracks, result.RemovedTracks);
            }
            else
            {
                _logger.LogInformation("App-Verbindung: {AppId}: Touren nicht abgeglichen — {Problem}", appId, result.Problem);
            }

            return result;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stand der übernommenen Touren einer App (ohne Netz).</summary>
    public TourSummary Summary(string appId)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MIN(StartUtc), MAX(StartUtc) FROM AppTours WHERE AppId = $a;";
        cmd.Parameters.AddWithValue("$a", appId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) == 0)
        {
            return TourSummary.Empty;
        }

        static int Year(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.Year;
        return new TourSummary(reader.GetInt32(0), Year(reader.GetInt64(1)), Year(reader.GetInt64(2)));
    }

    private async Task<TourSyncResult> SyncCoreAsync(string appId, bool mayStartApps, CancellationToken ct)
    {
        if (_connections.Find(appId) is not { } connection || !connection.GrantedByPeer.Contains(Rights.ToursRead, StringComparer.Ordinal))
        {
            var gone = await Task.Run(() => RemoveApp(appId), ct).ConfigureAwait(false);
            Announce(gone.Groups);
            return new TourSyncResult(appId, 0, 0, gone.Tracks, "keine Freigabe für Touren");
        }

        if (_client.Peers().FirstOrDefault(p => p.AppId == appId) is not { } registration)
        {
            return new TourSyncResult(appId, 0, 0, 0, "nicht auf diesem PC angemeldet");
        }

        var started = false;
        var peer = registration;
        if (!RegistrationFiles.IsAlive(registration))
        {
            if (!mayStartApps || !connection.GrantedByPeer.Contains(Rights.BackgroundStart, StringComparer.Ordinal))
            {
                return new TourSyncResult(appId, 0, 0, 0, "läuft nicht");
            }

            peer = await _client.EnsureRunningInBackgroundAsync(appId, ct).ConfigureAwait(false);
            started = true;
            await _client.HoldAsync(appId, LeaseSeconds, ct).ConfigureAwait(false);
        }

        try
        {
            var tours = new List<Tour>();
            string? cursor = null;
            do
            {
                var page = await _client.ToursAsync(peer, cursor, ct: ct).ConfigureAwait(false);
                tours.AddRange(page.Items.Where(t => t.Source != "demo")); // Lumora fragt nie nach Demo-Touren
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            var tracksAllowed = connection.GrantedByPeer.Contains(Rights.TracksRead, StringComparer.Ordinal);
            var stored = await Task.Run(() => StoreTours(appId, tours, tracksAllowed), ct).ConfigureAwait(false);
            var changedGroups = new HashSet<int>(stored.Groups);
            var added = 0;
            var fetched = 0;
            foreach (var (tourId, externalId, groups) in stored.Missing)
            {
                ct.ThrowIfCancellationRequested();
                if (started && ++fetched % RenewEvery == 0)
                {
                    await _client.HoldAsync(appId, LeaseSeconds, ct).ConfigureAwait(false);
                }

                var tour = tours.First(t => t.Id == externalId);
                if (await _client.TrackAsync(peer, externalId, ct).ConfigureAwait(false) is not { } track || ToParsed(tour, track, registration.DisplayName) is not { } parsed)
                {
                    continue;
                }

                var item = new NewGpsTrack(parsed, TrackCodec.Checksum(parsed.Points), tourId);
                foreach (var group in groups)
                {
                    // gleiche Punkte schon selbst eingelesen → bleibt die eigene Zeile (INSERT OR IGNORE), gleiche Daten
                    var inserted = await _tracks.InsertAsync(group, [item], ct).ConfigureAwait(false);
                    added += inserted;
                    if (inserted > 0)
                    {
                        changedGroups.Add(group);
                    }
                }
            }

            Announce(changedGroups);
            return new TourSyncResult(appId, tours.Count, added, stored.RemovedTracks);
        }
        finally
        {
            if (started)
            {
                await _client.ReleaseAsync(appId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Punkte der Spur (Sekunden seit Beginn → Unix-Sekunden); null, wenn weniger als zwei gültige Punkte.</summary>
    internal static ParsedTrack? ToParsed(Tour tour, TourTrack track, string device)
    {
        var count = Math.Min(track.T.Count, Math.Min(track.Lat.Count, track.Lon.Count));
        var start = new DateTimeOffset(DateTime.SpecifyKind(track.StartUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var points = new List<TrackPoint>(count);
        for (var i = 0; i < count; i++)
        {
            double? ele = track.Ele is { } e && i < e.Count && double.IsFinite(e[i]) ? e[i] : null;
            points.Add(new TrackPoint(start + (long)Math.Round(track.T[i]), track.Lat[i], track.Lon[i], ele));
        }

        var normalized = ParsedTrack.Normalize(points);
        return normalized.Count < 2 ? null : new ParsedTrack(normalized, TourSports.Name(tour.Sport), device, tour.Name, GpsTrackSource.App);
    }

    private readonly record struct Stored(IReadOnlyList<(int TourId, string ExternalId, IReadOnlyList<int> Groups)> Missing, int RemovedTracks, IReadOnlyList<int> Groups);

    private readonly record struct Removed(int Tracks, IReadOnlyList<int> Groups);

    /// <summary>
    /// Touren in einer Transaktion: neue anlegen, vorhandene nachführen (Zeiten, Länge oder Spur geändert → ihre Spuren neu),
    /// verschwundene samt Spuren löschen; ohne <c>tracks.read</c> alle Spuren dieser App weg. Liefert die Spuren, die je Gruppe
    /// noch fehlen.
    /// </summary>
    private Stored StoreTours(string appId, IReadOnlyList<Tour> tours, bool tracksAllowed)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var tx = connection.BeginTransaction();
        var existing = new Dictionary<string, (int Id, long Start, long End, double Distance, bool HasTrack)>(StringComparer.Ordinal);
        using (var read = Command(connection, tx, "SELECT ExternalId, Id, StartUtc, EndUtc, DistanceMeters, HasTrack FROM AppTours WHERE AppId = $a;", ("$a", appId)))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                existing[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetDouble(4), reader.GetBoolean(5));
            }
        }

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var stale = new List<int>(); // Touren, deren Spur neu geholt werden muss
        foreach (var tour in tours)
        {
            var start = Unix(tour.StartUtc);
            var end = Unix(tour.EndUtc);
            var bbox = tour.Bbox.Count == 4 ? tour.Bbox : [0d, 0d, 0d, 0d];
            var summits = JsonSerializer.Serialize(
                tour.Summits.Select(s => new { s.Name, s.ElevationM, ArrivalUtc = s.ArrivalUtc.ToString("O", CultureInfo.InvariantCulture), s.Confirmed }), SummitJson);
            var args = new (string, object?)[]
            {
                ("$a", appId), ("$x", tour.Id), ("$name", tour.Name), ("$start", start), ("$end", end),
                ("$offset", (int)tour.Start.Offset.TotalMinutes), ("$sport", tour.Sport), ("$region", string.IsNullOrWhiteSpace(tour.Region) ? null : tour.Region),
                ("$dist", tour.DistanceM), ("$ascent", tour.AscentM), ("$duration", tour.DurationS),
                ("$minLon", bbox[0]), ("$minLat", bbox[1]), ("$maxLon", bbox[2]), ("$maxLat", bbox[3]),
                ("$track", tour.HasTrack), ("$summits", summits), ("$now", now),
            };
            if (existing.TryGetValue(tour.Id, out var old))
            {
                ids[tour.Id] = old.Id;
                if (old.Start != start || old.End != end || Math.Abs(old.Distance - tour.DistanceM) > 0.5 || old.HasTrack != tour.HasTrack)
                {
                    stale.Add(old.Id);
                }

                Execute(connection, tx, """
                    UPDATE AppTours SET Name = $name, StartUtc = $start, EndUtc = $end, StartOffsetMinutes = $offset, Sport = $sport,
                        Region = $region, DistanceMeters = $dist, AscentMeters = $ascent, DurationSeconds = $duration, MinLongitude = $minLon,
                        MinLatitude = $minLat, MaxLongitude = $maxLon, MaxLatitude = $maxLat, HasTrack = $track, Summits = $summits,
                        UpdatedAtUtc = $now
                    WHERE AppId = $a AND ExternalId = $x;
                    """, args);
            }
            else
            {
                using var insert = Command(connection, tx, """
                    INSERT INTO AppTours (AppId, ExternalId, Name, StartUtc, EndUtc, StartOffsetMinutes, Sport, Region, DistanceMeters,
                        AscentMeters, DurationSeconds, MinLongitude, MinLatitude, MaxLongitude, MaxLatitude, HasTrack, Summits, UpdatedAtUtc)
                    VALUES ($a, $x, $name, $start, $end, $offset, $sport, $region, $dist, $ascent, $duration, $minLon, $minLat, $maxLon,
                        $maxLat, $track, $summits, $now);
                    SELECT last_insert_rowid();
                    """, args);
                ids[tour.Id] = Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        var groupsTouched = new HashSet<int>();
        var removedTracks = 0;
        var goneIds = existing.Where(e => !ids.ContainsKey(e.Key)).Select(e => e.Value.Id).ToList();
        foreach (var id in goneIds.Concat(stale))
        {
            removedTracks += DeleteTracks(connection, tx, $"AppTourId = {id.ToString(CultureInfo.InvariantCulture)}", groupsTouched);
        }

        foreach (var id in goneIds)
        {
            Execute(connection, tx, "DELETE FROM AppTours WHERE Id = $id;", ("$id", id));
        }

        if (!tracksAllowed)
        {
            removedTracks += DeleteTracks(connection, tx, "AppTourId IN (SELECT Id FROM AppTours WHERE AppId = $a)", groupsTouched, ("$a", appId));
        }

        var missing = new List<(int, string, IReadOnlyList<int>)>();
        if (tracksAllowed)
        {
            var groups = new List<int>();
            using (var g = Command(connection, tx, "SELECT DISTINCT GroupId FROM Libraries WHERE GroupId > 0 ORDER BY GroupId;"))
            using (var reader = g.ExecuteReader())
            {
                while (reader.Read())
                {
                    groups.Add(reader.GetInt32(0));
                }
            }

            var present = new HashSet<(int Tour, int Group)>();
            using (var p = Command(connection, tx, "SELECT t.AppTourId, t.GroupId FROM GpsTracks t JOIN AppTours a ON a.Id = t.AppTourId WHERE a.AppId = $a;", ("$a", appId)))
            using (var reader = p.ExecuteReader())
            {
                while (reader.Read())
                {
                    present.Add((reader.GetInt32(0), reader.GetInt32(1)));
                }
            }

            foreach (var tour in tours.Where(t => t.HasTrack).OrderByDescending(t => t.StartUtc))
            {
                var id = ids[tour.Id];
                var lacking = groups.Where(g => !present.Contains((id, g))).ToList();
                if (lacking.Count > 0)
                {
                    missing.Add((id, tour.Id, lacking));
                }
            }
        }

        tx.Commit();
        return new Stored(missing, removedTracks, groupsTouched.ToList());
    }

    /// <summary>Touren (samt Spuren) von Apps, die keine Touren mehr geben dürfen bzw. nicht mehr verbunden sind.</summary>
    private Removed RemoveOtherApps(IReadOnlyList<string> allowed)
    {
        using var connection = _factory.OpenSqliteConnection();
        var others = new List<string>();
        using (var cmd = Command(connection, null, "SELECT DISTINCT AppId FROM AppTours;"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                others.Add(reader.GetString(0));
            }
        }

        var tracks = 0;
        var groups = new List<int>();
        foreach (var appId in others.Except(allowed, StringComparer.Ordinal))
        {
            var removed = RemoveApp(appId);
            tracks += removed.Tracks;
            groups.AddRange(removed.Groups);
        }

        return new Removed(tracks, groups.Distinct().ToList());
    }

    private Removed RemoveApp(string appId)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var tx = connection.BeginTransaction();
        var groups = new HashSet<int>();
        var tracks = DeleteTracks(connection, tx, "AppTourId IN (SELECT Id FROM AppTours WHERE AppId = $a)", groups, ("$a", appId));
        var tours = Execute(connection, tx, "DELETE FROM AppTours WHERE AppId = $a;", ("$a", appId));
        tx.Commit();
        if (tours > 0)
        {
            _logger.LogInformation("App-Verbindung: {AppId}: {Tours} Touren und {Tracks} Spuren entfernt (keine Freigabe mehr)", appId, tours, tracks);
        }

        return new Removed(tracks, groups.ToList());
    }

    private static int DeleteTracks(SqliteConnection connection, SqliteTransaction tx, string where, HashSet<int> groups, params (string, object?)[] args)
    {
        using (var read = Command(connection, tx, $"SELECT DISTINCT GroupId FROM GpsTracks WHERE {where};", args))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                groups.Add(reader.GetInt32(0));
            }
        }

        return Execute(connection, tx, $"DELETE FROM GpsTracks WHERE {where};", args);
    }

    private void Announce(IEnumerable<int> groups)
    {
        foreach (var group in groups.Distinct())
        {
            try
            {
                _messenger.Send(new GpsTracksChangedMessage(group));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der GpsTracksChangedMessage warf eine Ausnahme");
            }
        }
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(connection, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }
}
