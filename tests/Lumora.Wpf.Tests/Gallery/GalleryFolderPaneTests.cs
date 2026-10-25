using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Folders;
using Lumora.Gallery.ListView;
using Lumora.Gallery.Services;
using Lumora.Gallery.Views;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Shell;
using Lumora.Wpf.Tests.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.PhotoMenu;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Galerie mit Ordnerleiste (ADR-0068, Mockup 18): der Navigationspunkt „Ordner“ entfällt (alte Ziele öffnen die Galerie mit
/// offener Leiste), Strg+B schaltet die Leiste um und der Zustand wird gemerkt, nur EINE Auswahl-Markierung im Baum (#23),
/// Ordnersuche, Breadcrumb/Unterordner ziehen den Baum nach, Kreuz am Chip hebt die Auswahl auf.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GalleryFolderPaneTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private ArchiveTestDb _db = null!;
    private string _reise = null!;
    private string _berge = null!;
    private int _reiseId;
    private int _bergeId;

    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var root = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
        _reise = Path.Combine(_db.ArchiveDir, "Reise");
        _berge = Path.Combine(_reise, "Berge");
        var reise = await _db.AddFolderAsync(_db.ArchiveRootId, _reise, root.Id);
        var berge = await _db.AddFolderAsync(_db.ArchiveRootId, _berge, reise.Id);
        _reiseId = reise.Id;
        _bergeId = berge.Id;
        await _db.AddFileAsync(reise, "a.jpg");
        await _db.AddFileAsync(berge, "b.jpg");
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private static Task OnUi(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    [Fact]
    public Task Frueheres_Ziel_Ordner_oeffnet_die_Galerie_mit_offener_Leiste() => OnUi(() =>
    {
        var pane = new GalleryFolderPaneState();
        pane.ToggleCommand.Execute(null);
        pane.IsOpen.Should().BeFalse();
        var navigation = new NavigationService(NullLogger<NavigationService>.Instance, new SettingsSectionState(), pane);
        var navigated = new List<string>();
        navigation.Attach(tag =>
        {
            navigated.Add(tag);
            return true;
        });

        navigation.NavigateTo(NavigationTags.Folders);

        navigated.Should().Equal(NavigationTags.Gallery);
        pane.IsOpen.Should().BeTrue();
        return Task.CompletedTask;
    });

    [Fact]
    public void Navigationsleiste_hat_keinen_Punkt_Ordner_mehr()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumora.slnx")))
        {
            dir = dir.Parent;
        }

        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Lumora.Wpf", "MainWindow.xaml"));
        xaml.Should().NotContain("TargetPageTag=\"folders\"");
        xaml.Should().Contain("TargetPageType=\"{x:Type gallery:GalleryPage}\"");
    }

    [Fact]
    public Task Zustand_der_Leiste_wird_gemerkt_und_ein_frueher_Klick_gewinnt() => OnUi(async () =>
    {
        var (store, script) = ScriptedService.Create<IGalleryViewSettingsStore>();
        var saved = new Dictionary<string, string?>();
        script.Handlers[nameof(IGalleryViewSettingsStore.SetAsync)] = args =>
        {
            saved[(string)args[0]!] = (string?)args[1];
            return Task.CompletedTask;
        };
        script.Handlers[nameof(IGalleryViewSettingsStore.GetAsync)] = _ => Task.FromResult<string?>("closed");

        var restored = new GalleryFolderPaneState(store);
        await restored.LoadAsync();
        restored.IsOpen.Should().BeFalse("gemerkter Zustand „zu“");
        restored.ToggleToolTip.Should().Be("Ordnerleiste öffnen (Strg+B)");

        var early = new GalleryFolderPaneState(store);
        early.ToggleCommand.Execute(null); // vor dem Laden: zu
        early.ToggleCommand.Execute(null); // wieder auf
        await early.LoadAsync();
        early.IsOpen.Should().BeTrue("ein früher Klick gewinnt gegen den gespeicherten Wert");
        saved[GalleryFolderPaneState.SettingKey].Should().Be("open");
    });

    [Fact]
    public Task Breite_der_Leiste_laesst_sich_ziehen_wird_begrenzt_und_gemerkt() => OnUi(async () =>
    {
        var (store, script) = ScriptedService.Create<IGalleryViewSettingsStore>();
        var saved = new Dictionary<string, string?>();
        script.Handlers[nameof(IGalleryViewSettingsStore.SetAsync)] = args =>
        {
            saved[(string)args[0]!] = (string?)args[1];
            return Task.CompletedTask;
        };
        script.Handlers[nameof(IGalleryViewSettingsStore.GetAsync)] = args =>
            Task.FromResult<string?>((string)args[0]! == GalleryFolderPaneState.WidthSettingKey ? "412" : null);

        var restored = new GalleryFolderPaneState(store);
        await restored.LoadAsync();
        restored.PaneWidth.Should().Be(412, "gemerkte Breite");

        restored.ResizeBy(-1000);
        restored.PaneWidth.Should().Be(GalleryFolderPaneState.MinWidth);
        restored.ResizeBy(5000);
        restored.PaneWidth.Should().Be(GalleryFolderPaneState.MaxWidth);
        saved.Should().NotContainKey(GalleryFolderPaneState.WidthSettingKey, "beim Ziehen wird erst am Ende gespeichert");

        restored.ResizeBy(-140.4);
        restored.CommitWidth();
        restored.PaneWidth.Should().Be(500);
        saved[GalleryFolderPaneState.WidthSettingKey].Should().Be("500");

        restored.ResetWidth();
        restored.PaneWidth.Should().Be(GalleryFolderPaneState.DefaultWidth);
        saved[GalleryFolderPaneState.WidthSettingKey].Should().Be("290");

        var early = new GalleryFolderPaneState(store);
        early.ResizeBy(30);
        await early.LoadAsync();
        early.PaneWidth.Should().Be(320, "frühes Ziehen gewinnt gegen den gespeicherten Wert");
    });

    [Fact]
    public Task StrgB_schaltet_zwischen_Leiste_und_schmaler_Kante_um() => OnUi(() =>
    {
        var state = new GalleryFolderPaneState();
        var page = new GalleryPage(null!, null!, state);
        var window = new Window { Width = 900, Height = 600, Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = page };
        try
        {
            window.Show();
            window.UpdateLayout();
            var paneHost = (ContentControl)page.FindName("PaneHost");
            paneHost.Visibility.Should().Be(Visibility.Visible);
            paneHost.ActualWidth.Should().Be(GalleryFolderPaneState.DefaultWidth);
            ((FrameworkElement)page.FindName("PaneResizeGrip")).Visibility.Should().Be(Visibility.Visible, "Ziehgriff am Rand der offenen Leiste");
            state.ResizeBy(60);
            window.UpdateLayout();
            paneHost.ActualWidth.Should().Be(GalleryFolderPaneState.DefaultWidth + 60);

            var binding = page.InputBindings.OfType<KeyBinding>().Single();
            (binding.Key, binding.Modifiers).Should().Be((Key.B, ModifierKeys.Control));
            binding.Command.Execute(null);
            window.UpdateLayout();

            state.IsOpen.Should().BeFalse();
            paneHost.Visibility.Should().Be(Visibility.Collapsed, "Leiste zu: nur die schmale Kante mit Ordner-Symbol");
            binding.Command.Execute(null);
            state.IsOpen.Should().BeTrue();
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Baum_zeigt_nur_eine_Auswahl_Markierung() => OnUi(() =>
    {
        var tree = new TreeView();
        var item = new TreeViewItem { Header = "Reise", Style = (Style)Application.Current.FindResource("DefaultTreeViewItemStyle") };
        tree.Items.Add(item);
        var window = new Window { Width = 300, Height = 200, Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = tree };
        try
        {
            window.Show();
            item.IsSelected = true;
            window.UpdateLayout();
            var indicator = (UIElement)item.Template.FindName(FolderTreeItemChrome.IndicatorPartName, item);
            indicator.Visibility.Should().Be(Visibility.Visible, "ohne Abhilfe zeigt WPF-UI eine zweite, schmale Marke (#23)");

            FolderTreeItemChrome.SetHideSelectionIndicator(item, true);
            window.UpdateLayout();

            indicator.Visibility.Should().Be(Visibility.Collapsed);
            item.IsSelected = false;
            item.IsSelected = true;
            window.UpdateLayout();
            indicator.Visibility.Should().Be(Visibility.Collapsed, "der lokale Wert schlägt den Auswahl-Trigger der Vorlage");
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Ordnersuche_Breadcrumb_und_Kreuz_am_Chip_ziehen_den_Baum_nach() => OnUi(async () =>
    {
        var vm = CreateTree();
        await vm.EnsureLoadedAsync();

        vm.FolderSearchText = "berg";
        vm.IsSearching.Should().BeTrue();
        vm.FolderSearchResults.Select(h => h.Name).Should().Equal("Berge");
        var sent = new List<FolderSelectedMessage>();
        var recipient = new object();
        WeakReferenceMessenger.Default.Register<FolderSelectedMessage>(recipient, (_, m) => sent.Add(m));
        try
        {
            vm.OpenSearchHitCommand.Execute(vm.FolderSearchResults[0]);
            vm.FolderSearchText.Should().BeNull();
            vm.SelectionText.Should().Be(_berge);
            sent.Should().ContainSingle(m => m.FolderId == _bergeId);

            // Galerie (Breadcrumb/Unterordner-Kachel) wählt „Reise“ → Baum zieht still nach
            sent.Clear();
            WeakReferenceMessenger.Default.Send(new FolderSelectedMessage(_db.LibraryId, _reiseId, true));
            vm.SelectionText.Should().Be(_reise);
            sent.Should().ContainSingle("nur die eigene Nachricht des Tests — der Baum sendet nicht erneut");

            // Kreuz am Chip → Auswahl aufgehoben
            WeakReferenceMessenger.Default.Send(new FolderSelectedMessage(_db.LibraryId, null, true));
            vm.SelectionText.Should().Be("Alle Fotos der Bibliothek");
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    });

    private FolderTreeViewModel CreateTree()
    {
        var libraries = new LibraryService(_db.Factory, _db.Paths, NullLogger<LibraryService>.Instance, new WeakReferenceMessenger());
        var folderActions = new FolderActions(null!, null!, null!, null!, null!, null!, null!, NullService.Create<IGalleryDialogService>(), NullLogger<FolderActions>.Instance);
        return new FolderTreeViewModel(
            _db.Factory,
            libraries,
            NullService.Create<IFileOperationService>(),
            new ScriptedDialogs(),
            folderActions,
            NullLogger<FolderTreeViewModel>.Instance);
    }
}
