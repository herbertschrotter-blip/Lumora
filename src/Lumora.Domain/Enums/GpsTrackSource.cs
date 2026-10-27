namespace Lumora.Domain.Enums;

/// <summary>Dateiformat, aus dem eine GPS-Spur eingelesen wurde (Spalte <c>GpsTracks.Source</c>, ADR-0062).</summary>
public enum GpsTrackSource
{
    Unknown = 0,

    /// <summary>Garmin FIT (Datenexport, Uhr per USB, beobachteter Ordner).</summary>
    Fit = 1,

    /// <summary>GPX 1.0/1.1 (<c>trkpt</c> mit Zeit).</summary>
    Gpx = 2,

    /// <summary>Garmin TCX (<c>Trackpoint</c> mit Position).</summary>
    Tcx = 3,

    /// <summary>Aus einer verbundenen App (Gipfelbuch-Tour, App-Verbindung Stufe 7) — hat bei derselben Aktivität Vorrang.</summary>
    App = 4,
}
