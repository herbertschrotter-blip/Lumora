namespace Lumora.AppConnect;

/// <summary>Bestiegener Gipfel einer Tour (Schema <c>TourSummit</c>, Vertrag Abschnitt 8.2).</summary>
public sealed record TourSummit(string SummitId, string Name, int ElevationM, DateTime ArrivalUtc, bool Confirmed);

/// <summary>Tour des Gipfelbuchs (Schema <c>Tour</c>, Vertrag Abschnitt 8.2).</summary>
public sealed record Tour
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Beginn mit Zeitzonen-Abstand.</summary>
    public required DateTimeOffset Start { get; init; }

    public required DateTime StartUtc { get; init; }

    public required DateTime EndUtc { get; init; }

    /// <summary>Sportart (<c>hiking</c> …); unbekannte Werte gelten als <c>other</c>.</summary>
    public required string Sport { get; init; }

    public string? Region { get; init; }

    public double DistanceM { get; init; }

    public double AscentM { get; init; }

    public double? DescentM { get; init; }

    public double DurationS { get; init; }

    public double? MovingS { get; init; }

    /// <summary><c>[minLon, minLat, maxLon, maxLat]</c>.</summary>
    public required IReadOnlyList<double> Bbox { get; init; }

    public bool HasTrack { get; init; }

    /// <summary><c>garmin</c>, <c>strava</c>, <c>gpx</c>, <c>fit</c>, <c>manual</c> oder <c>demo</c>.</summary>
    public required string Source { get; init; }

    public IReadOnlyList<TourSummit> Summits { get; init; } = [];
}

/// <summary>Eine Seite Touren (Schema <c>TourPage</c>).</summary>
public sealed record TourPage(IReadOnlyList<Tour> Items, string? NextCursor);

/// <summary>Spur einer Tour, spaltenweise (Schema <c>Track</c>): <see cref="T"/> = Sekunden seit <see cref="StartUtc"/>.</summary>
public sealed record TourTrack
{
    public required string TourId { get; init; }

    public required DateTime StartUtc { get; init; }

    public int Count { get; init; }

    public required IReadOnlyList<double> T { get; init; }

    public required IReadOnlyList<double> Lat { get; init; }

    public required IReadOnlyList<double> Lon { get; init; }

    /// <summary>Fehlt, wenn die Quelldatei keine Höhen enthält.</summary>
    public IReadOnlyList<double>? Ele { get; init; }
}

/// <summary>Sportarten des Vertrags in Worten (wie die Sportart eigener Spuren, ADR-0062).</summary>
public static class TourSports
{
    public static string Name(string sport) => sport switch
    {
        "hiking" => "Wandern",
        "mountain_hiking" => "Bergtour",
        "mountaineering" => "Hochtour",
        "trail_running" => "Trailrunning",
        "running" => "Laufen",
        "cycling" => "Radfahren",
        "road_cycling" => "Rennrad",
        "mountain_biking" => "Mountainbike",
        "ski_touring" => "Skitour",
        "skiing" => "Ski alpin",
        _ => "Aktivität", // other und unbekannte Werte (Vertrag Abschnitt 3)
    };
}
