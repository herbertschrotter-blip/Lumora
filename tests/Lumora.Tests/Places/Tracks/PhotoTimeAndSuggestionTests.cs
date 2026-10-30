using Lumora.Domain.Enums;
using Lumora.Services.Places;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;

namespace Lumora.Tests.Places.Tracks;

/// <summary>Zeitabgleich (Offset-Tag, GPS-Zeit, Sommerzeit, Kamera-Abweichung) und Vorschläge mit Stufen (ADR-0062).</summary>
public sealed class PhotoTimeAndSuggestionTests
{
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private static long Unix(int y, int mo, int d, int h, int mi, int s = 0) => new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>Wanduhrzeit, wie sie der Katalog speichert: CapturedAt (UTC) + Abstand.</summary>
    private static PhotoTimeInput Wall(long wallUnix, int storedOffset = 0, int? exifOffset = null, long? gps = null, bool exact = false, CaptureDateSource source = CaptureDateSource.Exif)
        => new(wallUnix - (storedOffset * 60L), storedOffset, source, exifOffset, gps, exact, null);

    [Fact]
    public void Offset_Tag_der_Datei_gewinnt()
    {
        var wall = Unix(2020, 3, 15, 14, 56);
        var t = PhotoTimeResolver.Resolve(Wall(wall, storedOffset: 60, exifOffset: 120), Vienna, 0)!.Value;
        t.Basis.Should().Be(PhotoTimeBasis.FileOffset);
        t.Utc.Should().Be(wall - 7200, "+02:00 aus der Datei, nicht die PC-Zeitzone");
    }

    [Fact]
    public void Genaue_GPS_Zeit_bestimmt_die_Zone_auf_Viertelstunden_gerundet()
    {
        var wall = Unix(2020, 3, 15, 14, 56, 10);
        var gps = Unix(2020, 3, 15, 13, 56, 4); // Kamera 6 s vor, Zone +1 h
        var t = PhotoTimeResolver.Resolve(Wall(wall, gps: gps, exact: true), Vienna, 0)!.Value;
        t.Basis.Should().Be(PhotoTimeBasis.GpsTime);
        t.Utc.Should().Be(wall - 3600);
        PhotoTimeResolver.Resolve(Wall(wall, gps: gps, exact: false), Vienna, 0)!.Value.Basis.Should().Be(PhotoTimeBasis.TimeZone, "geschätzte Orte: GPS-Zeit nicht verlässlich");
        PhotoTimeResolver.ZoneFromGps(Unix(2020, 1, 1, 18, 0), Unix(2020, 1, 1, 12, 30)).Should().Be(330, "Indien +5:30");
        PhotoTimeResolver.ZoneFromGps(Unix(2020, 1, 2, 18, 0), Unix(2020, 1, 1, 12, 0)).Should().BeNull("über 14 h");
    }

    [Fact]
    public void Zeitzone_aus_der_Einstellung_mit_Sommerzeit_je_Datum()
    {
        var summer = PhotoTimeResolver.Resolve(Wall(Unix(2020, 7, 15, 12, 0)), Vienna, 0)!.Value;
        summer.Utc.Should().Be(Unix(2020, 7, 15, 10, 0), "Sommerzeit +2 h");
        var winter = PhotoTimeResolver.Resolve(Wall(Unix(2020, 1, 15, 12, 0)), Vienna, 0)!.Value;
        winter.Utc.Should().Be(Unix(2020, 1, 15, 11, 0), "Winterzeit +1 h");
        winter.Basis.Should().Be(PhotoTimeBasis.TimeZone);
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        PhotoTimeResolver.Resolve(Wall(Unix(2020, 7, 15, 12, 0)), tokyo, 0)!.Value.Utc.Should().Be(Unix(2020, 7, 15, 3, 0));
    }

    [Fact]
    public void Kamera_Abweichung_Video_und_unbekanntes_Datum()
    {
        PhotoTimeResolver.Resolve(Wall(Unix(2020, 1, 15, 12, 3, 12)), Vienna, 192)!.Value.Utc.Should().Be(Unix(2020, 1, 15, 11, 0), "Kamera geht 3 min 12 s vor");
        PhotoTimeResolver.Resolve(Wall(Unix(2020, 1, 15, 12, 0), storedOffset: 60, source: CaptureDateSource.Video), Vienna, 0)!.Value
            .Should().Be(new PhotoTime(Unix(2020, 1, 15, 11, 0), PhotoTimeBasis.Video), "Videos speichern UTC");
        PhotoTimeResolver.Resolve(Wall(0, source: CaptureDateSource.Unknown), Vienna, 0).Should().BeNull();
    }

    [Fact]
    public void Kamera_Abweichung_aus_GPS_Zeit_ermittelt_formatiert_und_gelesen()
    {
        var samples = Enumerable.Range(0, 5).Select(i => (Unix(2020, 5, 1, 14, i * 10, 12) + 0L, Unix(2020, 5, 1, 12, i * 10, 0) - 180L)).ToList();
        CameraOffsetEstimator.Estimate(samples).Should().Be(192, "Wanduhr − GPS − 2 h Zone = 3 min 12 s");
        CameraOffsetEstimator.Estimate(samples.Take(2).ToList()).Should().BeNull("zu wenige Fotos mit GPS-Zeit");
        CameraOffsetEstimator.Estimate([(100, 98), (200, 197), (300, 299)]).Should().Be(0, "unter 5 s: passt");
        var tenMinutesFast = Enumerable.Range(0, 3).Select(i => (Unix(2020, 5, 1, 14, 10 + i), Unix(2020, 5, 1, 12, i) + 0L)).ToList();
        CameraOffsetEstimator.Estimate(tenMinutesFast).Should().Be(600, "10 min vor in UTC+2: Zone auf volle Stunden");
        CameraOffsetEstimator.Estimate(tenMinutesFast.Select(s => (s.Item1, s.Item2, (int?)120)).ToList()).Should().Be(600, "mit OffsetTimeOriginal");
        CameraOffsetEstimator.Estimate(tenMinutesFast.Select(s => (s.Item1, s.Item2, (int?)90)).ToList()).Should().Be(2400, "Zone aus der Datei gewinnt");
        CameraOffsetEstimator.Format(192).Should().Be("+ 3 min 12 s");
        CameraOffsetEstimator.Format(-3600).Should().Be("– 1 h 00 min");
        CameraOffsetEstimator.Format(0).Should().Be("± 0 s");
        CameraOffsetEstimator.Parse("+3:12").Should().Be(192);
        CameraOffsetEstimator.Parse("-1:00:00").Should().Be(-3600);
        CameraOffsetEstimator.Parse("– 1 h 00 min").Should().Be(-3600);
        CameraOffsetEstimator.Parse("+ 3 min 12 s").Should().Be(192);
        CameraOffsetEstimator.Parse("90").Should().Be(90);
        CameraOffsetEstimator.Parse("± 0 s").Should().Be(0);
        CameraOffsetEstimator.Parse("bald").Should().BeNull();
    }

    // ---- Vorschläge ----

    private const long T0 = 1_584_280_000; // 2020-03-15 13:46:40 UTC

    /// <summary>Spur von (47,0 / 15,0) nach Osten, ein Punkt je <paramref name="step"/> s, optional mit Lücke.</summary>
    private static SuggestionTrack Track(int id = 7, int points = 120, int step = 10, (int From, int To)? gap = null, bool gapStandStill = false)
    {
        var list = new List<TrackPoint>();
        for (var i = 0; i < points; i++)
        {
            if (gap is { } g && i > g.From && i < g.To)
            {
                continue;
            }

            var lonIndex = gap is { } g2 && gapStandStill && i >= g2.To ? g2.From + (i - g2.To) : i;
            list.Add(new TrackPoint(T0 + (i * step), 47.0, 15.0 + (lonIndex * 0.0001), 1000 + i));
        }

        var array = list.ToArray();
        return new SuggestionTrack(id, array[0].Utc, array[^1].Utc, () => array);
    }

    private static GeoSuggestion? One(long t, IReadOnlyList<SuggestionAnchor>? anchors = null, IReadOnlyList<SuggestionTrack>? tracks = null, long tolerance = 120)
        => GeoSuggestionEngine.Suggest([new SuggestionCandidate(1, t)], anchors ?? [], tracks ?? [], tolerance).GetValueOrDefault(1);

    [Fact]
    public void Spur_interpoliert_zwischen_Punkten_mit_kurzer_Luecke()
    {
        var s = One(T0 + 105, tracks: [Track()])!;
        s.Level.Should().Be(GeoSuggestionLevel.Track);
        s.Kind.Should().Be(GeoSuggestionKind.Track);
        s.TrackId.Should().Be(7);
        s.Longitude.Should().BeApproximately(15.00105, 1e-7, "Mitte zwischen Punkt 10 und 11");
        s.Altitude.Should().BeApproximately(1010.5, 1e-6);
        s.GapSeconds.Should().Be(10);
    }

    [Fact]
    public void Toleranz_vor_und_nach_der_Aktivitaet()
    {
        One(T0 - 100, tracks: [Track()])!.Level.Should().Be(GeoSuggestionLevel.Track, "100 s vor dem Start, Toleranz 2 min");
        One(T0 - 200, tracks: [Track()]).Should().BeNull("außerhalb der Toleranz und keine Nachbarfotos");
        One(T0 - 200, tracks: [Track()], tolerance: 300)!.Level.Should().Be(GeoSuggestionLevel.Likely, "Toleranz 5 min, aber 200 s vom Spurende: lange Lücke");
    }

    [Fact]
    public void Lange_Luecke_wahrscheinlich_Pause_am_selben_Ort_bleibt_Spur_zu_lange_Luecke_nichts()
    {
        One(T0 + 400, tracks: [Track(gap: (20, 60))])!.Level.Should().Be(GeoSuggestionLevel.Likely, "400 s Lücke, dabei 4 × 10 m … 40 Punkte weiter");
        One(T0 + 400, tracks: [Track(gap: (20, 60), gapStandStill: true)])!.Level.Should().Be(GeoSuggestionLevel.Track, "Pause: vor und nach der Lücke am selben Ort");
        One(T0 + 2000, tracks: [Track(points: 400, gap: (100, 300))]).Should().BeNull("2 000 s Lücke: über 15 min");
    }

    [Fact]
    public void Zwei_genaue_Fotos_ergeben_sicher_ein_einzelnes_wahrscheinlich()
    {
        var a = new SuggestionAnchor(10, T0, 47.0, 15.0);
        var b = new SuggestionAnchor(11, T0 + 1200, 47.0, 15.02);
        var sure = One(T0 + 600, [a, b])!;
        sure.Level.Should().Be(GeoSuggestionLevel.Sure);
        sure.Kind.Should().Be(GeoSuggestionKind.Interpolated);
        sure.Longitude.Should().BeApproximately(15.01, 1e-9);
        sure.AnchorBefore.Should().Be(a);
        sure.AnchorAfter.Should().Be(b);

        One(T0 + 600, [a])!.Should().Match<GeoSuggestion>(x => x.Level == GeoSuggestionLevel.Likely && x.Kind == GeoSuggestionKind.Neighbor);
        One(T0 + 1000, [a]).Should().BeNull("16 min vom einzigen Nachbarn");
        var far = new SuggestionAnchor(12, T0 + 1200, 48.21, 16.37); // Wien in 20 min: unplausibel
        One(T0 + 60, [a, far])!.Level.Should().Be(GeoSuggestionLevel.Likely, "unplausible Geschwindigkeit → nur das nahe Foto");
        var late = new SuggestionAnchor(13, T0 + 2400, 47.0, 15.02);
        One(T0 + 300, [a, late])!.Level.Should().Be(GeoSuggestionLevel.Likely, "40 min zwischen den Fotos: keine Interpolation");
    }

    [Fact]
    public void Spur_schlaegt_Fotos_das_Foto_selbst_ist_keine_Stuetze()
    {
        var a = new SuggestionAnchor(10, T0 + 100, 46.0, 14.0);
        var b = new SuggestionAnchor(11, T0 + 200, 46.0, 14.0);
        One(T0 + 150, [a, b], [Track()])!.Kind.Should().Be(GeoSuggestionKind.Track);
        var self = new SuggestionAnchor(1, T0 + 150, 46.0, 14.0);
        GeoSuggestionEngine.Suggest([new SuggestionCandidate(1, T0 + 150)], [self], [], 120).Should().BeEmpty();
    }

    [Fact]
    public void Vergleich_zeigt_Spur_und_Zeit_nebeneinander_auch_wenn_die_Spur_gewinnt()
    {
        var a = new SuggestionAnchor(10, T0 + 100, 47.0, 15.010);
        var b = new SuggestionAnchor(11, T0 + 200, 47.0, 15.020);
        var c = GeoSuggestionEngine.Compare([new SuggestionCandidate(1, T0 + 150)], [a, b], [Track()], 120)[1];
        c.FromTrack!.Level.Should().Be(GeoSuggestionLevel.Track);
        c.FromTime!.Kind.Should().Be(GeoSuggestionKind.Interpolated);
        c.FromTime.Longitude.Should().BeApproximately(15.015, 1e-9);
        c.NearTrack.Should().BeNull("die Spur deckt die Zeit ab");
        c.TrackToTimeMeters.Should().BeApproximately(GeoMath.DistanceMeters(47.0, 15.0015, 47.0, 15.015), 1);
        c.Disagree.Should().BeTrue("rund 1 km zwischen Spur und Zeit");
    }

    [Fact]
    public void Vergleich_schaetzt_die_Spur_auch_ueber_lange_Luecken()
    {
        One(T0 + 2000, tracks: [Track(points: 400, gap: (100, 300))]).Should().BeNull("kein Vorschlag aus der Lücke");
        var c = GeoSuggestionEngine.Compare([new SuggestionCandidate(1, T0 + 2000)], [], [Track(points: 400, gap: (100, 300))], 120)[1];
        c.TrackInGap.Should().BeTrue();
        c.FromTrack!.GapSeconds.Should().Be(2000);
        c.FromTrack.Longitude.Should().BeApproximately(15.02, 1e-7, "Mitte der Lücke zwischen Punkt 100 und 300");
        c.FromTime.Should().BeNull();
        c.Disagree.Should().BeFalse("ohne Zeit kein Vergleich");
    }

    [Fact]
    public void Ohne_Spur_zur_Zeit_nennt_der_Vergleich_die_naechste_Spur_als_Uhr_Hinweis()
    {
        var track = Track(); // T0 … T0 + 1190 s
        var after = GeoSuggestionEngine.Compare([new SuggestionCandidate(1, T0 + 1190 + 1320)], [], [track], 120)[1];
        after.FromTrack.Should().BeNull();
        after.NearTrack!.Value.OffsetSeconds.Should().Be(-1320, "22 min nach dem Ende: Kamera geht vor");
        after.NearTrack.Value.Longitude.Should().BeApproximately(15.0119, 1e-7, "Spur-Ende");
        var before = GeoSuggestionEngine.Compare([new SuggestionCandidate(1, T0 - 600)], [], [track], 120)[1];
        before.NearTrack!.Value.OffsetSeconds.Should().Be(600, "10 min vor dem Start: Kamera geht nach");
        GeoSuggestionEngine.Compare([new SuggestionCandidate(1, T0 + 1190 + 7300)], [], [track], 120).Should().BeEmpty("über 2 h weg");
    }

    [Fact]
    public void Texte_der_Vorschlaege()
    {
        GeoSuggestionText.LevelName(GeoSuggestionLevel.Track).Should().Be("Garmin-Spur");
        GeoSuggestionText.LevelName(GeoSuggestionLevel.None).Should().Be("kein Vorschlag");
        var anchor = new SuggestionAnchor(10, T0 - 2100, 47, 15);
        var item = new GeoSuggestionItem(1, 1, "a.jpg", 0, null, T0, PhotoTimeBasis.TimeZone, 46.9, 15.1, GeoAccuracyReason.NoAltitudeNoTime,
            new GeoSuggestion(1, GeoSuggestionLevel.Likely, GeoSuggestionKind.Neighbor, 47, 15, AnchorBefore: anchor), null);
        GeoSuggestionText.Source(item, 60).Should().Be("Foto 14:11 (35 min davor)");
        GeoSuggestionText.Detail(item, 60).Should().StartWith("ohne GPS-Höhe/-Zeit → ").And.EndWith("korrigieren");
        GeoMath.FormatDistance(4800).Should().Be("4,8 km");
        GeoMath.FormatDistance(830).Should().Be("830 m");
        GeoMath.DirectionText(47, 15.06, 47, 15).Should().Be("westlich");
    }
}
