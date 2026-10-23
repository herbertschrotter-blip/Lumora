using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.Containers;
using Lumora.Services.Infrastructure;

namespace Lumora.Tests.HiddenLibraries;

/// <summary>
/// ADR-0061: Container-Assistent — nur mit Fakes, nie echt: keine VHDX, keine UAC-Rückfrage, kein BitLocker, kein
/// winget, kein VeraCrypt. Geprüft wird, WAS Lumora starten würde (Programm, Befehlszeile) und dass nichts liegen bleibt.
/// </summary>
public sealed class ContainerCreationTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;

    private const string SelfExe = @"C:\Programme\Lumora\Lumora.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "container-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDevices _devices = new() { ['C'] = @"\Device\HarddiskVolume3" };
    private readonly FakeElevated _elevated = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakeBitLocker _bitLocker = new();
    private readonly FakeInstaller _installer = new();
    private readonly FakeLocator _locator = new(null);
    private readonly FakeMountTools _containers = new();

    public ContainerCreationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DriveRoot => Path.GetPathRoot(_root)!;

    private ContainerCreationService CreateService(string? systemRoot = @"Q:\", IReadOnlyList<string>? cloud = null, long free = 500 * Gb, string fileSystem = "NTFS", string? self = SelfExe)
        => new(
            _containers,
            _locator,
            _devices,
            new FakeDrives(new DriveSpace(DriveRoot, free, fileSystem)),
            _elevated,
            _launcher,
            _bitLocker,
            _installer,
            systemRoot,
            () => cloud ?? [],
            (_, _) => Task.CompletedTask,
            () => self);

    private VhdxPlan Plan(string name = "sicherung.vhdx", char letter = 'X', int size = 50, string label = "Daten")
        => new(Path.Combine(_root, "Daten Ordner", name), size, letter, label);

    [Theory]
    [InlineData(@"D:\Daten\lumora-test-7f3a.vhdx", null)]
    [InlineData(@"D:\Daten\sicherung.vhd", "vhdx")]
    [InlineData(@"\\server\share\sicherung.vhdx", "lokalen")]
    [InlineData(@"D:\Bücher\sicherung.vhdx", null)] // seit 0.4.26 kein ASCII-Zwang mehr (Windows-Funktionen statt Skript)
    [InlineData("D:\\Da\"ten\\sicherung.vhdx", "unzulässige")]
    [InlineData(@"D:\OneDrive - Firma\sicherung.vhdx", "Cloud")]
    [InlineData(@"D:\Users\x\Dropbox\sicherung.vhdx", "Cloud")]
    [InlineData(@"D:\Google Drive\sicherung.vhdx", "Cloud")]
    [InlineData(@"E:\Wolke\a\sicherung.vhdx", "Cloud")]
    [InlineData("sicherung.vhdx", "Speicherort")]
    [InlineData(@"D:\ Backup\sicherung.vhdx", "Leerzeichen")]
    public void Vhdx_path_rules(string path, string? problem)
    {
        var result = VhdxRules.ValidatePath(path, [@"E:\Wolke"]);
        if (problem is null)
        {
            result.Should().BeNull();
        }
        else
        {
            result.Should().Contain(problem);
        }
    }

    [Theory]
    [InlineData("Daten", true)]
    [InlineData("Sicherung_2024-1.0", true)]
    [InlineData("", false)]
    [InlineData("Daten\"x", false)]
    [InlineData("Äpfel", false)]
    [InlineData("123456789012345678901234567890123", false)]
    public void Volume_label_rules(string label, bool ok)
        => (VhdxRules.ValidateLabel(label) is null).Should().Be(ok);

    [Fact]
    public void Suggestion_prefers_a_non_system_drive_neutral_name_and_last_free_letter()
    {
        var service = new ContainerCreationService(
            _containers, _locator, _devices,
            new FakeDrives(new DriveSpace(@"C:\", 900 * Gb), new DriveSpace(DriveRoot, 300 * Gb), new DriveSpace(@"U:\", 2000 * Gb, "exFAT")),
            _elevated, _launcher, _bitLocker, _installer, systemRoot: @"C:\", cloudFolders: () => [], selfExecutable: () => SelfExe);
        _devices['Z'] = @"\Device\HarddiskVolume9";

        var suggestion = service.SuggestVhdx();

        if (!string.Equals(DriveRoot, @"C:\", StringComparison.OrdinalIgnoreCase))
        {
            suggestion.FilePath.Should().StartWith(DriveRoot);
        }

        suggestion.FilePath.Should().EndWith(@"Daten\sicherung.vhdx").And.NotStartWith(@"U:\", "exFAT-Laufwerke taugen nicht für eine VHDX");
        suggestion.DriveLetter.Should().Be('Y', "Z: ist belegt");
        suggestion.VolumeLabel.Should().Be("Daten");
        VhdxRules.SuggestSizeGigabytes(300 * Gb).Should().Be(150);
        VhdxRules.SuggestSizeGigabytes(35 * Gb).Should().Be(17);
        VhdxRules.SuggestSizeGigabytes(57 * Gb).Should().Be(20);
        VhdxRules.SuggestSizeGigabytes(5 * Gb).Should().Be(2);
        VhdxRules.SuggestSizeGigabytes(0).Should().Be(1);
        VhdxRules.SuggestSizeGigabytes(10_000 * Gb).Should().Be(1024);
        service.SystemDriveHint(@"C:\Daten\x.vhdx").Should().Contain("Systemlaufwerk");
        service.SystemDriveHint(@"D:\Daten\x.vhdx").Should().BeNull();
    }

    [Fact]
    public async Task Create_starts_lumora_itself_elevated_with_a_readable_command()
    {
        _elevated.OnRun = (_, _) =>
        {
            _devices['X'] = @"\Device\HarddiskVolume12";
            File.WriteAllText(Plan().FilePath, "vhdx");
            return new ElevatedRunResult(false, 0, false);
        };

        var result = await CreateService().CreateVhdxAsync(Plan());

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("X:");
        var call = _elevated.Calls.Single();
        call.File.Should().Be(SelfExe, "der erhöhte Schritt ist Lumora selbst");
        call.Args.Should().Be($"--container-create-vhdx --path \"{Plan().FilePath}\" --size-mb 51200 --letter X --label \"Daten\"");
        call.Args.Should().NotContainAny("powershell", "PowerShell", "-EncodedCommand", "diskpart", "Bypass", "-Command", "cmd.exe");
        Directory.Exists(Path.Combine(_root, "appdata")).Should().BeFalse("keine Datei als Übergabe");
    }

    [Fact]
    public async Task Create_refuses_when_the_running_program_is_not_lumora()
    {
        var result = await CreateService(self: @"C:\Program Files\dotnet\dotnet.exe").CreateVhdxAsync(Plan());
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Lumora.exe");
        (await CreateService(self: null).CreateVhdxAsync(Plan())).Success.Should().BeFalse();
        _elevated.Calls.Should().BeEmpty();
        Directory.Exists(Path.GetDirectoryName(Plan().FilePath)).Should().BeFalse();

        ContainerCreationService.IsOwnExecutable(@"C:\Programme\Lumora\Lumora.exe").Should().BeTrue();
        ContainerCreationService.IsOwnExecutable(@"E:\Portable\Lumora-0.4.26.exe").Should().BeTrue("eine umbenannte Lumora.exe hat denselben Einsprung");
        ContainerCreationService.IsOwnExecutable(@"C:\x\testhost.exe").Should().BeFalse();
        ContainerCreationService.IsOwnExecutable(@"C:\x\Lumora.dll").Should().BeFalse();
        ContainerCreationService.IsOwnExecutable("Lumora.exe").Should().BeFalse("nur ein voller Pfad");
    }

    [Fact]
    public async Task Elevated_start_blocked_by_windows_is_explained()
    {
        _elevated.OnRun = (_, _) => new ElevatedRunResult(false, -1, false, StartFailed: true);

        var result = await CreateService().CreateVhdxAsync(Plan());

        result.Message.Should().Contain("nicht mit Administratorrechten starten").And.Contain("Sicherheitsprogramm").And.Contain("nichts angelegt");
        Directory.Exists(Path.GetDirectoryName(Plan().FilePath)).Should().BeFalse();
    }

    [Fact]
    public async Task A_refused_check_never_claims_a_foreign_file()
    {
        var plan = Plan();
        _elevated.OnRun = (_, _) =>
        {
            File.WriteAllText(plan.FilePath, "fremd"); // inzwischen von jemand anderem angelegt
            return new ElevatedRunResult(false, (int)VhdxJobExit.FileExists, false);
        };

        var result = await CreateService().CreateVhdxAsync(plan);

        result.Message.Should().Contain("gibt es schon").And.Contain("nichts angelegt").And.NotContain("Auswerfen");
        File.Exists(plan.FilePath).Should().BeTrue("Lumora löscht nichts, was es nicht angelegt hat");
    }

    [Fact]
    public async Task Declined_uac_leaves_nothing_behind()
    {
        _elevated.OnRun = (_, _) => new ElevatedRunResult(Declined: true, -1, false);
        var plan = Plan();

        var result = await CreateService().CreateVhdxAsync(plan);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("abgelehnt").And.Contain("nichts angelegt");
        Directory.Exists(Path.GetDirectoryName(plan.FilePath)).Should().BeFalse("den Ordner hatte Lumora selbst angelegt");
        File.Exists(plan.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task Occupied_letter_cloud_folder_file_system_and_size_are_refused_before_anything_runs()
    {
        _devices['X'] = @"\Device\VeraCryptVolumeX";
        var service = CreateService(cloud: [Path.Combine(_root, "Daten Ordner")], free: 10 * Gb);

        (await service.CreateVhdxAsync(Plan(letter: 'X'))).Message.Should().Contain("Cloud");
        var plain = CreateService(free: 10 * Gb);
        (await plain.CreateVhdxAsync(Plan(letter: 'X', size: 5))).Message.Should().Contain("X: ist schon belegt");
        (await plain.CreateVhdxAsync(Plan(letter: 'W', size: 11))).Message.Should().Contain("nur 10 GB frei");
        (await plain.CreateVhdxAsync(Plan(letter: 'W', label: "Ä"))).Success.Should().BeFalse();
        (await CreateService(fileSystem: "exFAT").CreateVhdxAsync(Plan(letter: 'W'))).Message.Should().Contain("NTFS");

        _elevated.Calls.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "Daten Ordner")).Should().BeFalse();
    }

    [Fact]
    public async Task Failures_and_timeout_explain_what_is_left()
    {
        var plan = Plan();
        _elevated.OnRun = (_, _) =>
        {
            File.WriteAllText(plan.FilePath, "halb");
            return new ElevatedRunResult(false, (int)VhdxJobExit.FormatFailed, false);
        };
        var halfway = await CreateService().CreateVhdxAsync(plan);
        halfway.Success.Should().BeFalse();
        halfway.Message.Should().Contain("nicht formatieren").And.Contain("Fehler 7116").And.Contain("Auswerfen");

        File.Delete(plan.FilePath);
        Directory.Delete(Path.GetDirectoryName(plan.FilePath)!);
        _elevated.OnRun = (_, _) => new ElevatedRunResult(false, (int)VhdxJobExit.AttachFailed, false); // hat selbst aufgeräumt
        var nothing = await CreateService().CreateVhdxAsync(Plan("andere.vhdx"));
        nothing.Message.Should().Contain("nicht einhängen").And.Contain("nichts angelegt");
        Directory.Exists(Path.GetDirectoryName(plan.FilePath)).Should().BeFalse();

        _elevated.OnRun = (_, _) => new ElevatedRunResult(false, -1, TimedOut: true);
        var slow = await CreateService().CreateVhdxAsync(Plan("langsam.vhdx"));
        slow.Message.Should().Contain("nicht rechtzeitig").And.Contain("X:");
        Directory.Exists(Path.GetDirectoryName(plan.FilePath)).Should().BeTrue("der erhöhte Lauf arbeitet vielleicht noch — nichts wegräumen");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(unchecked((int)0xC0000409))]
    [InlineData(0)]
    public async Task A_run_ended_from_outside_is_explained_as_security_software(int exitCode)
    {
        _elevated.OnRun = (_, _) => new ElevatedRunResult(false, exitCode, false);

        var result = await CreateService().CreateVhdxAsync(Plan());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Sicherheitsprogramm").And.Contain("VeraCrypt").And.Contain("schon eingehängt").And.Contain("nichts angelegt");
        Directory.Exists(Path.GetDirectoryName(Plan().FilePath)).Should().BeFalse();
    }

    [Fact]
    public void Exit_codes_become_understandable_messages()
    {
        ContainerCreationService.DescribeFailure(7111, 'X', fileLeft: false).Should().Contain("nicht anlegen").And.Contain("nichts angelegt");
        ContainerCreationService.DescribeFailure(7117, 'X', fileLeft: true).Should().Contain("X:").And.Contain("Auswerfen");
        ContainerCreationService.DescribeFailure(7105, 'T', fileLeft: false).Should().Contain("T: ist inzwischen belegt");
        ContainerCreationService.DescribeFailure(7108, 'X', fileLeft: false).Should().Contain("ohne Administratorrechte");
        ContainerCreationService.DescribeFailure(0, 'X', fileLeft: true).Should().Contain("nicht erschienen");
        ContainerCreationService.DescribeFailure(1, 'X', fileLeft: true).Should().Contain("Sicherheitsprogramm").And.Contain("Auswerfen");
        foreach (var exit in Enum.GetValues<VhdxJobExit>().Where(e => e != VhdxJobExit.Success))
        {
            var text = ContainerCreationService.DescribeFailure((int)exit, 'X', fileLeft: false);
            text.Should().Contain($"Fehler {(int)exit}").And.NotContain("Sicherheitsprogramm");
            text.Should().NotContainAny("diskpart", "PowerShell");
        }
    }

    [Fact]
    public void BitLocker_wizard_is_the_windows_one_for_the_new_drive()
    {
        var service = CreateService();
        service.OpenBitLockerWizard('x').Success.Should().BeTrue();
        _launcher.Calls.Single().Should().Be((FakeBitLocker.Wizard, @"X:\ T"));

        _launcher.Next = ShellLaunchResult.Declined;
        service.OpenBitLockerWizard('X').Message.Should().Contain("abgelehnt");
        _launcher.Next = ShellLaunchResult.Failed;
        service.OpenBitLockerWizard('X').Message.Should().Contain("BitLocker aktivieren");

        _launcher.Calls.Clear();
        _bitLocker.Reason = "Diese Windows-Ausgabe enthält BitLocker nicht (z. B. Windows Home). Bitte VeraCrypt verwenden.";
        var home = service.OpenBitLockerWizard('X');
        home.Success.Should().BeFalse();
        home.Message.Should().Contain("VeraCrypt");
        _launcher.Calls.Should().BeEmpty();
        service.BitLockerUnavailableReason.Should().Contain("Home");
    }

    [Fact]
    public async Task BitLocker_platform_reads_edition_and_protection_without_admin()
    {
        BitLockerPlatform.IsEditionSupported("Professional").Should().BeTrue();
        BitLockerPlatform.IsEditionSupported("Enterprise").Should().BeTrue();
        BitLockerPlatform.IsEditionSupported("Core").Should().BeFalse();
        BitLockerPlatform.IsEditionSupported("CoreSingleLanguage").Should().BeFalse();
        BitLockerPlatform.IsEditionSupported(null).Should().BeFalse();

        var runner = new FakeOutputRunner { Output = "3\r\n" };
        var home = new BitLockerPlatform(runner, () => "Core", _ => true);
        home.UnavailableReason.Should().Contain("Home");
        var pro = new BitLockerPlatform(runner, () => "Professional", _ => true);
        pro.UnavailableReason.Should().BeNull();
        new BitLockerPlatform(runner, () => "Professional", _ => false).UnavailableReason.Should().NotBeNull("ohne Assistent kein BitLocker");
        pro.WizardPath.Should().Be(Path.Combine(Environment.SystemDirectory, "BitLockerWizardElev.exe"));
        pro.WizardArguments('y').Should().Be(@"Y:\ T");

        (await pro.GetProtectionAsync('x', CancellationToken.None)).Should().Be(BitLockerProtection.Encrypting);
        var call = runner.Calls.Single();
        call.File.Should().EndWith("powershell.exe");
        string.Join(" ", call.Args).Should().Contain("Shell.Application").And.Contain("'X:'").And.Contain("System.Volume.BitLockerProtection")
            .And.NotContain("manage-bde").And.NotContain("Enable-BitLocker");

        BitLockerPlatform.ParseProtection("1").Should().Be(BitLockerProtection.On);
        BitLockerPlatform.ParseProtection("2").Should().Be(BitLockerProtection.Off);
        BitLockerPlatform.ParseProtection("6").Should().Be(BitLockerProtection.Locked);
        BitLockerPlatform.ParseProtection("").Should().Be(BitLockerProtection.Unknown);
        BitLockerPlatform.ParseProtection("Fehler").Should().Be(BitLockerProtection.Unknown);
        runner.ExitCode = 1;
        (await pro.GetProtectionAsync('X', CancellationToken.None)).Should().Be(BitLockerProtection.Unknown);
    }

    [Fact]
    public async Task Winget_installs_veracrypt_with_the_exact_command_only_when_asked()
    {
        var runner = new FakeOutputRunner();
        var installer = new WingetVeraCryptInstaller(runner, () => @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\winget.exe");
        installer.IsWingetAvailable.Should().BeTrue();
        runner.Calls.Should().BeEmpty("Lumora installiert nichts von selbst");

        var lines = new List<string>();
        runner.Lines = ["Gefunden VeraCrypt [IDRIX.VeraCrypt]", "Erfolgreich installiert"];
        var result = await installer.InstallAsync(new SyncProgress(lines), CancellationToken.None);

        result.Success.Should().BeTrue();
        runner.Calls.Single().File.Should().EndWith("winget.exe");
        runner.Calls.Single().Args.Should().Equal("install", "--id", "IDRIX.VeraCrypt", "-e", "--accept-package-agreements", "--accept-source-agreements");
        lines.Should().Contain("Erfolgreich installiert");

        var none = new FakeOutputRunner();
        var noWinget = new WingetVeraCryptInstaller(none, () => null);
        noWinget.IsWingetAvailable.Should().BeFalse();
        (await noWinget.InstallAsync(null, CancellationToken.None)).Message.Should().Contain("veracrypt.fr");
        none.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, true, "installiert")]
    [InlineData(unchecked((int)0x8A15002B), true, "installiert")]
    [InlineData(unchecked((int)0x8A15010C), false, "abgebrochen")]
    [InlineData(unchecked((int)0x8A150107), false, "Internet")]
    [InlineData(unchecked((int)0x8A150014), false, "nicht gefunden")]
    [InlineData(unchecked((int)0x8A15010A), false, "neu gestartet")]
    [InlineData(unchecked((int)0x80070005), false, "0x80070005")]
    public void Winget_results_are_explained(int exitCode, bool success, string text)
    {
        var result = WingetVeraCryptInstaller.Describe(new ProcessOutputResult(exitCode, false, "Zugriff verweigert\n"));
        result.Success.Should().Be(success);
        result.Message.Should().Contain(text);
        WingetVeraCryptInstaller.Describe(new ProcessOutputResult(-1, true, "")).Message.Should().Contain("zu lange");
        WingetVeraCryptInstaller.Describe(new ProcessOutputResult(-1, false, "", StartFailed: true)).Message.Should().Contain("nicht gestartet");
    }

    [Fact]
    public async Task Install_success_counts_only_when_veracrypt_is_found_afterwards()
    {
        _installer.Result = new ContainerCommandResult(true, "VeraCrypt ist installiert.");
        var service = CreateService();
        (await service.InstallVeraCryptAsync(null)).Message.Should().Contain("nicht gefunden");

        _locator.Exe = Path.Combine(_root, "VeraCrypt", "VeraCrypt.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_locator.Exe)!);
        File.WriteAllText(_locator.Exe, string.Empty);
        (await service.InstallVeraCryptAsync(null)).Success.Should().BeTrue();
        _installer.Calls.Should().Be(2);
    }

    [Fact]
    public void VeraCrypt_format_wizard_starts_next_to_veracrypt_exe()
    {
        var service = CreateService();
        service.StartVeraCryptFormat().Message.Should().Contain("nicht installiert");

        var folder = Path.Combine(_root, "VeraCrypt");
        Directory.CreateDirectory(folder);
        _locator.Exe = Path.Combine(folder, "VeraCrypt.exe");
        File.WriteAllText(_locator.Exe, string.Empty);
        service.StartVeraCryptFormat().Message.Should().Contain("nicht gefunden");
        _launcher.Calls.Should().BeEmpty();

        File.WriteAllText(Path.Combine(folder, "VeraCrypt Format.exe"), string.Empty);
        service.StartVeraCryptFormat().Success.Should().BeTrue();
        _launcher.Calls.Single().Should().Be((Path.Combine(folder, "VeraCrypt Format.exe"), (string?)null));

        service.OpenVeraCryptDownloadPage().Success.Should().BeTrue();
        _launcher.Urls.Single().Should().Be("https://www.veracrypt.fr/en/Downloads.html");
    }

    [Fact]
    public async Task Mount_uses_the_existing_container_tools()
    {
        var file = Path.Combine(_root, "sicherung.dat");
        File.WriteAllText(file, "container");
        var result = await CreateService().MountVeraCryptAsync(file, 'M');
        result.Success.Should().BeTrue();
        _containers.Mounted.Single().Should().Be((file, 'M'));

        (await CreateService().MountVeraCryptAsync(Path.Combine(_root, "fehlt.dat"), 'M')).Message.Should().Contain("Containerdatei");
        var cloud = CreateService(cloud: [_root]);
        cloud.ValidateContainerFile(file).Should().Contain("Cloud");
        (await cloud.MountVeraCryptAsync(file, 'N')).Success.Should().BeFalse();
        _containers.Mounted.Should().ContainSingle("in einem Cloud-Ordner wird nichts eingehängt");
    }

    [Fact]
    public void VeraCrypt_guide_defaults_to_hidden_volume_with_warnings()
    {
        VeraCryptGuide.DefaultType.Should().Be(VeraCryptVolumeType.Hidden);
        var hidden = VeraCryptGuide.Steps(VeraCryptVolumeType.Hidden);
        hidden.Select(s => s.Number).Should().Equal(Enumerable.Range(1, hidden.Count));
        string.Join("\n", hidden.Select(s => s.Detail)).Should()
            .Contain("Containerdatei erstellen").And.Contain("Verstecktes VeraCrypt-Volume").And.Contain("Normaler Modus")
            .And.Contain("Cloud").And.Contain("ÄUSSEREN").And.Contain("harmlose Fotos").And.Contain("ANDERES Kennwort");
        var warnings = string.Join("\n", VeraCryptGuide.Warnings(VeraCryptVolumeType.Hidden));
        warnings.Should().Contain("zerstören").And.Contain("Verstecktes Volume vor Beschädigung schützen").And.Contain("INNEREN");

        var normal = VeraCryptGuide.Steps(VeraCryptVolumeType.Normal);
        string.Join("\n", normal.Select(s => s.Detail)).Should().Contain("Standard-VeraCrypt-Volume").And.NotContain("Verstecktes VeraCrypt-Volume");
        string.Join("\n", VeraCryptGuide.Warnings(VeraCryptVolumeType.Normal)).Should().NotContain("zerstören").And.Contain("Kennw");
        VeraCryptGuide.WhyWizard.Should().Contain("Befehlszeile");
    }

    private sealed class FakeDrives(params DriveSpace[] drives) : IDriveSpaceSource
    {
        public IReadOnlyList<DriveSpace> GetFixedDrives() => drives;
    }

    private sealed class FakeDevices : Dictionary<char, string>, IVolumeDeviceQuery
    {
        public string? GetDeviceName(char driveLetter) => TryGetValue(char.ToUpperInvariant(driveLetter), out var name) ? name : null;
    }

    private sealed class FakeLocator(string? exe) : IVeraCryptLocator
    {
        public string? Exe { get; set; } = exe;

        public string? FindExecutable() => Exe is not null && File.Exists(Exe) ? Exe : null;
    }

    private sealed class FakeElevated : IElevatedRunner
    {
        public List<(string File, string Args)> Calls { get; } = new();

        public Func<string, string, ElevatedRunResult> OnRun { get; set; } = (_, _) => new ElevatedRunResult(false, 0, false);

        public Task<ElevatedRunResult> RunElevatedAsync(string fileName, string argumentLine, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((fileName, argumentLine));
            return Task.FromResult(OnRun(fileName, argumentLine));
        }
    }

    private sealed class FakeLauncher : IShellLauncher
    {
        public List<(string File, string? Args)> Calls { get; } = new();

        public List<string> Urls { get; } = new();

        public ShellLaunchResult Next { get; set; } = ShellLaunchResult.Started;

        public ShellLaunchResult Start(string fileName, string? argumentLine)
        {
            Calls.Add((fileName, argumentLine));
            return Next;
        }

        public bool OpenUrl(string url)
        {
            Urls.Add(url);
            return true;
        }
    }

    private sealed class FakeBitLocker : IBitLockerPlatform
    {
        public const string Wizard = @"C:\Windows\System32\BitLockerWizardElev.exe";

        public string? Reason { get; set; }

        public string? UnavailableReason => Reason;

        public string WizardPath => Wizard;

        public string WizardArguments(char driveLetter) => $"{char.ToUpperInvariant(driveLetter)}:\\ T";

        public Task<BitLockerProtection> GetProtectionAsync(char driveLetter, CancellationToken ct) => Task.FromResult(BitLockerProtection.On);
    }

    private sealed class FakeInstaller : IVeraCryptInstaller
    {
        public int Calls { get; private set; }

        public ContainerCommandResult Result { get; set; } = new(false, "nicht");

        public bool IsWingetAvailable => true;

        public Task<ContainerCommandResult> InstallAsync(IProgress<string>? progress, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeMountTools : IContainerTools
    {
        public List<(string File, char Letter)> Mounted { get; } = new();

        public bool IsVeraCryptInstalled => true;

        public ContainerKind Detect(string driveRoot) => ContainerKind.Unknown;

        public Task<ContainerCommandResult> DismountAsync(string driveRoot, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ContainerCommandResult> MountVeraCryptAsync(string containerFile, char driveLetter, CancellationToken ct = default)
        {
            Mounted.Add((containerFile, driveLetter));
            return Task.FromResult(new ContainerCommandResult(true, "ok"));
        }
    }

    private sealed class FakeOutputRunner : IProcessOutputRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();

        public string Output { get; set; } = string.Empty;

        public int ExitCode { get; set; }

        public IReadOnlyList<string> Lines { get; set; } = [];

        public Task<ProcessOutputResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, IProgress<string>? lines, CancellationToken ct)
        {
            Calls.Add((fileName, arguments.ToList()));
            foreach (var line in Lines)
            {
                lines?.Report(line);
            }

            return Task.FromResult(new ProcessOutputResult(ExitCode, false, Output + string.Join("\n", Lines)));
        }
    }

    /// <summary>Progress ohne SynchronizationContext-Umweg (sofort, im Test-Thread).</summary>
    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}
