using System.Runtime.InteropServices;
using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora.Tests.HiddenLibraries;

/// <summary>
/// ADR-0061, Nachtrag 0.4.26: der erhöhte Schritt <c>Lumora.exe --container-create-vhdx …</c>. Nur mit einem Fake der
/// Windows-Seite (<see cref="IVirtualDiskApi"/>) — nie echt: keine VHDX, keine Administratorrechte, keine UAC-Rückfrage.
/// Geprüft werden Befehlszeile (lesbar, streng gelesen), Prüfung im erhöhten Lauf, Ablauf und Aufräumen je Schritt,
/// Exit-Codes, P/Invoke-Strukturgrößen und dass im Quelltext kein Skript-/PowerShell-Weg mehr steckt.
/// </summary>
public sealed class VhdxElevatedJobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "vhdx-job-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDiskApi _api = new();

    public VhdxElevatedJobTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Daten"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string File1 => Path.Combine(_root, "Daten", "sicherung.vhdx");

    private VhdxElevatedJob Job(IReadOnlyList<string>? cloud = null) => new(_api, () => cloud ?? []);

    private string[] Args(string? path = null, string size = "51200", string letter = "X", string label = "Daten")
        => ["--container-create-vhdx", "--path", path ?? File1, "--size-mb", size, "--letter", letter, "--label", label];

    [Fact]
    public void Command_line_is_readable_and_round_trips_through_windows_parsing()
    {
        var plan = new VhdxPlan(@"D:\Meine Daten\Bücher\sicherung.vhdx", 50, 'x', "Daten 1");

        var line = VhdxElevatedCommand.BuildArguments(plan);

        line.Should().Be("--container-create-vhdx --path \"D:\\Meine Daten\\Bücher\\sicherung.vhdx\" --size-mb 51200 --letter X --label \"Daten 1\"");
        VhdxElevatedCommand.TryParse(SplitLikeWindows(line), out var request).Should().BeTrue();
        request.Should().Be(new VhdxElevatedRequest(@"D:\Meine Daten\Bücher\sicherung.vhdx", 51200, 'X', "Daten 1"));

        var quote = () => VhdxElevatedCommand.BuildArguments(plan with { VolumeLabel = "a\"b" });
        quote.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("--container-create-vhdx")]
    [InlineData("--container-create-vhdx --path D:\\a.vhdx --size-mb 1024 --letter X")]
    [InlineData("--container-create-vhdx --path D:\\a.vhdx --size-mb 1024 --letter X --label Daten --extra 1")]
    [InlineData("--container-create-vhdx --path D:\\a.vhdx --path D:\\b.vhdx --size-mb 1024 --letter X")]
    [InlineData("--container-create-vhdx --path D:\\a.vhdx --size-mb 1e3 --letter X --label Daten")]
    [InlineData("--container-create-vhdx --path D:\\a.vhdx --size-mb -1024 --letter X --label Daten")]
    [InlineData("--container-create-vhdx --path D:\\a.vhdx --size-mb 1024 --letter XY --label Daten")]
    [InlineData("--container-create-vhdx --path D:\\a.vhdx --size-mb 1024 --letter 7 --label Daten")]
    [InlineData("--container-create-vhdx --path D:\\a.vhdx --size-mb 1024 --letter X --Label Daten")]
    [InlineData("--other --path D:\\a.vhdx --size-mb 1024 --letter X --label Daten")]
    public void Malformed_command_lines_are_refused_without_touching_windows(string line)
    {
        var args = line.Split(' ');
        VhdxElevatedCommand.TryParse(args, out _).Should().BeFalse();
        Job().Run(args).Should().Be((int)VhdxJobExit.InvalidArguments);
        _api.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"D:\Daten\sicherung.vhdx", true)]
    [InlineData(@"D:\Bücher\Sicherung 2.VHDX", true)]
    [InlineData(@"D:\Daten\..\Windows\sicherung.vhdx", false)]
    [InlineData(@"D:\Daten\.\sicherung.vhdx", false)]
    [InlineData(@"D:\Daten\\sicherung.vhdx", false)]
    [InlineData(@"D:/Daten/sicherung.vhdx", false)]
    [InlineData(@"D:sicherung.vhdx", false)]
    [InlineData(@"\\server\share\sicherung.vhdx", false)]
    [InlineData(@"\\?\D:\Daten\sicherung.vhdx", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"D:\Daten.\sicherung.vhdx", false)]
    [InlineData(@"D:\Daten \sicherung.vhdx", false)]
    [InlineData(@"D:\Daten\sicherung.vhdx:stream", false)]
    [InlineData(@"D:\Daten\sicherung.vhdx.", false)]
    [InlineData(@"D:\Daten\sicherung.vhd", false)]
    [InlineData(@"sicherung.vhdx", false)]
    [InlineData("", false)]
    public void Only_plain_local_paths_pass_the_elevated_check(string path, bool ok)
        => VhdxElevatedJob.IsStrictLocalPath(path).Should().Be(ok);

    [Fact]
    public void Elevated_validation_refuses_everything_suspicious()
    {
        var job = Job(cloud: [Path.Combine(_root, "Wolke")]);
        Validate(job, Args()).Should().Be(VhdxJobExit.Success);

        Validate(job, Args(path: Path.Combine(_root, "Wolke", "a.vhdx"))).Should().Be(VhdxJobExit.InvalidPath, "Cloud-Ordner aus der Umgebung");
        Validate(job, Args(path: Path.Combine(_root, "OneDrive - Firma", "a.vhdx"))).Should().Be(VhdxJobExit.InvalidPath, "Cloud-Ordner am Namen");
        Validate(job, Args(path: Path.Combine(_root, "Daten", "..", "Daten", "a.vhdx"))).Should().Be(VhdxJobExit.InvalidPath);
        Validate(job, Args(path: Path.Combine(_root, "fehlt", "a.vhdx"))).Should().Be(VhdxJobExit.DriveNotSuitable, "der Ordner legt die Oberfläche an");
        File.WriteAllText(Path.Combine(_root, "Daten", "da.vhdx"), "x");
        Validate(job, Args(path: Path.Combine(_root, "Daten", "da.vhdx"))).Should().Be(VhdxJobExit.FileExists);
        Validate(job, Args(size: "1023")).Should().Be(VhdxJobExit.InvalidSize);
        Validate(job, Args(size: (64L * 1024 * 1024 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))).Should().Be(VhdxJobExit.InvalidSize);
        Validate(job, Args(letter: "C")).Should().Be(VhdxJobExit.InvalidArguments, "A–C sind nie ein neues Laufwerk");
        Validate(job, Args(label: "Äpfel")).Should().Be(VhdxJobExit.InvalidLabel);
        Validate(job, Args(label: " Daten")).Should().Be(VhdxJobExit.InvalidLabel);

        _api.ReparsePoints.Add(Path.Combine(_root, "Daten"));
        Validate(job, Args()).Should().Be(VhdxJobExit.InvalidPath, "eine Verknüpfung im Pfad könnte woandershin zeigen");
        _api.ReparsePoints.Clear();

        _api.DriveFormat = "FAT32";
        Validate(job, Args()).Should().Be(VhdxJobExit.DriveNotSuitable);
        _api.DriveFormat = null;
        Validate(job, Args()).Should().Be(VhdxJobExit.DriveNotSuitable, "kein lokales Laufwerk");
        _api.DriveFormat = "NTFS";

        _api.UsedLetters.Add('X');
        Validate(job, Args()).Should().Be(VhdxJobExit.LetterInUse);

        _api.Calls.Should().BeEmpty("vor einer bestandenen Prüfung wird nichts angelegt");
    }

    [Fact]
    public void Success_runs_every_windows_step_in_order_and_assigns_the_letter_last()
    {
        var code = Job().Run(Args());

        code.Should().Be(0);
        _api.Calls.Should().Equal(
            $"create {File1} {51200L * 1024 * 1024}",
            $"attach {File1}",
            $"disk {File1}",
            "init 7",
            "partition 7",
            "format 7/2 Daten",
            "letter 7/2 X");
    }

    [Theory]
    [InlineData("create", VhdxJobExit.CreateFailed)]
    [InlineData("attach", VhdxJobExit.AttachFailed)]
    [InlineData("disk", VhdxJobExit.DiskNumberFailed)]
    [InlineData("init", VhdxJobExit.InitializeFailed)]
    [InlineData("partition", VhdxJobExit.PartitionFailed)]
    [InlineData("format", VhdxJobExit.FormatFailed)]
    [InlineData("letter", VhdxJobExit.LetterFailed)]
    public void A_failing_step_cleans_up_what_this_run_created(string failAt, VhdxJobExit expected)
    {
        _api.FailAt = failAt;

        var code = Job().Run(Args());

        code.Should().Be((int)expected);
        // Auch nach einem gescheiterten Anlegen: die Datei gab es vorher nicht, ein Rest davon stammt von diesem Lauf
        Cleanup().Should().Equal($"detach {File1}", $"delete {File1}");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(183)]
    public void A_file_created_meanwhile_by_someone_else_is_not_deleted(int nativeError)
    {
        _api.FailAt = "create";
        _api.Failure = new System.ComponentModel.Win32Exception(nativeError);

        Job().Run(Args()).Should().Be((int)VhdxJobExit.CreateFailed);

        Cleanup().Should().BeEmpty();
    }

    [Fact]
    public void A_folder_turned_into_a_link_during_the_run_blocks_the_delete()
    {
        _api.FailAt = "format";
        _api.OnStep = name =>
        {
            if (name == "partition")
            {
                _api.ReparsePoints.Add(Path.Combine(_root, "Daten"));
            }
        };

        Job().Run(Args()).Should().Be((int)VhdxJobExit.FormatFailed);

        Cleanup().Should().Equal($"detach {File1}");
    }

    private List<string> Cleanup()
        => _api.Calls.Where(c => c.StartsWith("detach", StringComparison.Ordinal) || c.StartsWith("delete", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Cleanup_failures_do_not_hide_the_step_code()
    {
        _api.FailAt = "format";
        _api.FailCleanup = true;

        Job().Run(Args()).Should().Be((int)VhdxJobExit.FormatFailed);
    }

    [Fact]
    public void Without_admin_rights_nothing_is_attempted_and_surprises_become_unexpected()
    {
        _api.Elevated = false;
        Job().Run(Args()).Should().Be((int)VhdxJobExit.NotElevated);
        _api.Calls.Should().BeEmpty();

        _api.Elevated = true;
        _api.ThrowOnDriveFormat = true;
        Job().Run(Args()).Should().Be((int)VhdxJobExit.Unexpected);
        _api.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Exit_codes_are_recognizable_as_lumora_codes()
    {
        VhdxElevatedCommand.IsOwnExitCode(0).Should().BeTrue();
        VhdxElevatedCommand.IsOwnExitCode(7116).Should().BeTrue();
        VhdxElevatedCommand.IsOwnExitCode(1).Should().BeFalse();
        VhdxElevatedCommand.IsOwnExitCode(-1).Should().BeFalse();
        VhdxElevatedCommand.IsOwnExitCode(1223).Should().BeFalse();
        Enum.GetValues<VhdxJobExit>().Where(e => e != VhdxJobExit.Success).Should().OnlyContain(e => (int)e >= 7100 && (int)e < 7200);
    }

    [Fact]
    public void Virtdisk_structures_match_the_windows_header_for_64_bit()
    {
        Environment.Is64BitProcess.Should().BeTrue("Lumora läuft nur als 64-Bit-Prozess");
        VirtualDiskNative.StructSizes.Should().Be((20, 256, 44, 24));
        Marshal.OffsetOf<VirtualDiskNative.CreateParameters>(nameof(VirtualDiskNative.CreateParameters.UniqueId)).Should().Be(8);
        Marshal.OffsetOf<VirtualDiskNative.CreateParameters>(nameof(VirtualDiskNative.CreateParameters.MaximumSize)).Should().Be(24);
        Marshal.OffsetOf<VirtualDiskNative.CreateParameters>(nameof(VirtualDiskNative.CreateParameters.ParentPath)).Should().Be(48);
        Marshal.OffsetOf<VirtualDiskNative.CreateParameters>(nameof(VirtualDiskNative.CreateParameters.ResiliencyGuid)).Should().Be(108);
        Marshal.OffsetOf<VirtualDiskNative.AttachParameters>(nameof(VirtualDiskNative.AttachParameters.Reserved)).Should().Be(8);
        Marshal.OffsetOf<VirtualDiskNative.OpenParameters>(nameof(VirtualDiskNative.OpenParameters.ResiliencyGuid)).Should().Be(12);
        VirtualDiskNative.VhdxType.DeviceId.Should().Be(3);
        VirtualDiskNative.AttachFlagPermanentLifetime.Should().Be(4, "dauerhaft eingehängt wie vorher");
        Marshal.SizeOf<VirtualDiskNative.TokenPrivileges>().Should().Be(16);

        VirtualDiskNative.ParseDiskNumber(@"\\.\PhysicalDrive12").Should().Be(12);
        var bad = () => VirtualDiskNative.ParseDiskNumber(@"\\.\CdRom0");
        bad.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Source_no_longer_contains_a_script_powershell_or_encoded_elevation()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        files.Should().NotBeEmpty();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            text.Should().NotContainEquivalentOf("EncodedCommand", $"{Path.GetFileName(file)} darf nichts kodiert starten");
            text.Should().NotContainEquivalentOf("diskpart", $"{Path.GetFileName(file)}: kein Skript-Werkzeug mehr");
        }

        // Erhöht startet nur ElevatedProcessRunner (runas), und der Container-Weg übergibt ihm Lumora selbst
        files.Where(f => File.ReadAllText(f).Contains("\"runas\"", StringComparison.Ordinal)).Select(Path.GetFileName)
            .Should().Equal("ContainerProcesses.cs");
        var containers = Path.Combine(src, "Lumora.Services", "HiddenLibraries", "Containers");
        Directory.EnumerateFiles(containers, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("powershell", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Should().Equal(["BitLockerPlatform.cs"], "nur die unveränderte Statusabfrage ohne Administratorrechte nutzt noch PowerShell");
        var creation = File.ReadAllText(Path.Combine(containers, "ContainerCreationService.cs"));
        creation.Should().NotContain("PowerShellPath").And.Contain("VhdxElevatedCommand.BuildArguments");
    }

    private static VhdxJobExit Validate(VhdxElevatedJob job, string[] args)
    {
        VhdxElevatedCommand.TryParse(args, out var request).Should().BeTrue();
        return job.Validate(request!);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumora.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo-Wurzel (Lumora.slnx) nicht gefunden");
    }

    /// <summary>Wie Windows die Befehlszeile für <c>Main(string[] args)</c> zerlegt (<c>CommandLineToArgvW</c>, nur lesend).</summary>
    private static string[] SplitLikeWindows(string arguments)
    {
        var argv = CommandLineToArgvW("Lumora.exe " + arguments, out var count);
        try
        {
            var result = new string[count - 1];
            for (var i = 1; i < count; i++)
            {
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
#pragma warning restore SYSLIB1054

    /// <summary>Die Windows-Seite als Fake: protokolliert jeden Schritt, kann an einem Schritt scheitern.</summary>
    private sealed class FakeDiskApi : IVirtualDiskApi
    {
        public List<string> Calls { get; } = new();

        public HashSet<string> ReparsePoints { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<char> UsedLetters { get; } = new();

        public string? DriveFormat { get; set; } = "NTFS";

        public bool Elevated { get; set; } = true;

        public bool ThrowOnDriveFormat { get; set; }

        public string? FailAt { get; set; }

        public bool FailCleanup { get; set; }

        public Exception? Failure { get; set; }

        public Action<string>? OnStep { get; set; }

        public bool IsElevated => Elevated;

        public string? GetLocalDriveFormat(string driveRoot)
            => ThrowOnDriveFormat ? throw new IOException("Laufwerk weg") : DriveFormat;

        public bool IsDriveLetterInUse(char letter) => UsedLetters.Contains(letter);

        public bool IsReparsePoint(string directory) => ReparsePoints.Contains(directory);

        public void CreateDynamicVhdx(string path, ulong maximumSizeBytes) => Step("create", $"create {path} {maximumSizeBytes}");

        public void AttachPermanently(string path) => Step("attach", $"attach {path}");

        public int GetDiskNumber(string path)
        {
            Step("disk", $"disk {path}");
            return 7;
        }

        public void InitializeGpt(int diskNumber) => Step("init", $"init {diskNumber}");

        public int CreateMaximumPartition(int diskNumber)
        {
            Step("partition", $"partition {diskNumber}");
            return 2;
        }

        public void FormatNtfs(int diskNumber, int partitionNumber, string label) => Step("format", $"format {diskNumber}/{partitionNumber} {label}");

        public void AssignDriveLetter(int diskNumber, int partitionNumber, char letter) => Step("letter", $"letter {diskNumber}/{partitionNumber} {letter}");

        public void Detach(string path)
        {
            Calls.Add($"detach {path}");
            if (FailCleanup)
            {
                throw new IOException("bleibt eingehängt");
            }
        }

        public void DeleteFile(string path)
        {
            Calls.Add($"delete {path}");
            if (FailCleanup)
            {
                throw new IOException("in Benutzung");
            }
        }

        private void Step(string name, string call)
        {
            Calls.Add(call);
            OnStep?.Invoke(name);
            if (FailAt == name)
            {
                throw Failure ?? new InvalidOperationException(name + " scheitert (Test)");
            }
        }
    }
}
