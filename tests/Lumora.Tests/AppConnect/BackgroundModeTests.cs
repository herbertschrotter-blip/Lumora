using System.Text.Json;
using Lumora.AppConnect;
using Lumora.Tests.Activity;

namespace Lumora.Tests.AppConnect;

/// <summary>
/// App-Verbindung Stufe 6 (ADR-0073, Vertrag Abschnitt 9): Start ohne Fenster nur mit Schalter und Recht, Lebensdauer im
/// Hintergrund (Halte-Zusagen, 10 Minuten ohne Anfrage, startender Prozess beendet), Schalter-Datei, Modus in Anmeldedatei
/// und <c>/info</c>, letzte Anfrage, Halte-Zusagen-Halter.
/// </summary>
public sealed class BackgroundModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "background-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimerTime _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(new[] { "--background", "--started-by", "gipfelbuch:4711" }, "gipfelbuch", 4711)]
    [InlineData(new[] { "--started-by", "gipfel-buch_2:1", "--BACKGROUND" }, "gipfel-buch_2", 1)]
    public void Befehlszeile_mit_Abfrager_wird_erkannt(string[] args, string appId, int pid)
    {
        BackgroundLaunch.IsRequested(args).Should().BeTrue();
        BackgroundLaunch.Parse(args).Should().Be(new BackgroundLaunch(appId, pid));
    }

    [Theory]
    [InlineData(new[] { "--started-by", "gipfelbuch:4711" }, false)]
    [InlineData(new[] { "--background" }, true)]
    [InlineData(new[] { "--background", "--started-by", "gipfelbuch" }, true)]
    [InlineData(new[] { "--background", "--started-by", "gipfelbuch:0" }, true)]
    [InlineData(new[] { "--background", "--started-by", "Gipfel Buch:12" }, true)]
    [InlineData(new[] { "--background", "--started-by" }, true)]
    public void Ohne_gueltigen_Abfrager_gibt_es_keinen_Hintergrundstart(string[] args, bool requested)
    {
        BackgroundLaunch.IsRequested(args).Should().Be(requested);
        BackgroundLaunch.Parse(args).Should().BeNull("ohne gültiges --started-by lehnt Lumora den Hintergrundstart ab");
    }

    [Fact]
    public void Hintergrundstart_braucht_Schalter_und_Recht_background_start()
    {
        var store = new ConnectionStore(Path.Combine(_root, "connections.json"), new XorProtector());
        var launch = new BackgroundLaunch("gipfelbuch", 4711);
        var on = new AppConnectSettings();

        launch.Refusal(on, store).Should().Be("die App hat kein Recht background.start", "nicht gekoppelt");
        store.Add(new AppConnection("gipfelbuch", "Gipfelbuch", [Rights.PhotosSearch], [], DateTime.UtcNow), "geheim");
        launch.Refusal(on, store).Should().Be("die App hat kein Recht background.start");

        store.SetGrantedToPeer("gipfelbuch", [Rights.PhotosSearch, Rights.BackgroundStart]);
        launch.Refusal(on, store).Should().BeNull();
        launch.Refusal(on with { AllowBackgroundStart = false }, store).Should().Be("Hintergrundstart ist aus");
        launch.Refusal(on with { Enabled = false }, store).Should().Be("Verbindungen sind aus");
        BackgroundLaunch.ArgumentsFor("--background", "lumora", 42).Should().Be("--background --started-by lumora:42");
    }

    [Fact]
    public void Schalter_werden_gespeichert_und_fehlende_oder_kaputte_Datei_gilt_als_Standard()
    {
        var path = Path.Combine(_root, "appconnect", "settings.json");
        var store = new AppConnectSettingsStore(path);
        store.Current.Should().Be(new AppConnectSettings(Enabled: true, AllowBackgroundStart: true, TrayIcon: false), "Standard laut Mockup 11");

        var changes = 0;
        store.Changed += (_, _) => changes++;
        store.Update(s => s with { TrayIcon = true, AllowBackgroundStart = false });
        store.Update(s => s with { TrayIcon = true });
        changes.Should().Be(1, "gleicher Wert → keine Meldung");
        new AppConnectSettingsStore(path).Current.Should().Be(new AppConnectSettings(true, false, true));
        Directory.GetFiles(Path.GetDirectoryName(path)!).Should().Equal([path], "atomar, keine temporäre Datei");

        File.WriteAllText(path, "{ kaputt");
        new AppConnectSettingsStore(path).Current.Should().Be(new AppConnectSettings());
    }

    [Fact]
    public void Ohne_Zusage_endet_der_Hintergrund_nach_10_Minuten_ohne_Anfrage()
    {
        var leases = new LeaseRegistry(_time);
        DateTimeOffset? lastRequest = null;
        using var lifetime = new BackgroundLifetime(leases, () => lastRequest, _time, isProcessAlive: _ => true);
        var reasons = new List<string>();
        lifetime.ExitRequested += reasons.Add;

        _time.Advance(TimeSpan.FromMinutes(30));
        reasons.Should().BeEmpty("mit Fenster gelten die Hintergrund-Regeln nicht");

        lifetime.EnterBackground();
        _time.Advance(TimeSpan.FromMinutes(9));
        lastRequest = _time.GetUtcNow(); // eine Anfrage setzt die 10 Minuten zurück
        _time.Advance(TimeSpan.FromMinutes(9));
        reasons.Should().BeEmpty();

        _time.Advance(TimeSpan.FromMinutes(1) + BackgroundLifetime.CheckInterval);
        reasons.Should().Equal("keine Halte-Zusage und 10 Minuten keine Anfrage");
        _time.Advance(TimeSpan.FromMinutes(5));
        reasons.Should().HaveCount(1, "nur einmal melden");
    }

    [Fact]
    public void Gueltige_Halte_Zusage_haelt_Lumora_im_Hintergrund_bis_sie_ablaeuft()
    {
        var leases = new LeaseRegistry(_time);
        using var lifetime = new BackgroundLifetime(leases, () => null, _time, isProcessAlive: _ => true);
        lifetime.EnterBackground();
        leases.Create("gipfelbuch", 900);

        _time.Advance(TimeSpan.FromMinutes(14));
        lifetime.ExitReason().Should().BeNull("Zusage gilt noch, auch ohne Anfrage");
        leases.Holders.Should().Equal("gipfelbuch");
        lifetime.Requesters.Should().Equal("gipfelbuch");

        _time.Advance(TimeSpan.FromMinutes(1));
        leases.Holders.Should().BeEmpty("abgelaufen");
        lifetime.ExitReason().Should().NotBeNull("abgelaufen und seit dem Wechsel in den Hintergrund keine Anfrage");
    }

    [Fact]
    public void Ist_die_startende_App_beendet_endet_Lumora_ausser_ein_anderer_haelt_eine_Zusage()
    {
        var leases = new LeaseRegistry(_time);
        var alive = true;
        using var lifetime = new BackgroundLifetime(leases, () => _time.GetUtcNow(), _time, isProcessAlive: pid => pid == 4711 && alive);
        lifetime.EnterBackground(new BackgroundLaunch("gipfelbuch", 4711));
        leases.Create("gipfelbuch", 600);
        leases.Create("tourplaner", 600);
        lifetime.Requesters.Should().Equal("gipfelbuch", "tourplaner");

        alive = false;
        lifetime.ExitReason().Should().BeNull("der Tourplaner braucht Lumora noch");

        leases.ReleaseAll("tourplaner");
        lifetime.ExitReason().Should().Be("gipfelbuch ist beendet", "eine Zusage der beendeten App selbst hält Lumora nicht");

        lifetime.EnterWindow();
        lifetime.ExitReason().Should().BeNull("mit Fenster gehört Lumora dem Benutzer");
        lifetime.EnterBackground();
        lifetime.StartedBy.Should().BeNull("nach dem ersten Fenster gilt Regel (b) nicht mehr");
        lifetime.ExitReason().Should().BeNull();
    }

    [Fact]
    public async Task Moduswechsel_steht_in_Anmeldedatei_und_info_letzte_Anfrage_wird_gemerkt()
    {
        var files = new RegistrationFiles(RegistrationFiles.ResolveFolder(_root));
        AppConnectHost? hostRef = null;
        var server = new AppConnectServer(new AppConnectIdentity("0.5.19"), NoPeers.Instance, () => hostRef!.Mode) { Time = _time };
        var host = new AppConnectHost(server, files, "0.5.19", @"C:\Programme\Lumora\Lumora.exe") { Mode = AppConnectHost.BackgroundMode };
        hostRef = host;
        await host.StartAsync();
        try
        {
            files.Read("lumora")!.Mode.Should().Be("background", "beim Hintergrundstart gleich richtig angemeldet");
            server.LastRequestUtc.Should().BeNull();

            using var http = new HttpClient();
            (await http.GetAsync($"http://127.0.0.1:{server.Port}/av/v1/info")).IsSuccessStatusCode.Should().BeTrue();
            server.LastRequestUtc.Should().Be(_time.GetUtcNow());

            host.Mode = AppConnectHost.WindowMode;
            files.Read("lumora")!.Mode.Should().Be("window");
            files.Read("lumora")!.Port.Should().Be(server.Port);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] protectedData) => Protect(protectedData);
    }
}
