using System.Text.Json;
using Lumora.AppConnect;

namespace Lumora.Tests.AppConnect;

/// <summary>
/// Anmeldedatei (Abschnitt 4), eine Instanz je Benutzer (Abschnitt 9) und wann Lumora überhaupt teilnimmt (ADR-0073:
/// mit <c>_hidden</c> gar nicht, Testläufe abgeschirmt).
/// </summary>
public sealed class AppConnectHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "appconnect-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Registration_is_written_after_listening_and_marked_stopped_on_exit()
    {
        var files = new RegistrationFiles(RegistrationFiles.ResolveFolder(_root));
        var server = new AppConnectServer(new AppConnectIdentity("0.5.5"), NoPeers.Instance, () => "window");
        var host = new AppConnectHost(server, files, "0.5.5", @"C:\Programme\Lumora\Lumora.exe");

        await host.StartAsync();
        try
        {
            var path = Path.Combine(_root, "AppVerbindung", "apps", "lumora.json");
            File.Exists(path).Should().BeTrue();
            Directory.GetFiles(Path.GetDirectoryName(path)!).Should().Equal([path], "atomar geschrieben, keine temporäre Datei bleibt liegen");

            var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            json.GetProperty("appId").GetString().Should().Be("lumora");
            json.GetProperty("displayName").GetString().Should().Be("Lumora");
            json.GetProperty("appVersion").GetString().Should().Be("0.5.5");
            json.GetProperty("contractVersion").GetString().Should().Be("1.1");
            json.GetProperty("exePath").GetString().Should().Be(@"C:\Programme\Lumora\Lumora.exe");
            json.GetProperty("backgroundArg").GetString().Should().Be("--background");
            json.GetProperty("running").GetBoolean().Should().BeTrue();
            json.GetProperty("pid").GetInt32().Should().Be(Environment.ProcessId);
            json.GetProperty("port").GetInt32().Should().Be(server.Port).And.BeGreaterThan(0);
            json.GetProperty("mode").GetString().Should().Be("window");
            json.GetProperty("startedAtUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            json.GetProperty("provides").EnumerateArray().Select(e => e.GetString()).Should()
                .Equal("photos.search", "photos.thumbnail", "photos.original", "photos.people", "background.start");
            json.GetProperty("wants").EnumerateArray().Select(e => e.GetString()).Should()
                .Equal("tours.read", "tracks.read", "summits.read", "background.start");
            File.ReadAllText(path).Should().NotContainEquivalentOf("secret", "kein Geheimnis in der Anmeldedatei");

            using var http = new HttpClient();
            (await http.GetAsync($"http://127.0.0.1:{server.Port}/av/v1/info")).IsSuccessStatusCode.Should().BeTrue("der Port aus der Datei nimmt Anfragen an");
        }
        finally
        {
            await host.DisposeAsync();
        }

        var stopped = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "AppVerbindung", "apps", "lumora.json"))).RootElement;
        stopped.GetProperty("running").GetBoolean().Should().BeFalse();
        stopped.GetProperty("port").ValueKind.Should().Be(JsonValueKind.Null, "beim Beenden ausdrücklich port: null");
        files.Read("lumora")!.Running.Should().BeFalse();
        server.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Registration_folder_is_local_app_data_unless_the_root_is_redirected()
    {
        RegistrationFiles.ResolveFolder(null).Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppVerbindung", "apps"));
        RegistrationFiles.ResolveFolder(_root).Should().Be(Path.Combine(_root, "AppVerbindung", "apps"));
    }

    [Theory]
    [InlineData(true, false, AppConnectParticipation.Off)]
    [InlineData(true, true, AppConnectParticipation.Off)]
    [InlineData(false, true, AppConnectParticipation.Isolated)]
    [InlineData(false, false, AppConnectParticipation.Normal)]
    public void Hidden_start_never_takes_part_and_test_runs_stay_isolated(bool hidden, bool redirected, AppConnectParticipation expected)
        => AppConnectHost.Decide(hidden, redirected).Should().Be(expected);

    [Fact]
    public async Task Second_start_hands_show_window_to_the_running_instance()
    {
        var appId = "lumoratest" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.TryAcquire(appId);
        first.Should().NotBeNull();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        first!.Listen(action => received.TrySetResult(action));

        SingleInstance.TryAcquire(appId).Should().BeNull("es läuft schon eine Lumora");
        SingleInstance.SendShowWindow(appId).Should().BeTrue();

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("showWindow");
        SingleInstance.MutexName(appId).Should().Be($@"Local\AppVerbindung.{appId}");
        SingleInstance.PipeName(appId).Should().Be($"AppVerbindung.{appId}");
    }

    [Fact]
    public void After_exit_the_next_start_is_the_running_instance_again()
    {
        var appId = "lumoratest" + Guid.NewGuid().ToString("N")[..8];
        var first = SingleInstance.TryAcquire(appId);
        first!.Dispose();

        using var next = SingleInstance.TryAcquire(appId);
        next.Should().NotBeNull();
        SingleInstance.SendShowWindow("lumoratest-niemand", timeoutMs: 200).Should().BeFalse("ohne laufende Instanz");
    }

    [Theory]
    [InlineData("{\"action\":\"showWindow\"}", "showWindow")]
    [InlineData("{\"action\":42}", null)]
    [InlineData("[]", null)]
    [InlineData("kein json", null)]
    public void Pipe_messages_are_read_strictly(string line, string? expected)
        => SingleInstance.ParseAction(line).Should().Be(expected);
}
