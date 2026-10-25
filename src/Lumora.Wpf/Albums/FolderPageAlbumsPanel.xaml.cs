using System.Windows;
using System.Windows.Controls;

namespace Lumora.Albums;

/// <summary>Alben unter dem Ordnerbaum (ADR-0054): Ablegen von Galerie-Dateien auf ein Album fügt sie hinzu.</summary>
public partial class FolderPageAlbumsPanel : UserControl
{
    public FolderPageAlbumsPanel()
    {
        InitializeComponent();
    }

    private void OnAlbumDragOver(object sender, DragEventArgs e)
    {
        var ok = GalleryDragData.Read(e.Data) is { Length: > 0 } && (sender as FrameworkElement)?.DataContext is AlbumFilterEntry { IsSmart: false };
        e.Effects = ok ? GalleryDragData.LinkEffect(e.AllowedEffects) : DragDropEffects.None; // ADR-0068: mit Datei-Drop nur „Kopieren“ erlaubt
        e.Handled = true;
    }

    private void OnAlbumDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (GalleryDragData.Read(e.Data) is not { Length: > 0 } ids
            || (sender as FrameworkElement)?.DataContext is not AlbumFilterEntry entry
            || DataContext is not FolderPageAlbumsViewModel vm)
        {
            return;
        }

        // Nicht im OLE-Drop-Rückruf arbeiten (Dialoge, lange Schreibvorgänge) — danach auf dem UI-Thread.
        Dispatcher.BeginInvoke(() => _ = vm.DropOnAlbumAsync(entry, ids));
    }
}
