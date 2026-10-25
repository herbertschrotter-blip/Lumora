using System.IO;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.HiddenLibraries;
using Lumora.Shell;
using Lumora.Shell.HiddenLibraries;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.HiddenLibraries;

/// <summary>
/// ADR-0049: Oberfläche der versteckten Bibliotheken — nur mit Startschalter sichtbar (Reiter, Umschalter-Einträge),
/// Öffnen/Anlegen über die Dialoge, Meldungen bei Fehlern, Dialog prüft Kennwort und Eingaben.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class HiddenLibrariesUiTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", "hidden-ui-" + Guid.NewGuid().ToString("N"));
    private readonly FakeContainers _containers = new();
    private readonly FakeHostSwitcher _switcher = new();

    public HiddenLibrariesUiTests() => Directory.CreateDirectory(_root);

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

    private CatalogSessionManager CreateManager(bool enabled)
    {
        var manager = new CatalogSessionManager(
            new HiddenModeOptions(enabled, Path.Combine(_root, "appdata"), Path.Combine(_root, "appdata"), int.MaxValue, UseUiHooks: false),
            new HiddenLibraryStore(iterations: 1_000),
            new FixedDriveRoots(_root),
            _containers,
            TimeProvider.System,
            NullLogger.Instance);
        manager.AttachSwitcher(_switcher);
        return manager;
    }

    private HiddenLibrariesViewModel CreateViewModel(CatalogSessionManager manager, ScriptedHiddenDialogs dialogs, ScriptedDialogs messages)
        => new(manager, dialogs, messages, NullLogger<HiddenLibrariesViewModel>.Instance);

    [Fact]
    public void Settings_tab_tag_is_a_section_but_not_in_the_fixed_list()
    {
        NavigationTags.IsSettingsSection(NavigationTags.SettingsHiddenLibraries).Should().BeTrue();
        NavigationTags.SettingsSections.Should().NotContain(NavigationTags.SettingsHiddenLibraries);
        new SettingsSectionState { SelectedSection = NavigationTags.SettingsHiddenLibraries }.SelectedSection.Should().Be("settings-hidden");
    }

    [Fact]
    public Task Settings_tab_exists_only_in_an_open_hidden_library_and_sits_before_info() => OnUiAsync(async () =>
    {
        TabControl NewTabs()
        {
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Tag = NavigationTags.SettingsStorage });
            tabs.Items.Add(new TabItem { Tag = NavigationTags.SettingsInfo });
            return tabs;
        }

        var off = NewTabs();
        HiddenLibrariesUi.AddSettingsTab(off, CreateViewModel(CreateManager(enabled: false), new(), new()));
        HiddenLibrariesUi.AddSettingsTab(off, null);
        off.Items.Count.Should().Be(2, "ohne _hidden keinerlei Spur");

        // Mit _hidden, aber in der normalen Bibliothek: auch kein Reiter (0.4.14, Herbert) — Anlegen/Öffnen über den Umschalter
        var manager = CreateManager(enabled: true);
        var normal = NewTabs();
        HiddenLibrariesUi.AddSettingsTab(normal, CreateViewModel(manager, new(), new()));
        normal.Items.Count.Should().Be(2, "in der normalen Bibliothek nie ein Reiter");

        await manager.CreateAsync(Path.Combine(_root, "Tresor"), "Tresor", "kennwort-1");
        var on = NewTabs();
        var vm = CreateViewModel(manager, new(), new());
        HiddenLibrariesUi.AddSettingsTab(on, vm);
        on.Items.Count.Should().Be(3);
        var tab = (TabItem)on.Items[1];
        tab.Tag.Should().Be(NavigationTags.SettingsHiddenLibraries);
        tab.Header.Should().Be("Versteckte Bibliotheken");
        ((System.Windows.FrameworkElement)tab.Content).DataContext.Should().BeSameAs(vm);

        await manager.LockAsync();
        var afterLock = NewTabs();
        HiddenLibrariesUi.AddSettingsTab(afterLock, CreateViewModel(manager, new(), new()));
        afterLock.Items.Count.Should().Be(2, "nach dem Zusperren keinerlei Spur");
    });

    [Fact]
    public async Task Switcher_shows_hidden_entries_only_with_start_switch()
    {
        var libraries = new InMemoryLibraries();
        var plain = new LibrarySwitcherViewModel(libraries, new RecordingNavigationService(), new ScriptedDialogs(), NullLogger<LibrarySwitcherViewModel>.Instance, new WeakReferenceMessenger(),
            hidden: CreateViewModel(CreateManager(enabled: false), new(), new()));
        await plain.RefreshAsync();
        plain.HasChoices.Should().BeFalse("ohne Bibliothek und ohne _hidden führt der Klick zur Verwaltung");

        var manager = CreateManager(enabled: true);
        var withSwitch = new LibrarySwitcherViewModel(libraries, new RecordingNavigationService(), new ScriptedDialogs(), NullLogger<LibrarySwitcherViewModel>.Instance, new WeakReferenceMessenger(),
            hidden: CreateViewModel(manager, new(), new()));
        await withSwitch.RefreshAsync();
        withSwitch.HasChoices.Should().BeTrue("Menü mit „Versteckte Bibliothek anlegen…“");
        withSwitch.CaptionText.Should().Be("Bibliothek einrichten");

        await manager.CreateAsync(Path.Combine(_root, "Tresor"), "Tresor", "kennwort-1");
        var inSession = new LibrarySwitcherViewModel(libraries, new RecordingNavigationService(), new ScriptedDialogs(), NullLogger<LibrarySwitcherViewModel>.Instance, new WeakReferenceMessenger(),
            hidden: CreateViewModel(manager, new(), new()));
        inSession.CaptionText.Should().Be("Versteckte Bibliothek");
        inSession.Hidden!.IsNormalSession.Should().BeFalse();
        inSession.Hidden.ActiveLibraryName.Should().Be("Tresor");
    }

    [Fact]
    public async Task View_model_opens_via_password_dialog_and_reports_wrong_password()
    {
        var manager = CreateManager(enabled: true);
        var folder = Path.Combine(_root, "Tresor");
        Directory.CreateDirectory(folder);
        new HiddenLibraryStore(iterations: 1_000).Create(folder, "Tresor", "kennwort-1");
        var dialogs = new ScriptedHiddenDialogs { UnlockAnswer = new HiddenLibraryUnlockAnswer("falsch-123", IsReset: false) };
        var messages = new ScriptedDialogs();
        var vm = CreateViewModel(manager, dialogs, messages);

        await vm.RefreshAsync();
        vm.Found.Select(f => f.Name).Should().Equal("Tresor");
        await vm.OpenCommand.ExecuteAsync(vm.Found[0]);
        dialogs.Asked.Should().Equal("unlock:Tresor");
        dialogs.LastCanReset.Should().BeFalse();
        messages.Errors.Should().ContainSingle(e => e.Contains("falsch"));
        manager.IsHiddenSessionActive.Should().BeFalse();

        dialogs.UnlockAnswer = new HiddenLibraryUnlockAnswer("kennwort-1", IsReset: false);
        await vm.OpenCommand.ExecuteAsync(vm.Found[0]);
        manager.IsHiddenSessionActive.Should().BeTrue();

        await vm.LockCommand.ExecuteAsync(null);
        manager.IsHiddenSessionActive.Should().BeFalse();

        // Nach dem Zusperren zeigt der Umschalter nichts mehr davon (0.4.14)
        var switcher = new LibrarySwitcherViewModel(new InMemoryLibraries(), new RecordingNavigationService(), new ScriptedDialogs(), NullLogger<LibrarySwitcherViewModel>.Instance, new WeakReferenceMessenger(),
            hidden: CreateViewModel(manager, new(), new()));
        await switcher.RefreshAsync();
        switcher.HasChoices.Should().BeFalse("nach dem Zusperren wie ein normaler Start");
    }

    [Fact]
    public async Task View_model_creates_and_cancel_does_nothing()
    {
        var manager = CreateManager(enabled: true);
        var dialogs = new ScriptedHiddenDialogs();
        var vm = CreateViewModel(manager, dialogs, new ScriptedDialogs());

        await vm.CreateCommand.ExecuteAsync(null); // abgebrochen
        _switcher.Calls.Should().BeEmpty();

        dialogs.CreateAnswer = new HiddenLibraryCreateAnswer(Path.Combine(_root, "Neu"), "Neu", "kennwort-1");
        await vm.CreateCommand.ExecuteAsync(null);
        manager.IsHiddenSessionActive.Should().BeTrue();
        HiddenLibraryStore.Exists(Path.Combine(_root, "Neu")).Should().BeTrue();

        var disabled = CreateViewModel(CreateManager(enabled: false), dialogs, new ScriptedDialogs());
        dialogs.Asked.Clear();
        await disabled.CreateCommand.ExecuteAsync(null);
        await disabled.MountContainerCommand.ExecuteAsync(null);
        dialogs.Asked.Should().BeEmpty("ohne _hidden keine Dialoge");
    }

    [Fact]
    public Task Dialog_validates_inputs_and_password() => OnUiAsync(async () =>
    {
        var create = new HiddenLibraryDialog(HiddenLibraryDialog.Mode.Create) { ValidateFolder = f => Task.FromResult<string?>(f.EndsWith("Verboten", StringComparison.Ordinal) ? "nicht geeignet" : null) };
        create.SetDrives([@"X:\", @"Y:\"]);
        create.Show();
        create.FillCreate(@"X:\", "Fotos", "Tresor", "kennwort-1", "kennwort-2");
        await create.SubmitAsync();
        create.ErrorMessage.Should().Contain("stimmen nicht überein");
        create.FillCreate(@"Y:\", "Verboten", "Tresor", "kennwort-1", "kennwort-1");
        await create.SubmitAsync();
        create.ErrorMessage.Should().Be("nicht geeignet");
        create.FillCreate(@"X:\", "Fotos", "Tresor", "kennwort-1", "kennwort-1");
        await create.SubmitAsync();
        create.CreateAnswer.Should().Be(new HiddenLibraryCreateAnswer(@"X:\Fotos", "Tresor", "kennwort-1"));
        create.Confirmed.Should().BeTrue();

        var unlock = new HiddenLibraryDialog(HiddenLibraryDialog.Mode.Unlock) { Verify = p => Task.FromResult(p == "richtig-1") };
        unlock.SetLibrary("Tresor", canReset: false);
        unlock.Show();
        unlock.FillUnlock("falsch-1");
        await unlock.SubmitAsync();
        unlock.ErrorMessage.Should().Be("Das Kennwort ist falsch.");
        unlock.UnlockAnswer.Should().BeNull();
        unlock.FillUnlock("richtig-1");
        await unlock.SubmitAsync();
        unlock.UnlockAnswer.Should().Be(new HiddenLibraryUnlockAnswer("richtig-1", IsReset: false));
    });

    [Fact]
    public async Task Container_oeffnen_haengt_eine_BitLocker_VHDX_auch_ohne_VeraCrypt_ein()
    {
        // 0.5.4: BitLocker-VHDX über Windows (Einhängen + Entsperr-Abfrage), VeraCrypt nur für andere Containerdateien
        _containers.IsVeraCryptInstalled = false;
        var manager = CreateManager(enabled: true);
        var dialogs = new ScriptedHiddenDialogs { MountAnswer = new ContainerMountAnswer(@"J:\sicherung.vhdx", '\0') };
        var messages = new ScriptedDialogs();
        var vm = CreateViewModel(manager, dialogs, messages);

        await vm.MountContainerCommand.ExecuteAsync(null);

        _containers.MountedImages.Should().Equal(@"J:\sicherung.vhdx");
        _containers.Mounted.Should().BeEmpty("kein VeraCrypt für eine VHDX");
        messages.Errors.Should().BeEmpty();

        _containers.VirtualDiskResult = new ContainerCommandResult(false, "Container ist als X: eingehängt, aber noch gesperrt.");
        await vm.MountContainerCommand.ExecuteAsync(null);
        messages.Errors.Should().Equal("Container ist als X: eingehängt, aber noch gesperrt.");

        dialogs.MountAnswer = new ContainerMountAnswer(@"J:\tresor.hc", 'M');
        await vm.MountContainerCommand.ExecuteAsync(null);
        _containers.Mounted.Should().BeEmpty();
        messages.Errors.Last().Should().Contain("VeraCrypt ist nicht installiert");
    }

    [Fact]
    public Task Dialog_Container_oeffnen_braucht_fuer_eine_VHDX_keinen_Laufwerksbuchstaben() => OnUiAsync(async () =>
    {
        var vhdx = Path.Combine(_root, "sicherung.vhdx");
        var hc = Path.Combine(_root, "tresor.hc");
        File.WriteAllBytes(vhdx, []);
        File.WriteAllBytes(hc, []);
        var mount = new HiddenLibraryDialog(HiddenLibraryDialog.Mode.Mount);
        mount.SetLetters(['M', 'N']);
        mount.Show();
        try
        {
            mount.IsLetterVisible.Should().BeTrue("ohne Datei wie bisher");
            mount.FillMount(vhdx);
            mount.IsLetterVisible.Should().BeFalse("Windows wählt den Buchstaben selbst");
            await mount.SubmitAsync();
            mount.MountAnswer.Should().Be(new ContainerMountAnswer(vhdx, '\0'));

            var veraCrypt = new HiddenLibraryDialog(HiddenLibraryDialog.Mode.Mount);
            veraCrypt.SetLetters(['M', 'N']);
            veraCrypt.Show();
            veraCrypt.FillMount(hc);
            veraCrypt.IsLetterVisible.Should().BeTrue();
            await veraCrypt.SubmitAsync();
            veraCrypt.MountAnswer.Should().Be(new ContainerMountAnswer(hc, 'N'));
        }
        finally
        {
            mount.Close();
        }
    });
}

