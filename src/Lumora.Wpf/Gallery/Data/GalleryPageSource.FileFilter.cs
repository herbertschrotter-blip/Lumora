using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Lumora.Gallery.Data;

/// <summary>
/// Datei-Teil des Galerie-Filters (ADR-0041): Dateiart, nur Favoriten, Ablage und Mindest-Bewertung als einzelne
/// UND-Bedingungen. Dateiart und Favorit treffen die Indizes <c>IX_Files_Library_MediaType</c> bzw.
/// <c>IX_Files_Library_Favorite</c> (SQLite wählt für die Zählung einen davon, die übrigen Bedingungen prüft es je
/// Zeile); die Seiten-Abfrage läuft weiter über den Sortier-Index (<c>IX_Files_Library_Modified</c>). Die Ablage läuft
/// wie bisher als IN-Subquery über die kleine Folders-Tabelle (ADR-0020). Für die Bewertung gibt es bewusst keinen Index
/// (keine Migration in 0.2.43) — sie wird je Zeile geprüft, wie vorher der Favoriten-Filter auf der Seiten-Abfrage.
/// </summary>
public sealed partial class GalleryPageSource
{
    // Ablage (ADR-0020): die Ordner-Liste ist nicht mehr auf $libId beschränkt (ADR-0048) — der Bereich steht schon an
    // f.LibraryId, und in „Alle verknüpften“ liegen die Ordner in mehreren Bibliotheken. Folders ist klein.

    /// <summary>Ablage „Noch im Eingang“: Ordner, deren Wurzel die Rolle Eingang (0) hat.</summary>
    private const string InboxClause =
        "AND f.FolderId IN (SELECT d.Id FROM Folders d JOIN LibraryRoots r ON r.Id = d.RootId WHERE r.Role = 0)";

    /// <summary>Ablage „In der Bibliothek“: Ordner unter der Archiv-/Bibliotheks-Wurzel (Rolle 1).</summary>
    private const string ArchiveClause =
        "AND f.FolderId IN (SELECT d.Id FROM Folders d JOIN LibraryRoots r ON r.Id = d.RootId WHERE r.Role = 1)";

    /// <summary>Alle wirkenden Datei-Bedingungen, je eine Zeile „AND …“ (leer ohne Datei-Filter).</summary>
    internal static string BuildFilterClause(GalleryFilterState filter)
    {
        var parts = new List<string>(4);
        switch (filter.Media)
        {
            case GalleryMediaKind.Images:
                parts.Add("AND f.MediaType = 0");
                break;
            case GalleryMediaKind.Videos:
                parts.Add("AND f.MediaType = 1");
                break;
            case GalleryMediaKind.Archives:
                parts.Add("AND f.MediaType = 2");
                break;
        }

        if (filter.FavoritesOnly)
        {
            parts.Add("AND f.IsFavorite = 1");
        }

        if (filter.EffectiveMinRating > 0)
        {
            parts.Add("AND f.Rating >= $minRating");
        }

        switch (filter.Location)
        {
            case GalleryLocation.Inbox:
                parts.Add(InboxClause);
                break;
            case GalleryLocation.Library:
                parts.Add(ArchiveClause);
                break;
            case GalleryLocation.Missing:
                parts.Add("AND f.Missing = 1"); // „Nur fehlende“ (ADR-0065)
                break;
        }

        parts.AddRange(BuildPlaceClauses(filter)); // Ort und Kartenausschnitt (ADR-0050, GalleryPageSource.Places.cs)
        parts.AddRange(BuildDuplicateClauses(filter)); // nur Doppelte (ADR-0053, GalleryPageSource.Duplicates.cs)
        if (filter.LovedOnly)
        {
            // „Lieblinge“ (ADR-0072): kleine Menge aus FileUsageDays, leer bei „Nutzung lernen“ aus
            parts.Add("AND f.Id IN (" + Lumora.Services.Usage.UsageScoring.LovedFileIdsSql(respectSetting: true) + ")");
        }
        return string.Join("\n  ", parts);
    }

    private static void BindFileFilterParams(SqliteCommand cmd, GalleryFilterState filter)
    {
        if (filter.EffectiveMinRating > 0)
        {
            cmd.Parameters.AddWithValue("$minRating", filter.EffectiveMinRating);
        }

        BindPlaceParams(cmd, filter); // ADR-0050
    }

    /// <summary>
    /// Zahl der Dateien nur mit Ordnerfilter (ohne Datei-, Tag-, Personen-, Kategorie- und Suchfilter) — Bezugsgröße der
    /// Trefferzeile „342 von 1.248 Dateien“. Zählt über <c>IX_Files_Library_Folder</c>.
    /// </summary>
    public Task<int> GetUnfilteredCountAsync(GalleryQuery query, CancellationToken ct)
    {
        var baseQuery = new GalleryQuery(query.LibraryId, GalleryFilterState.None, null, query.FolderId, query.IncludeSubfolders)
        {
            LibraryIds = query.LibraryIds, // Bereich bleibt (ADR-0048)
        };
        return GetTotalCountAsync(baseQuery, ct);
    }
}
