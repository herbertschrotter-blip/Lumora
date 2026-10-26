using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Lumora.AppConnect;

namespace Lumora.Tests.AppConnect;

/// <summary>
/// App-Verbindung Stufe 1 (ADR-0073, Vertrag 1.1): echte HTTP-Anfragen gegen die Schnittstelle auf 127.0.0.1 — Schutzregeln
/// aus Abschnitt 3, Fehlerformat aus Abschnitt 11, <c>GET /info</c> aus Abschnitt 7.
/// </summary>
public sealed class AppConnectServerTests : IAsyncLifetime
{
    private readonly FakePeers _peers = new();
    private long _second = 1000;
    private AppConnectServer _server = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _server = new AppConnectServer(new AppConnectIdentity("9.9.9"), _peers, () => "window", second: () => _second);
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Info_without_secret_names_lumora_and_the_contract_version_only()
    {
        using var response = await _http.GetAsync("/av/v1/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var json = await ReadAsync(response);
        json.GetProperty("appId").GetString().Should().Be("lumora");
        json.GetProperty("displayName").GetString().Should().Be("Lumora");
        json.GetProperty("appVersion").GetString().Should().Be("9.9.9");
        json.GetProperty("contractVersion").GetString().Should().Be("1.1");
        json.GetProperty("provides").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("photos.search", "photos.thumbnail", "photos.original", "photos.people", "background.start");
        json.TryGetProperty("grantedToYou", out _).Should().BeFalse("ohne Geheimnis keine Rechte");
        json.TryGetProperty("mode", out _).Should().BeFalse();
        response.Headers.Contains("Server").Should().BeFalse();
    }

    [Fact]
    public async Task Info_with_a_valid_secret_adds_granted_rights_and_mode()
    {
        _peers.Paired["gipfelbuch"] = ("geheim", ["photos.search"]);
        using var request = Authorized(HttpMethod.Get, "/av/v1/info", "gipfelbuch", "geheim");

        var json = await ReadAsync(await _http.SendAsync(request));

        json.GetProperty("grantedToYou").EnumerateArray().Select(e => e.GetString()).Should().Equal("photos.search");
        json.GetProperty("mode").GetString().Should().Be("window");
    }

    [Fact]
    public async Task Requests_from_web_pages_or_with_a_foreign_host_are_refused()
    {
        using var withOrigin = new HttpRequestMessage(HttpMethod.Get, "/av/v1/info");
        withOrigin.Headers.Add("Origin", "https://example.org");
        await ExpectErrorAsync(await _http.SendAsync(withOrigin), HttpStatusCode.Forbidden, "forbidden_origin");

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/av/v1/info");
        rebinding.Headers.Host = $"localhost:{_server.Port}";
        await ExpectErrorAsync(await _http.SendAsync(rebinding), HttpStatusCode.Forbidden, "forbidden_host");

        using var otherHost = new HttpRequestMessage(HttpMethod.Get, "/av/v1/info");
        otherHost.Headers.Host = "evil.example:80";
        await ExpectErrorAsync(await _http.SendAsync(otherHost), HttpStatusCode.Forbidden, "forbidden_host");
    }

    [Fact]
    public async Task No_cors_headers_even_when_a_browser_asks()
    {
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/av/v1/info");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using var response = await _http.SendAsync(preflight);
        response.Headers.Select(h => h.Key).Should().NotContain(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));

        using var info = await _http.GetAsync("/av/v1/info");
        info.Headers.Select(h => h.Key).Should().NotContain(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Protected_paths_need_a_pairing_and_unknown_paths_follow_the_error_format()
    {
        await ExpectErrorAsync(await _http.GetAsync("/av/v1/photos?fromUtc=2020-01-01T00:00:00Z"), HttpStatusCode.Unauthorized, "not_paired");

        using var wrongSecret = Authorized(HttpMethod.Get, "/av/v1/photos", "gipfelbuch", "falsch");
        await ExpectErrorAsync(await _http.SendAsync(wrongSecret), HttpStatusCode.Unauthorized, "not_paired");

        _peers.Paired["gipfelbuch"] = ("geheim", []);
        using var paired = Authorized(HttpMethod.Get, "/av/v1/gibt-es-nicht", "gipfelbuch", "geheim");
        await ExpectErrorAsync(await _http.SendAsync(paired), HttpStatusCode.NotFound, "not_found");

        await ExpectErrorAsync(await _http.GetAsync("/av/v2/info"), (HttpStatusCode)426, "contract_mismatch");
        await ExpectErrorAsync(await _http.GetAsync("/av/v1"), HttpStatusCode.NotFound, "not_found");
        await ExpectErrorAsync(await _http.GetAsync("/irgendwas"), HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData("GET", "/av/v1/info", true)]
    [InlineData("POST", "/av/v1/pairing", true)]
    [InlineData("GET", "/av/v1/pairing/abc", true)]
    [InlineData("DELETE", "/av/v1/pairing", false)]
    [InlineData("POST", "/av/v1/info", false)]
    [InlineData("GET", "/av/v1/photos", false)]
    [InlineData("POST", "/av/v1/lease", false)]
    public void Only_info_and_the_pairing_steps_are_open(string method, string path, bool open)
        => AppConnectServer.IsOpen(method, path).Should().Be(open);

    [Fact]
    public async Task More_than_fifty_requests_per_second_get_429_with_retry_after()
    {
        for (var i = 0; i < AppConnectServer.MaxRequestsPerSecond; i++)
        {
            (await _http.GetAsync("/av/v1/info")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var tooMany = await _http.GetAsync("/av/v1/info");
        await ExpectErrorAsync(tooMany, HttpStatusCode.TooManyRequests, "too_many_requests");
        tooMany.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));

        _second++;
        (await _http.GetAsync("/av/v1/info")).StatusCode.Should().Be(HttpStatusCode.OK, "neue Sekunde, neues Kontingent");
    }

    [Fact]
    public async Task Listens_only_on_ipv4_loopback()
    {
        using var ipv6 = new TcpClient(AddressFamily.InterNetworkV6);
        var connect = async () => await ipv6.ConnectAsync(IPAddress.IPv6Loopback, _server.Port);
        await connect.Should().ThrowAsync<SocketException>("nur 127.0.0.1, nie ::1 oder 0.0.0.0");
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string appId, string secret)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add("X-AV-App", appId);
        return request;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task ExpectErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            response.StatusCode.Should().Be(status);
            var json = await ReadAsync(response);
            json.GetProperty("error").GetString().Should().Be(code);
            json.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
            json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["error", "message"]);
        }
    }

    private sealed class FakePeers : IAppConnectPeers
    {
        public Dictionary<string, (string Secret, string[] Rights)> Paired { get; } = new();

        public bool Verify(string appId, string secret) => Paired.TryGetValue(appId, out var p) && p.Secret == secret;

        public IReadOnlyList<string> GrantedTo(string appId) => Paired.TryGetValue(appId, out var p) ? p.Rights : [];
    }
}
