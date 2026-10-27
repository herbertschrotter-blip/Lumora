using Lumora.Domain.Enums;

namespace Lumora.Services.Places.Suggestions;

/// <summary>Woher die UTC-Zeit eines Fotos stammt (Reihenfolge des Zeitabgleichs, ADR-0062).</summary>
public enum PhotoTimeBasis
{
    /// <summary><c>OffsetTimeOriginal</c> der Datei.</summary>
    FileOffset,

    /// <summary>Abstand zwischen GPS-Zeit und Aufnahmezeit (Zeitzone auf Viertelstunden gerundet).</summary>
    GpsTime,

    /// <summary>Zeitzone aus der Einstellung (Standard: Windows-Zeitzone mit Sommerzeit für das Datum).</summary>
    TimeZone,

    /// <summary>Video-Container (Zeit ist schon UTC).</summary>
    Video,
}

/// <summary>Zeit-Angaben eines Fotos aus dem Katalog.</summary>
/// <param name="CapturedAtUnix">Files.CapturedAt (UTC, Unix-Sekunden).</param>
/// <param name="CapturedAtOffsetMinutes">Files.CapturedAtOffset (Wanduhrzeit = UTC + Abstand).</param>
/// <param name="Source">Files.CapturedAtSource.</param>
/// <param name="ExifOffsetMinutes">Files.ExifTimeOffset (nur wenn die Datei ihn nennt).</param>
/// <param name="GpsTimeUnix">Files.GpsTimeUtc.</param>
/// <param name="GpsIsExact">Ort nach den Tag-Regeln genau (nur dann zählt die GPS-Zeit für die Zone).</param>
public readonly record struct PhotoTimeInput(
    long CapturedAtUnix,
    int? CapturedAtOffsetMinutes,
    CaptureDateSource Source,
    int? ExifOffsetMinutes,
    long? GpsTimeUnix,
    bool GpsIsExact,
    string? Camera);

/// <summary>Ergebnis: UTC (Unix-Sekunden) und Grundlage.</summary>
public readonly record struct PhotoTime(long Utc, PhotoTimeBasis Basis);

/// <summary>
/// Zeitabgleich Foto → UTC (ADR-0062, rein und getestet). Die Aufnahmezeit ist eine Wanduhrzeit
/// (<c>DateTimeOriginal</c>); der Katalog speichert sie als UTC mit dem beim Lesen angenommenen Abstand — daraus wird die
/// Wanduhrzeit zurückgerechnet und neu eingeordnet: (1) <c>OffsetTimeOriginal</c> der Datei, (2) bei genauem GPS die
/// Differenz GPS-Zeit ↔ Wanduhrzeit (auf 15 min gerundet = Zeitzone der Aufnahme), (3) Zeitzone aus der Einstellung
/// (Standard Windows mit Sommerzeit für das jeweilige Datum). Danach die Kamera-Abweichung (+ = Kamera geht vor) abziehen.
/// Videos speichern UTC aus dem Container; Fotos ohne Aufnahmedatum (Herkunft „unbekannt“) haben keine Zeit.
/// </summary>
public static class PhotoTimeResolver
{
    /// <summary>Größter plausibler Abstand zu UTC (14 h).</summary>
    public const int MaxOffsetMinutes = 14 * 60;

    /// <summary>
    /// Zeit aus den Katalog-Spalten einer Datei (gemeinsam für „Ungenaue Orte“ und den Galerie-Filter „Tour“): nur mit genauem
    /// Aufnahmedatum; die GPS-Zeit zählt nur bei genauem EXIF-Ort.
    /// </summary>
    public static PhotoTime? ResolveCatalog(long? capturedAt, int? offset, int? source, int precision, int? exifOffset, long? gpsTime,
        int? accuracy, int? geoSource, string? camera, TimeZoneInfo zone, Tracks.GeoTrackSettings settings)
    {
        if (capturedAt is not { } at || source is not { } kind || precision != (int)CaptureDatePrecision.Exact)
        {
            return null;
        }

        return Resolve(
            new PhotoTimeInput(at, offset, (CaptureDateSource)kind, exifOffset, gpsTime,
                accuracy == (int)GeoAccuracy.Exact && geoSource == (int)GeoSource.Exif, camera),
            zone,
            settings.OffsetFor(camera));
    }

    public static PhotoTime? Resolve(PhotoTimeInput input, TimeZoneInfo zone, int cameraOffsetSeconds)
    {
        if (input.Source == CaptureDateSource.Unknown)
        {
            return null;
        }

        if (input.Source == CaptureDateSource.Video)
        {
            return new PhotoTime(input.CapturedAtUnix - cameraOffsetSeconds, PhotoTimeBasis.Video);
        }

        var wall = input.CapturedAtUnix + ((input.CapturedAtOffsetMinutes ?? 0) * 60L);
        long utc;
        PhotoTimeBasis basis;
        if (input.ExifOffsetMinutes is { } fileOffset)
        {
            utc = wall - (fileOffset * 60L);
            basis = PhotoTimeBasis.FileOffset;
        }
        else if (input.GpsIsExact && input.GpsTimeUnix is { } gps && ZoneFromGps(wall, gps) is { } gpsOffset)
        {
            utc = wall - (gpsOffset * 60L);
            basis = PhotoTimeBasis.GpsTime;
        }
        else
        {
            var wallClock = DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(wall).UtcDateTime, DateTimeKind.Unspecified);
            utc = wall - (long)Math.Round(zone.GetUtcOffset(wallClock).TotalSeconds);
            basis = PhotoTimeBasis.TimeZone;
        }

        return new PhotoTime(utc - cameraOffsetSeconds, basis);
    }

    /// <summary>Zeitzone aus Wanduhrzeit − GPS-Zeit, auf 15 min gerundet; null außerhalb ±14 h.</summary>
    public static int? ZoneFromGps(long wallUnix, long gpsUnix)
    {
        var minutes = (wallUnix - gpsUnix) / 60.0;
        var rounded = (int)(Math.Round(minutes / 15.0) * 15);
        return Math.Abs(rounded) <= MaxOffsetMinutes ? rounded : null;
    }
}

/// <summary>
/// Vorschlag „aus GPS-Zeit ermittelt“ für die Kamera-Abweichung (ADR-0062): nur für Kameras, deren Fotos selbst einen
/// GPS-Zeitstempel haben — Restabweichung = Wanduhrzeit − GPS-Zeit − Zeitzone, Median über mindestens
/// <see cref="MinSamples"/> Fotos; unter <see cref="MinSeconds"/> passt die Uhr („± 0 s“). Die Zeitzone kommt aus
/// <c>OffsetTimeOriginal</c>, sonst wird der Median auf volle Stunden gerundet (Abweichungen bis ±30 min erkennbar).
/// Ohne GPS-Zeit lässt sich die Abweichung nicht sauber berechnen — dann von Hand.
/// </summary>
public static class CameraOffsetEstimator
{
    public const int MinSamples = 3;
    public const int MinSeconds = 5;

    /// <summary>Abweichung in Sekunden (+ = Kamera geht vor) oder null (zu wenige Fotos mit GPS-Zeit).</summary>
    public static int? Estimate(IReadOnlyList<(long WallUnix, long GpsUnix)> samples)
        => Estimate(samples.Select(s => (s.WallUnix, s.GpsUnix, (int?)null)).ToList());

    /// <summary>Wie oben, mit bekannter Zeitzone je Foto (OffsetTimeOriginal in Minuten, null = unbekannt).</summary>
    public static int? Estimate(IReadOnlyList<(long WallUnix, long GpsUnix, int? ZoneMinutes)> samples)
    {
        var known = new List<long>();
        var unknown = new List<long>();
        foreach (var (wall, gps, zone) in samples)
        {
            var diff = wall - gps;
            if (Math.Abs(diff) > (PhotoTimeResolver.MaxOffsetMinutes + 60) * 60L)
            {
                continue; // unplausibel
            }

            if (zone is { } z)
            {
                known.Add(diff - (z * 60L));
            }
            else
            {
                unknown.Add(diff);
            }
        }

        if (unknown.Count > 0)
        {
            // gemeinsame Zone der übrigen: Median auf volle Stunden
            var median = Median(unknown);
            var zone = (long)Math.Round(median / 3600.0) * 3600;
            known.AddRange(unknown.Select(d => d - zone));
        }

        if (known.Count < MinSamples)
        {
            return null;
        }

        var result = Median(known);
        return Math.Abs(result) < MinSeconds ? 0 : (int)result;
    }

    private static long Median(List<long> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>„+ 3 min 12 s“, „– 1 h 00 min“, „± 0 s“.</summary>
    public static string Format(int seconds)
    {
        if (seconds == 0)
        {
            return "± 0 s";
        }

        var sign = seconds > 0 ? "+" : "–";
        var d = TimeSpan.FromSeconds(Math.Abs(seconds));
        if (d.TotalHours >= 1)
        {
            return $"{sign} {(int)d.TotalHours} h {d.Minutes:00} min";
        }

        return d.TotalMinutes >= 1 ? $"{sign} {d.Minutes} min {d.Seconds} s" : $"{sign} {d.Seconds} s";
    }

    /// <summary>Eingabe „+3:12“, „-1:00:00“, „90“ (Sekunden), „+3 min 12 s“ → Sekunden; null bei Unlesbarem.</summary>
    public static int? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var t = text.Trim().Replace('–', '-').Replace('−', '-').Replace("±", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        var sign = 1;
        if (t.StartsWith('-'))
        {
            sign = -1;
            t = t[1..];
        }
        else if (t.StartsWith('+'))
        {
            t = t[1..];
        }

        if (t.Length == 0)
        {
            return 0;
        }

        if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var plain))
        {
            return sign * plain;
        }

        var parts = t.Split(':');
        if (parts.Length is 2 or 3 && parts.All(p => int.TryParse(p, out _)))
        {
            var numbers = parts.Select(int.Parse).ToArray();
            var seconds = parts.Length == 2 ? (numbers[0] * 60) + numbers[1] : (numbers[0] * 3600) + (numbers[1] * 60) + numbers[2];
            return sign * seconds;
        }

        // „3min12s“, „1h00min“, „12s“
        var match = System.Text.RegularExpressions.Regex.Match(t, @"^(?:(\d+)h)?(?:(\d+)min)?(?:(\d+)s)?$");
        if (match.Success && match.Value.Length > 0)
        {
            var h = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
            var m = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
            var s = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            return sign * ((h * 3600) + (m * 60) + s);
        }

        return null;
    }
}
