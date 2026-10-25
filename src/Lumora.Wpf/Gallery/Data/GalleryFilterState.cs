namespace Lumora.Gallery.Data;

/// <summary>Dateiart im Galerie-Filter (Segment „Alle | Bilder | Videos | Archive“), ADR-0041.</summary>
public enum GalleryMediaKind
{
    All,

    /// <summary>MediaType 0.</summary>
    Images,

    /// <summary>MediaType 1.</summary>
    Videos,

    /// <summary>Archive (ZIP/RAR/7z …, MediaType 2, ADR-0022).</summary>
    Archives,
}

/// <summary>Ablage im Galerie-Filter (ADR-0020/0041): wo die Datei liegt, bestimmt die Rolle der Wurzel ihres Ordners.</summary>
public enum GalleryLocation
{
    /// <summary>Keine Einschränkung.</summary>
    Anywhere,

    /// <summary>Unter einer Eingangs-Wurzel (Folders.RootId → LibraryRoots.Role = 0) — „Noch im Eingang“.</summary>
    Inbox,

    /// <summary>Unter der Bibliotheks-(Archiv-)Wurzel (Role = 1) — „In der Bibliothek“.</summary>
    Library,

    /// <summary>Nur fehlende Dateien (Files.Missing = 1) — „Nur fehlende“, für „Aus der Bibliothek entfernen“ (ADR-0065).</summary>
    Missing,
}

/// <summary>
/// Datei-Teil des Galerie-Filters (ADR-0041, ersetzt das Einzel-Enum <c>GalleryFilter</c>): Dateiart, nur Favoriten,
/// Ablage und Mindest-Bewertung wirken unabhängig voneinander und werden in SQL mit UND verknüpft — zusammen mit
/// Tag-, Personen-, Kategorie-, Ordner- und Suchfilter aus <see cref="GalleryQuery"/>.
/// </summary>
/// <param name="Media">Dateiart.</param>
/// <param name="FavoritesOnly">Nur Favoriten (Files.IsFavorite = 1).</param>
/// <param name="Location">Ablage (Eingang / Bibliothek).</param>
/// <param name="MinRating">Mindest-Bewertung 0–5; 0 = egal, sonst Files.Rating ≥ Wert (ohne Bewertung fällt dann heraus).</param>
public sealed record GalleryFilterState(
    GalleryMediaKind Media = GalleryMediaKind.All,
    bool FavoritesOnly = false,
    GalleryLocation Location = GalleryLocation.Anywhere,
    int MinRating = 0)
{
    /// <summary>Kein Datei-Filter.</summary>
    public static GalleryFilterState None { get; } = new();

    public const int MaxRating = 5;

    /// <summary>Ort-Filter (ADR-0050, Zeile „Inhalt“); null = keiner.</summary>
    public GalleryPlaceFilter? Place { get; init; }

    /// <summary>Kartenausschnitt (ADR-0050, von der Karte); null = keiner.</summary>
    public GalleryGeoFilter? Geo { get; init; }

    /// <summary>Nur Dateien mit byte-gleichem Gegenstück im Bereich (ADR-0053, Chip in der Zeile „Inhalt“); null = keiner.</summary>
    public GalleryDuplicateFilter? Duplicates { get; init; }

    /// <summary>Nur „Lieblinge“: Fotos mit gelernter Vorliebe aus der Nutzung (ADR-0072, Chip in der Zeile „Datei“).</summary>
    public bool LovedOnly { get; init; }

    /// <summary>Mindest-Bewertung auf 0–5 begrenzt.</summary>
    public int EffectiveMinRating => Math.Clamp(MinRating, 0, MaxRating);

    /// <summary>Anzahl wirkender Teile (für den Zähler am Knopf „Filter zurücksetzen“).</summary>
    public int ActiveCount
        => (Media != GalleryMediaKind.All ? 1 : 0)
           + (FavoritesOnly ? 1 : 0)
           + (Location != GalleryLocation.Anywhere ? 1 : 0)
           + (EffectiveMinRating > 0 ? 1 : 0)
           + (Place is { IsActive: true } ? 1 : 0)
           + (Geo is { IsActive: true } ? 1 : 0)
           + (Duplicates is not null ? 1 : 0)
           + (LovedOnly ? 1 : 0);

    public bool IsActive => ActiveCount > 0;

    /// <summary>Kurzform fürs Log („Art=Images Fav=True Ablage=Inbox Min=3“).</summary>
    public override string ToString()
        => $"Art={Media} Fav={FavoritesOnly} Ablage={Location} Min={EffectiveMinRating}"
           + (Place is { IsActive: true } p ? $" Ort={p.PlaceIds.Count}{(p.IncludeWithoutLocation ? "+ohne" : string.Empty)}" : string.Empty)
           + (Geo is { IsActive: true } g ? $" Karte={g.FileIds?.Count.ToString() ?? "Ausschnitt"}" : string.Empty)
           + (Duplicates is not null ? " Doppelte" : string.Empty)
           + (LovedOnly ? " Lieblinge" : string.Empty);
}
