using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.Containers;
using Lumora.Services.Infrastructure;
using Lumora.Shell.HiddenLibraries;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.HiddenLibraries;

/// <summary>
/// ADR-0061: Zustandsmaschine des Container-Assistenten (Wahl → Erstellen → verschlüsselt/eingehängt → Bibliothek
/// anlegen), Abbruch an jeder Stelle ohne Seiteneffekte, Übergabe an „Versteckte Bibliothek anlegen“ und kein Pfad,
/// keine Bezeichnung, kein Kennwort im Protokoll. Alles mit Fakes — nie echt diskpart, BitLocker, winget oder VeraCrypt.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ContainerAssistantTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Existing_container_closes_without_doing_anything()
    {
        var creation = new RecordingCreation();
        var vm = new ContainerAssistantViewModel(creation, new ScriptedPicker());
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.Step.Should().Be(ContainerAssistantStep.Choice);
        vm.IsExistingChoice.Should().BeTrue("Standard: Container ist schon eingehängt");
        await vm.PrimaryCommand.ExecuteAsync(null);

        closed.Should().Be(1);
        vm.Result.Should().Be(new ContainerAssistantResult(ContainerAssistantOutcome.ExistingContainer));
        creation.SideEffects.Should().BeEmpty();
    }

    [Fact]
    public async Task BitLocker_flow_creates_encrypts_and_hands_over_the_new_drive()
    {
        var creation = new RecordingCreation();
        var vm = new ContainerAssistantViewModel(creation, new ScriptedPicker { Vhdx = @"E:\Ablage\sicherung.vhdx" });

        vm.IsBitLockerChoice = true;
        await vm.NextAsync();
        vm.Step.Should().Be(ContainerAssistantStep.BitLockerSetup);
        vm.VhdxPath.Should().Be(@"D:\Daten\sicherung.vhdx");
        vm.SizeText.Should().Be("200");
        vm.SelectedLetter.Should().Be("X:");
        vm.VolumeLabel.Should().Be("Daten");
        vm.PrimaryText.Should().Be("Container erstellen");
        creation.SideEffects.Should().BeEmpty("Vorschlagen tut nichts");

        vm.BrowseVhdxCommand.Execute(null);
        vm.VhdxPath.Should().Be(@"E:\Ablage\sicherung.vhdx");
        vm.SizeText = "zwanzig";
        await vm.CreateVhdxAsync();
        vm.Error.Should().Contain("ganze Zahl");
        creation.SideEffects.Should().BeEmpty();

        vm.SizeText = "20";
        await vm.PrimaryCommand.ExecuteAsync(null);
        creation.SideEffects.Should().Equal(@"vhdx:E:\Ablage\sicherung.vhdx|20|X|Daten");
        vm.Step.Should().Be(ContainerAssistantStep.BitLockerEncrypt);
        vm.CanGoBack.Should().BeFalse("der Container ist schon angelegt");

        vm.OpenBitLockerWizardCommand.Execute(null);
        creation.SideEffects.Should().EndWith("bitlocker-wizard:X");
        vm.Info.Should().Contain("Verschlüsselung prüfen");

        creation.Protection = BitLockerProtection.Off;
        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.Step.Should().Be(ContainerAssistantStep.BitLockerEncrypt);
        vm.Error.Should().Contain("noch nicht mit BitLocker verschlüsselt");

        creation.Protection = BitLockerProtection.Encrypting;
        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.Step.Should().Be(ContainerAssistantStep.Ready);
        vm.PrimaryText.Should().Be("Bibliothek auf X: anlegen");
        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.Result.Should().Be(new ContainerAssistantResult(ContainerAssistantOutcome.NewContainer, @"X:\"));
    }

    [Fact]
    public async Task Declined_uac_keeps_the_page_with_a_clear_message()
    {
        var creation = new RecordingCreation { CreateResult = new ContainerCommandResult(false, "Abgebrochen: Die Windows-Rückfrage (Administratorrechte) wurde abgelehnt. Es wurde nichts angelegt.") };
        var vm = new ContainerAssistantViewModel(creation, new ScriptedPicker());
        vm.IsBitLockerChoice = true;
        await vm.NextAsync();

        await vm.CreateVhdxAsync();

        vm.Step.Should().Be(ContainerAssistantStep.BitLockerSetup);
        vm.Error.Should().Contain("abgelehnt").And.Contain("nichts angelegt");
        vm.Cancel();
        vm.Result.Should().Be(ContainerAssistantResult.Cancelled(), "nichts angelegt → kein Hinweis nötig");
    }

    [Fact]
    public async Task Without_bitlocker_the_assistant_suggests_veracrypt()
    {
        var creation = new RecordingCreation { BitLockerReason = "Diese Windows-Ausgabe enthält BitLocker nicht (z. B. Windows Home). Bitte VeraCrypt verwenden." };
        var vm = new ContainerAssistantViewModel(creation, new ScriptedPicker());
        vm.IsBitLockerAvailable.Should().BeFalse();

        vm.IsBitLockerChoice = true;
        await vm.NextAsync();

        vm.Step.Should().Be(ContainerAssistantStep.Choice);
        vm.Error.Should().Contain("VeraCrypt");
        vm.IsVeraCryptChoice.Should().BeTrue();
        creation.SideEffects.Should().BeEmpty();
    }

    [Fact]
    public async Task VeraCrypt_flow_installs_only_after_confirmation_then_guides_and_mounts()
    {
        var creation = new RecordingCreation { VeraCryptInstalled = false };
        var vm = new ContainerAssistantViewModel(creation, new ScriptedPicker { Container = @"D:\Daten\sicherung.dat" });

        vm.IsVeraCryptChoice = true;
        await vm.NextAsync();
        vm.Step.Should().Be(ContainerAssistantStep.VeraCryptInstall);
        vm.PrimaryText.Should().Be("Zustimmen und installieren");
        vm.PackageText.Should().Contain("IDRIX.VeraCrypt");
        vm.LicenseText.Should().Contain("Apache License 2.0").And.Contain("stimmen Sie");
        creation.SideEffects.Should().BeEmpty("Installation nur nach dem Klick");

        creation.InstallResult = new ContainerCommandResult(false, "Keine Internetverbindung — bitte Verbindung prüfen und erneut versuchen.");
        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.Error.Should().Contain("Internet");
        vm.Step.Should().Be(ContainerAssistantStep.VeraCryptInstall);

        creation.InstallResult = new ContainerCommandResult(true, "VeraCrypt ist installiert.");
        await vm.PrimaryCommand.ExecuteAsync(null);
        creation.SideEffects.Should().Equal("winget", "winget");
        vm.Step.Should().Be(ContainerAssistantStep.VeraCryptGuide);

        vm.IsHiddenVolume.Should().BeTrue("Standard: verstecktes Volume (Herbert)");
        vm.GuideSteps.Should().Contain(s => s.Detail.Contains("Verstecktes VeraCrypt-Volume"));
        vm.GuideWarnings.Should().Contain(w => w.Contains("zerstören"));
        vm.IsNormalVolume = true;
        vm.GuideSteps.Should().NotContain(s => s.Detail.Contains("Verstecktes VeraCrypt-Volume"));
        vm.GuideWarnings.Should().NotContain(w => w.Contains("zerstören"));
        vm.IsHiddenVolume = true;

        vm.StartVeraCryptFormatCommand.Execute(null);
        creation.SideEffects.Should().EndWith("veracrypt-format");

        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.Step.Should().Be(ContainerAssistantStep.VeraCryptMount);
        vm.SelectedMountLetter.Should().Be("X:");
        await vm.MountAsync();
        vm.Error.Should().Contain("Containerdatei");

        vm.BrowseContainerCommand.Execute(null);
        vm.SelectedMountLetter = "M:";
        await vm.PrimaryCommand.ExecuteAsync(null);
        creation.SideEffects.Should().EndWith(@"mount:D:\Daten\sicherung.dat|M");
        vm.Step.Should().Be(ContainerAssistantStep.Ready);
        await vm.FinishAsync();
        vm.Result.Should().Be(new ContainerAssistantResult(ContainerAssistantOutcome.NewContainer, @"M:\"));
    }

    [Fact]
    public async Task Without_winget_the_assistant_points_to_the_official_page()
    {
        var creation = new RecordingCreation { VeraCryptInstalled = false, WingetAvailable = false };
        var vm = new ContainerAssistantViewModel(creation, new ScriptedPicker());
        vm.IsVeraCryptChoice = true;
        await vm.NextAsync();

        vm.IsWingetMissing.Should().BeTrue();
        vm.PrimaryText.Should().Be("Erneut prüfen");
        vm.DownloadUrl.Should().Be("https://www.veracrypt.fr/en/Downloads.html");
        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.Error.Should().Contain("noch nicht gefunden");
        vm.OpenDownloadPageCommand.Execute(null);
        creation.SideEffects.Should().Equal("download-page");

        creation.VeraCryptInstalled = true; // selbst installiert
        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.Step.Should().Be(ContainerAssistantStep.VeraCryptGuide);
        creation.SideEffects.Should().NotContain("winget");

        // Mit winget, aber inzwischen selbst installiert: „Zustimmen und installieren“ startet kein zweites winget
        var again = new RecordingCreation { VeraCryptInstalled = false };
        var vm2 = new ContainerAssistantViewModel(again, new ScriptedPicker());
        vm2.IsVeraCryptChoice = true;
        await vm2.NextAsync();
        again.VeraCryptInstalled = true;
        await vm2.PrimaryCommand.ExecuteAsync(null);
        vm2.Step.Should().Be(ContainerAssistantStep.VeraCryptGuide);
        again.SideEffects.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ContainerAssistantStep.Choice)]
    [InlineData(ContainerAssistantStep.BitLockerSetup)]
    [InlineData(ContainerAssistantStep.BitLockerEncrypt)]
    [InlineData(ContainerAssistantStep.VeraCryptInstall)]
    [InlineData(ContainerAssistantStep.VeraCryptGuide)]
    [InlineData(ContainerAssistantStep.VeraCryptMount)]
    [InlineData(ContainerAssistantStep.Ready)]
    public async Task Cancel_works_on_every_page_and_does_nothing_itself(ContainerAssistantStep page)
    {
        var creation = new RecordingCreation { VeraCryptInstalled = page != ContainerAssistantStep.VeraCryptGuide && page != ContainerAssistantStep.VeraCryptMount ? false : true };
        var vm = new ContainerAssistantViewModel(creation, new ScriptedPicker { Container = @"D:\c.dat" });
        await ReachAsync(vm, page);
        vm.Step.Should().Be(page);
        var before = creation.SideEffects.ToList();

        vm.CancelCommand.Execute(null);

        vm.Result!.Outcome.Should().Be(ContainerAssistantOutcome.Cancelled);
        creation.SideEffects.Should().Equal(before, "Abbrechen startet, löscht und hängt nichts aus");
        if (page == ContainerAssistantStep.BitLockerEncrypt)
        {
            vm.Result.Message.Should().Contain("noch nicht verschlüsselt").And.Contain("X:");
        }
        else if (page == ContainerAssistantStep.Ready)
        {
            vm.Result.Message.Should().Contain("X: eingehängt").And.Contain("schon eingehängt");
        }
        else
        {
            vm.Result.Message.Should().BeNull();
        }
    }

    [Fact]
    public async Task Back_returns_without_side_effects_and_cancel_waits_while_busy()
    {
        var creation = new RecordingCreation();
        var vm = new ContainerAssistantViewModel(creation, new ScriptedPicker());
        vm.IsBitLockerChoice = true;
        await vm.NextAsync();
        vm.BackCommand.Execute(null);
        vm.Step.Should().Be(ContainerAssistantStep.Choice);

        var gate = new TaskCompletionSource<ContainerCommandResult>();
        creation.CreateGate = gate.Task;
        await vm.NextAsync();
        var running = vm.CreateVhdxAsync();
        vm.IsBusy.Should().BeTrue();
        vm.Cancel();
        vm.Result.Should().BeNull("während ein Schritt läuft, bleibt der Assistent offen");
        gate.SetResult(new ContainerCommandResult(true, "Der Container ist angelegt und als X: eingehängt."));
        await running;
        vm.IsBusy.Should().BeFalse();
        vm.Step.Should().Be(ContainerAssistantStep.BitLockerEncrypt);
    }

    [Fact]
    public async Task Create_command_asks_the_assistant_first_and_prefers_the_new_drive()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", "assistant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manager = new CatalogSessionManager(
                new HiddenModeOptions(true, Path.Combine(root, "appdata"), Path.Combine(root, "appdata"), int.MaxValue, UseUiHooks: false),
                new HiddenLibraryStore(iterations: 1_000),
                new FixedDriveRoots(root),
                new FakeContainers(),
                TimeProvider.System,
                NullLogger.Instance);
            manager.AttachSwitcher(new FakeHostSwitcher());
            var dialogs = new ScriptedHiddenDialogs();
            var messages = new ScriptedDialogs();
            var assistant = new ScriptedAssistant { Next = ContainerAssistantResult.Cancelled("Der Container ist als Laufwerk X: angelegt, aber noch nicht verschlüsselt.") };
            var vm = new HiddenLibrariesViewModel(manager, dialogs, messages, NullLogger<HiddenLibrariesViewModel>.Instance, assistant);

            await vm.CreateCommand.ExecuteAsync(null);
            assistant.Runs.Should().Be(1);
            dialogs.Asked.Should().BeEmpty("abgebrochen → kein Anlegen-Dialog");
            messages.Errors.Should().ContainSingle(e => e.Contains("noch nicht verschlüsselt"));

            assistant.Next = new ContainerAssistantResult(ContainerAssistantOutcome.NewContainer, @"X:\");
            await vm.CreateCommand.ExecuteAsync(null);
            dialogs.Asked.Should().Equal("create");
            dialogs.LastDrives.Should().Equal(@"X:\", root);

            assistant.Next = new ContainerAssistantResult(ContainerAssistantOutcome.ExistingContainer);
            await vm.CreateCommand.ExecuteAsync(null);
            dialogs.LastDrives.Should().Equal(root);
            HiddenLibrariesViewModel.PreferDrive([@"D:\", @"x:\"], @"X:\").Should().Equal(@"X:\", @"D:\");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Nothing_about_the_container_reaches_the_log()
    {
        // Wie die Protokoll-Tests aus ADR-0049: echter Serilog-Logger mit Senke; der ganze Ablauf über den echten
        // ContainerCreationService (nur die Windows-Seite gefälscht) darf nichts protokollieren — kein Pfad, keine
        // Bezeichnung, keine winget-Ausgabe, kein Kennwort.
        var sink = new CollectingSink();
        using var serilog = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog);
        var root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", "assistant-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var devices = new LetterDevices();
            var elevated = new MountingElevated(devices, 'T');
            var outputs = new SecretOutputs();
            var creation = new ContainerCreationService(
                new FakeContainers(),
                new NoVeraCrypt(),
                devices,
                new OneDrive(Path.GetPathRoot(root)!),
                elevated,
                new NullLauncher(),
                new BitLockerPlatform(outputs, () => "Professional", _ => true),
                new WingetVeraCryptInstaller(outputs, () => @"C:\winget.exe"),
                systemRoot: @"Q:\",
                cloudFolders: () => [],
                delay: (_, _) => Task.CompletedTask);
            var vm = new ContainerAssistantViewModel(creation, new ScriptedPicker { Vhdx = Path.Combine(root, "Geheimordner", "tarnname.vhdx") });

            vm.IsBitLockerChoice = true;
            await vm.NextAsync();
            vm.BrowseVhdxCommand.Execute(null);
            vm.VolumeLabel = "Tarnlabel";
            vm.SizeText = "1";
            vm.SelectedLetter = "T:";
            await vm.CreateVhdxAsync();
            vm.Error.Should().BeNull();
            vm.OpenBitLockerWizardCommand.Execute(null);
            await vm.CheckEncryptionAsync();
            vm.Step.Should().Be(ContainerAssistantStep.Ready);
            await vm.FinishAsync();

            var vera = new ContainerAssistantViewModel(creation, new ScriptedPicker());
            vera.IsVeraCryptChoice = true;
            await vera.NextAsync();
            await vera.InstallVeraCryptAsync();

            // Übergabe an „Versteckte Bibliothek anlegen“ mit Lumora-Kennwort — auch das landet nicht im Protokoll
            var manager = new CatalogSessionManager(
                new HiddenModeOptions(true, Path.Combine(root, "appdata"), Path.Combine(root, "appdata"), int.MaxValue, UseUiHooks: false),
                new HiddenLibraryStore(iterations: 1_000),
                new FixedDriveRoots(root),
                new FakeContainers(),
                TimeProvider.System,
                factory.CreateLogger("Lumora.HiddenLibraries"));
            manager.AttachSwitcher(new FakeHostSwitcher());
            var dialogs = new ScriptedHiddenDialogs { CreateAnswer = new HiddenLibraryCreateAnswer(Path.Combine(root, "Geheimordner"), "Tresorname", "geheim-123") };
            var hidden = new HiddenLibrariesViewModel(manager, dialogs, new ScriptedDialogs(), factory.CreateLogger<HiddenLibrariesViewModel>(),
                new ScriptedAssistant { Next = vm.Result! });
            await hidden.CreateCommand.ExecuteAsync(null);
            manager.IsHiddenSessionActive.Should().BeTrue();
            dialogs.LastDrives![0].Should().Be(@"T:\");

            var text = string.Join("\n", sink.Events.Select(e => e.RenderMessage() + e.Exception));
            text.Should().NotContain("Geheimordner").And.NotContain("tarnname").And.NotContain("Tarnlabel")
                .And.NotContain("geheim-123").And.NotContain("Tresorname").And.NotContain("Kennwort");
            sink.Events.Should().BeEmpty("der Container-Assistent protokolliert nichts (schon „angelegt“ wäre eine Spur)");
            elevated.Script.Should().Contain("tarnname.vhdx").And.NotContain("geheim-123");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public Task Window_shows_the_pages_and_closes_on_cancel() => TestUiThread.Dispatcher.InvokeAsync(async () =>
    {
        var vm = new ContainerAssistantViewModel(new RecordingCreation(), new ScriptedPicker());
        var window = new ContainerAssistantWindow(vm);
        window.Show();
        try
        {
            window.PrimaryButton.Content.Should().Be("Weiter");
            vm.IsBitLockerChoice = true;
            await vm.NextAsync();
            window.UpdateLayout();
            window.PrimaryButton.Content.Should().Be("Container erstellen");
            window.ActualHeight.Should().BeLessThan(900);

            vm.Cancel();
            window.IsClosing.Should().BeTrue();
            window.IsVisible.Should().BeFalse();
            vm.Result!.Outcome.Should().Be(ContainerAssistantOutcome.Cancelled);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }).Task.Unwrap().WaitAsync(Timeout);

    [Fact]
    public Task Auswahl_im_dunklen_Design_lesbar() => TestUiThread.Dispatcher.InvokeAsync(() =>
    {
        // 0.4.25: der eigene Stil ohne BasedOn ergab den WPF-Standard-Knopf mit schwarzer Schrift auf dunklem Grund.
        var vm = new ContainerAssistantViewModel(new RecordingCreation(), new ScriptedPicker());
        var window = new ContainerAssistantWindow(vm);
        window.Show();
        try
        {
            window.UpdateLayout();
            var style = (System.Windows.Style)window.FindResource("ChoiceRadio");
            style.BasedOn.Should().NotBeNull("der Stil baut auf dem Lumora/WPF-UI-Knopf auf");
            var radio = window.FindName("ExistingRadio").Should().BeOfType<System.Windows.Controls.RadioButton>().Subject;
            var brush = radio.Foreground.Should().BeOfType<System.Windows.Media.SolidColorBrush>().Subject;
            var c = brush.Color;
            (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B).Should().BeGreaterThan(160, "helle Schrift im dunklen Design");
        }
        finally
        {
            window.Close();
        }
    }).Task.WaitAsync(Timeout);

    [Fact]
    public Task Closing_the_window_with_x_cancels() => TestUiThread.Dispatcher.InvokeAsync(() =>
    {
        var vm = new ContainerAssistantViewModel(new RecordingCreation(), new ScriptedPicker());
        var window = new ContainerAssistantWindow(vm);
        window.Show();
        window.Close();
        vm.Result.Should().Be(ContainerAssistantResult.Cancelled());
    }).Task.WaitAsync(Timeout);

    private static async Task ReachAsync(ContainerAssistantViewModel vm, ContainerAssistantStep page)
    {
        switch (page)
        {
            case ContainerAssistantStep.Choice:
                return;
            case ContainerAssistantStep.BitLockerSetup:
            case ContainerAssistantStep.BitLockerEncrypt:
            case ContainerAssistantStep.Ready:
                vm.IsBitLockerChoice = true;
                await vm.NextAsync();
                if (page == ContainerAssistantStep.BitLockerSetup)
                {
                    return;
                }

                await vm.CreateVhdxAsync();
                if (page == ContainerAssistantStep.Ready)
                {
                    await vm.CheckEncryptionAsync();
                }

                return;
            default:
                vm.IsVeraCryptChoice = true;
                await vm.NextAsync();
                if (page == ContainerAssistantStep.VeraCryptMount)
                {
                    await vm.GoToMountAsync();
                }

                return;
        }
    }

    /// <summary>Merkt jeden Schritt mit Wirkung nach außen (Anlegen, Starten, Installieren, Einhängen).</summary>
    private sealed class RecordingCreation : IContainerCreation
    {
        public List<string> SideEffects { get; } = new();

        public string? BitLockerReason { get; set; }

        public bool VeraCryptInstalled { get; set; } = true;

        public bool WingetAvailable { get; set; } = true;

        public BitLockerProtection Protection { get; set; } = BitLockerProtection.On;

        public ContainerCommandResult CreateResult { get; set; } = new(true, "Der Container ist angelegt und als X: eingehängt.");

        public Task<ContainerCommandResult>? CreateGate { get; set; }

        public ContainerCommandResult InstallResult { get; set; } = new(true, "VeraCrypt ist installiert.");

        public string? BitLockerUnavailableReason => BitLockerReason;

        public bool IsVeraCryptInstalled => VeraCryptInstalled;

        public bool IsWingetAvailable => WingetAvailable;

        public IReadOnlyList<char> GetFreeDriveLetters() => ['M', 'X'];

        public VhdxSuggestion SuggestVhdx() => new(@"D:\Daten\sicherung.vhdx", 200, 'X', "Daten");

        public long? GetFreeBytes(string path) => 400L * 1024 * 1024 * 1024;

        public string? ValidateVhdx(VhdxPlan plan) => null;

        public string? SystemDriveHint(string? path) => null;

        public string? ValidateContainerFile(string containerFile) => null;

        public async Task<ContainerCommandResult> CreateVhdxAsync(VhdxPlan plan, CancellationToken ct = default)
        {
            SideEffects.Add($"vhdx:{plan.FilePath}|{plan.SizeGigabytes}|{plan.DriveLetter}|{plan.VolumeLabel}");
            return CreateGate is null ? CreateResult : await CreateGate;
        }

        public ContainerCommandResult OpenBitLockerWizard(char driveLetter)
        {
            SideEffects.Add("bitlocker-wizard:" + driveLetter);
            return new ContainerCommandResult(true, "Der BitLocker-Assistent von Windows ist geöffnet.");
        }

        public Task<BitLockerProtection> GetBitLockerProtectionAsync(char driveLetter, CancellationToken ct = default) => Task.FromResult(Protection);

        public Task<ContainerCommandResult> InstallVeraCryptAsync(IProgress<string>? progress, CancellationToken ct = default)
        {
            SideEffects.Add("winget");
            if (InstallResult.Success)
            {
                VeraCryptInstalled = true;
            }

            return Task.FromResult(InstallResult);
        }

        public ContainerCommandResult OpenVeraCryptDownloadPage()
        {
            SideEffects.Add("download-page");
            return new ContainerCommandResult(true, "ok");
        }

        public ContainerCommandResult StartVeraCryptFormat()
        {
            SideEffects.Add("veracrypt-format");
            return new ContainerCommandResult(true, "Der VeraCrypt-Assistent ist geöffnet.");
        }

        public Task<ContainerCommandResult> MountVeraCryptAsync(string containerFile, char driveLetter, CancellationToken ct = default)
        {
            SideEffects.Add($"mount:{containerFile}|{driveLetter}");
            return Task.FromResult(new ContainerCommandResult(true, "ok"));
        }
    }

    private sealed class ScriptedPicker : IContainerFilePicker
    {
        public string? Vhdx { get; set; }

        public string? Container { get; set; }

        public string? PickNewVhdx(string suggestedPath) => Vhdx;

        public string? PickContainerFile() => Container;
    }

    private sealed class ScriptedAssistant : IContainerAssistant
    {
        public ContainerAssistantResult Next { get; set; } = ContainerAssistantResult.Cancelled();

        public int Runs { get; private set; }

        public ContainerAssistantResult Run()
        {
            Runs++;
            return Next;
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            lock (Events)
            {
                Events.Add(logEvent);
            }
        }
    }

    private sealed class LetterDevices : IVolumeDeviceQuery
    {
        public HashSet<char> Present { get; } = new();

        public string? GetDeviceName(char driveLetter) => Present.Contains(char.ToUpperInvariant(driveLetter)) ? @"\Device\HarddiskVolume42" : null;
    }

    /// <summary>„erhöhte PowerShell“: liest den Befehl, legt die Datei an und lässt das Laufwerk erscheinen — nichts davon echt.</summary>
    private sealed class MountingElevated(LetterDevices devices, char letter) : IElevatedRunner
    {
        public string Script { get; private set; } = string.Empty;

        public Task<ElevatedRunResult> RunElevatedAsync(string fileName, string argumentLine, TimeSpan timeout, CancellationToken ct)
        {
            Script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(argumentLine[(argumentLine.LastIndexOf(' ') + 1)..]));
            var file = Script.Split('"')[1];
            File.WriteAllText(file, "vhdx");
            devices.Present.Add(letter);
            return Task.FromResult(new ElevatedRunResult(false, 0, false));
        }
    }

    /// <summary>Ausgaben mit einem „Kennwort“ darin — sie dürfen nie im Protokoll landen.</summary>
    private sealed class SecretOutputs : IProcessOutputRunner
    {
        public Task<ProcessOutputResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, IProgress<string>? lines, CancellationToken ct)
        {
            lines?.Report("Kennwort geheim-123 (nur Test)");
            return Task.FromResult(fileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
                ? new ProcessOutputResult(0, false, "1")
                : new ProcessOutputResult(unchecked((int)0x8A150107), false, "Kennwort geheim-123\n"));
        }
    }

    private sealed class NoVeraCrypt : IVeraCryptLocator
    {
        public string? FindExecutable() => null;
    }

    private sealed class OneDrive(string root) : IDriveSpaceSource
    {
        public IReadOnlyList<DriveSpace> GetFixedDrives() => [new DriveSpace(root, 100L * 1024 * 1024 * 1024)];
    }

    private sealed class NullLauncher : IShellLauncher
    {
        public ShellLaunchResult Start(string fileName, string? argumentLine) => ShellLaunchResult.Started;

        public bool OpenUrl(string url) => true;
    }
}
