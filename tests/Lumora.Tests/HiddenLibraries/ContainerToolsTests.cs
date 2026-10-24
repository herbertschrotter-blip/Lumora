using Lumora.Services.HiddenLibraries;

namespace Lumora.Tests.HiddenLibraries;

/// <summary>
/// ADR-0049: Ein-/Aushängen über die VeraCrypt-Befehlszeile bzw. Auswerfen (Shell, wie Explorer) — nur mit Fakes, nie echt. Lumora
/// übergibt nie ein Kennwort und öffnet kein Explorer-Fenster.
/// </summary>
public sealed class ContainerToolsTests
{
    private const string VeraCryptExe = @"C:\Program Files\VeraCrypt\VeraCrypt.exe";

    [Fact]
    public void VeraCrypt_volume_is_detected_by_its_device_name()
    {
        var devices = new FakeDevices { ['X'] = @"\Device\VeraCryptVolumeX", ['E'] = @"\Device\HarddiskVolume7" };
        var tools = new ContainerTools(new FakeRunner(), devices, new FakeLocator(VeraCryptExe), new FakeEjector());

        tools.Detect(@"X:\").Should().Be(ContainerKind.VeraCrypt);
        tools.Detect(@"E:\").Should().Be(ContainerKind.Unknown);
        tools.Detect(@"Q:\").Should().Be(ContainerKind.Unknown);
        tools.Detect("kein Laufwerk").Should().Be(ContainerKind.Unknown);
        tools.IsVeraCryptInstalled.Should().BeTrue();
    }

    [Fact]
    public async Task Dismount_veracrypt_uses_command_line_without_force()
    {
        var devices = new FakeDevices { ['X'] = @"\Device\VeraCryptVolumeX" };
        var runner = new FakeRunner { OnRun = (_, _) => devices.Remove('X') };
        var tools = new ContainerTools(runner, devices, new FakeLocator(VeraCryptExe), new FakeEjector());

        var result = await tools.DismountAsync(@"X:\Fotos");

        result.Success.Should().BeTrue();
        runner.Calls.Should().ContainSingle();
        runner.Calls[0].File.Should().Be(VeraCryptExe);
        runner.Calls[0].Args.Should().Equal("/dismount", "X", "/quit", "/silent");
        runner.Calls[0].Args.Should().NotContain(a => a.StartsWith("/f", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dismount_veracrypt_tries_newer_unmount_switch_and_reports_open_files()
    {
        var devices = new FakeDevices { ['X'] = @"\Device\VeraCryptVolumeX" };
        var runner = new FakeRunner { OnRun = (_, args) => { if (args[0] == "/unmount") { devices.Remove('X'); } } };
        var tools = new ContainerTools(runner, devices, new FakeLocator(VeraCryptExe), new FakeEjector());
        (await tools.DismountAsync(@"X:\")).Success.Should().BeTrue();
        runner.Calls.Select(c => c.Args[0]).Should().Equal("/dismount", "/unmount");

        var stuck = new FakeDevices { ['Y'] = @"\Device\VeraCryptVolumeY" };
        var stuckTools = new ContainerTools(new FakeRunner(), stuck, new FakeLocator(VeraCryptExe), new FakeEjector());
        var failed = await stuckTools.DismountAsync(@"Y:\");
        failed.Success.Should().BeFalse();
        failed.Message.Should().Contain("Y:").And.Contain("VeraCrypt aushängen");
    }

    [Fact]
    public async Task Dismount_without_veracrypt_installed_gives_a_hint()
    {
        var devices = new FakeDevices { ['X'] = @"\Device\VeraCryptVolumeX" };
        var runner = new FakeRunner();
        var result = await new ContainerTools(runner, devices, new FakeLocator(null), new FakeEjector()).DismountAsync(@"X:\");
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("nicht gefunden");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Dismount_virtual_disk_ejects_like_explorer_without_powershell()
    {
        // 0.4.31: Dismount-DiskImage brauchte Administratorrechte → beim Beenden blieb X: hängen; jetzt Auswerfen wie im Explorer
        var devices = new FakeDevices { ['V'] = @"\Device\HarddiskVolume9" };
        var runner = new FakeRunner();
        var ejector = new FakeEjector('V') { OnEject = () => devices.Remove('V') };
        var result = await new ContainerTools(runner, devices, new FakeLocator(null), ejector).DismountAsync(@"V:\");
        result.Success.Should().BeTrue();
        ejector.Ejected.Should().Equal('V');
        runner.Calls.Should().BeEmpty("kein PowerShell, kein Fremdprogramm");

        var plain = new FakeDevices { ['W'] = @"\Device\HarddiskVolume2" };
        var plainEjector = new FakeEjector();
        var hint = await new ContainerTools(new FakeRunner(), plain, new FakeLocator(null), plainEjector).DismountAsync(@"W:\");
        hint.Success.Should().BeFalse();
        hint.Message.Should().Contain("kein bekannter Container");
        plainEjector.Ejected.Should().BeEmpty("ein normales Laufwerk wird nie ausgeworfen");
    }

    [Fact]
    public async Task Virtual_disk_still_mounted_after_eject_gives_explorer_hint()
    {
        var devices = new FakeDevices { ['V'] = @"\Device\HarddiskVolume9" };
        var ejector = new FakeEjector('V');
        var result = await new ContainerTools(new FakeRunner(), devices, new FakeLocator(null), ejector).DismountAsync(@"V:\Fotos");
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("V:").And.Contain("Auswerfen");
        ejector.Ejected.Should().Equal('V');
    }

    [Fact]
    public void Container_tools_source_starts_no_powershell()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Lumora.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        dir.Should().NotBeNull();
        File.ReadAllText(Path.Combine(dir!, "src", "Lumora.Services", "HiddenLibraries", "ContainerTools.cs"))
            .Should().NotContainEquivalentOf("powershell.exe").And.NotContain("Dismount-DiskImage -");
    }

    [Fact]
    public async Task System_drive_is_never_dismounted()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        var runner = new FakeRunner();
        var result = await new ContainerTools(runner, new FakeDevices { [system[0]] = @"\Device\HarddiskVolume3" }, new FakeLocator(VeraCryptExe), new FakeEjector()).DismountAsync(system);
        result.Success.Should().BeFalse();
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mount_starts_veracrypt_which_asks_for_the_password_itself()
    {
        var file = Path.GetTempFileName();
        try
        {
            var devices = new FakeDevices();
            var runner = new FakeRunner { OnRun = (_, _) => devices['M'] = @"\Device\VeraCryptVolumeM" };
            var tools = new ContainerTools(runner, devices, new FakeLocator(VeraCryptExe), new FakeEjector());

            var result = await tools.MountVeraCryptAsync(file, 'm');

            result.Success.Should().BeTrue();
            runner.Calls.Single().Args.Should().Equal("/volume", file, "/letter", "M", "/quit");
            runner.Calls.Single().Args.Should().NotContain(a => a.Contains("password", StringComparison.OrdinalIgnoreCase) || a.Contains("explore", StringComparison.OrdinalIgnoreCase));

            var busy = await tools.MountVeraCryptAsync(file, 'M');
            busy.Success.Should().BeFalse("Laufwerk M: ist jetzt belegt");

            var canceled = await new ContainerTools(new FakeRunner(), new FakeDevices(), new FakeLocator(VeraCryptExe), new FakeEjector()).MountVeraCryptAsync(file, 'N');
            canceled.Success.Should().BeFalse();
            (await new ContainerTools(new FakeRunner(), new FakeDevices(), new FakeLocator(null), new FakeEjector()).MountVeraCryptAsync(file, 'N'))
                .Message.Should().Contain("nicht installiert");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\VeraCrypt\\VeraCrypt.exe\" /v \"%1\"", @"C:\Program Files\VeraCrypt\VeraCrypt.exe")]
    [InlineData(@"C:\VC\VeraCrypt.exe /v %1", @"C:\VC\VeraCrypt.exe")]
    [InlineData("", null)]
    public void Executable_from_registry_command(string command, string? expected)
        => VeraCryptLocator.FromCommand(command).Should().Be(expected);

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();

        public Action<string, IReadOnlyList<string>>? OnRun { get; init; }

        public int ExitCode { get; init; }

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((fileName, arguments.ToList()));
            OnRun?.Invoke(fileName, arguments);
            return Task.FromResult(new ProcessRunResult(ExitCode, TimedOut: false));
        }
    }

    private sealed class FakeDevices : Dictionary<char, string>, IVolumeDeviceQuery
    {
        public string? GetDeviceName(char driveLetter) => TryGetValue(char.ToUpperInvariant(driveLetter), out var name) ? name : null;
    }

    private sealed class FakeEjector(params char[] virtualDisks) : IVirtualDiskEjector
    {
        public List<char> Ejected { get; } = new();

        public Action? OnEject { get; init; }

        public bool IsVirtualDisk(char driveLetter) => virtualDisks.Contains(char.ToUpperInvariant(driveLetter));

        public Task<bool> EjectAsync(char driveLetter, Func<bool> isMounted, TimeSpan timeout, CancellationToken ct)
        {
            Ejected.Add(driveLetter);
            OnEject?.Invoke();
            return Task.FromResult(!isMounted());
        }
    }

    private sealed class FakeLocator(string? exe) : IVeraCryptLocator
    {
        public string? FindExecutable() => exe;
    }
}
