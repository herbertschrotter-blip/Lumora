using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumora.AppConnect;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Thumbnails;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.AppConnect;

/// <summary>
/// App-Verbindung Stufe 5 (ADR-0073, Vertrag 1.1 Abschnitt 8.1): Foto-Endpunkte gegen einen echten Testkatalog über HTTP —
/// Suche nach Zeitraum/Gebiet/Art mit Seiten, Felder nach Schema, Rechte je Anfrage, nur freigegebene Bibliotheken, Prüfsumme,
/// Personen nur mit Recht, Vorschaubilder (Größe, ETag/304, busy), Original mit „available“.
/// </summary>
public sealed class PhotoEndpointsTests : IAsyncLifetime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");

    private TempLibraryFixture _fx = null!;
    private ConnectionStore _store = null!;
    private AppConnectServer _server = null!;
    private HttpClient _http = null!;
    private readonly FakeThumbnails _thumbnails = new();
    private readonly FakeRenderer _renderer = new();
    private readonly string _secret = ConnectionStore.NewSecret();
    private int _libraryA;
    private int _libraryB;
    private int _a;
    private int _b;
    private int _c;
    private int _d;

    public async Task InitializeAsync()
    {
        _fx = await TempLibraryFixture.CreateAsync();
        var a = await _fx.CreateLibraryAsync("Familienfotos");
        _libraryA = a.Id;
        _libraryB = (await _fx.CreateLibraryAsync("Andere")).Id;
        _fx.WriteLibraryFile("tour/a.jpg");
        _fx.WriteLibraryFile("tour/b.jpg");
        _fx.WriteLibraryFile("tour/c.mp4");
        _fx.WriteLibraryFile("tour/d.jpg");
        await _fx.ScanAsync(_libraryA);
        _a = (await _fx.FindFileAsync("a.jpg"))!.Id;
        _b = (await _fx.FindFileAsync("b.jpg"))!.Id;
        _c = (await _fx.FindFileAsync("c.mp4"))!.Id;
        _d = (await _fx.FindFileAsync("d.jpg"))!.Id;
        Sql($"""
            UPDATE Files SET CapturedAt = {Unix("2020-03-15T13:56:10Z")}, CapturedAtOffset = 60, Latitude = 47.0707, Longitude = 15.4395,
              GeoAccuracy = 1, GeoSource = 1, Rating = 4, IsFavorite = 1, Width = 4000, Height = 3000 WHERE Id = {_a};
            UPDATE Files SET CapturedAt = {Unix("2020-03-15T15:00:00Z")}, CapturedAtOffset = NULL, Rating = NULL, Width = 3000, Height = 4000 WHERE Id = {_b};
            UPDATE Files SET CapturedAt = {Unix("2020-03-16T10:00:00Z")}, CapturedAtOffset = 60, DurationMs = 12500, Latitude = 47.2, Longitude = 15.5,
              GeoSource = 2, Width = 1920, Height = 1080 WHERE Id = {_c};
            UPDATE Files SET CapturedAt = {Unix("2020-03-15T14:00:00Z")}, LibraryId = {_libraryB} WHERE Id = {_d};
            """);

        _store = new ConnectionStore(Path.Combine(_fx.RootDir, "appconnect", "connections.json"), new XorProtector());
        Pair([Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal], [_libraryA]);
        var catalog = new CatalogPhotoCatalog(_fx.ContextFactory, _thumbnails, _renderer, Path.Combine(_fx.RootDir, "av-tmp"), Zone)
        {
            ThumbnailWait = TimeSpan.FromMilliseconds(200),
        };
        _server = new AppConnectServer(new AppConnectIdentity("0.5.14"), _store, () => "window", photos: catalog);
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _fx.Dispose();
    }

    [Fact]
    public async Task Suche_nach_Zeitraum_liefert_Felder_nach_Schema_sortiert_und_nur_aus_freigegebenen_Bibliotheken()
    {
        var (status, json) = await GetAsync("/av/v1/photos?fromUtc=2020-03-01T00:00:00Z&toUtc=2020-04-01T00:00:00Z");
        status.Should().Be(HttpStatusCode.OK);
        Ids(json).Should().Equal(Id(_a), Id(_b), Id(_c)); // d liegt in einer nicht freigegebenen Bibliothek
        json.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null, "nextCursor steht immer drin");

        var a = json.GetProperty("items")[0];
        a.GetProperty("mediaType").GetString().Should().Be("photo");
        a.GetProperty("taken").GetString().Should().Be("2020-03-15T14:56:10+01:00");
        a.GetProperty("takenUtc").GetString().Should().Be("2020-03-15T13:56:10Z");
        a.TryGetProperty("takenLocal", out _).Should().BeFalse();
        a.GetProperty("lat").GetDouble().Should().Be(47.0707);
        a.GetProperty("lon").GetDouble().Should().Be(15.4395);
        a.GetProperty("geoAccuracy").GetString().Should().Be("precise");
        a.GetProperty("geoSource").GetString().Should().Be("exif");
        a.GetProperty("rating").GetInt32().Should().Be(4);
        a.GetProperty("favorite").GetBoolean().Should().BeTrue();
        a.GetProperty("width").GetInt32().Should().Be(4000);
        a.GetProperty("libraryName").GetString().Should().Be("Familienfotos");
        a.TryGetProperty("people", out _).Should().BeFalse("ohne Recht photos.people fehlt das Feld");
        a.TryGetProperty("checksum", out _).Should().BeFalse("noch keine Prüfsumme berechnet");

        var b = json.GetProperty("items")[1];
        b.TryGetProperty("taken", out _).Should().BeFalse("Abstand unbekannt");
        b.GetProperty("takenLocal").GetString().Should().Be("2020-03-15T16:00:00", "Wanduhrzeit in der Zeitzone des PCs");
        b.TryGetProperty("lat", out _).Should().BeFalse();
        b.GetProperty("rating").GetInt32().Should().Be(0);

        var c = json.GetProperty("items")[2];
        c.GetProperty("mediaType").GetString().Should().Be("video");
        c.GetProperty("durationS").GetDouble().Should().Be(12.5);
        c.GetProperty("geoSource").GetString().Should().Be("video");
    }

    [Fact]
    public async Task Seiten_Gebiet_Art_und_ungueltige_Parameter()
    {
        var (_, first) = await GetAsync("/av/v1/photos?fromUtc=2020-03-01T00:00:00Z&limit=2");
        Ids(first).Should().Equal(Id(_a), Id(_b));
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var (_, second) = await GetAsync("/av/v1/photos?fromUtc=2020-03-01T00:00:00Z&limit=2&cursor=" + cursor);
        Ids(second).Should().Equal(Id(_c));
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        Ids((await GetAsync("/av/v1/photos?bbox=15.4,47.0,15.45,47.1")).Json).Should().Equal(Id(_a));
        Ids((await GetAsync("/av/v1/photos?bbox=15.0,46.0,16.0,48.0&mediaType=video")).Json).Should().Equal(Id(_c));
        Ids((await GetAsync("/av/v1/photos?toUtc=2020-03-15T14:00:00Z")).Json).Should().Equal(Id(_a));

        foreach (var bad in new[]
                 {
                     "", "?mediaType=photo", "?fromUtc=gestern", "?fromUtc=2020-03-01T00:00:00Z&limit=0", "?fromUtc=2020-03-01T00:00:00Z&limit=501",
                     "?bbox=1,2,3", "?bbox=16,47,15,48", "?fromUtc=2020-03-01T00:00:00Z&cursor=%21%21", "?fromUtc=2020-03-01T00:00:00Z&mediaType=foto",
                     "?fromUtc=2020-04-01T00:00:00Z&toUtc=2020-03-01T00:00:00Z",
                 })
        {
            var (status, json) = await GetAsync("/av/v1/photos" + bad);
            status.Should().Be(HttpStatusCode.BadRequest, bad);
            json.GetProperty("error").GetString().Should().Be(ErrorCodes.BadRequest);
        }
    }

    [Fact]
    public async Task Recht_wird_bei_jeder_Anfrage_geprueft_ohne_Bibliothek_gibt_es_nichts()
    {
        Pair([Rights.PhotosThumbnail], [_libraryA]);
        foreach (var path in new[] { "/av/v1/photos?fromUtc=2020-03-01T00:00:00Z", $"/av/v1/photos/{Id(_a)}", $"/av/v1/photos/{Id(_a)}/original" })
        {
            var (status, json) = await GetAsync(path);
            status.Should().Be(HttpStatusCode.Forbidden, path);
            json.GetProperty("error").GetString().Should().Be(ErrorCodes.MissingRight);
        }

        Pair([Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal], []);
        Ids((await GetAsync("/av/v1/photos?fromUtc=2020-03-01T00:00:00Z")).Json).Should().BeEmpty("neue Kopplungen sehen keine Bibliothek");
        (await GetAsync($"/av/v1/photos/{Id(_a)}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync($"/av/v1/photos/{Id(_a)}/thumbnail")).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync($"/av/v1/photos/{Id(_a)}/original")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Einzeln_und_ueber_die_Pruefsumme_nur_aus_freigegebenen_Bibliotheken()
    {
        (await GetAsync($"/av/v1/photos/{Id(_a)}")).Json.GetProperty("id").GetString().Should().Be(Id(_a));
        (await GetAsync($"/av/v1/photos/{Id(_d)}")).Status.Should().Be(HttpStatusCode.NotFound, "andere Bibliothek: kein Hinweis");
        (await GetAsync("/av/v1/photos/abc")).Status.Should().Be(HttpStatusCode.NotFound);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("inhalt"));
        Fingerprint(_a, hash);
        Fingerprint(_d, hash);
        var checksum = "sha256:" + Convert.ToHexStringLower(hash);
        var (status, json) = await GetAsync("/av/v1/photos/by-checksum/" + checksum);
        status.Should().Be(HttpStatusCode.OK);
        Ids(json).Should().Equal(Id(_a));
        json.GetProperty("items")[0].GetProperty("checksum").GetString().Should().Be(checksum);

        Sql($"UPDATE FileFingerprints SET ContentSize = ContentSize + 1 WHERE FileId = {_a};"); // Datei seither geändert
        Ids((await GetAsync("/av/v1/photos/by-checksum/" + checksum)).Json).Should().BeEmpty();
        (await GetAsync("/av/v1/photos/by-checksum/sha256:XYZ")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Personen_nur_mit_Recht_und_ohne_ausgeblendete()
    {
        await using (var ctx = _fx.CreateContext())
        {
            var group = (await ctx.Libraries.FindAsync(_libraryA))!.GroupId;
            var anna = new Person { Name = "Anna", FirstName = "Anna", GroupId = group, CreatedAtUtc = DateTime.UtcNow };
            var bert = new Person { Name = "Bert", FirstName = "Bert", GroupId = group, CreatedAtUtc = DateTime.UtcNow, IsHidden = true };
            ctx.Persons.AddRange(anna, bert);
            await ctx.SaveChangesAsync();
            ctx.Faces.AddRange(Face(_a, anna.Id, FaceState.Confirmed), Face(_a, bert.Id, FaceState.Confirmed), Face(_b, anna.Id, FaceState.Suggested));
            await ctx.SaveChangesAsync();
        }

        Pair([Rights.PhotosSearch, Rights.PhotosPeople], [_libraryA]);
        var items = (await GetAsync("/av/v1/photos?fromUtc=2020-03-01T00:00:00Z")).Json.GetProperty("items");
        items[0].GetProperty("people").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Equal("Anna");
        items[1].GetProperty("people").GetArrayLength().Should().Be(0, "nur bestätigte Gesichter");
    }

    [Fact]
    public async Task Vorschaubilder_in_der_Groesse_mit_ETag_und_busy()
    {
        _thumbnails.Ready = true;
        var small = await SendAsync($"/av/v1/photos/{Id(_a)}/thumbnail?size=256");
        small.StatusCode.Should().Be(HttpStatusCode.OK);
        small.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await small.Content.ReadAsStringAsync()).Should().Be("cache-" + _a + ".jpg|192", "aus dem Vorschaubild (533 × 400) auf 256 verkleinert");
        var etag = small.Headers.ETag!.Tag;

        var large = await SendAsync($"/av/v1/photos/{Id(_a)}/thumbnail?size=1024");
        (await large.Content.ReadAsStringAsync()).Should().Be("a.jpg|768", "groß aus dem Original (4000 × 3000)");

        var video = await SendAsync($"/av/v1/photos/{Id(_c)}/thumbnail?size=1024");
        (await video.Content.ReadAsStringAsync()).Should().Be("vorschau", "Vorschaubild passt schon — unverändert");

        var unreadable = await SendAsync($"/av/v1/photos/{Id(_b)}/thumbnail?size=1024");
        (await unreadable.Content.ReadAsStringAsync()).Should().Be("vorschau", "Original nicht lesbar → Vorschaubild");

        var again = new HttpRequestMessage(HttpMethod.Get, $"/av/v1/photos/{Id(_a)}/thumbnail?size=256");
        again.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        (await SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.NotModified);

        (await GetAsync($"/av/v1/photos/{Id(_a)}/thumbnail?size=300")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync($"/av/v1/photos/{Id(_d)}/thumbnail")).Status.Should().Be(HttpStatusCode.NotFound);

        _thumbnails.Ready = false; // Vorschaubild kommt nicht rechtzeitig
        var busy = await SendAsync($"/av/v1/photos/{Id(_b)}/thumbnail");
        busy.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        busy.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(10));
        JsonDocument.Parse(await busy.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be(ErrorCodes.Busy);
    }

    [Fact]
    public async Task Original_nennt_Pfad_und_ob_es_erreichbar_ist()
    {
        var (status, json) = await GetAsync($"/av/v1/photos/{Id(_a)}/original");
        status.Should().Be(HttpStatusCode.OK);
        var path = json.GetProperty("path").GetString()!;
        path.Should().EndWith("a.jpg");
        json.GetProperty("available").GetBoolean().Should().BeTrue();

        File.Delete(path); // z. B. Laufwerk fehlt
        (await GetAsync($"/av/v1/photos/{Id(_a)}/original")).Json.GetProperty("available").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Bibliotheken_je_App_bleiben_gespeichert_neue_Kopplung_sieht_keine()
    {
        _store.SetLibraries("gipfelbuch", [_libraryB, _libraryA, _libraryA]);
        var path = Path.Combine(_fx.RootDir, "appconnect", "connections.json");
        new ConnectionStore(path, new XorProtector()).LibrariesOf("gipfelbuch").Should().Equal(Math.Min(_libraryA, _libraryB), Math.Max(_libraryA, _libraryB));

        _store.SetGrantedToPeer("gipfelbuch", [Rights.PhotosSearch]);
        _store.LibrariesOf("gipfelbuch").Should().HaveCount(2, "Rechte ändern lässt die Bibliotheken stehen");

        _store.Add(new AppConnection("gipfelbuch", "Gipfelbuch", [Rights.PhotosSearch], [], DateTime.UtcNow), ConnectionStore.NewSecret());
        _store.LibrariesOf("gipfelbuch").Should().BeEmpty("neu gekoppelt");
    }

    // --- Hilfen -----------------------------------------------------------------------------------------------------

    private void Pair(string[] rights, int[] libraries)
        => _store.Add(new AppConnection("gipfelbuch", "Gipfelbuch", rights, [], DateTime.UtcNow) { Libraries = libraries }, _secret);

    private static string Id(int fileId) => "lum-" + fileId;

    private static long Unix(string utc) => DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds();

    private static IEnumerable<string?> Ids(JsonElement json) => json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString());

    private static Face Face(int fileId, int personId, FaceState state) => new()
    {
        FileId = fileId,
        Landmarks = [],
        Embedding = [],
        ModelVersion = "test",
        PersonId = state == FaceState.Confirmed ? personId : null,
        SuggestedPersonId = state == FaceState.Suggested ? personId : null,
        State = state,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private void Fingerprint(int fileId, byte[] hash)
    {
        using var connection = _fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO FileFingerprints (FileId, ContentHash, ContentSize, ContentModifiedAt, ImageLowDetail) "
                          + "SELECT Id, $hash, Size, ModifiedAtUtc, 0 FROM Files WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.ExecuteNonQuery();
    }

    private void Sql(string sql)
    {
        using var connection = _fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private Task<HttpResponseMessage> SendAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        request.Headers.Add(AppConnectContract.AppHeader, "gipfelbuch");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
        return _http.SendAsync(request);
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> GetAsync(string path)
    {
        using var response = await SendAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>Vorschaubild-Dienst: meldet ein Vorschaubild im Cache sofort — oder gar nicht.</summary>
    private sealed class FakeThumbnails : IThumbnailService
    {
        public bool Ready { get; set; }

        public int ThumbnailSize => ThumbnailSizing.ShortEdge;

        public int PendingCount => 0;

        public event Action<ThumbnailReadyEventArgs>? ThumbnailReady;

        public Task RequestThumbnailsAsync(IReadOnlyList<ThumbnailRequest> requests, CancellationToken cancellationToken = default)
        {
            if (Ready)
            {
                foreach (var r in requests)
                {
                    var path = Path.Combine(Path.GetDirectoryName(r.SourcePath)!, $"cache-{r.FileId}.jpg");
                    File.WriteAllText(path, "vorschau");
                    ThumbnailReady?.Invoke(new ThumbnailReadyEventArgs(r.LibraryId, r.FileId, path));
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Renderer: Vorschaubilder 533 × 400, a.jpg 4000 × 3000, b.jpg nicht lesbar; schreibt „Quelle|kurze Kante“.</summary>
    private sealed class FakeRenderer : IImageThumbnailRenderer
    {
        public ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetShortEdge)
        {
            File.WriteAllText(targetThumbPath, $"{Path.GetFileName(sourcePath)}|{targetShortEdge}");
            return ThumbnailRenderResult.Ok(1, 1, 1);
        }

        public (int Width, int Height)? ReadDimensions(string sourcePath)
            => Path.GetFileName(sourcePath) switch
            {
                var n when n.StartsWith("cache-", StringComparison.Ordinal) => (533, 400),
                "a.jpg" => (4000, 3000),
                "b.jpg" => null, // Format, das der Renderer nicht liest
                _ => (3000, 4000),
            };
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] protectedData) => Protect(protectedData);
    }
}
