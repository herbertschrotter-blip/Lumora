using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lumora.Albums;
using Lumora.Gallery.ViewModels;
using Lumora.Gallery.Views;
using Lumora.Shell;

namespace Lumora.Folders;

/// <summary>
/// Ordnerleiste der Galerie (ADR-0068, Mockup 18; bis 0.4.37 linke Hälfte der Seite „Ordner“): Ordnerbaum, Suche, Alben.
/// Galerie-Dateien lassen sich auf Ordner und Alben ziehen (ADR-0054, <see cref="GalleryDragData"/>: ohne Taste verknüpfen, mit
/// Umschalt verschieben); Ordner lassen sich im Baum auf andere Ordner ziehen (ADR-0066). Code-Behind nur UI-Glue.
/// </summary>
public partial class FolderPane : UserControl
{
    /// <summary>Datenformat eines gezogenen Ordnerknotens (nur innerhalb von Lumora).</summary>
    internal const string FolderDragFormat = "Lumora.FolderNode";

    private readonly FolderPageAlbumsViewModel _albums;
    private readonly FolderTreeViewModel _viewModel;
    private readonly GalleryFolderPaneState _paneState;
    private readonly INavigationService _navigation;
    private readonly GalleryViewModel? _gallery;
    private Point _dragStart;
    private FolderNode? _dragCandidate;
    private FolderNode? _dropTarget;

    public FolderPane(
        FolderTreeViewModel viewModel,
        FolderPageAlbumsViewModel albums,
        GalleryFolderPaneState paneState,
        INavigationService navigation,
        GalleryViewModel? gallery = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _paneState = paneState;
        _navigation = navigation;
        _gallery = gallery;
        DataContext = viewModel;
        _albums = albums;
        AlbumsPanel.DataContext = albums;
        Loaded += (_, _) => _ = viewModel.EnsureLoadedAsync();
    }

    public FolderTreeViewModel ViewModel => _viewModel;

    private void OnCollapseClick(object sender, RoutedEventArgs e) => _paneState.ToggleCommand.Execute(null);

    private void OnManageExclusionsClick(object sender, RoutedEventArgs e) => _navigation.NavigateTo(NavigationTags.Library);

    // ---- Ordnersuche ---------------------------------------------------------------------------------------------------

    private void OnSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } source && FindAncestor<ListBoxItem>(source)?.DataContext is FolderSearchHit hit)
        {
            _viewModel.OpenSearchHitCommand.Execute(hit);
        }
    }

    private void OnSearchResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && SearchResults.SelectedItem is FolderSearchHit hit)
        {
            e.Handled = true;
            _viewModel.OpenSearchHitCommand.Execute(hit);
        }
    }

    // ---- Tastatur und Kontextmenü --------------------------------------------------------------------------------------

    /// <summary>F2 = Umbenennen, Entf = In den Papierkorb (wie im Kontextmenü angezeigt).</summary>
    private void OnTreePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode { IsFolder: true } node || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (e.Key == Key.F2 && node.CanRename)
        {
            e.Handled = true;
            _viewModel.RenameFolderCommand.Execute(node);
        }
        else if (e.Key == Key.Delete && _viewModel.HasRemovalActions)
        {
            e.Handled = true;
            _viewModel.RecycleFolderCommand.Execute(node);
        }
    }

    private void OnNodeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Platzhalter und Bibliotheks-Köpfe haben kein Menü
        if (sender is TreeViewItem { DataContext: FolderNode { IsFolder: false } })
        {
            e.Handled = true;
        }
    }

    /// <summary>„Titelbild des Ordners ▸“: aktueller Stand und „Wieder automatisch wählen lassen…“ (ADR-0068).</summary>
    private async void OnCoverSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: FolderNode node } menu || !ReferenceEquals(e.OriginalSource, menu))
        {
            return;
        }

        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "Wird geladen…", IsEnabled = false });
        var info = await _viewModel.GetFolderCoverAsync(node).ConfigureAwait(true);
        menu.Items.Clear();
        menu.Items.Add(new MenuItem
        {
            Header = info is null || info.FileId is null
                ? "Noch kein Titelbild (keine Fotos)"
                : info.IsManual ? "Selbst gewählt" : "Automatisch gewählt (beste Foto-Güte)",
            IsEnabled = false,
        });
        menu.Items.Add(new MenuItem
        {
            Header = "Foto wählen: Rechtsklick auf ein Foto › Als Titelbild für",
            IsEnabled = false,
            FontStyle = FontStyles.Italic,
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Wieder automatisch wählen lassen…",
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Sparkle24 },
            IsEnabled = info?.IsManual == true,
            Command = _viewModel.ResetFolderCoverCommand,
            CommandParameter = node,
        });
    }

    /// <summary>„Diashow“ für den Ordner: Ordner wählen, auf die Galerie warten, dann die ganze Ansicht zeigen.</summary>
    private async void OnFolderSlideshowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: FolderNode { IsFolder: true } node } || _gallery is null)
        {
            return;
        }

        node.IsSelected = true;
        var waited = 0;
        while ((_gallery.FolderId != node.Id || _gallery.IsLoading) && waited < 5000)
        {
            await Task.Delay(50).ConfigureAwait(true);
            waited += 50;
        }

        _gallery.ClearSelectionCommand.Execute(null);
        _gallery.StartSlideshowCommand.Execute(null);
    }

    // ---- Ziehen und Ablegen (ADR-0054, ADR-0066) ---------------------------------------------------------------------

    /// <summary>Ordner unter dem Mauszeiger (kein Bibliotheks-Kopf, kein Platzhalter) oder null.</summary>
    private static FolderNode? NodeAt(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null && current is not TreeViewItem)
        {
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }

        return (current as TreeViewItem)?.DataContext is FolderNode { IsFolder: true, IsLibraryHeader: false, IsPlaceholder: false } node ? node : null;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(FolderDragFormat) is FolderNode source)
        {
            // ADR-0066: Ordner auf Ordner — Markierung nur, wo das Verschieben erlaubt aussieht
            var target = NodeAt(e.OriginalSource);
            var ok = _viewModel.CanDropFolder(source, target);
            SetDropTarget(ok ? target : null);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // ADR-0068: die Tasten entscheiden (verknüpfen/verschieben), der Effekt ist nur der Mauszeiger
        var node = GalleryDragData.Read(e.Data) is { Length: > 0 } ? NodeAt(e.OriginalSource) : null;
        SetDropTarget(node);
        e.Effects = node is not null ? GalleryDragData.EffectFor(e.KeyStates, e.AllowedEffects) : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(FolderDragFormat) is FolderNode source)
        {
            var target = NodeAt(e.OriginalSource);
            SetDropTarget(null);
            if (target is not null && _viewModel.CanDropFolder(source, target))
            {
                // Rückfrage nicht im OLE-Drop-Rückruf öffnen
                Dispatcher.BeginInvoke(() => _ = _viewModel.DropFolderAsync(source, target));
            }

            return;
        }

        SetDropTarget(null);
        if (GalleryDragData.Read(e.Data) is not { Length: > 0 } ids || NodeAt(e.OriginalSource) is not { } node)
        {
            return;
        }

        var move = GalleryDragData.IsMove(e.KeyStates);
        // Nicht im OLE-Drop-Rückruf einen Dialog öffnen (DoDragDrop liefe sonst bis zum Schließen weiter).
        Dispatcher.BeginInvoke(() => _ = _albums.DropOnFolderAsync(node.Id, node.Name, node.Path, ids, move));
    }

    private void OnTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FolderTree);
        _dragCandidate = NodeAt(e.OriginalSource) is { CanMove: true } node && _viewModel.HasMoveActions ? node : null;
    }

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is not { } node || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(FolderTree);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragCandidate = null;
        try
        {
            // Esc bricht ab (Standardverhalten von DoDragDrop); danach Markierung immer entfernen
            DragDrop.DoDragDrop(FolderTree, new DataObject(FolderDragFormat, node), DragDropEffects.Move);
        }
        finally
        {
            SetDropTarget(null);
        }
    }

    private void SetDropTarget(FolderNode? target)
    {
        if (ReferenceEquals(_dropTarget, target))
        {
            return;
        }

        if (_dropTarget is not null)
        {
            _dropTarget.IsDropTarget = false;
        }

        _dropTarget = target;
        if (target is not null)
        {
            target.IsDropTarget = true;
        }
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

            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}
