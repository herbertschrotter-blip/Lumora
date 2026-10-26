using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Lumora.AppConnect;
using Lumora.Tests.HiddenLibraries;

namespace Lumora.Tests.AppConnect;

/// <summary>
/// App-Verbindung Stufe 2 (ADR-0073, Vertrag 1.1 Abschnitte 5 und 7): Kopplung als Anbieter über echte HTTP-Anfragen — Dialog,
/// Geheimnis, eine offene Anfrage, Ablauf, Trennen, Halte-Zusagen. Dialog und Geheimnis-Schutz sind Fakes.
/// </summary>
public sealed class PairingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "pairing-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
    private readonly ScriptedPrompt _prompt = new();
    private ConnectionStore _store = null!;
    private AppConnectServer _server = null!;
    private HttpClient _http = null!;

    private static readonly string[] GipfelbuchWants = ["photos.search", "photos.thumbnail", "photos.original", "background.start"];
    private static readonly string[] GipfelbuchProvides = ["tours.read", "tracks.read", "summits.read", "background.start"];

    public async Task InitializeAsync()
    {
        _store = new ConnectionStore(Path.Combine(_root, "appconnect", "connections.json"), new XorProtector());
        _server = new AppConnectServer(new AppConnectIdentity("0.5.8"), _store, () => "window",
            pairing: new PairingCoordinator(_store, _prompt, _time), leases: new LeaseRegistry(_time));
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Gipfelbuch_koppelt_mit_Code_und_bekommt_nur_die_bestaetigten_Rechte()
    {
        var id = await BeginAsync("gipfelbuch", "482913");

        var info = await _prompt.NextAsync();
        info.Should().Match<PairingPromptInfo>(i => i.AppId == "gipfelbuch" && i.DisplayName == "Gipfelbuch" && i.Code == "482913");
        info.Permissions.Select(p => (p.Key, p.DefaultOn)).Should().Equal(("photos", true), ("tours", true), ("background", false));
        info.Permissions.Single(p => p.Key == "background").Text.Should().Be("Sich gegenseitig im Hintergrund starten");

        (await StatusAsync(id, "gipfelbuch")).GetProperty("status").GetString().Should().Be("pending");
        (await StatusResponseAsync(id, "fremde-app")).StatusCode.Should().Be(HttpStatusCode.NotFound, "Status nur für die App, die gefragt hat");

        // Benutzer nimmt die vorgewählten Zeilen (Fotos, Touren) — ohne Hintergrundstart
        _prompt.Answer(PairingPermissions.Decide(info.Permissions.Where(p => p.DefaultOn)));
        var accepted = await WaitForAsync(id, "gipfelbuch", "accepted");
        var secret = accepted.GetProperty("secret").GetString()!;
        secret.Should().MatchRegex("^[A-Za-z0-9_-]{43}$", "32 Zufallsbytes als Base64url");
        Strings(accepted.GetProperty("grantedRights")).Should().Equal("photos.search", "photos.thumbnail", "photos.original");
        Strings(accepted.GetProperty("acceptedOfferedRights")).Should().Equal("tours.read", "tracks.read", "summits.read");

        _store.Find("gipfelbuch")!.GrantedByPeer.Should().Equal("tours.read", "tracks.read", "summits.read");
        using var withSecret = Authorized(HttpMethod.Get, "/av/v1/info", "gipfelbuch", secret);
        var infoJson = await ReadAsync(await _http.SendAsync(withSecret));
        Strings(infoJson.GetProperty("grantedToYou")).Should().Equal("photos.search", "photos.thumbnail", "photos.original");
        infoJson.GetProperty("mode").GetString().Should().Be("window");
        File.ReadAllText(Path.Combine(_root, "appconnect", "connections.json")).Should().NotContain(secret, "Geheimnis nur verschlüsselt gespeichert");
    }

    [Fact]
    public async Task Nur_eine_offene_Anfrage_und_ungueltige_Anfragen_werden_abgewiesen()
    {
        await BeginAsync("gipfelbuch", "111111");
        using var second = await _http.PostAsJsonAsync("/av/v1/pairing", Request("andere", "222222"));
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        (await ReadAsync(second)).GetProperty("error").GetString().Should().Be("too_many_requests");

        foreach (var bad in new object[]
                 {
                     Request("gipfelbuch", "12345"), Request("lumora", "123456"), Request("Gross", "123456"),
                     new { appId = "gipfelbuch", displayName = "", code = "123456", requestedRights = Array.Empty<string>(), offeredRights = Array.Empty<string>() },
                 })
        {
            using var response = await _http.PostAsJsonAsync("/av/v1/pairing", bad);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        using var garbage = await _http.PostAsync("/av/v1/pairing", new StringContent("{kein json", System.Text.Encoding.UTF8, "application/json"));
        garbage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Abgelehnt_bleibt_ohne_Kopplung_und_nach_fuenf_Minuten_ist_die_Anfrage_abgelaufen()
    {
        var rejected = await BeginAsync("gipfelbuch", "333333");
        await _prompt.NextAsync();
        _prompt.Answer(null);
        (await WaitForAsync(rejected, "gipfelbuch", "rejected")).TryGetProperty("secret", out _).Should().BeFalse();
        _store.All.Should().BeEmpty();

        var late = await BeginAsync("gipfelbuch", "444444");
        var info = await _prompt.NextAsync();
        _time.Advance(PairingCoordinator.Lifetime);
        (await StatusAsync(late, "gipfelbuch")).GetProperty("status").GetString().Should().Be("expired");
        _prompt.Answer(PairingPermissions.Decide(info.Permissions)); // zu spät angenommen
        await Task.Delay(100);
        (await StatusAsync(late, "gipfelbuch")).TryGetProperty("secret", out _).Should().BeFalse("nach Ablauf kein Geheimnis mehr");
        _store.All.Should().BeEmpty();

        _time.Advance(PairingCoordinator.Lifetime);
        (await StatusResponseAsync(late, "gipfelbuch")).StatusCode.Should().Be(HttpStatusCode.NotFound, "nach weiteren 5 Minuten vergessen");
        await BeginAsync("gipfelbuch", "555555"); // wieder frei für eine neue Anfrage
    }

    [Fact]
    public async Task Trennen_loescht_die_Kopplung_danach_401()
    {
        var secret = await PairAsync("gipfelbuch");

        using var unpair = Authorized(HttpMethod.Delete, "/av/v1/pairing", "gipfelbuch", secret);
        using var response = await _http.SendAsync(unpair);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();

        _store.Find("gipfelbuch").Should().BeNull();
        using var again = Authorized(HttpMethod.Delete, "/av/v1/pairing", "gipfelbuch", secret);
        (await _http.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Halte_Zusagen_60_bis_900_Sekunden_verlaengern_und_freigeben()
    {
        var secret = await PairAsync("gipfelbuch");

        using var tooShort = Authorized(HttpMethod.Post, "/av/v1/lease", "gipfelbuch", secret, new { seconds = 30 });
        (await _http.SendAsync(tooShort)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var create = Authorized(HttpMethod.Post, "/av/v1/lease", "gipfelbuch", secret, new { seconds = 120 });
        var lease = await ReadAsync(await _http.SendAsync(create));
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        lease.GetProperty("expiresUtc").GetDateTime().Should().Be(_time.GetUtcNow().AddSeconds(120).UtcDateTime);

        _time.Advance(TimeSpan.FromSeconds(100));
        using var extend = Authorized(HttpMethod.Put, $"/av/v1/lease/{leaseId}", "gipfelbuch", secret);
        (await ReadAsync(await _http.SendAsync(extend))).GetProperty("expiresUtc").GetDateTime()
            .Should().Be(_time.GetUtcNow().AddSeconds(120).UtcDateTime, "ohne Rumpf gilt die bisherige Dauer");
        using var tooLong = Authorized(HttpMethod.Put, $"/av/v1/lease/{leaseId}", "gipfelbuch", secret, new { seconds = 1000 });
        (await _http.SendAsync(tooLong)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var release = Authorized(HttpMethod.Delete, $"/av/v1/lease/{leaseId}", "gipfelbuch", secret);
        (await _http.SendAsync(release)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var releaseAgain = Authorized(HttpMethod.Delete, $"/av/v1/lease/{leaseId}", "gipfelbuch", secret);
        (await _http.SendAsync(releaseAgain)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var short2 = Authorized(HttpMethod.Post, "/av/v1/lease", "gipfelbuch", secret, new { seconds = 60 });
        var expiring = (await ReadAsync(await _http.SendAsync(short2))).GetProperty("leaseId").GetString()!;
        _time.Advance(TimeSpan.FromSeconds(61));
        using var expired = Authorized(HttpMethod.Put, $"/av/v1/lease/{expiring}", "gipfelbuch", secret);
        (await _http.SendAsync(expired)).StatusCode.Should().Be(HttpStatusCode.NotFound, "abgelaufen");
    }

    [Fact]
    public void Gespeicherte_Kopplung_bleibt_nach_Neustart_gueltig_fremder_Schutz_trennt_sie()
    {
        var path = Path.Combine(_root, "store", "connections.json");
        var store = new ConnectionStore(path, new XorProtector());
        store.Add(new AppConnection("gipfelbuch", "Gipfelbuch", ["photos.search"], ["tours.read"], DateTime.UtcNow), "geheim-123");

        var reloaded = new ConnectionStore(path, new XorProtector());
        reloaded.Verify("gipfelbuch", "geheim-123").Should().BeTrue();
        reloaded.Verify("gipfelbuch", "geheim-124").Should().BeFalse();
        reloaded.GrantedTo("gipfelbuch").Should().Equal("photos.search");
        File.ReadAllText(path).Should().NotContain("geheim-123");

        new ConnectionStore(path, new BrokenProtector()).All.Should().BeEmpty("anderer Benutzer/PC: Kopplung gilt als getrennt");
        reloaded.Remove("gipfelbuch").Should().BeTrue();
        new ConnectionStore(path, new XorProtector()).All.Should().BeEmpty();
    }

    [Fact]
    public void Dialogzeilen_buendeln_Rechte_und_ignorieren_Unbekanntes()
    {
        var lines = PairingPermissions.Lines(["photos.search", "photos.people", "photos.future"], ["tracks.read", "neues.recht"]);
        lines.Select(l => l.Key).Should().Equal("photos", "people", "tours");
        lines.Single(l => l.Key == "people").DefaultOn.Should().BeFalse("Personen nur, wenn ausdrücklich erlaubt");
        var decision = PairingPermissions.Decide(lines);
        decision.GrantedRights.Should().Equal("photos.search", "photos.people");
        decision.AcceptedOfferedRights.Should().Equal("tracks.read");
        PairingPermissions.Lines([], ["background.start"]).Single().Text.Should().Be("Im Hintergrund starten");
    }

    private async Task<string> PairAsync(string appId)
    {
        var id = await BeginAsync(appId, "123456");
        var info = await _prompt.NextAsync();
        _prompt.Answer(PairingPermissions.Decide(info.Permissions));
        return (await WaitForAsync(id, appId, "accepted")).GetProperty("secret").GetString()!;
    }

    private async Task<string> BeginAsync(string appId, string code)
    {
        using var response = await _http.PostAsJsonAsync("/av/v1/pairing", Request(appId, code));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return (await ReadAsync(response)).GetProperty("pairingId").GetString()!;
    }

    private static object Request(string appId, string code)
        => new { appId, displayName = appId == "gipfelbuch" ? "Gipfelbuch" : appId, code, requestedRights = GipfelbuchWants, offeredRights = GipfelbuchProvides };

    private async Task<HttpResponseMessage> StatusResponseAsync(string id, string appId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/av/v1/pairing/{id}");
        request.Headers.Add("X-AV-App", appId);
        return await _http.SendAsync(request);
    }

    private async Task<JsonElement> StatusAsync(string id, string appId) => await ReadAsync(await StatusResponseAsync(id, appId));

    private async Task<JsonElement> WaitForAsync(string id, string appId, string status)
    {
        for (var i = 0; i < 100; i++)
        {
            var json = await StatusAsync(id, appId);
            if (json.GetProperty("status").GetString() == status)
            {
                return json;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Status {status} nicht erreicht");
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string appId, string secret, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add("X-AV-App", appId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static IEnumerable<string?> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString());

    /// <summary>Dialog, dessen Antwort der Test gibt.</summary>
    private sealed class ScriptedPrompt : IPairingPrompt
    {
        private TaskCompletionSource<PairingDecision?>? _answer;
        private TaskCompletionSource<PairingPromptInfo> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PairingDecision?> AskAsync(PairingPromptInfo info, CancellationToken ct)
        {
            _answer = new TaskCompletionSource<PairingDecision?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _next.TrySetResult(info);
            return _answer.Task;
        }

        public async Task<PairingPromptInfo> NextAsync()
        {
            var info = await _next.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _next = new TaskCompletionSource<PairingPromptInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            return info;
        }

        public void Answer(PairingDecision? decision) => _answer!.TrySetResult(decision);
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] protectedData) => Protect(protectedData);
    }

    private sealed class BrokenProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain;

        public byte[] Unprotect(byte[] protectedData) => throw new CryptographicException("anderer Benutzer");
    }
}
