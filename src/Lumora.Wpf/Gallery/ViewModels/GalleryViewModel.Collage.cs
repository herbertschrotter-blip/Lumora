using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Gallery.ListView;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Collage-Ansicht (ADR-0044, Mockup 06): dritte Galerie-Ansicht neben Kacheln und Liste. Fotos und Videos im echten
/// Seitenverhältnis (<see cref="GalleryItem.LayoutAspectRatio"/>), Reihen randvoll (<c>JustifiedTilePanel</c>). Die
/// Ziel-Bildhöhe einer Reihe ist der Vorschaugrößen-Regler (<see cref="TileSize"/>, 120–320 DIP; Strg+Mausrad wie bei
/// Kacheln). Vorschaubilder werden für die Reihenhöhe × Bildschirm-Skalierung dekodiert (<see cref="DecodeBoxForView"/>).
/// </summary>
public sealed partial class GalleryViewModel
{
    private bool _collageRelayoutScheduled;

    /// <summary>
    /// Zähler für das Collage-Panel: steigt, wenn sich Seitenverhältnisse von Items der Seite geändert haben (Maße
    /// nachgetragen, erstes Vorschaubild ohne Maße). Gebündelt je Dispatcher-Runde (Vorschaubilder kommen in Schüben).
    /// </summary>
    [ObservableProperty] private int _collageLayoutVersion;

    public bool IsCollageView => ViewMode == GalleryViewMode.Collage;

    /// <summary>Collage sichtbar (Bibliothek da, kein Unterordner-Zustand).</summary>
    public bool ShowCollage => HasLibrary && !ShowSubfolders && ViewMode == GalleryViewMode.Collage;

    /// <summary>Die Galerie-ListBox ist sichtbar (Kacheln oder Collage — beide nutzen sie, nur Panel und Vorlage wechseln).</summary>
    public bool ShowGalleryList => ShowGrid || ShowCollage;

    /// <summary>Vorschaugrößen-Regler sichtbar: Kacheln (Kachelbreite) und Collage (Reihenhöhe).</summary>
    public bool IsTileSizeAdjustable => ViewMode is GalleryViewMode.Grid or GalleryViewMode.Collage or GalleryViewMode.Folders;

    /// <summary>Dekodierfläche für die aktuelle Ansicht: Collage = Reihenhöhe, sonst Bildbereich der Kachel (ADR-0043).</summary>
    private ThumbnailDecodeBox DecodeBoxForView(double dpiScale)
        => ViewMode is GalleryViewMode.Collage or GalleryViewMode.Folders // Ordner-Zeilen: Zeilenhöhe wie die Collage
            ? ThumbnailDecodeBox.FromCollageRow(TileSize, dpiScale)
            : ThumbnailDecodeBox.FromTile(TileWidth, TileHeight, dpiScale);

    /// <summary>Seitenverhältnis eines Items geändert → Collage einmal je Dispatcher-Runde neu umbrechen lassen.</summary>
    private void ScheduleCollageRelayout()
    {
        if (_collageRelayoutScheduled)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            CollageLayoutVersion++;
            return;
        }

        _collageRelayoutScheduled = true;
        _ = dispatcher.InvokeAsync(
            () =>
            {
                _collageRelayoutScheduled = false;
                CollageLayoutVersion++;
            },
            DispatcherPriority.Background);
    }
}
