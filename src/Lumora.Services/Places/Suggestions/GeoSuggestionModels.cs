using System.Globalization;
using Lumora.Domain.Enums;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Tracks;

namespace Lumora.Services.Places.Suggestions;

/// <summary>Welche Fotos die Liste zeigt.</summary>
public enum GeoSuggestionListKind
{
    /// <summary>Fotos mit geschätzter Position (EXIF, <c>GeoAccuracy = 2</c>).</summary>
    Inaccurate = 0,

    /// <summary>Fotos und Videos ohne Ort (gelesen, keine Koordinaten).</summary>
    Unlocated = 1,
}

/// <summary>Ein Foto der Liste samt Vorschlag (null = kein Vorschlag).</summary>
/// <param name="LocalTime">Wanduhrzeit der Aufnahme (wie fotografiert), null ohne Aufnahmedatum.</param>
/// <param name="Utc">Aufnahme nach dem Zeitabgleich (Unix-Sekunden), null ohne.</param>
/// <param name="Track">Spur des Vorschlags, sonst die des Vergleichs bzw. des Uhr-Hinweises.</param>
/// <param name="Sources">Alle Quellen nebeneinander (Mockup 25), null ohne Spur und genaue Fotos in der Nähe.</param>
public sealed record GeoSuggestionItem(
    int FileId,
    int LibraryId,
    string Name,
    int MediaType,
    DateTime? LocalTime,
    long? Utc,
    PhotoTimeBasis? TimeBasis,
    double? StoredLatitude,
    double? StoredLongitude,
    GeoAccuracyReason Reason,
    GeoSuggestion? Suggestion,
    GpsTrackInfo? Track,
    GeoSourceComparison? Sources = null)
{
    public GeoSuggestionLevel Level => Suggestion?.Level ?? GeoSuggestionLevel.None;

    public bool HasStored => StoredLatitude is not null && StoredLongitude is not null;

    /// <summary>Abstand gespeicherter Punkt → Vorschlag in Metern (null ohne einen der beiden).</summary>
    public double? CorrectionMeters => Suggestion is { } s && StoredLatitude is { } lat && StoredLongitude is { } lon
        ? GeoMath.DistanceMeters(lat, lon, s.Latitude, s.Longitude)
        : null;

    /// <summary>Herkunft beim Übernehmen.</summary>
    public GeoSource SourceOnApply => Suggestion?.Kind == GeoSuggestionKind.Track ? GeoSource.Track : GeoSource.Neighbors;
}

/// <summary>Ergebnis einer Abfrage: Fotos (neueste zuerst) und Zähler je Stufe.</summary>
public sealed record GeoSuggestionSet(GeoSuggestionListKind Kind, IReadOnlyList<GeoSuggestionItem> Items, int TrackCount)
{
    public int Count(GeoSuggestionLevel level) => Items.Count(i => i.Level == level);

    public int WithSuggestion => Items.Count(i => i.Suggestion is not null);

    public static GeoSuggestionSet Empty(GeoSuggestionListKind kind) => new(kind, [], 0);
}

/// <summary>Zähler für Kopfzeile und Statusleiste (billig, ohne Vorschläge zu rechnen).</summary>
public sealed record GeoAccuracyCounts(int Estimated, int Unlocated, int Activities);

/// <summary>Texte der Vorschläge (Liste, Tooltips, Lightbox).</summary>
public static class GeoSuggestionText
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Kurzname der Stufe („Garmin-Spur“, „sicher“, „wahrscheinlich“, „kein Vorschlag“).</summary>
    public static string LevelName(GeoSuggestionLevel level) => level switch
    {
        GeoSuggestionLevel.Track => "Garmin-Spur",
        GeoSuggestionLevel.Sure => "sicher",
        GeoSuggestionLevel.Likely => "wahrscheinlich",
        _ => "kein Vorschlag",
    };

    /// <summary>Quelle in Worten: „Garmin-Spur 14:56:10“, „zwischen Fotos 14:41 und 15:02“, „Foto 16:05 (35 min davor)“.</summary>
    public static string Source(GeoSuggestionItem item, int displayOffsetMinutes)
    {
        if (item.Suggestion is not { } s || item.Utc is not { } t)
        {
            return item.Utc is null ? "ohne Aufnahmezeit" : "kein genaues Foto und keine Spur innerhalb 30 min";
        }

        string Clock(long unix, string format) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.AddMinutes(displayOffsetMinutes).ToString(format, De);
        switch (s.Kind)
        {
            case GeoSuggestionKind.Track:
                var name = item.Track?.Device is { } device ? $"Spur ({device})" : "Garmin-Spur";
                var gap = s.GapSeconds is > GeoSuggestionEngine.TrackShortGapSeconds ? $", Lücke {s.GapSeconds / 60} min" : string.Empty;
                return $"{name} {Clock(t, "HH:mm:ss")}{gap}";
            case GeoSuggestionKind.Interpolated when s.AnchorBefore is { } b && s.AnchorAfter is { } a:
                return $"zwischen Fotos {Clock(b.Utc, "HH:mm")} und {Clock(a.Utc, "HH:mm")}";
            default:
                var n = s.AnchorBefore ?? s.AnchorAfter;
                if (n is not { } anchor)
                {
                    return "Nachbarfoto";
                }

                var minutes = Math.Max(1, (int)Math.Round(Math.Abs(t - anchor.Utc) / 60.0));
                return $"Foto {Clock(anchor.Utc, "HH:mm")} ({minutes} min {(anchor.Utc <= t ? "davor" : "danach")})";
        }
    }

    /// <summary>Zweite Zeile der Liste: „ohne GPS-Höhe/-Zeit → 4,8 km korrigieren · Fenix“.</summary>
    public static string Detail(GeoSuggestionItem item, int displayOffsetMinutes)
    {
        var why = item.HasStored ? GeoAccuracyRules.Describe(item.Reason) : "ohne Ort";
        if (item.Suggestion is null)
        {
            return $"{why} · {Source(item, displayOffsetMinutes)}";
        }

        var correction = item.CorrectionMeters is { } m ? $" → {GeoMath.FormatDistance(m)} korrigieren" : " → Vorschlag";
        var device = item.Suggestion.Kind == GeoSuggestionKind.Track && item.Track?.Device is { } d ? " · " + d : string.Empty;
        return why + correction + device;
    }
}
