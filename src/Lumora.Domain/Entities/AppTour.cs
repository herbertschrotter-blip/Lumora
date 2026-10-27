namespace Lumora.Domain.Entities;

/// <summary>
/// Tour aus einer verbundenen App (App-Verbindung Stufe 7, ADR-0073; Vertrag 1.1 Abschnitt 8.2): der Kopf einer Tour des
/// Gipfelbuchs, auch ohne Spur — Grundlage für den Galerie-Filter „Tour“ und den Stand im Reiter „Verbundene Apps“. Die
/// Spur selbst liegt je Bibliotheks-Gruppe in <see cref="GpsTrack"/> (<see cref="GpsTrack.AppTourId"/>). Gilt für den
/// ganzen Katalog (Touren gehören keiner Bibliothek); verschwindet die Tour drüben, löscht der Abgleich sie hier.
/// </summary>
public sealed class AppTour
{
    public int Id { get; set; }

    /// <summary>App-Kennung der Quelle (<c>gipfelbuch</c>).</summary>
    public required string AppId { get; set; }

    /// <summary>Kennung der Tour in der Quelle — undurchsichtiger Text (Vertrag Abschnitt 10).</summary>
    public required string ExternalId { get; set; }

    public required string Name { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    /// <summary>Zeitzonen-Abstand des Beginns in Minuten (aus <c>start</c>).</summary>
    public int StartOffsetMinutes { get; set; }

    /// <summary>Sportart wie im Vertrag (<c>hiking</c>, <c>mountain_hiking</c> …).</summary>
    public required string Sport { get; set; }

    public string? Region { get; set; }

    public double DistanceMeters { get; set; }

    public double AscentMeters { get; set; }

    public double DurationSeconds { get; set; }

    public double MinLatitude { get; set; }

    public double MinLongitude { get; set; }

    public double MaxLatitude { get; set; }

    public double MaxLongitude { get; set; }

    public bool HasTrack { get; set; }

    /// <summary>Bestiegene Gipfel als JSON (<c>[{ "name", "elevationM", "arrivalUtc", "confirmed" }]</c>) für spätere Storys.</summary>
    public required string Summits { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
