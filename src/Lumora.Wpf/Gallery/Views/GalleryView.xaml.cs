using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Controls;
using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;
using Lumora.Shell;

namespace Lumora.Gallery.Views;

/// <summary>
/// Galerie-View. Code-Behind enthält nur UI-Glue (Owner-Window, Enter/Doppelklick auf den
/// fokussierten Container); alle Fachlogik liegt im <see cref="GalleryViewModel"/>.
/// Seit 0.4.44 steckt sie rechts in der Galerie-Seite neben der Ordnerleiste (ADR-0068); den Lightbox-Owner liefert die
/// zuletzt geladene Instanz.
/// </summary>
public partial class GalleryView : UserControl
{
    private readonly GalleryViewModel _viewModel;
    private readonly INavigationService _navigation;
    private readonly ILogger<GalleryView> _logger;
    private readonly Lumora.Stories.StoryGalleryBridge? _stories;
    private Stopwatch? _gridStopwatch;

    /// <summary>Kachel bzw. Listenzeile, auf der das Kontextmenü geöffnet wurde (null beim Knopf „Tags“ der Aktionsleiste).</summary>
    private GalleryItem? _menuTarget;

    public GalleryView(
        GalleryViewModel viewModel,
        INavigationService navigation,
        ILogger<GalleryView> logger,
        Lumora.Stories.StoryGalleryBridge? stories = null,
        Lumora.PhotoMenu.PhotoActions? photoActions = null,
        Lumora.Folders.FolderTreeViewModel? folderTree = null)
    {
        _folderTree = folderTree; // Rechtsklick auf eine Ordner-Zeile (GalleryView.FolderRows.cs)
        _photoActions = photoActions; // Rechtsklick-Menü, Strg+C, Alt+Enter (ADR-0068, GalleryView.PhotoMenu.cs)
        _viewModel = viewModel;
        _navigation = navigation;
        _logger = logger;
        _stories = stories; // „Als Story…“, „In Storys zeigen“ (ADR-0060)
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Listenansicht erst bei Bedarf erzeugen (Start im Kachel-Modus bleibt so schnell wie bisher).
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.ShowList) && viewModel.ShowList)
            {
                EnsureListView();
            }

            if (e.PropertyName == nameof(GalleryViewModel.ViewMode))
            {
                ApplyGalleryViewMode(); // Kacheln ↔ Collage (ADR-0044)
            }
        };
        ApplyGalleryViewMode();
        if (viewModel.ShowList)
        {
            EnsureListView();
        }

        // Menü der Aktionsleiste: eigene Instanz (ein ContextMenu kann nur einem Besitzer zugleich gehören).
        var flyout = new ContextMenu();
        flyout.Opened += OnTagMenuOpened;
        TagsDropDown.Flyout = flyout;
        InitAlbumUi(); // Album-Menü, Ziehen aus der Galerie (ADR-0054, GalleryView.Albums.cs)
        InitPhotoMenu(); // Strg+C, Alt+Enter (ADR-0068, GalleryView.PhotoMenu.cs)
    }

    /// <summary>Strg+F: Suchfeld der Kopfzeile fokussieren (0.2.43).</summary>
    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        Toolbar.FocusSearch();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Seitentitel: seit 0.4.44 gibt es nur noch die Galerie (mit Ordnerleiste, ADR-0068).
        Toolbar.PageTitle = "Galerie";
        _viewModel.OwnerWindowResolver = ResolveOwnerWindow;
        _viewModel.CurrentPage.CollectionChanged += OnPageChanged;
        try
        {
            await _viewModel.EnsureInitializedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Galerie konnte nicht initialisiert werden");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.CurrentPage.CollectionChanged -= OnPageChanged;
        // Nur den eigenen Resolver abhängen — die andere Instanz kann inzwischen geladen worden sein.
        if (_viewModel.OwnerWindowResolver == (Func<Window?>)ResolveOwnerWindow)
        {
            _viewModel.OwnerWindowResolver = null;
        }
    }

    /// <summary>
    /// Diagnose: Zeit von der ersten Änderung an <see cref="GalleryViewModel.CurrentPage"/> (Seitenwechsel, Reload) bis
    /// zum Layout danach (Loaded-Priorität liegt unter Render) plus Zahl der realisierten Kachel-Container.
    /// </summary>
    private void OnPageChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_gridStopwatch is not null)
        {
            return; // Burst (ApplyRows fügt Item für Item ein) wird gebündelt
        }

        _gridStopwatch = Stopwatch.StartNew();
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                var sw = _gridStopwatch;
                _gridStopwatch = null;
                if (sw is null || !IsLoaded)
                {
                    return;
                }

                if (FindVisualChild<JustifiedTilePanel>(GalleryList) is { } collage)
                {
                    // Collage (ADR-0044): Reihen statt Spalten, dazu die Dauer des Umbruchs
                    _logger.LogInformation(
                        "Collage sichtbar: {Items} Items, {Containers} Container realisiert ({Rows} Reihen, Umbruch {LayoutMs:N2} ms), {Ms:N0} ms nach Seitenänderung",
                        _viewModel.CurrentPage.Count, collage.RealizedCount, collage.RowCount, collage.LastLayoutMilliseconds, sw.Elapsed.TotalMilliseconds);
                    return;
                }

                var panel = FindVisualChild<VirtualizingTilePanel>(GalleryList);
                _logger.LogInformation(
                    "Kachel-Grid sichtbar: {Items} Kacheln, {Containers} Container realisiert ({Columns} Spalten), {Ms:N0} ms nach Seitenänderung",
                    _viewModel.CurrentPage.Count, panel?.RealizedCount ?? -1, panel?.Columns ?? 0, sw.Elapsed.TotalMilliseconds);
            },
            DispatcherPriority.Loaded);
    }

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private Window? ResolveOwnerWindow() => Window.GetWindow(this);

    /// <summary>Doppelklick auf eine Kachel öffnet die Lightbox (nicht bei Klick auf Herz/Sterne).</summary>
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBoxItem { DataContext: GalleryItem item })
        {
            return;
        }

        if (IsInsideButton(e.OriginalSource as DependencyObject, (DependencyObject)sender))
        {
            return;
        }

        e.Handled = true;
        _viewModel.OpenInLightboxCommand.Execute(item);
    }

    /// <summary>Enter öffnet die Lightbox für den fokussierten (sonst den ersten ausgewählten) Eintrag.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        // Maßgeblich ist der Auswahlzustand der Items (bei Virtualisierung kann die ListBox-Liste kurz nachhinken).
        var item = (Keyboard.FocusedElement as ListBoxItem)?.DataContext as GalleryItem
                   ?? _viewModel.CurrentPage.FirstOrDefault(i => i.IsSelected);
        if (item is null)
        {
            return;
        }

        e.Handled = true;
        _viewModel.OpenInLightboxCommand.Execute(item);
    }

    /// <summary>Strg+Mausrad im Kachel-Grid: Vorschaugröße eine Stufe größer bzw. kleiner (statt zu scrollen).</summary>
    private void OnGridPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0)
        {
            return;
        }

        e.Handled = true;
        _viewModel.StepTileSize(e.Delta > 0 ? 1 : -1);
    }

    /// <summary>Strg+Plus / Strg+Minus im Kachel-Grid: Vorschaugröße ändern.</summary>
    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        var direction = e.Key switch
        {
            Key.OemPlus or Key.Add => 1,
            Key.OemMinus or Key.Subtract => -1,
            _ => 0,
        };
        if (direction != 0)
        {
            e.Handled = true;
            _viewModel.StepTileSize(direction);
        }
    }

    /// <summary>Das Kachel-Panel meldet den sichtbaren Index-Bereich — das ViewModel fordert Vorschaubilder dafür zuerst an.</summary>
    private void OnVisibleRangeChanged(object sender, VisibleRangeChangedEventArgs e)
        => _viewModel.ReportVisibleRange(e.FirstIndex, e.LastIndex);

    // ---- Kacheln / Collage (ADR-0044) ------------------------------------------------------------

    /// <summary>
    /// Vorlage, Container-Stil und Panel der Galerie-ListBox für Kacheln bzw. Collage als lokale Werte setzen (Liste lässt
    /// die ListBox unverändert, sie ist dann ausgeblendet).
    /// </summary>
    private void ApplyGalleryViewMode()
    {
        var mode = _viewModel.ViewMode;
        if (mode == GalleryViewMode.List)
        {
            return;
        }

        var collage = mode == GalleryViewMode.Collage;
        ApplyListTemplates(
            GalleryList,
            (DataTemplate)FindResource(collage ? "CollageTemplate" : "TileTemplate"),
            (Style)FindResource(collage ? "CollageContainerStyle" : "TileContainerStyle"),
            (ItemsPanelTemplate)FindResource(collage ? "CollagePanelTemplate" : "TilePanelTemplate"));
    }

    /// <summary>Setzt nur geänderte Werte (jede Zuweisung baut die Container neu); das Panel zuletzt.</summary>
    internal static void ApplyListTemplates(ItemsControl list, DataTemplate template, Style containerStyle, ItemsPanelTemplate panel)
    {
        if (!ReferenceEquals(list.ItemTemplate, template))
        {
            list.ItemTemplate = template;
        }

        if (!ReferenceEquals(list.ItemContainerStyle, containerStyle))
        {
            list.ItemContainerStyle = containerStyle;
        }

        if (!ReferenceEquals(list.ItemsPanel, panel))
        {
            list.ItemsPanel = panel;
        }
    }

    // ---- Listenansicht ---------------------------------------------------------------------------

    /// <summary>Die Listenansicht, sobald sie einmal angezeigt wurde (sonst null).</summary>
    public GalleryListView? ListView { get; private set; }

    private void EnsureListView()
    {
        if (ListView is not null)
        {
            return;
        }

        // DataContext erbt vom UserControl (GalleryViewModel); dasselbe Tag-Kontextmenü auf den Zeilen,
        // Rechtsklick wählt die Zeile, falls sie nicht ausgewählt ist.
        var listView = new GalleryListView();
        listView.DataGrid.ContextMenu = (ContextMenu)Resources["TagContextMenu"];
        listView.DataGrid.ContextMenuOpening += OnListContextMenuOpening;
        ListView = listView;
        ListViewHost.Content = listView;
        _logger.LogInformation("Listenansicht erzeugt");
    }

    // ---- Tags (ADR-0021): Kontextmenü / Menü der Aktionsleiste ----------------------------------

    /// <summary>Rechtsklick auf eine nicht ausgewählte Kachel wählt nur diese (wie im Explorer).</summary>
    private void OnTileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _menuTarget = (sender as ListBoxItem)?.DataContext as GalleryItem;
        if (_menuTarget is { IsSelected: false } item)
        {
            _viewModel.ClearSelectionCommand.Execute(null);
            item.IsSelected = true;
        }
    }

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        _menuTarget = row?.DataContext as GalleryItem;
        if (_menuTarget is { IsSelected: false } item)
        {
            _viewModel.ClearSelectionCommand.Execute(null);
            item.IsSelected = true;
        }
    }

    /// <summary>
    /// Menü des Knopfs „Tags“ der Aktionsleiste (ADR-0021): je Tag ein Häkchen-Eintrag (Zustand der Auswahl), dazu Dialog und
    /// Verwaltung. Das Rechtsklick-Menü der Kachel/Listenzeile baut seit 0.4.44 <see cref="OnPhotoMenuOpened"/>.
    /// </summary>
    private void OnTagMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        menu.Items.Clear();
        if (!_viewModel.HasSelection)
        {
            menu.Items.Add(new MenuItem { Header = "Keine Datei ausgewählt", IsEnabled = false });
        }
        else if (_viewModel.TagMenuEntries.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Noch keine Tags angelegt", IsEnabled = false });
        }
        else
        {
            foreach (var entry in _viewModel.TagMenuEntries)
            {
                var item = new MenuItem
                {
                    Header = entry.State is null ? $"{entry.Name}  (teilweise)" : entry.Name,
                    IsCheckable = true,
                    IsChecked = entry.State == true,
                    Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = entry.Brush },
                    Command = _viewModel.ToggleSelectionTagCommand,
                    CommandParameter = entry,
                };
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Tags bearbeiten…",
            InputGestureText = "Strg+T",
            Command = _viewModel.EditSelectionTagsCommand,
        });
        var manage = new MenuItem { Header = "Tags verwalten…" };
        manage.Click += (_, _) => _navigation.NavigateTo(NavigationTags.Tags);
        menu.Items.Add(manage);
    }

    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsInsideButton(DependencyObject? source, DependencyObject container)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, container))
        {
            if (current is ButtonBase)
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
