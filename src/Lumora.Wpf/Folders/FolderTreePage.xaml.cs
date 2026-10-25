using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lumora.Albums;
using Lumora.Gallery.Views;

namespace Lumora.Folders;

/// <summary>
/// Seite „Ordner“: Ordnerbaum links (280 px), rechts die bestehende GalleryView als eigene Instanz
/// (per DI aufgelöst; ihr ViewModel ist Singleton und zeigt denselben Zustand wie die Galerie-Seite).
/// Unter dem Baum die Alben; Galerie-Dateien lassen sich auf Ordner und Alben ziehen (ADR-0054, <see cref="GalleryDragData"/>).
/// Ordner lassen sich im Baum auf andere Ordner ziehen (ADR-0066): Drop-Markierung am Ziel, Esc bricht ab, Ablegen fragt nach.
/// </summary>
public partial class FolderTreePage : UserControl
{
    /// <summary>Datenformat eines gezogenen Ordnerknotens (nur innerhalb von Lumora).</summary>
    internal const string FolderDragFormat = "Lumora.FolderNode";

    private readonly FolderPageAlbumsViewModel _albums;
    private readonly FolderTreeViewModel _viewModel;
    private Point _dragStart;
    private FolderNode? _dragCandidate;
    private FolderNode? _dropTarget;

    public FolderTreePage(FolderTreeViewModel viewModel, GalleryView gallery, FolderPageAlbumsViewModel albums)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _albums = albums;
        AlbumsPanel.DataContext = albums;
        GalleryHost.Content = gallery;
        Loaded += (_, _) => _ = viewModel.EnsureLoadedAsync();
    }

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

        e.Effects = GalleryDragData.Read(e.Data) is { Length: > 0 } && NodeAt(e.OriginalSource) is not null
            ? GalleryDragData.EffectFor(e.KeyStates, e.AllowedEffects)
            : DragDropEffects.None;
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

        if (GalleryDragData.Read(e.Data) is not { Length: > 0 } ids || NodeAt(e.OriginalSource) is not { } node)
        {
            return;
        }

        var move = GalleryDragData.IsMove(e.KeyStates);
        // Nicht im OLE-Drop-Rückruf einen Dialog öffnen (DoDragDrop liefe sonst bis zum Schließen weiter).
        Dispatcher.BeginInvoke(() => _ = _albums.DropOnFolderAsync(node.Id, node.Name, node.Path, ids, move));
    }

    // ---- ADR-0066: Ordner ziehen ----------------------------------------------------------------

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
        if (System.Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && System.Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
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
}
