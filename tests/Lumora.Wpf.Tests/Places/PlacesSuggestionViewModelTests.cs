using Lumora.Domain.Enums;
using Lumora.Places.Suggestions;
using Lumora.Places.ViewModels;
using Lumora.Services.Messaging;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;
using Lumora.Shell;
using static Lumora.Wpf.Tests.Places.SuggestionSamples;

namespace Lumora.Wpf.Tests.Places;

/// <summary>
/// Seite „Orte“ › „Ungenau“ (ADR-0062, Mockup 10) mit Fakes: Zähler, Umschalter, Filter-Chips, Gruppen je Tag, Übernehmen
/// (Zeile, Ausflug, „Garmin + sichere“) mit Speicherart, Ablehnen, Rückgängig, Vorschlag ziehen, Karte, Lightbox-Sprung.
/// </summary>
public sealed class PlacesSuggestionViewModelTests
{
    private static async Task<(PlacesHarness H, PlacesViewModel Vm, FakeSuggestionService S, FakeGeoUndo U, FakeTrackSettings Settings)> LoadedAsync(Action<FakeSuggestionService>? seed = null)
    {
        var h = new PlacesHarness();
        h.SeedAustria();
        var (context, service, undo, settings, _) = Context(1);
        (seed ?? Seed)(service);
        var vm = h.Create(suggestions: context);
        await vm.ActivateAsync();
        await vm.LastSuggestionLoad;
        return (h, vm, service, undo, settings);
    }

    /// <summary>Ungenau: 2 Garmin, 1 sicher, 1 wahrscheinlich, 1 ohne Vorschlag (ein Tag), 1 Garmin am Vortag, 1 aus Bibliothek 2. Ohne Ort: 2, einer mit Vorschlag.</summary>
    private static void Seed(FakeSuggestionService s)
    {
        s.Inaccurate.AddRange([
            Item(1, GeoSuggestionLevel.Track, 0), Item(2, GeoSuggestionLevel.Track, 2), Item(3, GeoSuggestionLevel.Sure, 30),
            Item(4, GeoSuggestionLevel.Likely, 60), Item(5, GeoSuggestionLevel.None, 90), Item(6, GeoSuggestionLevel.Track, 0, dayOffset: -1),
            Item(7, GeoSuggestionLevel.Track, 0, library: 2)]);
        s.Unlocated.AddRange([Item(6, GeoSuggestionLevel.Sure, 10, stored: false), Item(8, GeoSuggestionLevel.None, 20, stored: false)]);
    }

    [Fact]
    public async Task Zaehler_im_Kopf_und_am_Umschalter_Hinweis_unter_Ohne_Ort()
    {
        var (_, vm, _, _, _) = await LoadedAsync();
        vm.HasSuggestions.Should().BeTrue();
        vm.EstimatedCount.Should().Be(6, "nur Bibliothek 1");
        vm.EstimatedText.Should().Be("6 mit geschätzter Position");
        vm.InaccurateButtonText.Should().Be("Ungenau 6");
        vm.UnlocatedButtonText.Should().Be("Ohne Ort 3");
        vm.IsUnlocatedMode.Should().BeTrue();
        vm.MapMarkers.Should().NotBeEmpty("in „Ohne Ort“ bleiben die Foto-Gruppen");
        vm.UnlocatedSuggestionCount.Should().Be(1);
        vm.UnlocatedHint.Should().StartWith("1 Foto ohne Ort liegt");
        vm.SidePanelWidth.Value.Should().Be(300);
    }

    [Fact]
    public async Task Ungenau_Gruppen_je_Tag_Chips_Filter_Auswahl_und_Karte()
    {
        var (_, vm, _, _, _) = await LoadedAsync();
        vm.ShowInaccurateModeCommand.Execute(null);

        vm.IsInaccurateMode.Should().BeTrue();
        vm.MapMarkers.Should().BeEmpty("die Karte zeigt die Korrekturen statt der Gruppen");
        vm.SidePanelWidth.Value.Should().Be(430);
        vm.FilterChips.Select(c => c.Text).Should().Equal("Alle", "Garmin-Spur 3", "Sicher 1", "Wahrscheinlich 1", "Ohne Vorschlag 1", "Quellen uneinig 0");
        vm.SuggestionSummary.Should().Contain("3 Garmin-Spur · 1 sicher · 1 wahrscheinlich · 1 ohne Vorschlag");
        var groups = vm.SuggestionRows.OfType<SuggestionGroupRow>().ToList();
        groups.Should().HaveCount(2, "zwei Tage");
        groups[0].Title.Should().Be("Sonntag, 15. März 2020");
        groups[0].Items.Select(r => r.FileId).Should().Equal(1, 2, 3, 4, 5);
        groups[0].Detail.Should().Be("Wanderung Weinebene · 5 Fotos, 4 Vorschläge");
        groups[0].OpenSuggestions.Should().Be(4);
        vm.TrackAndSureOpen.Should().Be(4);
        vm.ApplyTrackAndSureText.Should().Be("Garmin + sichere übernehmen (4)");

        // erste Zeile ausgewählt → Karte mit Spur, Korrekturen des Tages, gestrichelt grau → Akzent
        vm.SelectedRow!.FileId.Should().Be(1);
        await WaitForAsync(() => vm.MapOverlay is { Track.Count: > 0 });
        vm.MapOverlay!.Corrections.Should().ContainSingle("Standard „Nur dieses Foto“ (Mockup 25)").Which.Id.Should().Be(1);
        vm.ShowWholeTrip = true;
        await WaitForAsync(() => vm.MapOverlay is { Corrections.Count: 5 });
        vm.MapOverlay.Corrections.Single(c => c.Id == 1).IsSelected.Should().BeTrue();
        vm.MapOverlay.Corrections.Single(c => c.Id == 5).Suggested.Should().BeNull("kein Vorschlag: nur der graue Punkt");
        vm.LegendTrackText.Should().StartWith("GPS-Spur „Wanderung Weinebene“ ");
        var row1 = vm.SuggestionRows.OfType<SuggestionItemRow>().First();
        row1.BadgeText.Should().Be("Garmin-Spur");
        row1.BadgeKind.Should().Be("garmin");
        row1.Detail.Should().StartWith("ohne GPS-Höhe/-Zeit → ").And.EndWith("korrigieren · Fenix 7");

        vm.SelectFilterCommand.Execute(vm.FilterChips[2]);
        vm.SuggestionRows.OfType<SuggestionItemRow>().Select(r => r.FileId).Should().Equal(3);
        vm.EmptySuggestionText.Should().Be("Keine Fotos in diesem Filter.");
        vm.SelectFilterCommand.Execute(vm.FilterChips[4]);
        vm.SuggestionRows.OfType<SuggestionItemRow>().Single().CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Uebernehmen_je_Foto_nur_im_Katalog_dann_Garmin_und_sichere_ins_Foto_mit_Rueckgaengig()
    {
        var (_, vm, s, undo, settings) = await LoadedAsync();
        vm.ShowInaccurateModeCommand.Execute(null);
        var row = vm.SuggestionRows.OfType<SuggestionItemRow>().First(r => r.FileId == 3);

        await vm.ApplyRowCommand.ExecuteAsync(row);
        s.Applied.Should().ContainSingle();
        s.Applied[0].WriteToFile.Should().BeFalse("Standard: nur im Katalog");
        s.Applied[0].Items.Single().Should().Be(new GeoSuggestionApply(3, 47.0, 15.0, GeoSource.Neighbors));
        row.IsApplied.Should().BeTrue("die Zeile bleibt bis zum nächsten Laden mit „übernommen“ stehen");
        row.BadgeText.Should().Be("übernommen");
        vm.ToastText.Should().Be("1 Ort übernommen — nur im Katalog");
        vm.ToastBatchId.Should().Be(41);
        await vm.UndoToastCommand.ExecuteAsync(null);
        undo.Undone.Should().Equal(41);
        vm.HasToast.Should().BeFalse();

        vm.SaveToFile = true;
        await WaitForAsync(() => settings.Value.SaveToFile);
        vm.ShowInaccurateModeCommand.Execute(null);
        await vm.ApplyTrackAndSureCommand.ExecuteAsync(null);
        s.Applied[^1].WriteToFile.Should().BeTrue();
        s.Applied[^1].Items.Select(i => i.FileId).Should().BeEquivalentTo([1, 2, 6], "Garmin + sicher über alle Tage, nicht Bibliothek 2");
        s.Applied[^1].Items.Should().OnlyContain(i => i.Source == GeoSource.Track);
        vm.ToastText.Should().Be("3 Orte übernommen (Garmin-Spur und sicher) — ins Foto geschrieben");
    }

    [Fact]
    public async Task Ausflug_uebernehmen_Ablehnen_mit_Rueckgaengig_und_Vorschlag_ziehen()
    {
        var (_, vm, s, _, _) = await LoadedAsync();
        vm.ShowInaccurateModeCommand.Execute(null);
        var moved = vm.SuggestionRows.OfType<SuggestionItemRow>().First(r => r.FileId == 2);
        vm.MoveSuggestion(2, 46.97, 14.99);
        moved.IsMoved.Should().BeTrue();
        moved.BadgeText.Should().Be("verschoben");
        moved.Detail.Should().StartWith("von Hand auf der Karte gesetzt");

        vm.SelectRow(moved);
        vm.ApplyGroupText.Should().Be("Ausflug (4)");
        await vm.ApplyGroupCommand.ExecuteAsync(null);
        var items = s.Applied.Single().Items;
        items.Select(i => i.FileId).Should().BeEquivalentTo([1, 2, 3, 4]);
        items.Single(i => i.FileId == 2).Should().Be(new GeoSuggestionApply(2, 46.97, 14.99, GeoSource.Manual), "von Hand verschoben = manuell");

        await vm.ReloadAsync();
        await vm.LastSuggestionLoad;
        var five = vm.SuggestionRows.OfType<SuggestionItemRow>().Single(r => r.FileId == 5);
        await vm.RejectRowCommand.ExecuteAsync(five);
        s.Rejected.Should().Contain(5);
        vm.ToastText.Should().StartWith("Vorschlag abgelehnt");
        await vm.UndoToastCommand.ExecuteAsync(null);
        s.Rejected.Should().NotContain(5, "Rückgängig lässt den Vorschlag wieder zu");
    }

    [Fact]
    public async Task Ohne_Ort_mit_Vorschlag_und_Sprung_aus_der_Lightbox()
    {
        var (h, vm, _, _, _) = await LoadedAsync();
        vm.ShowUnlocatedSuggestionsCommand.Execute(null);
        vm.SideMode.Should().Be(PlacesSideMode.UnlocatedSuggestions);
        vm.SuggestionRows.OfType<SuggestionItemRow>().Select(r => r.FileId).Should().Equal(6, 8);
        vm.SuggestionRows.OfType<SuggestionItemRow>().First().Detail.Should().StartWith("ohne Ort → Vorschlag");
        vm.ShowUnlocatedModeCommand.Execute(null);
        vm.IsUnlocatedMode.Should().BeTrue();

        await vm.ShowFileSuggestionAsync(4);
        h.Navigation.Tags.Should().Contain(NavigationTags.Places);
        vm.IsInaccurateMode.Should().BeTrue();
        vm.SelectedRow!.FileId.Should().Be(4);
    }

    [Fact]
    public async Task Ohne_Dienste_kein_Bereich_Ungenau()
    {
        var h = new PlacesHarness();
        h.SeedAustria();
        var vm = h.Create();
        await vm.ActivateAsync();
        vm.HasSuggestions.Should().BeFalse();
        vm.EstimatedText.Should().BeEmpty();
        await vm.RefreshSuggestions();
    }

    [Fact]
    public void Texte_der_Liste()
    {
        PlacesSuggestionText.GroupDetail(null, 1, 0).Should().Be("1 Foto, 0 Vorschläge");
        PlacesSuggestionText.Matches(SuggestionFilter.Track, GeoSuggestionLevel.Sure).Should().BeFalse();
        PlacesSuggestionText.Matches(SuggestionFilter.All, GeoSuggestionLevel.None).Should().BeTrue();
        StatusBarViewModel.DescribeGeoTracks(new(true, 12, 120, 3, 1), GeoAccuracyStatus.Idle, null).Should().Be("GPS-Spuren: 12 / 120 Dateien");
        StatusBarViewModel.DescribeGeoTracks(GpsTrackImportStatus.Idle, new(true, 5, 10), null).Should().Be("Orte prüfen: 5 / 10");
        StatusBarViewModel.DescribeGeoTracks(GpsTrackImportStatus.Idle, GeoAccuracyStatus.Idle, new GeoPlacesSummaryMessage(158, 122, 1284))
            .Should().Be("Orte: 158 geschätzt · 122 Vorschläge · 1.284 Garmin-Aktivitäten");
        StatusBarViewModel.DescribeGeoTracks(GpsTrackImportStatus.Idle, GeoAccuracyStatus.Idle, new GeoPlacesSummaryMessage(0, 0, 0)).Should().BeNull();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        condition().Should().BeTrue();
    }
}
