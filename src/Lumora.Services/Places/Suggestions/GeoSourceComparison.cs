namespace Lumora.Services.Places.Suggestions;

/// <summary>
/// Keine Spur zur Aufnahmezeit, aber eine in der Nähe (Mockup 25): so weit müsste die Foto-Zeit verschoben werden, damit
/// sie die Spur trifft — Hinweis auf eine falsch gehende Kamerauhr.
/// </summary>
/// <param name="OffsetSeconds">Spur-Zeit minus Foto-Zeit: positiv = Kamera geht nach, negativ = Kamera geht vor.</param>
/// <param name="Latitude">Spur-Anfang bzw. -Ende, das der Foto-Zeit am nächsten liegt.</param>
public readonly record struct TrackClockHint(int TrackId, long OffsetSeconds, double Latitude, double Longitude);

/// <summary>
/// Alle Quellen eines Fotos nebeneinander (Mockup 25): laut Spur (auch in einer langen Lücke, dann nur geschätzt),
/// laut Zeit (zwischen bzw. neben genauen Fotos) und — ohne Spur zur Aufnahmezeit — der Uhr-Hinweis.
/// Die gespeicherte Position („laut Foto“) steht im <see cref="GeoSuggestionItem"/>.
/// </summary>
/// <param name="FromTrack">Ort laut Spur; <see cref="GeoSuggestionLevel.None"/> = Lücke über 15 min, nur geradlinig geschätzt.</param>
/// <param name="FromTime">Ort laut Zeit (Interpolation oder nächstes genaues Foto).</param>
/// <param name="NearTrack">Spur innerhalb von <see cref="GeoSuggestionEngine.NearTrackSeconds"/>, wenn keine die Zeit abdeckt.</param>
public sealed record GeoSourceComparison(GeoSuggestion? FromTrack, GeoSuggestion? FromTime, TrackClockHint? NearTrack)
{
    /// <summary>Weiter auseinander gelten Spur und Zeit als uneinig (Filter „Quellen uneinig“).</summary>
    public const double DisagreeMeters = 300;

    /// <summary>Spur nur über eine lange Lücke geschätzt.</summary>
    public bool TrackInGap => FromTrack is { Level: GeoSuggestionLevel.None };

    /// <summary>Abstand Spur ↔ Zeit in Metern (null ohne eine der beiden).</summary>
    public double? TrackToTimeMeters => FromTrack is { } t && FromTime is { } z
        ? GeoMath.DistanceMeters(t.Latitude, t.Longitude, z.Latitude, z.Longitude)
        : null;

    /// <summary>Spur und Zeit liegen weiter als <see cref="DisagreeMeters"/> auseinander.</summary>
    public bool Disagree => TrackToTimeMeters > DisagreeMeters;
}
