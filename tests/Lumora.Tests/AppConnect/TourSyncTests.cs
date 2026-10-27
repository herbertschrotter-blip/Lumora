using System.Net;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.AppConnect;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;
using Lumora.Tests.Activity;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.AppConnect;

/// <summary>
/// App-Verbindung Stufe 7 (ADR-0073, Vertrag 8.2): Touren und Spuren vom Gipfelbuch übernehmen — Touren auch ohne Spur in
/// AppTours, Spuren je Bibliotheks-Gruppe mit Herkunft App, nichts doppelt, geänderte/verschwundene Touren und entzogene
/// Rechte räumen ab, nicht laufende App nur mit Erlaubnis starten; Vorrang der Tour-Spur in den Vorschlägen; Taktung der
/// automatischen Abgleiche. Die Gegenseite ist ein HTTP-Handler im Test.
/// </summary>
public sealed class TourSyncTests : IAsyncLifetime
{
    private TempLibraryFixture _fx = null!;
    private string _root = null!;
    private RegistrationFiles _registrations = null!;
    private ConnectionStore _store = null!;
    private FakeGipfelbuch _gipfelbuch = null!;
    private AppConnectClient _client = null!;
    private TourSync _sync = null!;
    private readonly List<int> _changedGroups = [];

    public async Task InitializeAsync()
    {
        _fx = await TempLibraryFixture.CreateAsync();
        _root = Path.Combine(_fx.RootDir, "appconnect");
        _registrations = new RegistrationFiles(Path.Combine(_root, "apps"));
        _store = new ConnectionStore(Path.Combine(_root, "connections.json"), new XorProtector());
        _store.Add(new AppConnection("gipfelbuch", "Gipfelbuch", [Rights.PhotosSearch], [Rights.ToursRead, Rights.TracksRead], DateTime.UtcNow), "geheim");
        WriteRegistration(running: true);
        _gipfelbuch = new FakeGipfelbuch();
        _client = new AppConnectClient(_registrations, _store, new HttpClient(_gipfelbuch), AppConnectClientIdentity.Lumora,
            new AppConnectClientTimings(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300)));
        _sync = new TourSync(_client, _store, _fx.ContextFactory, new GpsTrackStore(_fx.ContextFactory), _fx.Messenger);
        _fx.Messenger.Register<GpsTracksChangedMessage>(this, (_, m) => _changedGroups.Add(m.GroupId));

        // zwei Bibliotheken in zwei Gruppen — Spuren gelten je Gruppe (ADR-0062)
        await _fx.CreateLibraryAsync("Familie");
        await _fx.CreateLibraryAsync("Sport");
        Exec("INSERT INTO LibraryGroups (Name, CreatedAtUtc) VALUES ('Familie', 0), ('Sport', 0);");
        Exec("UPDATE Libraries SET GroupId = (SELECT Id FROM LibraryGroups WHERE Name = Libraries.Name);");
    }

    public Task DisposeAsync()
    {
        _fx.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Touren_auch_ohne_Spur_und_Spuren_je_Gruppe_mit_Herkunft_App()
    {
        _gipfelbuch.Tours.AddRange([
            FakeGipfelbuch.Tour("t1", "Handalm", new DateTime(2020, 3, 15, 8, 0, 0, DateTimeKind.Utc), hasTrack: true, sport: "mountain_hiking"),
            FakeGipfelbuch.Tour("t2", "Schöckl", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), hasTrack: true),
            FakeGipfelbuch.Tour("t3", "Ohne Spur", new DateTime(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc), hasTrack: false),
            FakeGipfelbuch.Tour("demo", "Demo", new DateTime(2025, 1, 1, 9, 0, 0, DateTimeKind.Utc), hasTrack: true, source: "demo"),
        ]);

        var result = await _sync.SyncAsync("gipfelbuch", mayStartApps: false);

        result.Should().Be(new TourSyncResult("gipfelbuch", 3, 4, 0), "Demo-Touren nie; 2 Spuren × 2 Gruppen");
        _gipfelbuch.Requests.Should().Contain(r => r.StartsWith("/av/v1/tours?limit=500", StringComparison.Ordinal))
            .And.NotContain(r => r.Contains("includeDemo", StringComparison.Ordinal));
        _gipfelbuch.TrackRequests.Should().BeEquivalentTo(["t1", "t2"], "je Tour nur einmal geholt, auch für zwei Gruppen");
        Scalar("SELECT COUNT(*) FROM AppTours WHERE AppId = 'gipfelbuch'").Should().Be(3);
        Scalar("SELECT COUNT(*) FROM GpsTracks WHERE Source = 4 AND AppTourId IS NOT NULL AND Device = 'Gipfelbuch'").Should().Be(4);
        Scalar("SELECT COUNT(*) FROM GpsTracks WHERE Name = 'Handalm' AND Sport = 'Bergtour'").Should().Be(2);
        _changedGroups.Distinct().Should().HaveCount(2, "„Ungenaue Orte“ beider Gruppen rechnet neu");
        _sync.Summary("gipfelbuch").Should().Be(new TourSummary(3, 2020, 2026));

        _gipfelbuch.TrackRequests.Clear();
        (await _sync.SyncAsync("gipfelbuch", mayStartApps: false)).Should().Be(new TourSyncResult("gipfelbuch", 3, 0, 0));
        _gipfelbuch.TrackRequests.Should().BeEmpty("nichts geändert — keine Spur neu geholt");
    }

    [Fact]
    public async Task Geaenderte_und_verschwundene_Touren_raeumen_ihre_Spuren_ab()
    {
        var start = new DateTime(2025, 8, 1, 7, 0, 0, DateTimeKind.Utc);
        _gipfelbuch.Tours.AddRange([FakeGipfelbuch.Tour("a", "A", start, true), FakeGipfelbuch.Tour("b", "B", start.AddDays(1), true)]);
        await _sync.SyncAsync("gipfelbuch", mayStartApps: false);

        _gipfelbuch.Tours[0] = _gipfelbuch.Tours[0] with { EndUtc = start.AddHours(3) }; // im Gipfelbuch gekürzt
        _gipfelbuch.Tours.RemoveAt(1); // gelöscht
        _gipfelbuch.TrackRequests.Clear();

        var result = await _sync.SyncAsync("gipfelbuch", mayStartApps: false);

        result.Should().Be(new TourSyncResult("gipfelbuch", 1, 2, 4));
        _gipfelbuch.TrackRequests.Should().Equal("a");
        Scalar("SELECT COUNT(*) FROM AppTours").Should().Be(1);
        Scalar("SELECT COUNT(*) FROM GpsTracks").Should().Be(2);
    }

    [Fact]
    public async Task Entzogene_Rechte_und_getrennte_App_entfernen_Spuren_und_Touren()
    {
        _gipfelbuch.Tours.Add(FakeGipfelbuch.Tour("a", "A", new DateTime(2025, 8, 1, 7, 0, 0, DateTimeKind.Utc), true));
        await _sync.SyncAsync("gipfelbuch", mayStartApps: false);

        _store.SetGrantedByPeer("gipfelbuch", [Rights.ToursRead]);
        (await _sync.SyncAsync("gipfelbuch", mayStartApps: false)).RemovedTracks.Should().Be(2);
        Scalar("SELECT COUNT(*) FROM GpsTracks").Should().Be(0, "ohne tracks.read keine Spuren");
        Scalar("SELECT COUNT(*) FROM AppTours").Should().Be(1, "Touren bleiben");

        _store.Remove("gipfelbuch");
        (await _sync.SyncAllAsync(mayStartApps: false)).Should().BeEmpty();
        Scalar("SELECT COUNT(*) FROM AppTours").Should().Be(0, "getrennt → Touren weg");
    }

    [Fact]
    public async Task Gleiche_Punkte_schon_selbst_eingelesen_bleiben_einmal()
    {
        var tour = FakeGipfelbuch.Tour("a", "A", new DateTime(2025, 8, 1, 7, 0, 0, DateTimeKind.Utc), true);
        _gipfelbuch.Tours.Add(tour);
        var groups = Ints("SELECT Id FROM LibraryGroups ORDER BY Id");
        var parsed = TourSync.ToParsed(tour, FakeGipfelbuch.TrackOf(tour), "Fenix")! with { Source = GpsTrackSource.Fit };
        await new GpsTrackStore(_fx.ContextFactory).InsertAsync(groups[0], [new NewGpsTrack(parsed, TrackCodec.Checksum(parsed.Points))]);

        var result = await _sync.SyncAsync("gipfelbuch", mayStartApps: false);

        result.NewTracks.Should().Be(1, "nur in der zweiten Gruppe neu");
        Scalar($"SELECT COUNT(*) FROM GpsTracks WHERE GroupId = {groups[0]}").Should().Be(1);
    }

    [Fact]
    public async Task Nicht_laufende_App_nur_mit_Erlaubnis_im_Hintergrund_starten()
    {
        WriteRegistration(running: false);
        (await _sync.SyncAsync("gipfelbuch", mayStartApps: false)).Problem.Should().Be("läuft nicht");
        (await _sync.SyncAsync("gipfelbuch", mayStartApps: true)).Problem.Should().Be("läuft nicht", "ohne background.start von drüben");
        _gipfelbuch.Requests.Should().BeEmpty();

        _store.SetGrantedByPeer("gipfelbuch", [Rights.ToursRead, Rights.TracksRead, Rights.BackgroundStart]);
        var started = new List<string>();
        var client = new AppConnectClient(_registrations, _store, new HttpClient(_gipfelbuch), AppConnectClientIdentity.Lumora,
            new AppConnectClientTimings(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
            startInBackground: (_, args) =>
            {
                started.Add(args);
                WriteRegistration(running: true);
                return true;
            });
        var sync = new TourSync(client, _store, _fx.ContextFactory, new GpsTrackStore(_fx.ContextFactory), _fx.Messenger);
        _gipfelbuch.Tours.Add(FakeGipfelbuch.Tour("a", "A", new DateTime(2025, 8, 1, 7, 0, 0, DateTimeKind.Utc), false));

        (await sync.SyncAsync("gipfelbuch", mayStartApps: true)).Should().Be(new TourSyncResult("gipfelbuch", 1, 0, 0));
        started.Should().ContainSingle().Which.Should().StartWith("--background --started-by lumora:");
        _gipfelbuch.Requests.Should().Contain("POST /av/v1/lease").And.Contain(r => r.StartsWith("DELETE /av/v1/lease/", StringComparison.Ordinal),
            "Zusage während des Abgleichs, danach frei");
        client.Holds("gipfelbuch").Should().BeFalse();
    }

    [Fact]
    public void Bei_gleicher_Stufe_hat_die_Spur_der_verbundenen_App_Vorrang()
    {
        var points = new[] { new TrackPoint(1000, 47.0, 15.0), new TrackPoint(1010, 47.0001, 15.0001), new TrackPoint(1100, 47.001, 15.001) };
        var own = new SuggestionTrack(1, 1000, 1100, () => points); // Lücke um t=1005: 10 s
        var gipfel = new SuggestionTrack(2, 990, 1100, () => [new TrackPoint(990, 47.0, 15.0), new TrackPoint(1050, 47.0005, 15.0005)], preferred: true);
        var candidate = new SuggestionCandidate(7, 1005);

        var best = GeoSuggestionEngine.FromTracks(candidate, [gipfel, own], [990, 1000], 120);

        best!.TrackId.Should().Be(2, "Herbert Frage 6: bei derselben Aktivität hat das Gipfelbuch Vorrang — auch mit größerer Lücke");
        GeoSuggestionEngine.FromTracks(candidate, [new SuggestionTrack(2, 990, 1100, gipfel.LoadPoints), own], [990, 1000], 120)!
            .TrackId.Should().Be(1, "ohne Vorrang gewinnt die kleinere Lücke");
    }

    [Fact]
    public async Task Automatische_Abgleiche_hoechstens_alle_10_Minuten_mit_Starten_eigens_gezaehlt()
    {
        var time = new ManualTimerTime();
        var hub = new ExternalTrackSyncHub(time: time);
        var calls = new List<bool>();
        hub.Request(true); // noch nichts eingehängt
        hub.Attach((mayStart, _) =>
        {
            lock (calls)
            {
                calls.Add(mayStart);
            }

            return Task.CompletedTask;
        });

        hub.Request(false);
        await WaitAsync(() => calls.Count == 1);
        hub.Request(false);
        hub.Request(true); // „Ungenaue Orte“ darf die App trotzdem gleich starten
        await WaitAsync(() => calls.Count == 2);
        hub.Request(true);
        await Task.Delay(100);
        calls.Should().Equal(false, true);

        time.Advance(ExternalTrackSyncHub.MinInterval);
        hub.Request(true);
        await WaitAsync(() => calls.Count == 3);
        hub.Attach(null);
        time.Advance(ExternalTrackSyncHub.MinInterval);
        hub.Request(true);
        await Task.Delay(100);
        calls.Should().HaveCount(3, "ohne App-Verbindung nichts");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        condition().Should().BeTrue();
    }

    private void WriteRegistration(bool running) => _registrations.Write(new AppRegistration
    {
        AppId = "gipfelbuch",
        DisplayName = "Gipfelbuch",
        AppVersion = "0.6",
        ContractVersion = "1.1",
        ExePath = @"C:\Programme\Gipfelbuch\Gipfelbuch.exe",
        BackgroundArg = "--background",
        Running = running,
        Pid = running ? Environment.ProcessId : null,
        Port = running ? 4711 : null,
        Mode = running ? "background" : null,
        Provides = [Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead, Rights.BackgroundStart],
        Wants = [Rights.PhotosSearch],
    });

    private void Exec(string sql)
    {
        using var connection = _fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var connection = _fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private List<int> Ints(string sql)
    {
        using var connection = _fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<int>();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }

        return list;
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] protectedData) => Protect(protectedData);
    }

    /// <summary>Gipfelbuch-Schnittstelle als Handler: <c>/tours</c> (2 Seiten), <c>/tours/{id}/track</c>, Halte-Zusagen.</summary>
    private sealed class FakeGipfelbuch : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = AppConnectContract.Json;

        public List<Tour> Tours { get; } = [];

        public List<string> Requests { get; } = [];

        public List<string> TrackRequests { get; } = [];

        public static Tour Tour(string id, string name, DateTime startUtc, bool hasTrack, string sport = "hiking", string source = "fit") => new()
        {
            Id = id,
            Name = name,
            Start = new DateTimeOffset(startUtc).ToOffset(TimeSpan.FromHours(2)),
            StartUtc = startUtc,
            EndUtc = startUtc.AddHours(5),
            Sport = sport,
            DistanceM = 12000,
            AscentM = 900,
            DurationS = 18000,
            Bbox = [15.0, 47.0, 15.1, 47.1],
            HasTrack = hasTrack,
            Source = source,
            Summits = [new TourSummit("s1", "Gipfel", 1853, startUtc.AddHours(2), true)],
        };

        /// <summary>Spur im Abstand von 10 s über die ganze Tour (Länge hängt am Ende — eine Änderung ergibt neue Punkte).</summary>
        public static TourTrack TrackOf(Tour tour)
        {
            var seconds = (int)(tour.EndUtc - tour.StartUtc).TotalSeconds;
            var t = Enumerable.Range(0, seconds / 10 + 1).Select(i => i * 10.0).ToList();
            return new TourTrack
            {
                TourId = tour.Id,
                StartUtc = tour.StartUtc,
                Count = t.Count,
                T = t,
                Lat = t.Select(s => 47.0 + s / 1e6).ToList(),
                Lon = t.Select(s => 15.0 + s / 1e6).ToList(),
                Ele = t.Select(s => 1000 + s / 100).ToList(),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Requests)
            {
                Requests.Add(request.Method == HttpMethod.Get ? path : $"{request.Method} {request.RequestUri.AbsolutePath}");
            }

            request.Headers.Authorization!.Parameter.Should().Be("geheim");
            if (path.StartsWith("/av/v1/tours?", StringComparison.Ordinal))
            {
                // zwei Seiten: erst ein Eintrag mit Fortsetzung, dann der Rest (Vertrag: nextCursor)
                var second = path.Contains("cursor=next", StringComparison.Ordinal);
                var items = Tours.ToList(); // die echte App liefert Demo-Touren ohne includeDemo nicht — Lumora filtert trotzdem
                var page = second ? new TourPage(items.Skip(1).ToList(), null) : new TourPage(items.Take(1).ToList(), items.Count > 1 ? "next" : null);
                return Task.FromResult(JsonResponse(page));
            }

            if (path.StartsWith("/av/v1/tours/", StringComparison.Ordinal) && path.EndsWith("/track", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/av/v1/tours/".Length..^"/track".Length]);
                lock (TrackRequests)
                {
                    TrackRequests.Add(id);
                }

                var tour = Tours.FirstOrDefault(t => t.Id == id);
                return Task.FromResult(tour is { HasTrack: true }
                    ? JsonResponse(TrackOf(tour))
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\",\"message\":\"\"}", Encoding.UTF8, "application/json") });
            }

            if (path == "/av/v1/lease" && request.Method == HttpMethod.Post)
            {
                return Task.FromResult(JsonResponse(new LeaseResponse("lease1", DateTime.UtcNow.AddMinutes(2))));
            }

            if (path.StartsWith("/av/v1/lease/", StringComparison.Ordinal))
            {
                return Task.FromResult(request.Method == HttpMethod.Delete
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : JsonResponse(new LeaseResponse("lease1", DateTime.UtcNow.AddMinutes(2))));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };
    }
}
