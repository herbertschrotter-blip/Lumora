using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Libraries;
using Lumora.Services.Scanning;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Shell;

/// <summary>
/// 0.2.44 (ADR-0042): schlankere Navigation und Einstellungen mit Reitern — Reiter-Tags öffnen die Einstellungen auf dem
/// Reiter (auch „Tags verwalten…“ und die Ersteinrichtung ohne Bibliothek), Bibliotheks-Umschalter oben in der Leiste,
/// „Eingang hinzufügen…“ auf der Seite „Ordner“ über die Bibliotheksverwaltung.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class NavigationAndSettingsTabsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    [Fact]
    public void Settings_sections_are_in_display_order_and_pages_are_no_sections()
    {
        NavigationTags.SettingsSections.Should().Equal(
            NavigationTags.Library, NavigationTags.Tags, NavigationTags.Categories, NavigationTags.SettingsFaces,
            NavigationTags.SettingsMotifs, NavigationTags.SettingsEnhance, NavigationTags.SettingsArchives, NavigationTags.SettingsAppearance, NavigationTags.SettingsStories, NavigationTags.SettingsAssistant, NavigationTags.SettingsPlaces, NavigationTags.SettingsModels, NavigationTags.SettingsStorage, NavigationTags.SettingsInfo);
        NavigationTags.SettingsSections.Should().OnlyHaveUniqueItems();
        NavigationTags.IsSettingsSection(NavigationTags.Tags).Should().BeTrue();
        NavigationTags.IsSettingsSection(NavigationTags.SettingsInfo).Should().BeTrue();
        foreach (var page in new[] { NavigationTags.Gallery, NavigationTags.Folders, NavigationTags.Persons, NavigationTags.Sorter, NavigationTags.Settings, null })
        {
            NavigationTags.IsSettingsSection(page).Should().BeFalse(page ?? "null");
        }
    }

    [Fact]
    public void Section_state_rejects_unknown_tags()
    {
        var state = new SettingsSectionState();
        state.SelectedSection.Should().Be(NavigationTags.Library, "Standard: Bibliotheken & Eingänge");
        var act = () => state.SelectedSection = NavigationTags.Gallery;
        act.Should().Throw<ArgumentException>();
        state.SelectedSection.Should().Be(NavigationTags.Library);
    }

    [Fact]
    public Task Section_tags_open_settings_on_that_tab_and_pages_pass_through() => OnUiAsync(() =>
    {
        var state = new SettingsSectionState();
        var navigation = new NavigationService(NullLogger<NavigationService>.Instance, state);
        var navigated = new List<string>();

        // Vor dem Anbinden (Start ohne Bibliothek): gemerkt und beim Attach ausgeführt — auf dem Reiter
        navigation.NavigateTo(NavigationTags.Library);
        navigation.Attach(tag =>
        {
            navigated.Add(tag);
            return true;
        });
        navigated.Should().Equal(NavigationTags.Settings);
        state.SelectedSection.Should().Be(NavigationTags.Library);

        // „Tags verwalten…“ aus der Galerie, „Einstellungen öffnen“ der Personen-Seite
        navigation.NavigateTo(NavigationTags.Tags);
        state.SelectedSection.Should().Be(NavigationTags.Tags);
        navigation.NavigateTo(NavigationTags.SettingsFaces);
        state.SelectedSection.Should().Be(NavigationTags.SettingsFaces);

        // Seiten und „settings“ selbst lassen den Reiter stehen
        navigation.NavigateTo(NavigationTags.Gallery);
        navigation.NavigateTo(NavigationTags.Settings);
        state.SelectedSection.Should().Be(NavigationTags.SettingsFaces);
        navigated.Should().Equal(NavigationTags.Settings, NavigationTags.Settings, NavigationTags.Settings, NavigationTags.Gallery, NavigationTags.Settings);

        navigation.NotifyNavigated(NavigationTags.Settings);
        navigation.CurrentTag.Should().Be(NavigationTags.Settings, "CurrentTag ist nie ein Reiter-Tag");
        return Task.CompletedTask;
    });

    [Fact]
    public Task Library_switcher_shows_active_library_switches_and_opens_management() => OnUiAsync(async () =>
    {
        var libraries = new InMemoryLibraries();
        libraries.Add(1, "Familie", 9410, DateTime.UtcNow.AddDays(-2));
        libraries.Add(2, "PhotoTest", 1248, DateTime.UtcNow);
        var navigation = new RecordingNavigationService();
        var vm = new LibrarySwitcherViewModel(libraries, navigation, new ScriptedDialogs(), NullLogger<LibrarySwitcherViewModel>.Instance, new WeakReferenceMessenger());

        await vm.RefreshAsync();
        vm.HasLibrary.Should().BeTrue();
        vm.ActiveName.Should().Be("PhotoTest");
        vm.CaptionText.Should().Be("Bibliothek wechseln");
        vm.HasChoices.Should().BeTrue();
        vm.Libraries.Select(l => (l.Name, l.IsActive)).Should().Equal(("Familie", false), ("PhotoTest", true));
        vm.Libraries[0].DetailText.Should().Be($"{9410:N0} Dateien");

        // Die aktive erneut wählen: nichts passiert
        await vm.SwitchToCommand.ExecuteAsync(vm.Libraries[1]);
        libraries.SetActiveCalls.Should().BeEmpty();

        await vm.SwitchToCommand.ExecuteAsync(vm.Libraries[0]);
        libraries.SetActiveCalls.Should().Equal(1);
        vm.ActiveName.Should().Be("Familie");
        vm.Libraries.Single(l => l.IsActive).Id.Should().Be(1);

        vm.ManageCommand.Execute(null);
        navigation.Tags.Should().Equal(NavigationTags.Library);
    });

    [Fact]
    public Task Library_switcher_without_library_leads_to_setup() => OnUiAsync(async () =>
    {
        var navigation = new RecordingNavigationService();
        var vm = new LibrarySwitcherViewModel(new InMemoryLibraries(), navigation, new ScriptedDialogs(), NullLogger<LibrarySwitcherViewModel>.Instance, new WeakReferenceMessenger());

        await vm.RefreshAsync();
        vm.HasLibrary.Should().BeFalse();
        vm.HasChoices.Should().BeFalse("ohne Bibliothek öffnet der Klick direkt die Verwaltung");
        vm.ActiveName.Should().Be("Keine Bibliothek");
        vm.CaptionText.Should().Be("Bibliothek einrichten");
    });

    [Fact]
    public Task Library_switcher_refreshes_after_navigation() => OnUiAsync(async () =>
    {
        var libraries = new InMemoryLibraries();
        var navigation = new RecordingNavigationService();
        var vm = new LibrarySwitcherViewModel(libraries, navigation, new ScriptedDialogs(), NullLogger<LibrarySwitcherViewModel>.Instance, new WeakReferenceMessenger());
        await vm.RefreshAsync();
        vm.HasLibrary.Should().BeFalse();

        // Navigation zwischen Arbeitsseiten liest nicht neu (Zählung über alle Dateien) …
        libraries.Add(5, "Neu", 0, DateTime.UtcNow);
        navigation.RaiseNavigated(NavigationTags.Gallery);
        navigation.RaiseNavigated(NavigationTags.Settings);
        await Task.Delay(50);
        vm.HasLibrary.Should().BeFalse();

        // … aber das Verlassen der Einstellungen (dort wird angelegt/umbenannt)
        navigation.RaiseNavigated(NavigationTags.Gallery);
        var deadline = DateTime.UtcNow + Timeout;
        while (!vm.HasLibrary && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        vm.ActiveName.Should().Be("Neu");
    });

    [Fact]
    public Task Library_switcher_coalesces_refreshes_and_awaits_the_running_one() => OnUiAsync(async () =>
    {
        var libraries = new InMemoryLibraries();
        var vm = new LibrarySwitcherViewModel(libraries, new RecordingNavigationService(), new ScriptedDialogs(), NullLogger<LibrarySwitcherViewModel>.Instance, new WeakReferenceMessenger());
        var first = vm.RefreshAsync();
        libraries.Add(1, "Später", 1, DateTime.UtcNow);
        var second = vm.RefreshAsync(); // läuft schon → wird danach wiederholt, liefert dieselbe Aufgabe
        second.Should().BeSameAs(first);
        await second;
        vm.ActiveName.Should().Be("Später", "der wartende Aufrufer sieht einen Stand nach seinem Aufruf");
    });

    [Fact]
    public Task Library_switcher_does_not_switch_during_a_scan_and_reports_failures() => OnUiAsync(async () =>
    {
        var libraries = new InMemoryLibraries();
        libraries.Add(1, "A", 1, DateTime.UtcNow);
        libraries.Add(2, "B", 1, DateTime.UtcNow.AddDays(-1));
        var dialogs = new ScriptedDialogs();
        var management = new FakeInboxActions { IsScanning = true };
        var vm = new LibrarySwitcherViewModel(libraries, new RecordingNavigationService(), dialogs, NullLogger<LibrarySwitcherViewModel>.Instance, new WeakReferenceMessenger(), management);
        await vm.RefreshAsync();

        await vm.SwitchToCommand.ExecuteAsync(vm.Libraries.Single(l => l.Name == "B"));
        libraries.SetActiveCalls.Should().BeEmpty("während eines Scans bleibt die aktive Bibliothek");
        dialogs.Errors.Should().ContainSingle().Which.Should().Contain("Scan");

        management.IsScanning = false;
        await vm.SwitchToCommand.ExecuteAsync(new LibrarySwitchItem(99, "Weg", 0, false)); // gibt es nicht mehr
        dialogs.Errors.Should().HaveCount(2);
        dialogs.Errors[1].Should().Contain("„Weg“ konnte nicht aktiviert werden");
    });

    private sealed class FakeInboxActions : IInboxActions
    {
        public bool IsScanning { get; set; }

        public int Calls { get; private set; }

        public Task<bool> AddInboxToActiveLibraryAsync()
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public Task Folders_page_add_inbox_uses_library_management() => OnUiAsync(async () =>
    {
        var folderActions = new Lumora.Folders.FolderActions(
            NullService.Create<Lumora.Services.Operations.IFolderAdoptionService>(),
            NullService.Create<Lumora.Folders.Adoption.IFolderAdoptionPicker>(),
            NullService.Create<Lumora.Services.Catalog.IFolderMetadataService>(),
            NullService.Create<Lumora.Services.Tags.ITagService>(),
            NullService.Create<Lumora.Services.Categories.ICategoryService>(),
            NullService.Create<Lumora.Gallery.Tags.ITagPickerService>(),
            NullService.Create<Lumora.Gallery.Categories.ICategoryPickerService>(),
            NullService.Create<Lumora.Gallery.Services.IGalleryDialogService>(),
            NullLogger<Lumora.Folders.FolderActions>.Instance);
        Lumora.Folders.FolderTreeViewModel Create(IInboxActions? inbox) => new(
            NullService.Create<Lumora.Services.Database.ILibraryContextFactory>(),
            new InMemoryLibraries(),
            NullService.Create<Lumora.Services.Operations.IFileOperationService>(),
            new ScriptedDialogs(),
            folderActions,
            NullLogger<Lumora.Folders.FolderTreeViewModel>.Instance,
            inbox);

        var without = Create(null);
        without.CanShowAddInbox.Should().BeFalse();
        without.AddInboxCommand.CanExecute(null).Should().BeFalse();

        var inbox = new FakeInboxActions();
        var page = Create(inbox);
        page.CanShowAddInbox.Should().BeTrue();
        page.AddInboxCommand.CanExecute(null).Should().BeTrue();
        await page.AddInboxCommand.ExecuteAsync(null);
        inbox.Calls.Should().Be(1);
        page.IsAddingInbox.Should().BeFalse();
        page.ErrorMessage.Should().BeNull();
    });

    private static LibraryViewModel CreateLibraryViewModel(InMemoryLibraries libraries, ScriptedDialogs dialogs, RecordingNavigationService navigation) => new(
        libraries,
        NullService.Create<IScanService>(),
        dialogs,
        navigation,
        NullLogger<LibraryViewModel>.Instance,
        new WeakReferenceMessenger());

    [Fact]
    public Task Add_inbox_from_folders_page_adds_to_the_active_library() => OnUiAsync(async () =>
    {
        var libraries = new InMemoryLibraries();
        libraries.Add(1, "Alt", 3, DateTime.UtcNow.AddDays(-1));
        libraries.Add(2, "Aktiv", 5, DateTime.UtcNow);
        var dialogs = new ScriptedDialogs { FolderToPick = @"D:\Eingang\Handy", ConfirmAnswer = false };
        IInboxActions inbox = CreateLibraryViewModel(libraries, dialogs, new RecordingNavigationService());

        inbox.IsScanning.Should().BeFalse();
        var added = await inbox.AddInboxToActiveLibraryAsync();

        added.Should().BeTrue();
        libraries.AddedRoots.Should().Equal((2, @"D:\Eingang\Handy", LibraryRootRole.Inbox));
        dialogs.Confirmations.Should().Equal("Eingang hinzugefügt"); // Angebot „Jetzt aktualisieren“ (hier „Später“)
        dialogs.Errors.Should().BeEmpty();
    });

    [Fact]
    public Task Add_inbox_cancelled_or_failing_or_without_library_adds_nothing() => OnUiAsync(async () =>
    {
        var libraries = new InMemoryLibraries();
        var dialogs = new ScriptedDialogs();
        IInboxActions inbox = CreateLibraryViewModel(libraries, dialogs, new RecordingNavigationService());

        // Ohne Bibliothek: Hinweis statt Ordnerauswahl
        (await inbox.AddInboxToActiveLibraryAsync()).Should().BeFalse();
        dialogs.Errors.Should().ContainSingle().Which.Should().Contain("noch keine Bibliothek");

        // Ordnerauswahl abgebrochen
        libraries.Add(1, "Fotos", 3, DateTime.UtcNow);
        dialogs.Errors.Clear();
        (await inbox.AddInboxToActiveLibraryAsync()).Should().BeFalse();
        libraries.AddedRoots.Should().BeEmpty();
        dialogs.Errors.Should().BeEmpty();

        // Dienst lehnt ab (z. B. Ordner innerhalb des Bibliotheksordners): Fehler als Dialog
        dialogs.FolderToPick = @"C:\Fotos\2024";
        libraries.AddRootFailure = new InvalidOperationException("liegt innerhalb einer Wurzel");
        (await inbox.AddInboxToActiveLibraryAsync()).Should().BeFalse();
        dialogs.Errors.Should().ContainSingle().Which.Should().Contain("liegt innerhalb einer Wurzel");
    });
}
