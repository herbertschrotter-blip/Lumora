using OpenCvSharp;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Tests.Synthetic;
using Lumora.Imaging.Worker;

namespace Lumora.Imaging.Tests.Worker;

/// <summary>
/// Hilfsprozess über das echte Protokoll (ADR-0078 Nachtrag 0.5.44 Punkt 5): Start, Ausrichten und Zusammensetzen, Absturz,
/// Hängen, Abbrechen — jeweils ein verständlicher Fehler, der Testprozess (wie Lumora) läuft weiter.
/// </summary>
public sealed class ImagingWorkerProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Lumora.Imaging.Tests", Guid.NewGuid().ToString("N"));

    internal static ImagingWorkerOptions Options(TimeSpan? silence = null, TimeSpan? grace = null) => new()
    {
        ExePath = Path.Combine(AppContext.BaseDirectory, WorkerProtocol.WorkerExeName),
        SilenceTimeout = silence ?? TimeSpan.FromMinutes(5),
        CancelGrace = grace ?? TimeSpan.FromSeconds(10),
        Environment = new Dictionary<string, string> { [WorkerLimits.TestHooksVariable] = "1" },
    };

    private Task<ImagingWorkerProcess> StartAsync(ImagingWorkerOptions? options = null)
        => ImagingWorkerProcess.StartAsync(options ?? Options(), Path.Combine(_dir, "temp"), 2);

    private List<SourceSpec> Views(int count = 4)
    {
        using var scene = new SyntheticScene(seed: 21);
        var list = new List<SourceSpec>();
        for (var i = 0; i < count; i++)
        {
            using var v = scene.Render(-45 + i * 30, 0, 700, 800, 600);
            list.Add(new SourceSpec { Path = SyntheticScene.Save(v, Path.Combine(_dir, $"w{i}.jpg")), Index = i });
        }

        return list;
    }

    [Fact]
    public async Task Ausrichten_und_Zusammensetzen_laufen_im_Hilfsprozess_und_schreiben_in_den_Temp_Ordner()
    {
        await using var worker = await StartAsync();
        worker.OpenCvVersion.Should().StartWith("4.");
        var progress = new List<WorkerStep>();

        var estimate = await worker.RunAsync<EstimateResult>(id => new EstimateRequest { Id = id, Sources = Views(), DecodeEdge = 800 }, p => progress.Add(p.Step), CancellationToken.None);
        var output = Path.Combine(_dir, "temp", "vorschau.jpg");
        var composed = await worker.RunAsync<ComposeResult>(id => new ComposeRequest { Id = id, Settings = new ComposeSpec { Projection = WireProjection.Auto }, OutputPath = output }, p => progress.Add(p.Step), CancellationToken.None);

        estimate.Alignment.Cameras.Should().HaveCount(4);
        estimate.SuggestedProjection.Should().BeOneOf(WireProjection.Cylindrical, WireProjection.Perspective);
        composed.Check.Passed.Should().BeTrue(string.Join(",", composed.Check.Problems));
        File.Exists(output).Should().BeTrue();
        using var img = Cv2.ImDecode(File.ReadAllBytes(output), ImreadModes.Color);
        (img.Width, img.Height).Should().Be((composed.Width, composed.Height));
        progress.Should().Contain([WorkerStep.Load, WorkerStep.Features, WorkerStep.Match, WorkerStep.Seams, WorkerStep.Warp]);
        composed.PeakBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Absturz_des_Hilfsprozesses_wird_ein_Fehler_der_Arbeit()
    {
        await using var worker = await StartAsync();

        var act = () => worker.RunAsync<ReadyMessage>(id => new DebugRequest { Id = id, Action = "exit" }, null, CancellationToken.None);

        (await act.Should().ThrowAsync<ImagingWorkerException>()).Which.Failure.Should().Be(ImagingFailure.Crashed);
        worker.HasExited.Should().BeTrue();
        // ein neuer Prozess rechnet wieder
        await using var again = await StartAsync();
        (await again.RunAsync<ReadyMessage>(id => new DebugRequest { Id = id, Action = "echo" }, null, CancellationToken.None)).ProtocolVersion.Should().Be(WorkerProtocol.Version);
    }

    [Fact]
    public async Task Hart_beendeter_Prozess_mitten_im_Rechnen_wird_als_Absturz_gemeldet()
    {
        await using var worker = await StartAsync();
        var job = worker.RunAsync<EstimateResult>(id => new EstimateRequest { Id = id, Sources = Views(6), DecodeEdge = 0 }, null, CancellationToken.None);
        await Task.Delay(300);
        System.Diagnostics.Process.GetProcessById(worker.ProcessId).Kill(); // wie ein nativer Absturz: Prozess weg, keine Antwort

        var act = () => job;
        (await act.Should().ThrowAsync<ImagingWorkerException>()).Which.Failure.Should().Be(ImagingFailure.Crashed);
    }

    [Fact]
    public async Task Haengender_Hilfsprozess_wird_nach_der_Stille_beendet()
    {
        await using var worker = await StartAsync(Options(silence: TimeSpan.FromSeconds(2)));

        var act = () => worker.RunAsync<ReadyMessage>(id => new DebugRequest { Id = id, Action = "hang" }, null, CancellationToken.None);

        (await act.Should().ThrowAsync<ImagingWorkerException>()).Which.Failure.Should().Be(ImagingFailure.TimedOut);
        await Task.Delay(500);
        worker.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task Abbrechen_waehrend_der_Ausrichtung_endet_sauber_und_der_Prozess_rechnet_weiter()
    {
        await using var worker = await StartAsync();
        using var cts = new CancellationTokenSource();

        var act = () => worker.RunAsync<EstimateResult>(id => new EstimateRequest { Id = id, Sources = Views(6), DecodeEdge = 0 }, p =>
        {
            if (p.Step == WorkerStep.Features)
            {
                cts.Cancel();
            }
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        worker.HasExited.Should().BeFalse("höflicher Abbruch zwischen den Stufen");
        (await worker.RunAsync<ReadyMessage>(id => new DebugRequest { Id = id, Action = "echo" }, null, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Abbrechen_ohne_Antwort_beendet_den_Prozess_nach_der_Frist()
    {
        await using var worker = await StartAsync(Options(grace: TimeSpan.FromSeconds(1)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => worker.RunAsync<ReadyMessage>(id => new DebugRequest { Id = id, Action = "hang" }, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await Task.Delay(500);
        worker.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task Verwaltete_Ausnahme_ohne_Faenger_endet_sofort_mit_eigenem_Code_ohne_Fehlerbericht()
    {
        // 0.5.48: SetErrorMode + Handler → TerminateProcess; die Windows-Fehlerberichterstattung hielte den Prozess sonst offen
        // (Lumora wartete bis zur Stille-Grenze) und legte Berichte außerhalb des Containers ab
        const string action = "unhandled";
        const int exitCode = 5;
        await using var worker = await StartAsync(Options(silence: TimeSpan.FromSeconds(30)));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var act = () => worker.RunAsync<ReadyMessage>(id => new DebugRequest { Id = id, Action = action }, null, CancellationToken.None);

        (await act.Should().ThrowAsync<ImagingWorkerException>()).Which.Failure.Should().Be(ImagingFailure.Crashed);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "kein Warten auf einen Fehlerbericht");
        worker.ExitCode.Should().Be(exitCode, "beendet über den eigenen Handler, nicht über Fast-Fail und Fehlerberichterstattung");
    }

    [Fact]
    public async Task Threadzahl_erreicht_OpenCV_erst_zwischen_zwei_Arbeiten()
    {
        await using var worker = await StartAsync();
        (await worker.RunAsync<ReadyMessage>(id => new DebugRequest { Id = id, Action = "native-threads" }, null, CancellationToken.None)).Threads.Should().Be(2);

        worker.SetThreads(1); // „Im Hintergrund weiterrechnen“ — kommt auf dem Lese-Thread an, nicht während OpenCV rechnet

        var echo = await worker.RunAsync<ReadyMessage>(id => new DebugRequest { Id = id, Action = "native-threads" }, null, CancellationToken.None);
        echo.Threads.Should().Be(1, "vor der nächsten Arbeit an OpenCV gegeben");
    }

    [Fact]
    public async Task Fehlende_Programmdatei_ist_ein_verstaendlicher_Fehler()
    {
        var act = () => ImagingWorkerProcess.StartAsync(new ImagingWorkerOptions { ExePath = Path.Combine(_dir, "fehlt.exe") }, _dir, 1);
        (await act.Should().ThrowAsync<ImagingWorkerException>()).Which.Failure.Should().Be(ImagingFailure.NotFound);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
