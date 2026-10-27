using System.Diagnostics;
using System.IO;
using System.Windows;
using Lumora.AppConnect;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;
using Wpf.Ui.Appearance;

namespace Lumora.Wpf.Tests.AppConnect;

/// <summary>
/// Einstellungen › Verbundene Apps (ADR-0073 Stufe 4, Mockup 11) gegen eine echte zweite Schnittstelle im Test (Lumora-Code als
/// „Gipfelbuch“): Leer-Zustand, Verbinden mit Code (angenommen, abgelehnt, abgebrochen), Rechte in beide Richtungen und ändern,
/// von drüben getrennt, Trennen mit Rückfrage, Starten/Öffnen; seit Stufe 6 Schalter und „Im Hintergrund beenden“; Reiter
/// hell/dunkel ohne Binding-Fehler.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ConnectedAppsTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string[] PeerProvides = [Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead, Rights.BackgroundStart];
    private static readonly string[] PeerWants = [Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal, Rights.BackgroundStart];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", "apps-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedPrompt _prompt = new();
    private readonly ScriptedDialogs _dialogs = new();
    private readonly AppConnectRuntime _runtime = new();
    private readonly InMemoryLibraries _libraries = new();
    private RegistrationFiles _registrations = null!;
    private ConnectionStore _peerStore = null!;
    private ConnectionStore _store = null!;
    private AppConnectServer _peer = null!;
    private LeaseRegistry _peerLeases = null!;
    private AppConnectSettingsStore _settings = null!;
    private Func<string, bool> _start = _ => false;

    public async Task InitializeAsync()
    {
        _registrations = new RegistrationFiles(Path.Combine(_root, "AppVerbindung", "apps"));
        _peerStore = new ConnectionStore(Path.Combine(_root, "peer", "connections.json"), new XorProtector());
        _store = new ConnectionStore(Path.Combine(_root, "lumora", "connections.json"), new XorProtector());
        _settings = new AppConnectSettingsStore(Path.Combine(_root, "lumora", "settings.json"));
        _peerLeases = new LeaseRegistry();
        _peer = new AppConnectServer(new AppConnectIdentity("0.6.3"), _peerStore, () => "window",
            pairing: new PairingCoordinator(_peerStore, _prompt), leases: _peerLeases);
        await _peer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _peer.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    [Fact]
    public Task Ohne_App_Verbindung_Hinweis_danach_Leer_Zustand() => OnUiAsync(async () =>
    {
        var vm = new ConnectedAppsViewModel(_runtime, _dialogs);
        vm.IsAvailable.Should().BeFalse("vor dem Start der App-Verbindung bzw. mit _hidden");
        await vm.RefreshAsync();
        vm.Apps.Should().BeEmpty();

        Attach();
        await WaitAsync(() => vm.IsAvailable);
        await vm.RefreshAsync();
        vm.HasApps.Should().BeFalse();
        vm.Summary.Should().Be("Noch keine App verbunden");
    });

    [Fact]
    public Task Verbinden_mit_Code_angenommen_zeigt_Rechte_in_beide_Richtungen() => OnUiAsync(async () =>
    {
        WritePeer(running: true);
        var vm = AttachedViewModel();
        await vm.RefreshAsync();
        var app = vm.Apps.Should().ContainSingle().Subject;
        app.DisplayName.Should().Be("Gipfelbuch");
        app.StateText.Should().Be("nicht verbunden");
        app.CanPair.Should().BeTrue();
        app.CanDisconnect.Should().BeFalse();

        var pairing = app.PairCommand.ExecuteAsync(null);
        var info = await _prompt.NextAsync();
        app.IsPairing.Should().BeTrue();
        app.StateText.Should().Be("wartet auf Bestätigung");
        app.PairingCode.Should().Be(info.Code[..3] + " " + info.Code[3..], "derselbe Code wie im Dialog drüben");
        app.PairingText.Should().Contain("in Gipfelbuch derselbe Code");
        _prompt.Answer(new PairingDecision([Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead], [Rights.PhotosSearch, Rights.PhotosThumbnail]));
        await pairing;

        app.IsPaired.Should().BeTrue();
        app.IsPairing.Should().BeFalse();
        app.PairingCode.Should().BeNull();
        app.StateText.Should().Be("läuft");
        app.Notice.Should().Be("Mit Gipfelbuch verbunden.");
        app.DetailText.Should().StartWith("Version 0.6 · verbunden seit ");
        app.GrantedHeader.Should().Be("Gipfelbuch darf von Lumora");
        app.GrantedToPeer.Select(o => (o.Right, o.IsOn, o.IsEditable)).Should().Equal(
            (Rights.PhotosSearch, true, true), (Rights.PhotosThumbnail, true, true), (Rights.PhotosOriginal, false, true), (Rights.BackgroundStart, false, true));
        app.ReceivedFromPeer.Select(o => (o.Right, o.IsOn, o.IsEditable)).Should().Equal(
            (Rights.ToursRead, true, false), (Rights.TracksRead, true, false), (Rights.SummitsRead, true, false), (Rights.BackgroundStart, false, false));
        app.ReceivedFromPeer[^1].Text.Should().Be("Gipfelbuch im Hintergrund starten, wenn Lumora etwas braucht");
        vm.Summary.Should().Be("1 App verbunden");

        // Lumora ändert, was es erlaubt (Abschnitt 5, Schritt 7) — gespeichert, ohne die Karte neu aufzubauen
        var original = app.GrantedToPeer.Single(o => o.Right == Rights.PhotosOriginal);
        original.IsOn = true;
        _store.Find("gipfelbuch")!.GrantedToPeer.Should().Equal(Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal);
        app.GrantedToPeer.Should().Contain(original);

        // Gipfelbuch ändert, was es erlaubt → Lumora erfährt es beim Aktualisieren über /info
        _peerStore.Add(_peerStore.Find("lumoratest")! with { GrantedToPeer = [Rights.ToursRead] }, _peerStore.Secret("lumoratest")!);
        await vm.RefreshAsync();
        app.ReceivedFromPeer.Where(o => o.IsOn).Select(o => o.Right).Should().Equal(Rights.ToursRead);
    });

    [Fact]
    public Task Abgelehnt_und_abgebrochen_bleibt_ohne_Verbindung() => OnUiAsync(async () =>
    {
        WritePeer(running: true);
        var vm = AttachedViewModel();
        await vm.RefreshAsync();
        var app = vm.Apps.Single();

        var rejected = app.PairCommand.ExecuteAsync(null);
        await _prompt.NextAsync();
        _prompt.Answer(null);
        await rejected;
        app.Notice.Should().Be("Gipfelbuch hat die Verbindung abgelehnt.");
        app.IsPaired.Should().BeFalse();

        var cancelled = app.PairCommand.ExecuteAsync(null);
        await _prompt.NextAsync();
        app.PairCancelCommand.Execute(null);
        await cancelled;
        app.Notice.Should().StartWith("Verbinden abgebrochen.");
        app.IsPairing.Should().BeFalse();
        app.CanPair.Should().BeTrue();
        _store.All.Should().BeEmpty();
    });

    [Fact]
    public Task Von_drueben_getrennt_und_hier_getrennt_mit_Rueckfrage() => OnUiAsync(async () =>
    {
        WritePeer(running: true);
        var vm = AttachedViewModel();
        var app = await PairAsync(vm);

        // Gipfelbuch trennt → Aktualisieren erkennt es (Stufe 3: /info ohne grantedToYou bzw. 401)
        _peerStore.Remove("lumoratest");
        await vm.RefreshAsync();
        app.IsPaired.Should().BeFalse();
        app.Notice.Should().Be("Die Verbindung mit Gipfelbuch ist getrennt.");
        app.StateText.Should().Be("nicht verbunden");
        vm.Summary.Should().Be("Noch keine App verbunden");

        app = await PairAsync(vm);
        _dialogs.ConfirmAnswer = false;
        await app.DisconnectCommand.ExecuteAsync(null);
        app.IsPaired.Should().BeTrue("Rückfrage abgelehnt");

        _dialogs.ConfirmAnswer = true;
        await app.DisconnectCommand.ExecuteAsync(null);
        _dialogs.Confirmations.Should().Equal("Verbindung trennen?", "Verbindung trennen?");
        app.IsPaired.Should().BeFalse();
        _store.All.Should().BeEmpty();
        _peerStore.All.Should().BeEmpty("die laufende Gegenseite erfährt es");
    });

    [Fact]
    public Task Nicht_gestartete_App_starten_und_Fehler_beim_Start() => OnUiAsync(async () =>
    {
        WritePeer(running: false);
        var secret = ConnectionStore.NewSecret(); // Kopplung von früher, beide Seiten kennen sie
        _store.Add(new AppConnection("gipfelbuch", "Gipfelbuch", [Rights.PhotosSearch], [Rights.ToursRead], new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc)), secret);
        _peerStore.Add(new AppConnection("lumoratest", "Lumora", [Rights.ToursRead], [Rights.PhotosSearch], new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc)), secret);
        var vm = AttachedViewModel();
        await vm.RefreshAsync();
        var app = vm.Apps.Single();
        app.StateText.Should().Be("nicht gestartet");
        app.StartLabel.Should().Be("Gipfelbuch öffnen");
        app.CanStart.Should().BeTrue();
        app.DetailText.Should().Be("Version 0.6 · verbunden seit 22.10.2026");

        await app.StartCommand.ExecuteAsync(null);
        app.Notice.Should().Be("Gipfelbuch ließ sich nicht starten oder meldete sich nicht innerhalb von 20 Sekunden.");
        app.NoticeIsError.Should().BeTrue();

        _start = _ =>
        {
            WritePeer(running: true); // die gestartete App meldet sich mit Port
            return true;
        };
        await app.StartCommand.ExecuteAsync(null);
        app.Notice.Should().BeNull();
        app.IsRunning.Should().BeTrue();
        app.CanStart.Should().BeTrue("„öffnen“ bleibt — der zweite Start holt das Fenster (Abschnitt 9)");
        app.StateText.Should().Be("läuft");
        app.IsPaired.Should().BeTrue("Gipfelbuch kennt die Kopplung");

        var opened = 0;
        _start = _ => ++opened > 0;
        await app.StartCommand.ExecuteAsync(null);
        opened.Should().Be(1, "läuft schon → normal starten, die App zeigt ihr Fenster");
        app.Notice.Should().BeNull();
    });

    [Fact]
    public Task Schalter_oben_im_Reiter_werden_gespeichert_und_Verbindungen_aus_blendet_die_Apps_aus() => OnUiAsync(async () =>
    {
        WritePeer(running: true);
        var vm = AttachedViewModel();
        await vm.RefreshAsync();
        vm.HasSettings.Should().BeTrue();
        (vm.ConnectionsEnabled, vm.BackgroundStartAllowed, vm.TrayIconEnabled).Should().Be((true, true, false), "Standard laut Mockup 11");

        vm.TrayIconEnabled = true;
        vm.BackgroundStartAllowed = false;
        _settings.Current.Should().Be(new AppConnectSettings(Enabled: true, AllowBackgroundStart: false, TrayIcon: true));

        vm.ConnectionsEnabled = false;
        _settings.Current.Enabled.Should().BeFalse();
        await vm.RefreshAsync(); // aus: keine Nachfrage bei den Apps

        _settings.Update(s => s with { Enabled = true, TrayIcon = false }); // z. B. aus einer anderen Stelle
        await WaitAsync(() => vm.ConnectionsEnabled && !vm.TrayIconEnabled);
        vm.BackgroundStartAllowed.Should().BeFalse();
    });

    [Fact]
    public Task Im_Hintergrund_beenden_gibt_Lumoras_Zusage_frei() => OnUiAsync(async () =>
    {
        WritePeer(running: true);
        var vm = AttachedViewModel();
        var app = await PairAsync(vm);
        WritePeer(running: true, mode: "background");
        await vm.RefreshAsync();
        app.StateText.Should().Be("läuft im Hintergrund");
        app.CanStopBackground.Should().BeFalse("Lumora hält dort keine Zusage");

        await _runtime.Client!.HoldAsync("gipfelbuch", 300);
        vm.Reload();
        app.CanStopBackground.Should().BeTrue();
        _peerLeases.Holders.Should().Equal("lumoratest");

        await app.StopBackgroundCommand.ExecuteAsync(null);
        _peerLeases.Holders.Should().BeEmpty();
        app.CanStopBackground.Should().BeFalse();
        app.Notice.Should().Be("Lumora braucht Gipfelbuch nicht mehr — es beendet sich selbst, sobald es niemand mehr braucht.");
    });

    [Fact]
    public Task Bibliotheken_je_App_neu_verbunden_keine_angehakt_speichert_die_Auswahl() => OnUiAsync(async () =>
    {
        _libraries.Add(1, "Familienfotos", 10, DateTime.UtcNow);
        _libraries.Add(2, "Urlaub & Sport", 20, DateTime.UtcNow);
        WritePeer(running: true);
        var vm = AttachedViewModel();
        var app = await PairAsync(vm);

        app.LibrariesHeader.Should().Be("Bibliotheken, die Gipfelbuch sieht");
        app.Libraries.Select(l => (l.Name, l.IsOn)).Should().Equal(("Familienfotos", false), ("Urlaub & Sport", false));
        app.LibrariesHint.Should().Contain("findet bei Lumora keine Fotos");

        app.Libraries[1].IsOn = true;
        _store.LibrariesOf("gipfelbuch").Should().Equal(2);
        await WaitAsync(() => app.LibrariesHint is null);
        await vm.RefreshAsync();
        app.Libraries.Select(l => l.IsOn).Should().Equal(false, true);
    });

    [Fact]
    public Task Reiter_hell_und_dunkel_ohne_Binding_Fehler() => OnUiAsync(async () =>
    {
        WritePeer(running: true);
        _libraries.Add(1, "Familienfotos", 10, DateTime.UtcNow);
        var vm = AttachedViewModel();
        await PairAsync(vm);
        using var errors = new BindingErrors();
        var section = new SettingsConnectedAppsSection { DataContext = vm };
        var window = new Window { Content = section, Width = 1000, Height = 900, ShowActivated = false, Left = -10000 };
        window.Show();
        try
        {
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme);
                window.UpdateLayout();
            }

            section.AppList.Items.Count.Should().Be(1);
            section.ActualHeight.Should().BeGreaterThan(300);
            errors.Messages.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }
    });

    private ConnectedAppsViewModel AttachedViewModel()
    {
        Attach();
        return new ConnectedAppsViewModel(_runtime, _dialogs, libraries: _libraries);
    }

    private void Attach()
        => _runtime.Attach(
            new AppConnectClient(_registrations, _store, own: AppConnectClientIdentity.Lumora with { AppId = "lumoratest" },
                timings: new AppConnectClientTimings(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400)),
                start: path => _start(path)),
            _store,
            _settings);

    private async Task<ConnectedAppItemViewModel> PairAsync(ConnectedAppsViewModel vm)
    {
        await vm.RefreshAsync();
        var app = vm.Apps.Single();
        var pairing = app.PairCommand.ExecuteAsync(null);
        var info = await _prompt.NextAsync();
        _prompt.Answer(PairingPermissions.Decide(info.Permissions.Where(p => p.DefaultOn)));
        await pairing;
        app.IsPaired.Should().BeTrue();
        return app;
    }

    private void WritePeer(bool running, string mode = "window") => _registrations.Write(new AppRegistration
    {
        AppId = "gipfelbuch",
        DisplayName = "Gipfelbuch",
        AppVersion = "0.6",
        ContractVersion = "1.1",
        ExePath = @"C:\Programme\Gipfelbuch\Gipfelbuch.exe",
        BackgroundArg = "--background",
        Running = running,
        Pid = running ? Environment.ProcessId : null,
        Port = running ? _peer.Port : null,
        Mode = running ? mode : null,
        Provides = PeerProvides,
        Wants = PeerWants,
    });

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue();
    }

    /// <summary>Dialog der Gegenseite, dessen Antwort der Test gibt.</summary>
    private sealed class ScriptedPrompt : IPairingPrompt
    {
        private TaskCompletionSource<PairingDecision?>? _answer;
        private TaskCompletionSource<PairingPromptInfo> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PairingDecision?> AskAsync(PairingPromptInfo info, CancellationToken ct)
        {
            _answer = new TaskCompletionSource<PairingDecision?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => _answer.TrySetResult(null));
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

    private sealed class BindingErrors : TraceListener
    {
        public BindingErrors()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }

        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
