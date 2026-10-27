using Lumora.Domain.Enums;

namespace Lumora.Domain.Entities;

/// <summary>
/// GPS-Spur einer Aktivität (ADR-0062, Tabelle "GpsTracks"): eingelesen aus dem Garmin-Datenexport, aus FIT-/GPX-/TCX-
/// Dateien oder von der Uhr. Gehört wie Tags und Alben einer Bibliotheks-Gruppe (<see cref="GroupId"/> ohne Fremdschlüssel,
/// ADR-0048). Die Punkte stehen kompakt in <see cref="Points"/> (delta-kodiert, komprimiert — <c>TrackCodec</c>), nicht als
/// eine Zeile je Punkt; <see cref="Checksum"/> (SHA-256 der kodierten Punkte) verhindert doppeltes Einlesen je Gruppe.
/// </summary>
public sealed class GpsTrack
{
    public int Id { get; set; }

    /// <summary>Bibliotheks-Gruppe (ADR-0048); 0 = ohne Bibliothek eingelesen, die nächste Bibliothek übernimmt sie.</summary>
    public int GroupId { get; set; }

    /// <summary>Erster Punkt (UTC).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Letzter Punkt (UTC).</summary>
    public DateTime EndUtc { get; set; }

    public double MinLatitude { get; set; }

    public double MinLongitude { get; set; }

    public double MaxLatitude { get; set; }

    public double MaxLongitude { get; set; }

    public int PointCount { get; set; }

    /// <summary>Länge der Spur in Metern (aus den Punkten berechnet).</summary>
    public double DistanceMeters { get; set; }

    /// <summary>Sportart in Worten („Wandern“), null wenn unbekannt.</summary>
    public string? Sport { get; set; }

    /// <summary>Gerät („Fenix 7“), null wenn unbekannt.</summary>
    public string? Device { get; set; }

    /// <summary>Name der Aktivität (GPX/TCX), null ohne.</summary>
    public string? Name { get; set; }

    public GpsTrackSource Source { get; set; }

    /// <summary>SHA-256 der kodierten Punkte (je Gruppe eindeutig).</summary>
    public required byte[] Checksum { get; set; }

    /// <summary>Punkte, kodiert mit <c>TrackCodec</c> (Version im ersten Byte).</summary>
    public required byte[] Points { get; set; }

    public DateTime ImportedAtUtc { get; set; }

    /// <summary>Tour einer verbundenen App, aus der die Spur stammt (<see cref="AppTour"/>); null bei eigenem Einlesen.</summary>
    public int? AppTourId { get; set; }
}
