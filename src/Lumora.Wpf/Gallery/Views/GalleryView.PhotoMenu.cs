using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumora.Gallery.ViewModels;
using Lumora.PhotoMenu;
using Lumora.Services.Export;

namespace Lumora.Gallery.Views;

/// <summary>
/// Neues Rechtsklick-Menü der Kachel und Listenzeile (ADR-0068, Mockup 17) über <see cref="PhotoContextMenuBuilder"/>, dazu
/// Strg+C (Dateien in die Zwischenablage, nach außen nur als Kopie) und Alt+Enter (Eigenschaften). Was nur die Galerie kann
/// (Öffnen, Diashow, Alben, Story, Datum/Ort, Verschieben, Papierkorb, „Suchen ▸“) kommt über <see cref="PhotoMenuHost"/>.
/// </summary>
public partial class GalleryView
{
    private readonly PhotoActions? _photoActions;

    private void InitPhotoMenu()
    {
        GalleryList.PreviewKeyDown += OnPhotoKeysDown;
    }

    /// <summary>Strg+C und Alt+Enter in Kacheln und Liste.</summary>
    private void OnPhotoKeysDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _viewModel.HasSelection)
        {
            e.Handled = true;
            CopySelectionToClipboard();
        }
        else if (key == Key.Return && Keyboard.Modifiers == ModifierKeys.Alt && FocusedOrSelected() is { } item)
        {
            e.Handled = true;
            _ = _photoActions?.ShowPropertiesAsync(item.Id, ResolveOwnerWindow());
        }
    }

    /// <summary>Strg+C: ausgewählte Dateien als Kopie in die Zwischenablage (versteckte Bibliothek gesperrt).</summary>
    public void CopySelectionToClipboard()
    {
        if (_photoActions is null)
        {
            return;
        }

        _viewModel.ShowStatus(_photoActions.CopyToClipboard(_viewModel.ExternalCandidates(null)));
    }

    /// <summary>Dateien für den Datei-Drop beim Ziehen (null in der versteckten Bibliothek bzw. ohne Aktionen).</summary>
    internal ExternalFileSet? ExternalDragFiles(GalleryItem item)
    {
        if (_photoActions is null)
        {
            return null;
        }

        var files = ExternalFileTransfer.Plan(_viewModel.ExternalCandidates(item), _photoActions.IsExternalBlocked);
        if (files.Blocked)
        {
            _viewModel.ShowStatus("Ziehen in andere Programme ist in der versteckten Bibliothek gesperrt — innerhalb von Lumora geht es weiter.");
        }
        else if (files.SkippedText is { } skipped && files.Paths.Count > 0)
        {
            _viewModel.ShowStatus($"Nach außen: {skipped}.");
        }

        return files;
    }

    private GalleryItem? FocusedOrSelected()
        => (Keyboard.FocusedElement as ListBoxItem)?.DataContext as GalleryItem
           ?? _viewModel.CurrentPage.FirstOrDefault(i => i.IsSelected);

    /// <summary>Rechtsklick-Menü der Kachel bzw. Listenzeile bauen (Ziel: angeklickte Kachel, Auswahl: alle gewählten).</summary>
    private void OnPhotoMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var target = _menuTarget ?? FocusedOrSelected();
        if (target is null || _photoActions is null)
        {
            menu.Items.Clear();
            menu.Items.Add(new MenuItem { Header = "Keine Datei ausgewählt", IsEnabled = false });
            return;
        }

        var ids = _viewModel.SelectedIdsInPageOrder();
        if (ids.Count == 0 || !ids.Contains(target.Id))
        {
            // wie im Explorer: Rechtsklick außerhalb der Auswahl wählt nur dieses Foto — die Auswahl-Befehle
            // (Papierkorb, Verschieben, Tags, Datum, Ort) wirken dann auf genau das Foto im Menükopf
            _viewModel.ClearSelectionCommand.Execute(null);
            target.IsSelected = true;
            ids = [target.Id];
        }

        var context = new PhotoMenuContext(
            ReferenceEquals(menu.PlacementTarget, ListView?.DataGrid) ? PhotoMenuSource.List : PhotoMenuSource.Gallery,
            new PhotoMenuTarget(target.Id, target.Name, target.SourcePath, target.Extension, target.MediaType, target.Missing),
            ids);
        PhotoContextMenuBuilder.Build(menu.Items, context, CreateMenuHost(target, ids), _photoActions);
    }

    private PhotoMenuHost CreateMenuHost(GalleryItem target, System.Collections.Generic.IReadOnlyList<int> ids)
    {
        var multi = ids.Count > 1;
        return new PhotoMenuHost
        {
            Owner = ResolveOwnerWindow,
            ShowStatus = _viewModel.ShowStatus,
            Open = () => _viewModel.OpenInLightboxCommand.Execute(target),
            Slideshow = _viewModel.HasSlideshow ? () => _viewModel.StartSlideshowCommand.Execute(target) : null,
            SlideshowHeader = multi ? $"Diashow ({ids.Count:N0} ausgewählt)" : "Diashow ab hier",
            ShowArchiveContent = () => _viewModel.OpenInLightboxCommand.Execute(target),
            ExtractArchive = () => _viewModel.ExtractItemCommand.Execute(target),
            ArchiveIntoLibrary = multi ? () => _viewModel.ArchiveSelectionCommand.Execute(null) : () => _viewModel.ArchiveItemCommand.Execute(target),
            EditTags = () => _viewModel.EditSelectionTagsCommand.Execute(null),
            EditCategories = () => _viewModel.EditSelectionCategoriesCommand.Execute(null),
            FillAlbums = _viewModel.HasAlbumSupport ? items => AddAlbumItems(items, target) : null,
            FillStories = _stories is { } stories ? items => stories.AddMenuItems(items, ids, target.Id) : null,
            SetDate = () => _viewModel.SetSelectionCaptureDateCommand.Execute(null),
            SetPlace = () => _viewModel.SetSelectionPlaceCommand.Execute(null),
            ShowFaces = () => _ = _viewModel.OpenWithFacesAsync(target),
            ApplySearch = result => _viewModel.ApplyRelatedSearchAsync(result.Label, result.FileIds),
            MoveTo = () => _viewModel.MoveSelectionCommand.Execute(null),
            Recycle = () => _viewModel.RecycleSelectionCommand.Execute(null),
            ExternalCandidates = () => _viewModel.ExternalCandidates(target),
            CopyInitialDirectory = target.FolderPath,
        };
    }
}
