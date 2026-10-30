using Lumora.Domain.Enums;
using Lumora.Places.Map;
using Lumora.Places.Suggestions;
using Lumora.Places.ViewModels;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Suggestions;
using static Lumora.Wpf.Tests.Places.SuggestionSamples;

namespace Lumora.Wpf.Tests.Places;

/// <summary>
/// „Ungenau“ › Quellen vergleichen (Mockup 25): laut Foto / laut Spur / laut Zeit je Foto, Bewertung darunter, Quelle
/// wählen, Filter „Quellen uneinig“, Karte nur mit dem ausgewählten Foto samt beschrifteten Punkten.
/// </summary>
public sealed class PlacesSourceCompareTests
{
    private static readonly SuggestionAnchor Before = new(90, T0 - 300, 46.95, 14.94);
    private static readonly SuggestionAnchor After = new(91, T0 + 300, 46.95, 14.96);

    private static GeoSuggestion TrackAt(double lon, GeoSuggestionLevel level = GeoSuggestionLevel.Track, long gap = 4)
        => new(1, level, GeoSuggestionKind.Track, 46.95, lon, 7, gap);

    private static GeoSuggestion TimeAt(double lon)
        => new(1, GeoSuggestionLevel.Sure, GeoSuggestionKind.Interpolated, 46.95, lon, AnchorBefore: Before, AnchorAfter: After);

    /// <summary>Foto um T0 mit gespeichertem Punkt 46,85/15,12 (rund 17 km daneben) und den angegebenen Quellen.</summary>
    private static GeoSuggestionItem WithSources(GeoSourceComparison sources, GeoSuggestion? suggestion, int id = 1)
        => new(id, 1, $"foto{id}.jpg", 0, DateTimeOffset.FromUnixTimeSeconds(T0).UtcDateTime.AddHours(1), T0, PhotoTimeBasis.TimeZone,
            46.85, 15.12, GeoAccuracyReason.NoAltitudeNoTime, suggestion, Hike, sources);

    private static SuggestionItemRow Row(GeoSuggestionItem item) => new(item, new SuggestionGroupRow("d", "Tag"), 60);

    [Fact]
    public void Drei_Quellen_mit_Texten_Abstaenden_und_Bewertung()
    {
        var track = TrackAt(14.95);
        var row = Row(WithSources(new GeoSourceComparison(track, TimeAt(14.951), null), track));

        row.Sources.Select(s => s.Title).Should().Equal("laut Foto", "laut Spur", "laut Zeit");
        row.Sources[0].Line.Should().Be("gespeicherte Position (geschätzt)");
        row.Sources[0].DistanceText.Should().Be("—");
        row.Sources[0].CanKeep.Should().BeTrue();
        row.Sources[1].Line.Should().Be("Spur „Wanderung Weinebene“ (Fenix 7) · 14:46:40");
        row.Sources[1].DetailText.Should().Be("Spurpunkte 4 s auseinander");
        row.Sources[1].IsChosen.Should().BeTrue("der Vorschlag kommt aus der Spur");
        row.Sources[1].DistanceText.Should().EndWith("km");
        row.Sources[2].Line.Should().Be("zwischen GPS-Fotos 14:41 und 14:51");
        row.Sources[2].DetailText.Should().Be("10 min Abstand");
        row.Sources[2].CanTake.Should().BeTrue();
        row.VerdictKind.Should().Be(SuggestionVerdictKind.Ok);
        row.Verdict.Should().StartWith("Spur und Zeit liegen 80 m auseinander und stimmen überein.").And.Contain("Die Position laut Foto liegt");
    }

    [Fact]
    public void Quelle_wechseln_setzt_Punkt_Stufe_Herkunft_und_bleibt_bei_laut_Foto_stehen()
    {
        var track = TrackAt(14.95);
        var row = Row(WithSources(new GeoSourceComparison(track, TimeAt(14.951), null), track));
        row.Choose(SuggestionSourceKind.Stored).Should().BeFalse("„laut Foto“ heißt Behalten bzw. Ablehnen");

        row.Choose(SuggestionSourceKind.Time).Should().BeTrue();
        row.Longitude.Should().Be(14.951);
        row.ChosenSource.Should().Be(SuggestionSourceKind.Time);
        row.Sources[2].IsChosen.Should().BeTrue();
        row.Sources[1].IsChosen.Should().BeFalse();
        row.Level.Should().Be(GeoSuggestionLevel.Sure);
        row.BadgeKind.Should().Be("sure");
        row.SourceOnApply.Should().Be(GeoSource.Neighbors);

        row.Choose(SuggestionSourceKind.Track).Should().BeTrue();
        row.SourceOnApply.Should().Be(GeoSource.Track);
        row.BadgeKind.Should().Be("garmin");
    }

    [Fact]
    public void Luecke_in_der_Spur_Quellen_uneinig_und_nur_eine_Quelle()
    {
        var gap = TrackAt(14.97, GeoSuggestionLevel.None, gap: 1320);
        var inGap = Row(WithSources(new GeoSourceComparison(gap, TimeAt(14.951), null), TimeAt(14.951)));
        inGap.Sources[1].DetailText.Should().Be("in einer Lücke von 22 min · gerade Linie geschätzt");
        inGap.ChosenSource.Should().Be(SuggestionSourceKind.Time);
        inGap.VerdictKind.Should().Be(SuggestionVerdictKind.Warn);
        inGap.Verdict.Should().Contain("Lücke von 22 min");
        inGap.Choose(SuggestionSourceKind.Track).Should().BeTrue("auch die geschätzte Spur lässt sich nehmen");
        inGap.Level.Should().Be(GeoSuggestionLevel.Likely);

        var far = WithSources(new GeoSourceComparison(TrackAt(14.95), TimeAt(15.0), null), TrackAt(14.95));
        far.Sources!.Disagree.Should().BeTrue();
        PlacesSuggestionText.Matches(SuggestionFilter.Disagree, far).Should().BeTrue();
        PlacesSuggestionText.Matches(SuggestionFilter.Disagree, Item(2, GeoSuggestionLevel.Track)).Should().BeFalse();
        Row(far).Verdict.Should().Contain("eine der Quellen stimmt nicht");

        var only = Row(WithSources(new GeoSourceComparison(null, TimeAt(14.951), null), TimeAt(14.951)));
        only.Sources[1].IsAvailable.Should().BeFalse();
        only.Sources[1].Line.Should().Be("keine Spur zur Aufnahmezeit");
        only.Sources[1].DetailText.Should().Be("keine GPS-Spur innerhalb von 2 Stunden");
        only.VerdictKind.Should().Be(SuggestionVerdictKind.Info);
    }

    [Fact]
    public void Uhr_Hinweis_bei_naher_Spur_und_knapp_beieinander()
    {
        var hint = new TrackClockHint(7, -1320, 46.95, 14.99);
        var row = Row(WithSources(new GeoSourceComparison(null, TimeAt(14.951), hint), TimeAt(14.951)));
        row.Sources[1].DetailText.Should().Be("die Spur „Wanderung Weinebene“ endet 22 min davor");
        row.ClockHintText.Should().StartWith("Geht die Kamerauhr 22 min vor, läge das Foto auf der Spur „Wanderung Weinebene“");
        row.VerdictKind.Should().Be(SuggestionVerdictKind.Warn);

        // alles innerhalb von 40 m: „ändert kaum etwas“
        var near = new GeoSuggestionItem(1, 1, "a.jpg", 0, null, T0, PhotoTimeBasis.TimeZone, 46.95, 14.9500, GeoAccuracyReason.NoAltitudeNoTime,
            TrackAt(14.9003), Hike, new GeoSourceComparison(TrackAt(14.9503), TimeAt(14.9504), null));
        Row(near).Verdict.Should().StartWith("Alle Quellen liegen innerhalb von 30 m");
    }

    [Fact]
    public async Task Karte_zeigt_nur_das_Foto_mit_beschrifteten_Quellen_Taste_3_waehlt_die_Zeit_Uebernehmen_als_Nachbarfotos()
    {
        var h = new PlacesHarness();
        h.SeedAustria();
        var (context, service, _, _, _) = Context(1);
        var track = TrackAt(14.95);
        service.Inaccurate.Add(WithSources(new GeoSourceComparison(track, TimeAt(14.951), null), track));
        service.Inaccurate.Add(Item(2, GeoSuggestionLevel.Track, 2));
        var vm = h.Create(suggestions: context);
        await vm.ActivateAsync();
        await vm.LastSuggestionLoad;
        vm.ShowInaccurateModeCommand.Execute(null);

        await WaitForAsync(() => vm.MapOverlay is { Compare: not null, Track.Count: > 0 });
        var overlay = vm.MapOverlay!;
        overlay.Corrections.Should().ContainSingle();
        overlay.Compare!.Marks.Select(m => (m.Kind, m.Label)).Should().Equal(
            (MapCompareKind.Stored, "laut Foto"), (MapCompareKind.Track, "laut Spur · Vorschlag"), (MapCompareKind.Time, "laut Zeit"),
            (MapCompareKind.Anchor, "GPS-Foto 14:41"), (MapCompareKind.Anchor, "GPS-Foto 14:51"));
        overlay.Compare.Links.Should().HaveCount(2);
        vm.Viewport.Zoom.Should().BeGreaterThan(MapViewport.MaxFitZoom - 8, "Ausschnitt auf die Quellen statt auf den ganzen Ausflug");

        vm.SourceKey(3).Should().BeTrue();
        vm.SelectedRow!.ChosenSource.Should().Be(SuggestionSourceKind.Time);
        await WaitForAsync(() => vm.MapOverlay!.Compare!.Marks.Any(m => m.Label == "laut Zeit · Vorschlag"));

        await vm.ApplyRowsAsync([vm.SelectedRow], "1 Ort übernommen");
        service.Applied.Single().Items.Single().Should().Be(new GeoSuggestionApply(1, 46.95, 14.951, GeoSource.Neighbors));
    }

    [Fact]
    public void Massstab_in_runden_Laengen()
    {
        MapControl.NiceLength(110).Should().Be(100);
        MapControl.NiceLength(47).Should().Be(20);
        MapControl.NiceLength(5600).Should().Be(5000);
        MapControl.NiceLength(0.9).Should().Be(0.5);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        condition().Should().BeTrue();
    }
}
