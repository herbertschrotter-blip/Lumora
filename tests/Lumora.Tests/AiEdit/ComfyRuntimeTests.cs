using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.AiEdit;
using Lumora.Tests.Activity;

namespace Lumora.Tests.AiEdit;

public class ComfyRuntimeTests
{
    private static readonly ComfyRuntimeOptions FastOptions = new()
    {
        StartTimeout = TimeSpan.FromSeconds(5),
        PollInterval = TimeSpan.FromMilliseconds(10),
        StopTimeout = TimeSpan.FromSeconds(2),
        IdleCheckInterval = TimeSpan.FromSeconds(30),
    };

    private static ComfyRuntime Create(FakeContainer container, FakeLauncher launcher, FakeComfyHandler handler, TimeProvider? time = null)
        => new(() => container.Root, launcher, NullLogger<ComfyRuntime>.Instance, () => handler, () => 54321, time, FastOptions);

    [Fact]
    public async Task Start_waits_for_system_stats_and_reports_device()
    {
        using var container = new FakeContainer("0.43.0", true, "ComfyUI-GGUF");
        var launcher = new FakeLauncher();
        var handler = new FakeComfyHandler { FailFirst = 3 };
        using var runtime = Create(container, launcher, handler);
        runtime.Status.State.Should().Be(ComfyRuntimeState.Stopped);

        var status = await runtime.EnsureStartedAsync();

        status.State.Should().Be(ComfyRuntimeState.Ready);
        status.Device!.Type.Should().Be("cuda");
        status.Device.VramTotalBytes.Should().BeGreaterThan(10L * 1024 * 1024 * 1024);
        handler.StatsCalls.Should().Be(4);
        runtime.BaseAddress.Should().Be(new Uri("http://127.0.0.1:54321/"));

        // zweiter Aufruf startet nicht noch einmal
        await runtime.EnsureStartedAsync();
        launcher.Starts.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_uses_checked_flags_and_keeps_everything_in_the_container()
    {
        using var container = new FakeContainer("0.43.0", true, "ComfyUI-GGUF");
        var launcher = new FakeLauncher();
        using var runtime = Create(container, launcher, new FakeComfyHandler());
        await runtime.EnsureStartedAsync();

        var info = launcher.Starts.Single();
        info.FileName.Should().Be(container.Layout.PythonExe);
        info.WorkingDirectory.Should().Be(container.Layout.WorkRoot);
        var args = info.ArgumentList.ToList();
        args.Take(2).Should().Equal("-s", container.Layout.MainPy);
        args.Should().ContainInOrder("--listen", "127.0.0.1").And.ContainInOrder("--port", "54321");
        args.Should().Contain(["--disable-auto-launch", "--offline", "--disable-all-custom-nodes"]);
        args.Should().ContainInOrder("--whitelist-custom-nodes", "ComfyUI-GGUF");
        args.Should().ContainInOrder("--models-directory", container.Layout.ModelsRoot);
        args.Should().ContainInOrder("--input-directory", container.Layout.InputDir);
        args.Should().ContainInOrder("--output-directory", container.Layout.OutputDir);
        args.Should().ContainInOrder("--temp-directory", container.Layout.TempDir);
        args.Should().ContainInOrder("--user-directory", container.Layout.UserDir);
        args.Should().NotContain(["--windows-standalone-build", "--enable-manager", "--auto-launch", "0.0.0.0"]);
        args.Where(a => Path.IsPathRooted(a)).Should().OnlyContain(a => a.StartsWith(container.Layout.Root, StringComparison.OrdinalIgnoreCase),
            "alle Ordner liegen im Container");

        info.Environment["TEMP"].Should().StartWith(container.Layout.CacheRoot);
        info.Environment["TMP"].Should().StartWith(container.Layout.CacheRoot);
        info.Environment["HF_HOME"].Should().StartWith(container.Layout.CacheRoot);
        info.Environment["TORCH_HOME"].Should().StartWith(container.Layout.CacheRoot);
        info.Environment["CUDA_CACHE_PATH"].Should().StartWith(container.Layout.CacheRoot);
        info.Environment["PIP_CACHE_DIR"].Should().StartWith(container.Layout.CacheRoot);
        info.Environment["HF_HUB_OFFLINE"].Should().Be("1");
        info.Environment["TRANSFORMERS_OFFLINE"].Should().Be("1");
        info.Environment["DO_NOT_TRACK"].Should().Be("1");
        info.Environment["PYTHONNOUSERSITE"].Should().Be("1");
        info.Environment.ContainsKey("PYTHONPATH").Should().BeFalse();
        Directory.Exists(container.Layout.UserDir).Should().BeTrue("ComfyUI prüft, dass der Ordner existiert");
    }

    [Fact]
    public async Task Unverified_version_is_never_started()
    {
        using var container = new FakeContainer("0.44.1");
        var launcher = new FakeLauncher();
        using var runtime = Create(container, launcher, new FakeComfyHandler());

        var status = await runtime.EnsureStartedAsync();

        status.State.Should().Be(ComfyRuntimeState.Unavailable);
        status.Message.Should().Contain("nicht geprüft");
        launcher.Starts.Should().BeEmpty();
    }

    [Fact]
    public async Task Version_mismatch_or_missing_cuda_stops_the_process()
    {
        using var container = new FakeContainer();
        var launcher = new FakeLauncher();
        using var runtime = Create(container, launcher, new FakeComfyHandler { DeviceType = "cpu" });

        var status = await runtime.EnsureStartedAsync();

        status.State.Should().Be(ComfyRuntimeState.Failed);
        status.Message.Should().Contain("CUDA");
        launcher.Processes.Single().HasExited.Should().BeTrue();

        using var other = new FakeContainer();
        var launcher2 = new FakeLauncher();
        using var runtime2 = Create(other, launcher2, new FakeComfyHandler { Version = "0.50.0" });
        (await runtime2.EnsureStartedAsync()).Message.Should().Contain("0.50.0");
        launcher2.Processes.Single().HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task Process_ending_during_start_is_reported()
    {
        using var container = new FakeContainer();
        var launcher = new FakeLauncher { ExitImmediately = true };
        using var runtime = Create(container, launcher, new FakeComfyHandler { FailFirst = int.MaxValue });

        var status = await runtime.EnsureStartedAsync();

        status.State.Should().Be(ComfyRuntimeState.Failed);
        status.Message.Should().Contain("beendet");
    }

    [Fact]
    public async Task Stop_interrupts_kills_and_clears_work_folders()
    {
        using var container = new FakeContainer();
        var launcher = new FakeLauncher { WriteOutputFile = Path.Combine(container.Layout.OutputDir, "ComfyUI_00001_.png") };
        var handler = new FakeComfyHandler();
        using var runtime = Create(container, launcher, handler);
        await runtime.EnsureStartedAsync();
        File.Exists(launcher.WriteOutputFile).Should().BeTrue();

        await runtime.StopAsync("von Hand");

        handler.InterruptCalls.Should().Be(1);
        launcher.Processes.Single().KillCount.Should().BeGreaterThan(0);
        launcher.Processes.Single().Disposed.Should().BeTrue();
        Directory.EnumerateFileSystemEntries(container.Layout.OutputDir).Should().BeEmpty();
        runtime.Status.State.Should().Be(ComfyRuntimeState.Stopped);
        runtime.BaseAddress.Should().BeNull();
    }

    [Fact]
    public async Task Stop_cancels_a_running_start()
    {
        using var container = new FakeContainer();
        var launcher = new FakeLauncher();
        using var runtime = Create(container, launcher, new FakeComfyHandler { FailFirst = int.MaxValue });

        var start = runtime.EnsureStartedAsync();
        await AiEditWait.UntilAsync(() => runtime.Status.State == ComfyRuntimeState.Starting && launcher.Processes.Count == 1);
        await runtime.StopAsync("Zusperren");

        (await start).State.Should().NotBe(ComfyRuntimeState.Ready);
        launcher.Processes.Single().HasExited.Should().BeTrue();
        runtime.Status.State.Should().Be(ComfyRuntimeState.Stopped);
    }

    [Fact]
    public async Task Idle_minutes_are_read_from_the_catalog_only_when_comfyui_starts()
    {
        using var container = new FakeContainer();
        var loads = 0;
        using var runtime = new ComfyRuntime(() => container.Root, new FakeLauncher(), NullLogger<ComfyRuntime>.Instance,
            () => new FakeComfyHandler(), () => 54321, null, FastOptions,
            loadIdleMinutes: _ => { loads++; return Task.FromResult(25); });
        loads.Should().Be(0, "beim Sitzungsstart wird der Katalog noch migriert");

        await runtime.EnsureStartedAsync();

        loads.Should().Be(1);
        runtime.IdleTimeout.Should().Be(TimeSpan.FromMinutes(25));
    }

    [Fact]
    public async Task Start_waiting_behind_a_stop_does_not_launch()
    {
        using var container = new FakeContainer();
        var launcher = new FakeLauncher();
        using var runtime = Create(container, launcher, new FakeComfyHandler { FailFirst = int.MaxValue });
        var first = runtime.EnsureStartedAsync();
        await AiEditWait.UntilAsync(() => launcher.Processes.Count == 1);

        // Zweiter Start wartet auf die Sperre, dann kommt das Zusperren
        var second = runtime.EnsureStartedAsync();
        await runtime.StopAsync("Zusperren");
        await first;
        await second;

        launcher.Starts.Should().ContainSingle("ein Start, der vor dem Zusperren wartete, startet danach nicht mehr");
        runtime.Status.State.Should().Be(ComfyRuntimeState.Stopped);
    }

    [Fact]
    public async Task Idle_timeout_stops_comfyui_unless_work_is_running()
    {
        using var container = new FakeContainer();
        var launcher = new FakeLauncher();
        var time = new ManualTimerTime();
        using var runtime = Create(container, launcher, new FakeComfyHandler(), time);
        runtime.IdleTimeout = TimeSpan.FromMinutes(10);
        await runtime.EnsureStartedAsync();

        using (runtime.BeginWork())
        {
            time.Advance(TimeSpan.FromMinutes(30));
            runtime.Status.State.Should().Be(ComfyRuntimeState.Ready, "ein laufender Auftrag hält ComfyUI am Leben");
        }

        time.Advance(TimeSpan.FromMinutes(9));
        runtime.Status.State.Should().Be(ComfyRuntimeState.Ready);
        time.Advance(TimeSpan.FromMinutes(2));
        await AiEditWait.UntilAsync(() => runtime.Status.State == ComfyRuntimeState.Stopped);
        launcher.Processes.Single().HasExited.Should().BeTrue();
        runtime.Status.Message.Should().Contain("Leerlauf");
    }

    [Fact]
    public async Task Idle_timeout_zero_never_stops()
    {
        using var container = new FakeContainer();
        var time = new ManualTimerTime();
        using var runtime = Create(container, new FakeLauncher(), new FakeComfyHandler(), time);
        runtime.IdleTimeout = TimeSpan.Zero;
        await runtime.EnsureStartedAsync();

        time.Advance(TimeSpan.FromHours(5));

        runtime.Status.State.Should().Be(ComfyRuntimeState.Ready);
    }

    [Fact]
    public async Task Unexpected_exit_is_noticed_and_reported()
    {
        using var container = new FakeContainer();
        var launcher = new FakeLauncher();
        var time = new ManualTimerTime();
        using var runtime = Create(container, launcher, new FakeComfyHandler(), time);
        await runtime.EnsureStartedAsync();

        launcher.Processes.Single().Exit(137);
        time.Advance(TimeSpan.FromSeconds(31));

        await AiEditWait.UntilAsync(() => runtime.Status.State == ComfyRuntimeState.Failed);
        runtime.Status.Message.Should().Contain("unerwartet");
    }

    [Fact]
    public async Task Dispose_stops_comfyui_before_the_container_is_dismounted()
    {
        using var container = new FakeContainer();
        var launcher = new FakeLauncher { WriteOutputFile = Path.Combine(container.Layout.OutputDir, "x.png") };
        var runtime = Create(container, launcher, new FakeComfyHandler());
        await runtime.EnsureStartedAsync();

        runtime.Dispose();

        launcher.Processes.Single().HasExited.Should().BeTrue();
        Directory.EnumerateFileSystemEntries(container.Layout.OutputDir).Should().BeEmpty();
        var act = () => runtime.EnsureStartedAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Redact_removes_paths_from_output()
    {
        var layout = new AiEditLayout(@"X:\");
        ComfyRuntime.Redact(@"Adding extra search path checkpoints X:\.lumora-ki\models\checkpoints", layout)
            .Should().Be(@"Adding extra search path checkpoints .lumora-ki\models\checkpoints");
        ComfyRuntime.Redact(@"Fehler in C:\Users\jemand\AppData\x.py, Zeile 3", null).Should().Be("Fehler in <Pfad>, Zeile 3");
        ComfyRuntime.Redact("File \"D:/Bilder/Urlaub/foto.jpg\" fehlt", null).Should().Be("File \"<Pfad>\" fehlt");
        ComfyRuntime.Redact(@"Fehler in C:\Users\Vorname Nachname\AppData\x.py, Zeile 3", null).Should().Be("Fehler in <Pfad>, Zeile 3");
        ComfyRuntime.Redact("\u001b[32m[INFO]\u001b[0m To see the GUI go to: http://127.0.0.1:8188", null)
            .Should().Be("[INFO] To see the GUI go to: http://127.0.0.1:8188");
    }

    [Theory]
    [InlineData("cuda:0 NVIDIA GeForce GTX 1080 Ti : cudaMallocAsync", "NVIDIA GeForce GTX 1080 Ti")]
    [InlineData("cpu", "cpu")]
    public void Device_name_is_shown_without_torch_details(string raw, string expected)
        => ComfyRuntime.CleanDeviceName(raw).Should().Be(expected);
}
