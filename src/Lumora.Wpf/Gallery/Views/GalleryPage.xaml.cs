using System.Windows.Controls;
using Lumora.Folders;

namespace Lumora.Gallery.Views;

/// <summary>
/// Seite „Galerie“ (ADR-0068, Mockup 18): Ordnerleiste (<see cref="FolderPane"/>) links, <see cref="GalleryView"/> rechts. Ersetzt
/// die Seite „Ordner“ (bis 0.4.37: Baum + zweite Galerie-Instanz). DataContext = <see cref="GalleryFolderPaneState"/> (offen/zu,
/// Strg+B).
/// </summary>
public partial class GalleryPage : UserControl
{
    public GalleryPage(GalleryView gallery, FolderPane pane, GalleryFolderPaneState paneState)
    {
        InitializeComponent();
        Gallery = gallery;
        Pane = pane;
        PaneState = paneState;
        DataContext = paneState;
        PaneHost.Content = pane;
        GalleryHost.Content = gallery;
        Loaded += (_, _) => _ = paneState.LoadAsync();
    }

    public GalleryView Gallery { get; }

    public FolderPane Pane { get; }

    public GalleryFolderPaneState PaneState { get; }
}
