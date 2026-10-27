using System.Security.Cryptography;
using Lumora.AppConnect;
using Lumora.Tests.HiddenLibraries;

namespace Lumora.Tests.AppConnect;

/// <summary>
/// App-Verbindung Stufe 3 (ADR-0073, Vertrag 1.1 Abschnitte 4, 5, 9): Lumora als Abfrager gegen eine echte zweite Schnittstelle
/// im Test (Lumora-Code als „Gipfelbuch“) — Apps finden, koppeln mit Code, abgelehnt/abgelaufen, Rechte nachführen, 401 trennt,
/// Trennen, Gegenseite starten. Dialog und Geheimnis-Schutz sind Fakes.
/// </summary>
public sealed class AppConnectClientTests : IAsyncLifetime
{
    private static readonly string[] PeerProvides = [Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead, Rights.BackgroundStart];
    private static readonly string[] PeerWants = [Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal, Rights.BackgroundStart];
    private static readonly AppConnectClientIdentity Own = AppConnectClientIdentity.Lumora with { AppId = "lumoratest" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "client-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _peerTime = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
    private readonly ScriptedPrompt _prompt = new();
    private readonly List<string> _started = [];
    private RegistrationFiles _registrations = null!;
    private ConnectionStore _peerStore = null!;
    private ConnectionStore _store = null!;
    private AppConnectServer _peer = null!;
    private AppConnectClient _client = null!;

    public async Task InitializeAsync()
    {
        _registrations = new RegistrationFiles(Path.Combine(_root, "AppVerbindung", "apps"));
        _peerStore = new ConnectionStore(Path.Combine(_root, "peer", "connections.json"), new XorProtector());
        _store = new ConnectionStore(Path.Combine(_root, "lumora", "connections.json"), new XorProtector());
        _peer = new AppConnectServer(new AppConnectIdentity("0.6.3"), _peerStore, () => "window",
            pairing: new PairingCoordinator(_peerStore, _prompt, _peerTime), leases: new LeaseRegistry(_peerTime));
        await _peer.StartAsync();
        WritePeer(running: true);
        _registrations.Write(Registration("lumoratest", "Lumora", running: true, port: 1)); // eigene Anmeldedatei
        _client = NewClient(start: path =>
        {
            _started.Add(path);
            return true;
        });
    }

    public async Task DisposeAsync()
    {
        await _peer.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Findet_andere_Apps_und_fragt_passende_Rechte_an()
    {
        var peers = _client.Peers();
        peers.Select(p => p.AppId).Should().Equal("gipfelbuch");
        RegistrationFiles.IsAlive(peers[0]).Should().BeTrue();

        var (requested, offered) = _client.RightsFor(peers[0]);
        requested.Should().Equal(Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead, Rights.BackgroundStart);
        offered.Should().Equal(Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal, Rights.BackgroundStart);

        AppConnectClient.NewCode().Should().MatchRegex("^[0-9]{6}$");
        AppConnectClient.SameMajor("1.1").Should().BeTrue();
        AppConnectClient.SameMajor("1.0").Should().BeTrue();
        AppConnectClient.SameMajor("2.0").Should().BeFalse("andere Hauptversion → contract_mismatch");
        AppConnectClient.SameMajor(null).Should().BeFalse();
    }

    [Fact]
    public async Task Kopplung_angenommen_beide_Seiten_kennen_dasselbe_Geheimnis_und_die_Rechte()
    {
        var peer = _client.Peers().Single();
        var pairing = _client.PairAsync(peer, "135790");

        var info = await _prompt.NextAsync();
        info.Should().Match<PairingPromptInfo>(i => i.AppId == "lumoratest" && i.Code == "135790");
        _prompt.Answer(new PairingDecision([Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead], [Rights.PhotosSearch, Rights.PhotosThumbnail]));

        (await pairing).Should().Be(PairingOutcome.Accepted);
        var connection = _store.Find("gipfelbuch")!;
        connection.GrantedByPeer.Should().Equal(Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead);
        connection.GrantedToPeer.Should().Equal(Rights.PhotosSearch, Rights.PhotosThumbnail);
        _store.Secret("gipfelbuch").Should().NotBeNull().And.Be(_peerStore.Secret("lumoratest"), "ein Geheimnis für beide Richtungen");

        var withSecret = await _client.InfoAsync(peer);
        withSecret.GrantedToYou.Should().Equal(Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead);
        withSecret.Mode.Should().Be("window");

        // Gegenseite ändert ihre Erlaubnis → Lumora erfährt es über /info (Abschnitt 5, Schritt 7)
        _peerStore.Add(_peerStore.Find("lumoratest")! with { GrantedToPeer = [Rights.ToursRead] }, _peerStore.Secret("lumoratest")!);
        await _client.RefreshGrantsAsync(peer);
        _store.Find("gipfelbuch")!.GrantedByPeer.Should().Equal(Rights.ToursRead);
    }

    [Fact]
    public async Task Abgelehnt_oder_abgelaufen_bleibt_ohne_Kopplung()
    {
        var peer = _client.Peers().Single();
        var rejected = _client.PairAsync(peer, "111111");
        await _prompt.NextAsync();
        _prompt.Answer(null);
        (await rejected).Should().Be(PairingOutcome.Rejected);

        var expired = _client.PairAsync(peer, "222222");
        await _prompt.NextAsync();
        _peerTime.Advance(PairingCoordinator.Lifetime);
        (await expired).Should().Be(PairingOutcome.Expired);

        _store.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Antwort_401_der_Gegenseite_trennt_auch_in_Lumora()
    {
        var peer = await PairAsync();
        _peerStore.Remove("lumoratest"); // Gegenseite hat getrennt

        var refresh = () => _client.RefreshGrantsAsync(peer);

        (await refresh.Should().ThrowAsync<AppConnectException>()).Which.Code.Should().Be(ErrorCodes.NotPaired);
        _store.Find("gipfelbuch").Should().BeNull("/info ohne grantedToYou trotz Geheimnis → Geheimnis und Rechte weg");

        // Geschützter Weg: 401 not_paired (Abschnitt 11) trennt ebenso
        var again = await PairAsync();
        _peerStore.Remove("lumoratest");
        await _client.UnpairAsync("gipfelbuch"); // DELETE /pairing → 401
        _store.Find("gipfelbuch").Should().BeNull();
        RegistrationFiles.IsAlive(again).Should().BeTrue();
    }

    [Fact]
    public async Task Trennen_meldet_es_der_Gegenseite_und_loescht_lokal_auch_wenn_sie_nicht_laeuft()
    {
        await PairAsync();
        await _client.UnpairAsync("gipfelbuch");
        _store.Find("gipfelbuch").Should().BeNull();
        _peerStore.Find("lumoratest").Should().BeNull("DELETE /pairing hat die Gegenseite erreicht");

        await PairAsync();
        WritePeer(running: false);
        await _client.UnpairAsync("gipfelbuch");
        _store.Find("gipfelbuch").Should().BeNull("lokal immer gelöscht");
        _peerStore.Find("lumoratest").Should().NotBeNull("die Gegenseite lief nicht und erfährt es bei der nächsten Anfrage (401)");
    }

    [Fact]
    public async Task Nicht_laufende_Gegenseite_wird_normal_gestartet_und_abgewartet()
    {
        WritePeer(running: false);
        var client = NewClient(start: path =>
        {
            _started.Add(path);
            WritePeer(running: true); // die gestartete App meldet sich mit Port
            return true;
        });

        var running = await client.EnsureRunningAsync("gipfelbuch");

        RegistrationFiles.IsAlive(running).Should().BeTrue();
        _started.Should().Equal(@"C:\Programme\Gipfelbuch\Gipfelbuch.exe");
        (await client.EnsureRunningAsync("gipfelbuch")).Port.Should().Be(_peer.Port, "läuft schon — kein zweiter Start");
        _started.Should().HaveCount(1);

        WritePeer(running: false);
        var neverReady = () => NewClient(start: _ => true).EnsureRunningAsync("gipfelbuch");
        (await neverReady.Should().ThrowAsync<AppConnectException>()).Which.Code.Should().Be(ErrorCodes.Unavailable);
        var notStartable = () => NewClient(start: _ => false).EnsureRunningAsync("gipfelbuch");
        (await notStartable.Should().ThrowAsync<AppConnectException>()).Which.Message.Should().Contain("nicht starten");

        var notRunning = () => _client.InfoAsync(_registrations.Read("gipfelbuch")!);
        (await notRunning.Should().ThrowAsync<AppConnectException>()).Which.Code.Should().Be(ErrorCodes.Unavailable);
    }

    [Fact]
    public void Anmeldedatei_eines_abgestuerzten_Prozesses_gilt_nicht_als_laufend()
    {
        RegistrationFiles.IsAlive(Registration("x", "X", running: true, port: 5000) with { Pid = int.MaxValue }).Should().BeFalse("Prozess gibt es nicht");
        RegistrationFiles.IsAlive(Registration("x", "X", running: true, port: null)).Should().BeFalse("ohne Port nicht bereit");
        RegistrationFiles.IsAlive(Registration("x", "X", running: false, port: 5000)).Should().BeFalse();
    }

    private async Task<AppRegistration> PairAsync()
    {
        var peer = _client.Peers().Single();
        var pairing = _client.PairAsync(peer, AppConnectClient.NewCode());
        var info = await _prompt.NextAsync();
        _prompt.Answer(PairingPermissions.Decide(info.Permissions));
        (await pairing).Should().Be(PairingOutcome.Accepted);
        return peer;
    }

    private AppConnectClient NewClient(Func<string, bool> start)
        => new(_registrations, _store, own: Own,
            timings: new AppConnectClientTimings(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400)),
            start: start);

    private void WritePeer(bool running)
        => _registrations.Write(Registration("gipfelbuch", "Gipfelbuch", running, running ? _peer.Port : null) with
        {
            ExePath = @"C:\Programme\Gipfelbuch\Gipfelbuch.exe",
            Provides = PeerProvides,
            Wants = PeerWants,
        });

    private static AppRegistration Registration(string appId, string name, bool running, int? port) => new()
    {
        AppId = appId,
        DisplayName = name,
        AppVersion = "1.0",
        ContractVersion = "1.1",
        ExePath = @"C:\Programme\" + name + ".exe",
        BackgroundArg = "--background",
        Running = running,
        Pid = running ? Environment.ProcessId : null,
        Port = port,
        Mode = running ? "window" : null,
        Provides = [],
        Wants = [],
    };

    /// <summary>Dialog der Gegenseite, dessen Antwort der Test gibt.</summary>
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
}
