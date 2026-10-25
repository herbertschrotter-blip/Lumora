using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumora.PhotoMenu;
using Lumora.Stories;

namespace Lumora.Lightbox;

/// <summary>
/// Rechtsklick-Menü der Lightbox (ADR-0068, Mockup 17) — gleiche Struktur wie in der Galerie (<see cref="PhotoContextMenuBuilder"/>)
/// für das angezeigte Foto, dazu Strg+C (Datei als Kopie in die Zwischenablage) und Alt+Enter (Eigenschaften). Ohne „Öffnen“
/// (das Foto ist offen) und ohne „Suchen“ (das Ergebnis zeigt die Galerie).
/// </summary>
public partial class LightboxWindow
{
    /// <summary>Gemeinsame Menü-Aktionen (vom <see cref="LightboxService"/> gesetzt; null in Tests ohne DI).</summary>
    public PhotoActions? PhotoActions { get; set; }

    /// <summary>„Story ▸“ wie in der Galerie (optional).</summary>
    public StoryGalleryBridge? Stories { get; set; }

    private void InitPhotoMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += OnPhotoMenuOpened;
        ZoomPan.ContextMenu = menu;
        var videoMenu = new ContextMenu();
        videoMenu.Opened += OnPhotoMenuOpened;
        VideoOverlay.ContextMenu = videoMenu;
    }

    /// <summary>Strg+C und Alt+Enter (aus <see cref="OnPreviewKeyDown"/>); true, wenn behandelt.</summary>
    private bool HandlePhotoKeys(Key key)
    {
        if (_viewModel.CurrentItem is not { } item || PhotoActions is null)
        {
            return false;
        }

        if (key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _viewModel.ShowStatusText(PhotoActions.CopyToClipboard([Candidate(item)]));
            return true;
        }

        if (key == Key.Return && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            _ = PhotoActions.ShowPropertiesAsync(item.FileId, this);
            return true;
        }

        return false;
    }

    private static ExternalCandidate Candidate(LightboxItem item) => new(item.FileId, item.Path, !System.IO.File.Exists(item.Path), item.MediaType);

    private void OnPhotoMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        if (_viewModel.CurrentItem is not { } item || PhotoActions is null)
        {
            menu.Items.Clear();
            menu.Items.Add(new MenuItem { Header = "Kein Foto", IsEnabled = false });
            return;
        }

        var ids = new[] { item.FileId };
        var target = new PhotoMenuTarget(item.FileId, item.Name, item.Path, System.IO.Path.GetExtension(item.Path), item.MediaType, !System.IO.File.Exists(item.Path));
        var actions = PhotoActions;
        var host = new PhotoMenuHost
        {
            Owner = () => this,
            ShowStatus = _viewModel.ShowStatusText,
            Slideshow = () => _viewModel.StartSlideshowCommand.Execute(null),
            ExtractArchive = () => _viewModel.ExtractArchiveCommand.Execute(null),
            ArchiveIntoLibrary = () => _ = _viewModel.ArchiveCurrentCommand.ExecuteAsync(null),
            EditTags = () => _ = _viewModel.EditTagsCommand.ExecuteAsync(null),
            EditCategories = () => _ = _viewModel.EditCategoriesCommand.ExecuteAsync(null),
            FillAlbums = actions.HasAlbums ? items => FillAlbums(items, ids, actions) : null,
            FillStories = Stories is { } stories ? items => stories.AddMenuItems(items, ids, item.FileId) : null,
            SetDate = actions.HasCaptureDate ? () => PhotoContextMenuBuilder.Run(HostFor(actions), () => actions.SetDateAsync(ids, this)) : null,
            SetPlace = actions.HasPlaces ? () => PhotoContextMenuBuilder.Run(HostFor(actions), () => actions.SetPlaceAsync(ids, item.IsVideo, this)) : null,
            ShowFaces = item.IsImage && _viewModel.Faces.IsAvailable ? ShowFaces : null,
            Recycle = () => _ = _viewModel.DeleteCurrentCommand.ExecuteAsync(null),
            ExternalCandidates = () => [Candidate(item)],
            CopyInitialDirectory = System.IO.Path.GetDirectoryName(item.Path),
        };
        PhotoContextMenuBuilder.Build(menu.Items, new PhotoMenuContext(PhotoMenuSource.Lightbox, target, ids), host, actions);
    }

    private PhotoMenuHost HostFor(PhotoActions actions) => new() { Owner = () => this, ShowStatus = _viewModel.ShowStatusText };

    private void ShowFaces()
    {
        if (!_viewModel.Faces.IsVisible)
        {
            _ = _viewModel.ToggleFacesCommand.ExecuteAsync(null);
        }
    }

    private void FillAlbums(ItemCollection items, int[] ids, PhotoActions actions)
    {
        var add = new MenuItem
        {
            Header = "Zu Album hinzufügen…",
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Album24 },
        };
        add.Click += (_, _) => PhotoContextMenuBuilder.Run(HostFor(actions), () => actions.AddToAlbumByNameAsync(ids));
        items.Add(add);
        var manage = new MenuItem { Header = "Alben verwalten…" };
        manage.Click += (_, _) => actions.ManageAlbums();
        items.Add(manage);
    }
}
