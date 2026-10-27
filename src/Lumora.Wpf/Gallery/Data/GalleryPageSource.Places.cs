using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Lumora.Gallery.Data;

/// <summary>
/// Orte in der Galerie-SQL (ADR-0050): Filter „Ort“ (<see cref="GalleryPlaceFilter"/>) und „Kartenausschnitt“
/// (<see cref="GalleryGeoFilter"/>) als UND-Bedingungen des Datei-Teils (<see cref="GalleryFilterState"/>). Ids werden als
/// Zahlen-Literale eingesetzt (nur ganze Zahlen aus dem Katalog bzw. der Karte — keine Benutzereingabe, kein
/// Parameterlimit bei großen Kartengruppen); der Ausschnitt läuft über Parameter.
/// </summary>
public sealed partial class GalleryPageSource
{
    /// <summary>Bedingungen für Ort und Kartenausschnitt, je eine „AND …“-Zeile (leer ohne).</summary>
    internal static IEnumerable<string> BuildPlaceClauses(GalleryFilterState filter)
    {
        if (filter.Place is { IsActive: true } place)
        {
            var parts = new List<string>(3);
            if (place.PlaceIds.Count > 0)
            {
                parts.Add($"f.PlaceId IN ({Literals(place.PlaceIds)})");
            }

            if (place.IncludeWithoutLocation)
            {
                parts.Add("f.Latitude IS NULL");
            }

            if (place.IncludeWithoutPlaceName)
            {
                parts.Add("(f.Latitude IS NOT NULL AND f.PlaceId IS NULL)");
            }

            yield return $"AND ({string.Join(" OR ", parts)})";
        }

        if (filter.Tour is { } tour)
        {
            // Tour (ADR-0073 Stufe 7): die beim Wählen bestimmten Dateien; ohne Treffer nichts
            yield return tour.FileIds.Count == 0 ? "AND 0" : $"AND f.Id IN ({Literals(tour.FileIds)})";
        }

        if (filter.Geo is { IsActive: true } geo)
        {
            if (UsesIdList(geo) && geo.FileIds is { } ids)
            {
                yield return $"AND f.Id IN ({Literals(ids)})";
            }
            else if (geo.Bounds is { } b)
            {
                var lon = b.West <= b.East ? "f.Longitude BETWEEN $geoW AND $geoE" : "(f.Longitude >= $geoW OR f.Longitude <= $geoE)";
                yield return $"AND f.Latitude BETWEEN $geoS AND $geoN AND {lon}";
            }
        }
    }

    private static void BindPlaceParams(SqliteCommand cmd, GalleryFilterState filter)
    {
        if (filter.Geo is { IsActive: true } geo && !UsesIdList(geo) && geo.Bounds is { } b)
        {
            cmd.Parameters.AddWithValue("$geoS", b.South);
            cmd.Parameters.AddWithValue("$geoN", b.North);
            cmd.Parameters.AddWithValue("$geoW", b.West);
            cmd.Parameters.AddWithValue("$geoE", b.East);
        }
    }

    /// <summary>Höchstens so viele Ids als Literal-Liste; größere Kartengruppen laufen über ihren Ausschnitt (SQL-Text bleibt klein).</summary>
    internal const int MaxLiteralIds = 20_000;

    /// <summary>Kartengruppe über ihre Ids (bis <see cref="MaxLiteralIds"/> bzw. ohne Ausschnitt), sonst über den Ausschnitt.</summary>
    private static bool UsesIdList(GalleryGeoFilter geo)
        => geo.FileIds is { Count: > 0 } ids && (ids.Count <= MaxLiteralIds || geo.Bounds is null);

    private static string Literals(IEnumerable<int> ids)
        => string.Join(",", ids.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));
}
