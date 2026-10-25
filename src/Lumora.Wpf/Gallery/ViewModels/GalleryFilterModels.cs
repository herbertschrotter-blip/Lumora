using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Wpf.Ui.Controls;

namespace Lumora.Gallery.ViewModels;

/// <summary>Art einer Bedingung in der Zeile „Es gilt: … UND …“ der Filterkarte (ADR-0041).</summary>
public enum GalleryFilterConditionKind
{
    Search,
    Media,
    Favorites,
    Rating,
    Location,
    Tags,
    Persons,
    Category,

    /// <summary>Beziehung der Personen auf dem Foto (0.2.47, ADR-0045).</summary>
    Relations,

    /// <summary>Ort (ADR-0050).</summary>
    Place,

    /// <summary>Kartenausschnitt (von der Karte, ADR-0050).</summary>
    MapArea,
    /// <summary>Motive (ADR-0052).</summary>
    Motifs,

    /// <summary>Nur Doppelte (ADR-0053).</summary>
    Duplicates,
    /// <summary>Eigenes Album (ADR-0054).</summary>
    Album,

    /// <summary>Nur „Lieblinge“ aus der gelernten Nutzung (ADR-0072).</summary>
    Loved,
}

/// <summary>
/// Eine wirkende Bedingung der Galerie in Worten („Bilder“, „Tag „Urlaub““ …); × entfernt genau diese Bedingung.
/// <paramref name="CategoryId"/> nur bei <see cref="GalleryFilterConditionKind.Category"/>. <paramref name="IsFirst"/> =
/// erste Bedingung der Zeile (vor ihr steht kein „UND“).
/// </summary>
public sealed record GalleryFilterCondition(GalleryFilterConditionKind Kind, string Label, int? CategoryId = null, bool IsFirst = false);

/// <summary>Eintrag der Auswahl „Bewertung“: 0 = egal, sonst „ab n Sternen“.</summary>
public sealed record GalleryRatingOption(int MinRating, string Label)
{
    /// <summary>Sterne für die Anzeige im Flyout (null bei „egal“).</summary>
    public byte? Stars => MinRating == 0 ? null : (byte)MinRating;
}

/// <summary>Eintrag der Auswahl „Ablage“.</summary>
public sealed record GalleryLocationOption(GalleryLocation Location, string Label, string Description);

/// <summary>Eintrag des Segments „Dateiart“.</summary>
public sealed record GalleryMediaOption(GalleryMediaKind Media, string Label, SymbolRegular Symbol, string ToolTip);

/// <summary>
/// Eintrag des Ansicht-Segments („Kacheln | Collage | Liste“, Collage seit 0.2.46). Das Segment ist eine Liste dieser
/// Einträge — ein weiterer <see cref="GalleryViewMode"/>-Wert braucht nur einen weiteren Eintrag in
/// <c>GalleryViewModel.ViewModeOptions</c>.
/// </summary>
public sealed record GalleryViewModeOption(GalleryViewMode Mode, string Label, SymbolRegular Symbol, string ToolTip);

/// <summary>Sortierfeld der Sortierleiste (fest oder Kategorie).</summary>
public enum GallerySortField
{
    Modified,
    Name,
    Size,
    Category,

    /// <summary>Aufnahmedatum (ADR-0046); ohne Aufnahmedatum zählt das Änderungsdatum.</summary>
    Captured,
}

/// <summary>
/// Eintrag der Feld-Auswahl „Sortieren [Feld ▾]“. <paramref name="CategoryId"/> nur bei <see cref="GallerySortField.Category"/>.
/// Die Richtung wählt der Knopf daneben; zusammen ergeben beide die <see cref="GallerySortOption"/>.
/// </summary>
public sealed record GallerySortFieldOption(GallerySortField Field, string Label, int? CategoryId = null);
