using Lumora.Domain.Entities;
using Lumora.Domain.Enums;

namespace Lumora.Services.Merge;

/// <summary>
/// Zeit und Ort einer Quelle, die frisch aus der Datei gelesen wurden (ADR-0078 Punkt 10): lag die Quelle bis eben nur in
/// OneDrive, fehlen sie im Katalog noch (ADR-0077: GPS erst, wenn die Datei lokal ist). Null-Felder = nichts gelesen.
/// </summary>
public sealed record MergeSourceMetadata(
    DateTime? CapturedAt = null,
    CaptureDateSource? CapturedAtSource = null,
    CaptureDatePrecision CapturedAtPrecision = CaptureDatePrecision.Exact,
    int? CapturedAtOffset = null,
    double? Latitude = null,
    double? Longitude = null,
    GeoAccuracy? GeoAccuracy = null,
    GeoAccuracyReason? GeoAccuracyReason = null,
    DateTime? GpsTimeUtc = null);

/// <summary>Welche Quelle Zeit, Ort und Kamera des Ergebnisses liefert (FileIds; null = keine).</summary>
public sealed record MergeMetadataChoice(int? TimeSourceId, int? LocationSourceId, int? CameraSourceId, byte? Rating, bool IsFavorite)
{
    /// <summary>Stelle (0-basiert) der Ortsquelle in der Auswahl, für „Ort: Bild 3 von 26 (erstes mit Ort)“.</summary>
    public int? LocationPosition { get; init; }

    /// <summary>Stelle der Zeitquelle.</summary>
    public int? TimePosition { get; init; }
}

/// <summary>
/// Metadaten des Ergebnisses (ADR-0078 Punkt 10 und Abnahme Nr. 12), rein und ohne Datenbank — dieselbe Wahl trifft der
/// Merge-Dienst für den EXIF-Block der Datei und <c>CreateMergedAsync</c> für die Katalogzeile:
/// <list type="bullet">
/// <item>Zeit: die früheste Quelle, die eine Aufnahmezeit hat (Quellen ohne Zeit zählen nicht; keine = leer, nie das
/// Erstelldatum der Ergebnisdatei). Eine vorläufige OneDrive-Zeit im Katalog weicht der frisch aus der Datei gelesenen.</item>
/// <item>Ort: die erste Quelle mit Ort — echtes GPS vor in Lumora gesetztem bzw. unbewertetem, das vor geschätztem; bei
/// gleicher Güte die früheste. Maßgeblich sind die Katalogwerte, ergänzt um frisch gelesene Werte.</item>
/// <item>Kamera: die erste Quelle mit Kamera.</item>
/// <item>Bewertung: höchste Sternezahl, Favorit, wenn mindestens eine Quelle Favorit ist (die gemeinsamen Tags bucht SQL).</item>
/// </list>
/// </summary>
public static class MergeMetadataPicker
{
    /// <param name="sources">Quellen in der Reihenfolge der Auswahl (Position 0 = erstes Einzelbild).</param>
    public static MergeMetadataChoice Pick(IReadOnlyList<MediaFile> sources, IReadOnlyDictionary<int, MergeSourceMetadata>? fromFiles = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        int? timeId = null, timePos = null;
        DateTime? best = null;
        for (var i = 0; i < sources.Count; i++)
        {
            var t = TimeOf(sources[i], fromFiles);
            if (t is { } value && (best is null || value < best))
            {
                best = value;
                timeId = sources[i].Id;
                timePos = i;
            }
        }

        int? locId = null, locPos = null;
        var bestRank = int.MaxValue;
        for (var i = 0; i < sources.Count; i++)
        {
            var (lat, _, accuracy) = LocationOf(sources[i], fromFiles);
            if (lat is null)
            {
                continue;
            }

            var rank = accuracy switch
            {
                GeoAccuracy.Exact => 0,
                GeoAccuracy.Estimated => 2,
                _ => 1,
            };
            if (rank < bestRank)
            {
                bestRank = rank;
                locId = sources[i].Id;
                locPos = i;
            }
        }

        var camera = sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.CameraModel))?.Id;
        var rating = sources.Where(s => s.Rating is not null).Select(s => s.Rating).DefaultIfEmpty(null).Max();
        var favorite = sources.Any(s => s.IsFavorite);
        return new MergeMetadataChoice(timeId, locId, camera, rating, favorite) { LocationPosition = locPos, TimePosition = timePos };
    }

    /// <summary>Setzt Zeit, Ort, Kamera und Bewertung der neuen Katalogzeile nach <paramref name="choice"/>.</summary>
    public static void Apply(MediaFile result, IReadOnlyList<MediaFile> sources, MergeMetadataChoice choice, IReadOnlyDictionary<int, MergeSourceMetadata>? fromFiles, bool copyRating)
    {
        ArgumentNullException.ThrowIfNull(result);
        var byId = sources.ToDictionary(s => s.Id);
        if (choice.TimeSourceId is { } tid && byId.TryGetValue(tid, out var ts))
        {
            if (FreshTime(ts, fromFiles) is { } f)
            {
                result.CapturedAt = f.CapturedAt;
                result.CapturedAtSource = f.CapturedAtSource ?? CaptureDateSource.Exif;
                result.CapturedAtPrecision = f.CapturedAtPrecision;
                result.CapturedAtOffset = f.CapturedAtOffset;
            }
            else if (ts.CapturedAt is not null)
            {
                result.CapturedAt = ts.CapturedAt;
                result.CapturedAtSource = ts.CapturedAtSource;
                result.CapturedAtPrecision = ts.CapturedAtPrecision;
                result.CapturedAtOffset = ts.CapturedAtOffset;
                result.ExifTimeOffset = ts.ExifTimeOffset;
            }
        }
        else
        {
            // keine Quelle mit Zeit: „gelesen, kein Datum“ — nie das Erstelldatum der Ergebnisdatei
            result.CapturedAt = null;
            result.CapturedAtSource = CaptureDateSource.Unknown;
        }

        if (choice.LocationSourceId is { } lid && byId.TryGetValue(lid, out var ls))
        {
            if (ls.Latitude is not null && ls.Longitude is not null)
            {
                result.Latitude = ls.Latitude;
                result.Longitude = ls.Longitude;
                result.GeoSource = ls.GeoSource;
                result.PlaceId = ls.PlaceId;
                result.GeoAccuracy = ls.GeoAccuracy;
                result.GeoAccuracyReason = ls.GeoAccuracyReason;
                result.GpsTimeUtc = ls.GpsTimeUtc;
            }
            else if (fromFiles?.GetValueOrDefault(lid) is { Latitude: not null, Longitude: not null } f)
            {
                result.Latitude = f.Latitude;
                result.Longitude = f.Longitude;
                result.GeoSource = GeoSource.Exif;
                result.GeoAccuracy = f.GeoAccuracy;
                result.GeoAccuracyReason = f.GeoAccuracyReason;
                result.GpsTimeUtc = f.GpsTimeUtc;
            }
        }
        else
        {
            // keine Quelle mit Ort: „gelesen, keine Koordinaten“ (kein Raten aus Nachbarfotos)
            result.Latitude = null;
            result.Longitude = null;
            result.GeoSource = GeoSource.None;
            result.GeoAccuracy = GeoAccuracy.Unknown;
        }

        if (choice.CameraSourceId is { } cid && byId.TryGetValue(cid, out var cs))
        {
            result.CameraModel = cs.CameraModel;
        }

        if (copyRating)
        {
            result.Rating = choice.Rating;
            result.IsFavorite = choice.IsFavorite;
        }
    }

    /// <summary>
    /// Maßgebliche Aufnahmezeit einer Quelle (UTC und Zeitzonen-Versatz): frisch aus der Datei gelesen, wenn der Katalog keine
    /// oder nur die vorläufige OneDrive-Zeit hat (<see cref="CaptureDateSource.Cloud"/>), sonst der Katalogwert.
    /// </summary>
    public static (DateTime? Utc, int? Offset) EffectiveTime(MediaFile f, IReadOnlyDictionary<int, MergeSourceMetadata>? fromFiles)
    {
        ArgumentNullException.ThrowIfNull(f);
        return FreshTime(f, fromFiles) is { } fresh ? (fresh.CapturedAt, fresh.CapturedAtOffset) : (f.CapturedAt, f.CapturedAtOffset);
    }

    private static MergeSourceMetadata? FreshTime(MediaFile f, IReadOnlyDictionary<int, MergeSourceMetadata>? fromFiles)
        => (f.CapturedAt is null || f.CapturedAtSource == CaptureDateSource.Cloud) && fromFiles?.GetValueOrDefault(f.Id) is { CapturedAt: not null } m ? m : null;

    private static DateTime? TimeOf(MediaFile f, IReadOnlyDictionary<int, MergeSourceMetadata>? fromFiles)
        => EffectiveTime(f, fromFiles).Utc;

    private static (double? Lat, double? Lon, GeoAccuracy? Accuracy) LocationOf(MediaFile f, IReadOnlyDictionary<int, MergeSourceMetadata>? fromFiles)
    {
        if (f.Latitude is not null && f.Longitude is not null)
        {
            return (f.Latitude, f.Longitude, f.GeoAccuracy);
        }

        return fromFiles?.GetValueOrDefault(f.Id) is { Latitude: not null, Longitude: not null } m ? (m.Latitude, m.Longitude, m.GeoAccuracy) : (null, null, null);
    }
}
