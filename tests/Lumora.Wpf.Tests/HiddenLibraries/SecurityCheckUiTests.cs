using System.IO;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.SecurityCheck;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;
using Lumora.Shell;
using Lumora.Shell.HiddenLibraries;
using Lumora.Shell.HiddenLibraries.SecurityCheck;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.HiddenLibraries;

/// <summary>
/// ADR-0064: Sicherheits-Check in der Oberfläche — Reiter nur in der geöffneten versteckten Sitzung (normal/versteckt/nach
/// dem Zusperren), Option „Lumora nach dem Zusperren ganz beenden“ (von Hand und nach Untätigkeit), Wertung und Anzeige im
/// ViewModel, Statusleiste nur mit Ergebnis der versteckten Sitzung, Registrierung nur im versteckten Container.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SecurityCheckUiTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", "security-" + Guid.NewGuid().ToString("N"));
    private readonly FakeContainers _containers = new();
    private readonly FakeHostSwitcher _switcher = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly MemorySettings _settings = new();
    private readonly FakeLauncher _launcher = new();

    public SecurityCheckUiTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        HiddenSessionPrivacy.IsHiddenSessionActive = false;
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
        }

        Directory.Delete(_root, recursive: true);
    }

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private CatalogSessionManager CreateManager(bool enabled = true)
    {
        var manager = new CatalogSessionManager(
            new HiddenModeOptions(enabled, Path.Combine(_root, "appdata"), Path.Combine(_root, "appdata"), int.MaxValue, UseUiHooks: false),
            new HiddenLibraryStore(iterations: 1_000),
            new FixedDriveRoots(_root),
            _containers,
            _time,
            NullLogger.Instance);
        manager.AttachSwitcher(_switcher);
        return manager;
    }

    private string NewContainerFolder() => Path.Combine(_root, "container", "Tresor" + Guid.NewGuid().ToString("N")[..6]);

    private SecurityCheckService CreateService(params (SecurityCheckId Id, SecurityCheckStatus Status)[] statuses)
    {
        var checks = Enum.GetValues<SecurityCheckId>()
            .Select(id => new StubCheck(id, statuses.FirstOrDefault(s => s.Id == id) is { Id: not 0 } s ? s.Status : SecurityCheckStatus.NotFulfilled))
            .ToList();
        return new SecurityCheckService(checks, _settings, () => 'X', NullLogger<SecurityCheckService>.Instance, new WeakReferenceMessenger());
    }

    private SecurityCheckViewModel CreateViewModel(ICatalogSessionManager manager, SecurityCheckService? service = null)
        => new(service ?? CreateService(), _launcher, manager, NullLogger<SecurityCheckViewModel>.Instance);

    // ----- Reiter nur in der versteckten Sitzung -----

    [Fact]
    public Task Tab_exists_only_in_an_open_hidden_session_right_after_the_hidden_libraries_tab() => OnUiAsync(async () =>
    {
        TabControl NewTabs(bool withHidden)
        {
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Tag = NavigationTags.SettingsStorage });
            if (withHidden)
            {
                tabs.Items.Add(new TabItem { Tag = NavigationTags.SettingsHiddenLibraries });
            }

            tabs.Items.Add(new TabItem { Tag = NavigationTags.SettingsInfo });
            return tabs;
        }

        var off = NewTabs(false);
        SecurityCheckUi.AddSettingsTab(off, CreateViewModel(CreateManager(enabled: false)));
        SecurityCheckUi.AddSettingsTab(off, null);
        off.Items.Count.Should().Be(2, "ohne _hidden keinerlei Spur");

        var manager = CreateManager();
        var normal = NewTabs(false);
        SecurityCheckUi.AddSettingsTab(normal, CreateViewModel(manager));
        normal.Items.Count.Should().Be(2, "mit _hidden in der normalen Bibliothek kein Reiter");

        await manager.CreateAsync(NewContainerFolder(), "Tresor", "kennwort-1");
        var hidden = NewTabs(true);
        var vm = CreateViewModel(manager);
        SecurityCheckUi.AddSettingsTab(hidden, vm);
        hidden.Items.Count.Should().Be(4);
        var tab = (TabItem)hidden.Items[2];
        tab.Tag.Should().Be(NavigationTags.SettingsSecurityCheck);
        tab.Header.Should().Be("Sicherheits-Check");
        ((System.Windows.FrameworkElement)tab.Content).DataContext.Should().BeSameAs(vm);
        NavigationTags.IsSettingsSection(NavigationTags.SettingsSecurityCheck).Should().BeTrue();
        NavigationTags.SettingsSections.Should().NotContain(NavigationTags.SettingsSecurityCheck);

        await manager.LockAsync();
        var afterLock = NewTabs(false);
        SecurityCheckUi.AddSettingsTab(afterLock, CreateViewModel(manager));
        afterLock.Items.Count.Should().Be(2, "nach dem Zusperren keinerlei Spur");
    });

    [Fact]
    public void Security_check_is_registered_only_in_the_hidden_container()
    {
        var manager = CreateManager();
        var paths = new AppDataPaths(Path.Combine(_root, "katalog"));
        var normal = new ServiceCollection();
        normal.AddCatalogSession(paths, new CatalogSessionSetup(manager, IsHidden: false, _root));
        normal.Should().NotContain(d => d.ServiceType == typeof(ISecurityCheckService) || d.ServiceType == typeof(ISecurityCheckSettingsStore)
                                        || d.ServiceType == typeof(SecurityCheckViewModel) || d.ImplementationType == typeof(SecurityCheckStartup),
            "im normalen Modus keine Einstellungen, keine Prüfung, kein Reiter");

        var hidden = new ServiceCollection();
        hidden.AddCatalogSession(paths, new CatalogSessionSetup(manager, IsHidden: true, _root));
        hidden.Should().Contain(d => d.ServiceType == typeof(ISecurityCheckService));
        hidden.Should().Contain(d => d.ServiceType == typeof(ISecurityCheckSettingsStore));
        hidden.Should().Contain(d => d.ServiceType == typeof(SecurityCheckViewModel));
        hidden.Count(d => d.ServiceType == typeof(ISecurityCheck)).Should().Be(10);
    }

    // ----- Lumora nach dem Zusperren ganz beenden -----

    [Fact]
    public async Task Exit_after_manual_lock_shuts_down_instead_of_opening_the_normal_library()
    {
        _settings.Value = new SecurityCheckSettings(ThreatLevel.SharedUser, ExitAfterLock: true);
        _switcher.HiddenSecuritySettings = _settings;
        var manager = CreateManager();
        var folder = NewContainerFolder();
        await manager.CreateAsync(folder, "Tresor", "kennwort-1");

        (await manager.LockAsync()).Success.Should().BeTrue();

        _switcher.ShutdownCalls.Should().Be(1);
        _switcher.Calls.Should().NotContain("normal", "keine normale Oberfläche dazwischen");
        _switcher.CloseWindowsCalls.Should().Be(1, "Lightbox und Dialoge zuerst geschlossen");
        manager.IsHiddenModeEnabled.Should().BeFalse("nichts vom versteckten Zustand bleibt sichtbar");
        _containers.Dismounted.Should().BeEmpty("das Aushängen übernimmt das Beenden");
        (await manager.LockAsync()).Success.Should().BeFalse("kein weiterer Wechsel bis zum Ende");

        // App.OnExit: Sitzung schließen, danach aushängen (Einstellung „Aushängen“ an)
        manager.PrepareExit();
        manager.IsHiddenSessionActive.Should().BeFalse();
        HiddenSessionPrivacy.IsHiddenSessionActive.Should().BeFalse();
        manager.DismountAfterExit(TimeSpan.FromSeconds(10))!.Success.Should().BeTrue();
        _containers.Dismounted.Should().Equal(Path.GetPathRoot(folder));
    }

    [Fact]
    public async Task Exit_after_idle_lock_shuts_down_too()
    {
        _settings.Value = new SecurityCheckSettings(ThreatLevel.Forensic, ExitAfterLock: true);
        _switcher.HiddenSecuritySettings = _settings;
        var manager = CreateManager();
        await manager.CreateAsync(NewContainerFolder(), "Tresor", "kennwort-1");

        _time.Advance(TimeSpan.FromMinutes(11));
        (await manager.CheckIdleAsync()).Should().BeTrue();
        _switcher.ShutdownCalls.Should().Be(1);
        _switcher.Calls.Should().NotContain("normal");
    }

    [Fact]
    public async Task A_just_toggled_option_counts_even_while_saving_is_still_running()
    {
        var slow = new SlowSettings();
        var service = new SecurityCheckService([new ExitAfterLockCheck()], slow, () => 'X', NullLogger<SecurityCheckService>.Instance, new WeakReferenceMessenger());
        await service.RunAsync();
        _switcher.HiddenSecuritySettings = slow; // gespeichert ist noch „aus“
        _switcher.HiddenSecurityService = service;
        var manager = CreateManager();
        await manager.CreateAsync(NewContainerFolder(), "Tresor", "kennwort-1");

        var saving = service.SetExitAfterLockAsync(true); // Speichern hängt noch
        service.Settings.ExitAfterLock.Should().BeTrue("sofort im Speicher");
        (await manager.LockAsync()).Success.Should().BeTrue();
        _switcher.ShutdownCalls.Should().Be(1, "der eben umgelegte Schalter gilt");
        slow.Release.SetResult();
        await saving;
    }

    [Fact]
    public async Task Without_the_option_or_with_unreadable_settings_lock_returns_to_the_normal_library()
    {
        _switcher.HiddenSecuritySettings = _settings; // Standard: aus
        var manager = CreateManager();
        await manager.CreateAsync(NewContainerFolder(), "Tresor", "kennwort-1");
        await manager.LockAsync();
        _switcher.ShutdownCalls.Should().Be(0);
        _switcher.Calls.Last().Should().Be("normal");

        _settings.ThrowOnLoad = true;
        _settings.Value = new SecurityCheckSettings(ThreatLevel.SharedUser, true);
        manager = CreateManager(); // Neustart mit „_hidden“
        await manager.CreateAsync(NewContainerFolder(), "Tresor2", "kennwort-1");
        await manager.LockAsync();
        _switcher.ShutdownCalls.Should().Be(0, "nicht lesbar = normal zusperren");
        _switcher.Calls.Last().Should().Be("normal");
    }

    // ----- ViewModel -----

    [Fact]
    public async Task View_model_shows_score_tags_dots_and_hides_fulfilled()
    {
        var manager = CreateManager();
        var service = CreateService(
            (SecurityCheckId.ActivityHistory, SecurityCheckStatus.Fulfilled),
            (SecurityCheckId.SearchIndex, SecurityCheckStatus.Fulfilled),
            (SecurityCheckId.VeraCryptHistory, SecurityCheckStatus.NotApplicable),
            (SecurityCheckId.Hibernation, SecurityCheckStatus.Unknown));
        var vm = CreateViewModel(manager, service);
        await vm.LoadAsync();

        vm.Rows.Should().HaveCount(10);
        vm.Level.Should().Be(ThreatLevel.SharedUser, "Standard Mitbenutzer");
        (vm.FulfilledCount, vm.CountedTotal).Should().Be((2, 8), "VeraCrypt nicht zutreffend, Lumora-Option grau");
        vm.ScoreText.Should().Be("2/8");
        vm.Levels.Single(l => l.IsSelected).Level.Should().Be(ThreatLevel.SharedUser);

        Row(vm, SecurityCheckId.BitLocker).Should().Match<SecurityCheckRowViewModel>(r => r.Dot == SecurityCheckDot.Critical && r.Tag == "wichtigster Punkt" && r.TagKind == "Critical");
        Row(vm, SecurityCheckId.Thumbnails).Should().Match<SecurityCheckRowViewModel>(r => r.Dot == SecurityCheckDot.Warning && r.Tag == "empfohlen aus");
        Row(vm, SecurityCheckId.PageFile).Tag.Should().Be("empfohlen an");
        Row(vm, SecurityCheckId.ActivityHistory).Should().Match<SecurityCheckRowViewModel>(r => r.Dot == SecurityCheckDot.Ok && r.Tag == null);
        Row(vm, SecurityCheckId.Hibernation).Dot.Should().Be(SecurityCheckDot.Unknown);
        Row(vm, SecurityCheckId.VeraCryptHistory).Dot.Should().Be(SecurityCheckDot.Grey);
        Row(vm, SecurityCheckId.ExitAfterLock).Should().Match<SecurityCheckRowViewModel>(r => r.IsToggle && r.Tag == "Lumora-Einstellung" && r.Dot == SecurityCheckDot.Grey && !r.HasHowto);
        Row(vm, SecurityCheckId.SearchIndex).Title.Should().Be("Windows-Suche auf Laufwerk X:");
        Row(vm, SecurityCheckId.VeraCryptHistory).OpenLabel.Should().Be("VeraCrypt öffnen");

        vm.HideFulfilled = true;
        vm.Rows.Where(r => !r.IsVisible).Select(r => r.Id).Should().BeEquivalentTo([SecurityCheckId.ActivityHistory, SecurityCheckId.SearchIndex]);
        vm.HideFulfilled = false;
        vm.Rows.Should().OnlyContain(r => r.IsVisible);
    }

    [Fact]
    public async Task View_model_level_and_exit_option_are_saved_and_rerated()
    {
        var manager = CreateManager();
        var vm = CreateViewModel(manager);
        await vm.RefreshAsync();

        vm.Levels.Single(l => l.Level == ThreatLevel.PoweredOff).IsSelected = true;
        await WaitAsync(() => _settings.Value.Level == ThreatLevel.PoweredOff && vm.CountedTotal == 1);
        vm.LevelNote.Should().Contain("genügt BitLocker");
        Row(vm, SecurityCheckId.Thumbnails).Should().Match<SecurityCheckRowViewModel>(r => r.Dot == SecurityCheckDot.Grey && r.Tag == "Zusatz");

        vm.Level = ThreatLevel.Forensic;
        await WaitAsync(() => _settings.Value.Level == ThreatLevel.Forensic && vm.CountedTotal == 10);
        Row(vm, SecurityCheckId.ExitAfterLock).Dot.Should().Be(SecurityCheckDot.Warning, "bei fachmännischer Untersuchung zählt die Lumora-Option");

        vm.ExitAfterLock = true;
        await WaitAsync(() => _settings.Value.ExitAfterLock && vm.FulfilledCount == 1);
        Row(vm, SecurityCheckId.ExitAfterLock).Dot.Should().Be(SecurityCheckDot.Ok);
        vm.Levels.Single(l => l.IsSelected).Level.Should().Be(ThreatLevel.Forensic);
    }

    [Fact]
    public async Task Open_starts_only_the_launcher_and_shows_the_howto_when_missing()
    {
        var vm = CreateViewModel(CreateManager());
        await vm.RefreshAsync();

        Row(vm, SecurityCheckId.Thumbnails).OpenCommand.Execute(null);
        _launcher.Opened.Should().Equal(SettingsTarget.FolderOptions);
        vm.Message.Should().BeNull();

        var pageFile = Row(vm, SecurityCheckId.PageFile);
        pageFile.CanOpen.Should().BeFalse("secpol.msc fehlt (Fake) — Knopf aus");
        pageFile.OpenCommand.Execute(null);
        pageFile.IsHowtoOpen.Should().BeTrue("dann gilt die Anleitung");
        vm.Message.Should().Contain("Anleitung");

        var bitLocker = Row(vm, SecurityCheckId.BitLocker);
        bitLocker.IsHowtoOpen.Should().BeFalse();
        bitLocker.ToggleHowtoCommand.Execute(null);
        bitLocker.IsHowtoOpen.Should().BeTrue();
        bitLocker.Howto.Should().Contain("BitLocker aktivieren");

        Row(vm, SecurityCheckId.ExitAfterLock).OpenCommand.Execute(null);
        _launcher.Opened.Should().Equal([SettingsTarget.FolderOptions, SettingsTarget.SecurityPolicy], "die Lumora-Option startet nichts");
    }

    // ----- Statusleiste -----

    [Fact]
    public async Task Status_bar_shows_the_score_only_with_a_result_of_the_hidden_session()
    {
        var messenger = new WeakReferenceMessenger();
        var normal = NewStatusBar(messenger, null);
        try
        {
            normal.SecurityCheckText.Should().BeNull("normale Sitzung: nichts");
            messenger.Send(new SecurityCheckSummaryMessage("Sicherheits-Check: 4 von 9 erfüllt"));
            // UiThread.Post: mit laufender Test-Anwendung kommt der Text über deren Dispatcher an
            await WaitAsync(() => normal.SecurityCheckText == "Sicherheits-Check: 4 von 9 erfüllt");
        }
        finally
        {
            normal.Dispose();
        }

        var service = CreateService((SecurityCheckId.BitLocker, SecurityCheckStatus.Fulfilled));
        await service.RunAsync();
        var hidden = NewStatusBar(new WeakReferenceMessenger(), service);
        try
        {
            hidden.SecurityCheckText.Should().Be("Sicherheits-Check: 1 von 9 erfüllt", "schon vorliegendes Ergebnis sofort");
        }
        finally
        {
            hidden.Dispose();
        }
    }

    private static StatusBarViewModel NewStatusBar(IMessenger messenger, ISecurityCheckService? service)
        => new(
            NullService.Create<IThumbnailService>(),
            NullService.Create<ILibraryService>(),
            NullService.Create<IFileOperationService>(),
            new ScanGate(messenger),
            NullLogger<StatusBarViewModel>.Instance,
            messenger,
            securityCheck: service);

    private static SecurityCheckRowViewModel Row(SecurityCheckViewModel vm, SecurityCheckId id) => vm.Rows.Single(r => r.Id == id);

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Bedingung nicht erreicht");
            }

            await Task.Delay(10);
        }
    }

    private sealed class StubCheck(SecurityCheckId id, SecurityCheckStatus status) : ISecurityCheck
    {
        public SecurityCheckId Id => id;

        public SecurityCheckFinding Run(SecurityCheckContext context)
            => id == SecurityCheckId.ExitAfterLock ? new ExitAfterLockCheck().Run(context) : new SecurityCheckFinding(id, status, "Satz");
    }

    private sealed class MemorySettings : ISecurityCheckSettingsStore
    {
        public SecurityCheckSettings Value { get; set; } = SecurityCheckSettings.Default;

        public bool ThrowOnLoad { get; set; }

        public Task<SecurityCheckSettings> LoadAsync(CancellationToken ct = default)
            => ThrowOnLoad ? throw new IOException("gesperrt") : Task.FromResult(Value);

        public Task SaveAsync(SecurityCheckSettings settings, CancellationToken ct = default)
        {
            Value = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>Speichern wartet, bis der Test es freigibt; Lesen liefert den gespeicherten Stand.</summary>
    private sealed class SlowSettings : ISecurityCheckSettingsStore
    {
        private SecurityCheckSettings _value = SecurityCheckSettings.Default;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SecurityCheckSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(_value);

        public async Task SaveAsync(SecurityCheckSettings settings, CancellationToken ct = default)
        {
            await Release.Task;
            _value = settings;
        }
    }

    private sealed class FakeLauncher : ISecuritySettingsLauncher
    {
        public List<SettingsTarget> Opened { get; } = new();

        public bool IsAvailable(SettingsTarget target) => target is not (SettingsTarget.None or SettingsTarget.SecurityPolicy);

        public bool Open(SettingsTarget target)
        {
            Opened.Add(target);
            return IsAvailable(target);
        }
    }
}
