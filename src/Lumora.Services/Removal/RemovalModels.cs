namespace Lumora.Services.Removal;

/// <summary>Wofür „Fehlende entfernen“ gilt (ADR-0065): Ordner samt Unterordnern, eine Galerie-Auswahl oder ganze Bibliotheken.</summary>
public abstract record MissingTarget
{
    private MissingTarget()
    {
    }

    /// <summary>Kontextmenü im Reiter „Ordner“: dieser Ordner samt Unterordnern.</summary>
    public sealed record InFolder(int FolderId) : MissingTarget;

    /// <summary>Galerie: genau diese Dateien (alle müssen fehlen).</summary>
    public sealed record Selection(IReadOnlyList<int> FileIds) : MissingTarget;

    /// <summary>Einstellungen › Speicher &amp; Wartung: alle fehlenden der aktuellen Bibliothek.</summary>
    public sealed record InLibraries(IReadOnlyList<int> LibraryIds) : MissingTarget;
}

/// <summary>
/// Was an Benutzerdaten an Dateien hängt (für die Bestätigung „Diese Angaben gehen verloren“): Sterne oder Favorit, benannte
/// Personen, Alben, Tags oder Kategorien — jeweils Anzahl Dateien.
/// </summary>
public sealed record UserDataCounts(int Files, int RatedOrFavorite, int WithPersons, int InAlbums, int WithTagsOrCategories)
{
    public static UserDataCounts None { get; } = new(0, 0, 0, 0, 0);

    public bool HasAny => RatedOrFavorite + WithPersons + InAlbums + WithTagsOrCategories > 0;

    public UserDataCounts Add(UserDataCounts other)
        => new(Files + other.Files, RatedOrFavorite + other.RatedOrFavorite, WithPersons + other.WithPersons, InAlbums + other.InAlbums, WithTagsOrCategories + other.WithTagsOrCategories);
}

/// <summary>Vorschau für „Fehlende entfernen“: was entfernt würde und was bleibt.</summary>
/// <param name="FileIds">Dateien, die jetzt entfernt werden dürfen (fehlen, Wurzel erreichbar).</param>
/// <param name="Present">In der Auswahl: Dateien, die nicht als fehlend markiert sind (gemischte Auswahl → Hinweis).</param>
/// <param name="Offline">Als fehlend markiert, aber unter einer nicht erreichbaren Wurzel — gelten als offline.</param>
/// <param name="OfflineRoots">Die nicht erreichbaren Wurzeln (Pfade, nur für die Anzeige).</param>
/// <param name="UserData">Benutzerdaten an den Dateien aus <paramref name="FileIds"/>.</param>
public sealed record MissingSummary(
    IReadOnlyList<int> FileIds,
    int Present,
    int Offline,
    IReadOnlyList<string> OfflineRoots,
    UserDataCounts UserData)
{
    public static MissingSummary Empty { get; } = new([], 0, 0, [], UserDataCounts.None);

    public int Count => FileIds.Count;

    /// <summary>Nichts entfernbar, aber fehlende unter offline-Wurzeln — Meldung „Laufwerk nicht erreichbar“.</summary>
    public bool OnlyOffline => Count == 0 && Offline > 0;
}

/// <summary>Vorschau für „Ordner in den Papierkorb…“ (Bestätigungsdialog).</summary>
/// <param name="BlockedReason">Grund, warum es nicht geht (Wurzel, offline …), sonst null.</param>
/// <param name="CatalogFiles">Katalogisierte Dateien im Ordner samt Unterordnern.</param>
/// <param name="FilesOnDisk">Alle Dateien auf der Platte (auch Nicht-Medien).</param>
/// <param name="SubfoldersOnDisk">Unterordner auf der Platte.</param>
/// <param name="NoRecycleBinReason">Laufwerk ohne Papierkorb (Rückfrage „endgültig löschen?“), sonst null.</param>
public sealed record FolderRecyclePreview(
    int FolderId,
    int LibraryId,
    string Name,
    string Path,
    string? BlockedReason,
    int CatalogFiles,
    int FilesOnDisk,
    int SubfoldersOnDisk,
    UserDataCounts UserData,
    string? NoRecycleBinReason);
