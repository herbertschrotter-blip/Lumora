using System.ComponentModel;
using Lumora.Marks;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Tag- und Kategorie-Marken auf Kacheln und Collage (Mockup 16, 0.4.35). Die Daten je Datei kommen wie bisher gebündelt je
/// Seite (<c>ApplyTagsToItemsAsync</c>, <c>ApplyCategoriesToItemsAsync</c> — je eine Abfrage für die ganze Seite); hier nur der
/// Schalter „Auf Galerie-Kacheln“, an den die Vorlagen über die ListBox binden.
/// </summary>
public sealed partial class GalleryViewModel
{
    private MarkSettingsState _markSettings = new();

    /// <summary>Schalter „Symbole für Tags und Kategorien › Auf Galerie-Kacheln“ (Einstellungen › Darstellung).</summary>
    public bool ShowTileMarks => _markSettings.ShowOnTiles;

    private void InitMarks(MarkSettingsState? settings)
    {
        _markSettings = settings ?? new MarkSettingsState();
        PropertyChangedEventManager.AddHandler(_markSettings, OnMarkSettingsChanged, nameof(MarkSettingsState.ShowOnTiles));
    }

    private void OnMarkSettingsChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(ShowTileMarks));
}
