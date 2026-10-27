using Lumora.Services.Places.Tracks;

namespace Lumora.Services.Places.Suggestions;

/// <summary>Stufe eines Vorschlags (höher = besser; Reihenfolge der Filter-Chips).</summary>
public enum GeoSuggestionLevel
{
    None = 0,

    /// <summary>„wahrscheinlich“: ein genaues Nachbarfoto wenige Minuten entfernt bzw. Spur mit langer Lücke.</summary>
    Likely = 1,

    /// <summary>„sicher“: zwischen zwei genauen Fotos (≤ 30 min, plausible Geschwindigkeit) interpoliert.</summary>
    Sure = 2,

    /// <summary>„Garmin-Spur“: Foto-Zeit liegt in einer Aktivität, zwischen Spurpunkten mit kurzer Lücke (bzw. in einer Pause).</summary>
    Track = 3,
}

/// <summary>Woraus ein Vorschlag stammt.</summary>
public enum GeoSuggestionKind
{
    None = 0,
    Track = 1,
    Interpolated = 2,
    Neighbor = 3,
}

/// <summary>Foto, für das ein Ort gesucht wird.</summary>
public readonly record struct SuggestionCandidate(int FileId, long Utc);

/// <summary>Foto mit echtem GPS (bzw. aus einer Spur übernommenem Ort) als Stütze.</summary>
public readonly record struct SuggestionAnchor(int FileId, long Utc, double Latitude, double Longitude);

/// <summary>Spur mit Zeitraum; Punkte werden erst bei Bedarf geladen (<see cref="LoadPoints"/>, zwischengespeichert).</summary>
/// <param name="preferred">Spur aus einer verbundenen App (Gipfelbuch-Tour): bei gleicher Stufe Vorrang vor einer eigenen
/// Spur derselben Aktivität (Herbert, ADR-0073 Frage 6).</param>
public sealed class SuggestionTrack(int id, long startUtc, long endUtc, Func<TrackPoint[]> loadPoints, bool preferred = false)
{
    private TrackPoint[]? _points;

    public int Id { get; } = id;

    public bool Preferred { get; } = preferred;

    public long StartUtc { get; } = startUtc;

    public long EndUtc { get; } = endUtc;

    public TrackPoint[] LoadPoints() => _points ??= loadPoints();
}

/// <summary>Ein Vorschlag.</summary>
/// <param name="GapSeconds">Spur: Lücke zwischen den umgebenden Punkten bzw. Abstand zum Spur-Ende.</param>
/// <param name="AnchorBefore">Nachbarfoto davor (Interpolation bzw. nächstes Foto).</param>
/// <param name="AnchorAfter">Nachbarfoto danach.</param>
/// <param name="Altitude">Spur: Höhe des Spurpunkts (Meter), falls bekannt.</param>
public sealed record GeoSuggestion(
    int FileId,
    GeoSuggestionLevel Level,
    GeoSuggestionKind Kind,
    double Latitude,
    double Longitude,
    int? TrackId = null,
    long? GapSeconds = null,
    SuggestionAnchor? AnchorBefore = null,
    SuggestionAnchor? AnchorAfter = null,
    double? Altitude = null);

/// <summary>
/// Vorschläge für Fotos mit geschätzter Position bzw. ohne Ort (ADR-0062, rein und getestet). Rangfolge: GPS-Spur vor
/// Interpolation zwischen genauen Fotos vor einem einzelnen Nachbarfoto. Die Konstanten sind im ADR begründet.
/// </summary>
public static class GeoSuggestionEngine
{
    /// <summary>Spur: Lücke bis hierhin gilt als „kurz“ (Garmin zeichnet je 1–10 s auf; zu Fuß ≤ ~170 m Fehler).</summary>
    public const long TrackShortGapSeconds = 120;

    /// <summary>Spur: längere Lücken bis hierhin ergeben „wahrscheinlich“ (Auto-Pause, Tunnel, Akku).</summary>
    public const long TrackLongGapSeconds = 15 * 60;

    /// <summary>Spur: liegen die Punkte um eine lange Lücke so nah beieinander, war es eine Pause am selben Ort → „Garmin-Spur“.</summary>
    public const double TrackPauseMeters = 150;

    /// <summary>Interpolation: höchstens so lange zwischen den beiden genauen Fotos.</summary>
    public const long SureSpanSeconds = 30 * 60;

    /// <summary>Interpolation: höchstens so schnell zwischen den beiden genauen Fotos (wie die Sprung-Regel).</summary>
    public const double MaxSpeedKmh = 150;

    /// <summary>Ein einzelnes genaues Nachbarfoto zählt bis zu diesem Abstand als „wahrscheinlich“.</summary>
    public const long LikelyNeighborSeconds = 15 * 60;

    /// <summary>Längste berücksichtigte Aktivität (mehrtägige Touren); begrenzt die Suche rückwärts.</summary>
    public const long MaxTrackSeconds = 14L * 86_400;

    /// <summary>Vorschläge für alle Kandidaten (Ergebnis nur für Kandidaten mit Vorschlag).</summary>
    /// <param name="anchors">Nach Zeit sortiert.</param>
    /// <param name="tracks">Nach Start sortiert.</param>
    /// <param name="toleranceSeconds">Fotos so lange vor/nach einer Aktivität zählen noch (Einstellung, Standard 2 min).</param>
    public static Dictionary<int, GeoSuggestion> Suggest(
        IReadOnlyList<SuggestionCandidate> candidates,
        IReadOnlyList<SuggestionAnchor> anchors,
        IReadOnlyList<SuggestionTrack> tracks,
        long toleranceSeconds)
    {
        var result = new Dictionary<int, GeoSuggestion>(candidates.Count);
        var anchorTimes = anchors.Select(a => a.Utc).ToArray();
        var trackStarts = tracks.Select(t => t.StartUtc).ToArray();
        foreach (var candidate in candidates)
        {
            var fromTrack = FromTracks(candidate, tracks, trackStarts, toleranceSeconds);
            if (fromTrack is { Level: GeoSuggestionLevel.Track })
            {
                result[candidate.FileId] = fromTrack;
                continue;
            }

            var fromPhotos = FromAnchors(candidate, anchors, anchorTimes);
            var best = (fromTrack?.Level ?? GeoSuggestionLevel.None) >= (fromPhotos?.Level ?? GeoSuggestionLevel.None) ? fromTrack : fromPhotos;
            if (best is not null)
            {
                result[candidate.FileId] = best;
            }
        }

        return result;
    }

    /// <summary>Bester Vorschlag aus den Spuren, die die Foto-Zeit (± Toleranz) abdecken.</summary>
    public static GeoSuggestion? FromTracks(SuggestionCandidate candidate, IReadOnlyList<SuggestionTrack> tracks, long[] trackStarts, long toleranceSeconds)
    {
        if (tracks.Count == 0)
        {
            return null;
        }

        var t = candidate.Utc;
        GeoSuggestion? best = null;
        var bestPreferred = false;
        // Spuren mit Start ≤ t + Toleranz; die mit Ende ≥ t − Toleranz zählen
        var upper = UpperBound(trackStarts, t + toleranceSeconds);
        for (var i = upper - 1; i >= 0; i--)
        {
            var track = tracks[i];
            if (track.StartUtc < t - MaxTrackSeconds)
            {
                break; // länger dauert keine Aktivität — noch frühere können nicht mehr passen
            }

            if (track.EndUtc < t - toleranceSeconds)
            {
                continue;
            }

            var suggestion = AtTime(candidate.FileId, track, t);
            if (suggestion is not null && (best is null || Better(suggestion, track.Preferred, best, bestPreferred)))
            {
                best = suggestion;
                bestPreferred = track.Preferred;
            }
        }

        return best;
    }

    /// <summary>Höhere Stufe gewinnt; bei gleicher Stufe die Spur der verbundenen App, sonst die kleinere Lücke.</summary>
    private static bool Better(GeoSuggestion candidate, bool candidatePreferred, GeoSuggestion best, bool bestPreferred)
        => candidate.Level != best.Level ? candidate.Level > best.Level
            : candidatePreferred != bestPreferred ? candidatePreferred
            : candidate.GapSeconds < best.GapSeconds;

    /// <summary>Position der Spur zur Zeit <paramref name="t"/> (interpoliert) samt Stufe; null bei zu großer Lücke.</summary>
    public static GeoSuggestion? AtTime(int fileId, SuggestionTrack track, long t)
    {
        var points = track.LoadPoints();
        if (points.Length == 0)
        {
            return null;
        }

        TrackPoint a, b;
        if (t <= points[0].Utc)
        {
            a = b = points[0];
        }
        else if (t >= points[^1].Utc)
        {
            a = b = points[^1];
        }
        else
        {
            var lo = 0;
            var hi = points.Length - 1;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (points[mid].Utc <= t)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            a = points[lo];
            b = points[hi];
        }

        long gap;
        double lat, lon;
        double? alt;
        if (a.Utc == b.Utc)
        {
            gap = Math.Abs(t - a.Utc); // vor dem Start bzw. nach dem Ende: Abstand zum Endpunkt
            (lat, lon, alt) = (a.Latitude, a.Longitude, a.Altitude);
        }
        else
        {
            gap = b.Utc - a.Utc;
            var fraction = (double)(t - a.Utc) / gap;
            (lat, lon) = GeoMath.Interpolate(a.Latitude, a.Longitude, b.Latitude, b.Longitude, fraction);
            alt = a.Altitude is { } aa && b.Altitude is { } ba ? aa + ((ba - aa) * fraction) : a.Altitude ?? b.Altitude;
        }

        GeoSuggestionLevel level;
        if (gap <= TrackShortGapSeconds)
        {
            level = GeoSuggestionLevel.Track;
        }
        else if (gap <= TrackLongGapSeconds)
        {
            var paused = a.Utc != b.Utc && GeoMath.DistanceMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude) <= TrackPauseMeters;
            level = paused ? GeoSuggestionLevel.Track : GeoSuggestionLevel.Likely;
        }
        else
        {
            return null;
        }

        return new GeoSuggestion(fileId, level, GeoSuggestionKind.Track, lat, lon, track.Id, gap, Altitude: alt);
    }

    /// <summary>Vorschlag aus genauen Nachbarfotos (Interpolation bzw. nächstes Foto).</summary>
    public static GeoSuggestion? FromAnchors(SuggestionCandidate candidate, IReadOnlyList<SuggestionAnchor> anchors, long[] anchorTimes)
    {
        if (anchors.Count == 0)
        {
            return null;
        }

        var t = candidate.Utc;
        // letztes Foto ≤ t und erstes ≥ t (das Foto selbst ist nie Stütze)
        var index = UpperBound(anchorTimes, t);
        SuggestionAnchor? before = null, after = null;
        for (var i = index - 1; i >= 0; i--)
        {
            if (anchors[i].FileId != candidate.FileId)
            {
                before = anchors[i];
                break;
            }
        }

        for (var i = LowerBound(anchorTimes, t); i < anchors.Count; i++)
        {
            if (anchors[i].FileId != candidate.FileId && (before is null || anchors[i].FileId != before.Value.FileId || anchors[i].Utc > t))
            {
                after = anchors[i];
                break;
            }
        }

        if (before is { } b && after is { } a && a.Utc - b.Utc <= SureSpanSeconds)
        {
            var meters = GeoMath.DistanceMeters(b.Latitude, b.Longitude, a.Latitude, a.Longitude);
            if (GeoMath.SpeedKmh(meters, Math.Max(60, a.Utc - b.Utc)) <= MaxSpeedKmh || meters < 500)
            {
                var span = a.Utc - b.Utc;
                var fraction = span == 0 ? 0 : (double)(t - b.Utc) / span;
                var (lat, lon) = GeoMath.Interpolate(b.Latitude, b.Longitude, a.Latitude, a.Longitude, fraction);
                return new GeoSuggestion(candidate.FileId, GeoSuggestionLevel.Sure, GeoSuggestionKind.Interpolated, lat, lon, AnchorBefore: b, AnchorAfter: a);
            }
        }

        SuggestionAnchor? nearest = null;
        if (before is { } b2 && t - b2.Utc <= LikelyNeighborSeconds)
        {
            nearest = b2;
        }

        if (after is { } a2 && a2.Utc - t <= LikelyNeighborSeconds && (nearest is null || a2.Utc - t < t - nearest.Value.Utc))
        {
            nearest = a2;
        }

        return nearest is { } n
            ? new GeoSuggestion(candidate.FileId, GeoSuggestionLevel.Likely, GeoSuggestionKind.Neighbor, n.Latitude, n.Longitude,
                AnchorBefore: n.Utc <= t ? n : null, AnchorAfter: n.Utc > t ? n : null)
            : null;
    }

    /// <summary>Erster Index mit Wert &gt; <paramref name="value"/>.</summary>
    private static int UpperBound(long[] sorted, long value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Erster Index mit Wert ≥ <paramref name="value"/>.</summary>
    private static int LowerBound(long[] sorted, long value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
