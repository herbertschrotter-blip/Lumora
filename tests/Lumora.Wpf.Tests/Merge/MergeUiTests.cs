using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Imaging.Merge;
using Lumora.Imaging.Protocol;
using Lumora.Lightbox;
using Lumora.Merge;
using Lumora.PhotoMenu;
using Lumora.Services.Merge;
using Lumora.Services.Related;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.PhotoMenu;
using Lumora.Wpf.Tests.Shell;
using Wpf.Ui.Appearance;

namespace Lumora.Wpf.Tests.Merge;

/// <summary>
/// Oberfläche von „Zusammenfügen“ (ADR-0078 Stufe 1 Teil B): Fenster-ViewModel (Vorschau ab 2 Haken, Zeit-Nachbarn, Regler
/// tont nur neu und wird gemerkt, Aufnahmeart setzt neu zusammen ohne neue Sitzung, Sperre bei gemischten Bibliotheken, Prüfung,
/// Speichern → Fertig → Rückgängig), Fenster hell/dunkel ohne Binding-Fehler, Menü-Einträge, Suchen ▸, Ergebnis-Marke,
/// Lightbox-Zeile, Einstellungen. Fake-Dienst — kein Hilfsprozess, keine echten Fotos.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class MergeUiTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _root = TestImages.CreateTempDirectory();

    public MergeUiTests()
    {
        MergeViewModel.SourceDebounce = TimeSpan.FromMilliseconds(10);
        MergeViewModel.BoostDebounce = TimeSpan.FromMilliseconds(10);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Task OnUiAsync(Func<Task> scenario) => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private static async Task WaitUntilAsync(Func<bool> condition, string what = "Bedingung")
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Timeout)
            {
                throw new TimeoutException(what + " nicht erreicht");
            }

            await Task.Delay(15);
        }
    }

    private static MergeSourceInfo Source(int id, int position, int seconds, MergeSourceState state = MergeSourceState.Usable)
        => new(id, position, $"IMG_{id:0000}.jpg", new DateTime(2025, 6, 21, 10, 15, 0).AddSeconds(seconds), "Testkamera", 4000, 3000, 3_000_000, state);

    // ---- Fenster-ViewModel ---------------------------------------------------------------------------------------------

    [Fact]
    public Task Vorschau_startet_ab_zwei_Fotos_nur_Panorama_ist_gebaut() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root) { Sources = [Source(1, 0, 0), Source(2, 1, 2), Source(3, 2, 4)] };
        await using var vm = new MergeViewModel(service, new MergeStart([1, 2, 3]));
        await vm.LoadAsync();
        await WaitUntilAsync(() => vm.Preview is not null, "Vorschau");

        service.Sessions.Should().ContainSingle();
        service.Sessions[0].Request.FileIds.Should().Equal(1, 2, 3);
        service.Sessions[0].StartCalls.Should().Be(1);
        vm.Methods.Single(m => m.Method == MergeMethod.Panorama).Should().Match<MergeMethodItem>(m => m.IsBuilt && m.Suits && m.FitText == "passt" && m.IsSuggested);
        vm.Methods.Where(m => m.Method != MergeMethod.Panorama).Should().OnlyContain(m => !m.IsBuilt && m.FitText == "kommt später" && m.Reason.StartsWith("kommt in Stufe", StringComparison.Ordinal));
        vm.StageImage.Should().NotBeNull();
        vm.HasStageFrame.Should().BeTrue("„Gerade Kanten“ zeigt den Zuschnitt im ungeschnittenen Panorama");
        vm.StageCrop.Should().Be(new Int32Rect(10, 6, 100, 50));
        vm.SourceSummary.Should().Be("3 von 3 angehakt");
        vm.TargetName.Should().Be("IMG_0001_panorama.jpg");
        vm.Sizes.Should().ContainSingle(s => s.IsSelected && s.Title == "Volle Auflösung");
        vm.EstimateText.Should().Contain("mit Vorrang");
        vm.WhyLines.Should().Contain(l => l.Contains("3 Fotos", StringComparison.Ordinal));
        vm.SaveCommand.CanExecute(null).Should().BeTrue();
    });

    [Fact]
    public Task Ein_Foto_zeigt_Zeit_Nachbarn_nicht_angehakt_und_rechnet_erst_ab_zwei_Haken() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root) { Sources = [Source(5, 0, 20)], Neighbors = [Source(4, 0, 0), Source(6, 1, 40), Source(7, 2, 60)] };
        await using var vm = new MergeViewModel(service, new MergeStart([5]));
        await vm.LoadAsync();

        service.Sessions.Should().BeEmpty("nur 1 Foto angehakt");
        vm.Sources.Select(s => (s.FileId, s.IsChecked)).Should().Equal((4, false), (5, true), (6, false), (7, false));
        vm.Sources.Single(s => s.FileId == 5).Label.Should().Be("gestartet");
        vm.Sources.Single(s => s.FileId == 6).Label.Should().Be("+0:20");
        vm.StageMessage.Should().StartWith("Mindestens 2 Fotos anhaken");
        vm.Methods.Single(m => m.Method == MergeMethod.Panorama).Should().Match<MergeMethodItem>(m => !m.Suits && m.Reason == "nur 1 Foto angehakt");
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.CheckAllText.Should().Be("Alle 4 anhaken");

        vm.CheckAllCommand.Execute(null);
        await WaitUntilAsync(() => vm.Preview is not null, "Vorschau nach „Alle anhaken“");
        service.Sessions[^1].Request.FileIds.Should().BeEquivalentTo([4, 5, 6, 7]);
    });

    [Fact]
    public Task Regler_Farbe_kraeftiger_tont_nur_neu_und_wird_gemerkt() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root) { Sources = [Source(1, 0, 0), Source(2, 1, 2)] };
        await using var vm = new MergeViewModel(service, new MergeStart([1, 2]));
        await vm.LoadAsync();
        await WaitUntilAsync(() => vm.Preview is not null);
        var session = service.Sessions.Single();
        vm.ColorBoost.Should().Be(MergeLimits.ColorBoostDefault, "erstes Mal 100");

        vm.ColorBoost = MergeLimits.ColorBoostOff;
        await WaitUntilAsync(() => session.Updates.Count == 1 && service.Saved.Count == 1, "neu getont und gemerkt");
        vm.IsColorBoostOff.Should().BeTrue();
        vm.ColorBoostText.Should().Be("aus");
        session.Updates[0].ColorBoost.Should().Be(0);
        session.Updates[0].SameGeometry(session.Request.Options!).Should().BeTrue("nur der Look ändert sich — kein Ausrichten, kein Zusammensetzen");
        session.StartCalls.Should().Be(1);
        service.Sessions.Should().ContainSingle("keine neue Sitzung");
        service.Saved[^1].ColorBoost.Should().Be(0);

        vm.SetColorBoostCommand.Execute("100");
        await WaitUntilAsync(() => session.Updates.Count == 2 && service.Saved[^1].ColorBoost == 100);
        vm.ColorBoostText.Should().Be("100 % (Standard)");
    });

    [Fact]
    public Task Aufnahmeart_und_Zuschnitt_setzen_neu_zusammen_ohne_neue_Sitzung() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root) { Sources = [Source(1, 0, 0), Source(2, 1, 2), Source(3, 2, 4)] };
        await using var vm = new MergeViewModel(service, new MergeStart([1, 2, 3]));
        await vm.LoadAsync();
        await WaitUntilAsync(() => vm.Preview is not null);
        var session = service.Sessions.Single();
        vm.Projections.Single(p => p.Projection == PanoramaProjection.Cylindrical).IsSuggested.Should().BeTrue();
        vm.Projections.Single(p => p.Projection == PanoramaProjection.Auto).Title.Should().Be("Automatisch → Zylinder");

        vm.ChooseProjectionCommand.Execute(PanoramaProjection.Spherical360);
        await WaitUntilAsync(() => session.Updates.Count == 1);
        session.Updates[0].Projection.Should().Be(PanoramaProjection.Spherical360);
        session.Updates[0].SameGeometry(session.Request.Options!).Should().BeFalse();
        vm.IsSphere.Should().BeTrue("Wahl Horizontstreifen | 2 : 1");
        vm.IsSphereBand.Should().BeTrue("erstes Mal Horizontstreifen (Abnahme Nr. 4)");

        vm.IsSphereFull = true;
        await WaitUntilAsync(() => session.Updates.Count == 2 && service.Saved.Any(s => s.SphereForm == MergeSphereForm.Full), "Form gewählt und gemerkt");
        vm.IsCropNone = true;
        await WaitUntilAsync(() => session.Updates.Count == 3);
        session.Updates[^1].Crop.Should().Be(MergeCrop.None);
        service.Sessions.Should().ContainSingle("Umschalten richtet nicht neu aus");
    });

    [Fact]
    public Task Gemischte_Bibliotheken_sperren_das_Fenster_mit_Grund() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root) { Sources = [Source(1, 0, 0), Source(2, 1, 2)], Mixed = true };
        await using var vm = new MergeViewModel(service, new MergeStart([1, 2]));
        await vm.LoadAsync();

        vm.IsLocked.Should().BeTrue();
        service.Sessions.Should().BeEmpty();
        vm.Notes.Should().Contain(n => n.Kind == MergeNoteKind.Error && n.Text == MergeTexts.BlockedMixedLibraries);
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.OptionsEnabled.Should().BeFalse();
    });

    [Fact]
    public Task Pruefung_nicht_bestanden_sperrt_Speichern_und_nennt_den_Grund() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root) { Sources = [Source(1, 0, 0), Source(2, 1, 2)], Passed = false };
        await using var vm = new MergeViewModel(service, new MergeStart([1, 2]));
        await vm.LoadAsync();
        await WaitUntilAsync(() => vm.Preview is not null);

        vm.SaveCommand.CanExecute(null).Should().BeFalse("nie still speichern (Abnahme Nr. 7)");
        vm.Notes.Should().Contain(n => n.Kind == MergeNoteKind.Error && n.Text.Contains("Nicht speicherbar", StringComparison.Ordinal) && n.Text.Contains("gekippt", StringComparison.Ordinal));
    });

    [Fact]
    public Task Speichern_zeigt_Fertig_Ansicht_mit_Kennzeichnung_Album_und_Rueckgaengig() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root) { Sources = [Source(1, 0, 0), Source(2, 1, 2), Source(3, 2, 4)] };
        var results = new FakeResults();
        await using var vm = new MergeViewModel(service, new MergeStart([1, 2, 3], AlbumId: 9, AlbumName: "Ausflüge"), results: results);
        await vm.LoadAsync();
        await WaitUntilAsync(() => vm.Preview is not null);
        vm.HasAlbum.Should().BeTrue();
        vm.AlbumText.Should().Be("auch ins Album „Ausflüge“");

        await vm.SaveCommand.ExecuteAsync(null);

        var save = service.Sessions.Single().SaveOptions.Single();
        save.AlbumId.Should().Be(9);
        save.CopyRating.Should().BeTrue("Sterne und Favorit übernehmen (Abnahme Nr. 12)");
        vm.IsDone.Should().BeTrue();
        vm.MadeFromText.Should().Be("aus 3 Fotos zusammengefügt");
        vm.DoneRows.Should().Contain(r => r.Label == "Übernommen" && r.Value.Contains("Sterne", StringComparison.Ordinal));
        vm.DoneRows.Should().Contain(r => r.Label == "Album" && r.Value.Contains("Ausflüge", StringComparison.Ordinal));
        vm.Steps.Should().OnlyContain(s => s.IsDone);
        vm.ResultFileId.Should().Be(77);

        await vm.OpenCommand.ExecuteAsync(null);
        results.Opened.Should().Equal(77);
        await vm.UndoCommand.ExecuteAsync(null);
        results.Undone.Should().Equal(5);
        vm.IsSetup.Should().BeTrue("zurück zur Vorschau");
        vm.Notes.Should().Contain(n => n.Text.StartsWith("Rückgängig", StringComparison.Ordinal));
    });

    [Fact]
    public Task Im_Hintergrund_weiterrechnen_gibt_den_Vorrang_zurueck_und_schliesst() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root) { Sources = [Source(1, 0, 0), Source(2, 1, 2)], HoldSave = true };
        await using var vm = new MergeViewModel(service, new MergeStart([1, 2]));
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        await vm.LoadAsync();
        await WaitUntilAsync(() => vm.Preview is not null);
        var saving = vm.SaveCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsSaving, "Speichern läuft");
        vm.Steps.Should().HaveCount(9);
        vm.DiscardCommand.CanExecute(null).Should().BeFalse();

        vm.ContinueInBackgroundCommand.Execute(null);
        service.Sessions[0].Backgrounded.Should().BeTrue();
        closed.Should().Be(1);
        service.Sessions[0].ReleaseSave();
        await saving;
        vm.IsDone.Should().BeTrue();
    });

    [Fact]
    public Task Fenster_hell_und_dunkel_ohne_Binding_Fehler() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root) { Sources = [Source(1, 0, 0), Source(2, 1, 2)], Neighbors = [] };
        using var errors = new BindingErrors();
        var vm = new MergeViewModel(service, new MergeStart([1, 2], AlbumId: 3, AlbumName: "Album"), results: new FakeResults());
        var window = new MergeWindow(vm) { ShowActivated = false, Left = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.Preview is not null);
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme);
                window.UpdateLayout();
            }

            vm.ChooseProjectionCommand.Execute(PanoramaProjection.Spherical360);
            await WaitUntilAsync(() => vm.IsSphere);
            window.UpdateLayout();
            await vm.SaveCommand.ExecuteAsync(null);
            window.UpdateLayout();
            vm.IsDone.Should().BeTrue();
            errors.Messages.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }
    });

    [Fact]
    public Task Einstellungen_speichern_sofort_und_ohne_Binding_Fehler() => OnUiAsync(async () =>
    {
        var service = new FakeMergeService(_root);
        using var vm = new MergeSettingsViewModel(service);
        using var errors = new BindingErrors();
        var section = new SettingsMergeSection { DataContext = vm };
        var window = new Window { Content = section, Width = 1000, Height = 900, ShowActivated = false, Left = -10000 };
        window.Show();
        try
        {
            await vm.RefreshAsync();
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme);
                window.UpdateLayout();
            }

            vm.ColorBoost.Should().Be(100);
            vm.SphereBand.Should().BeTrue();
            vm.FileNameExample.Should().Be("IMG_0001_panorama.jpg");
            vm.SphereFull = true;
            vm.DroneFolderOneUp = true;
            await WaitUntilAsync(() => service.Saved.Count > 0 && service.Saved[^1] is { SphereForm: MergeSphereForm.Full, DroneFolderOneUp: true });
            vm.ColorBoost = 0;
            await WaitUntilAsync(() => service.Saved[^1].ColorBoost == 0);
            await WaitUntilAsync(() => vm.ToolState == "geladen");
            errors.Messages.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }
    });

    // ---- Menü, Marke, Lightbox ------------------------------------------------------------------------------------------

    [Fact]
    public Task Menue_Zusammenfuegen_steht_nach_Verbessern_mit_Zahl_und_ist_gesperrt_mit_Grund() => OnUiAsync(() =>
    {
        var started = 0;
        var host = PhotoMenuTestKit.GalleryHost() with { Enhance = () => { }, Merge = () => started++ };
        var menu = new ContextMenu();
        PhotoContextMenuBuilder.Build(menu.Items, new PhotoMenuContext(PhotoMenuSource.Gallery, PhotoMenuTestKit.Jpeg(), [7, 8, 9, 10, 11]), host, PhotoMenuTestKit.Actions());
        var edit = PhotoMenuTestKit.Item(menu.Items, "Bearbeiten");
        var headers = PhotoMenuTestKit.Headers(edit.Items);
        headers.IndexOf("5 Fotos zusammenfügen…").Should().Be(headers.IndexOf("5 Fotos verbessern…") + 1, "direkt nach „… verbessern…“, ohne Leerzeichen vor „…“");
        var item = PhotoMenuTestKit.Item(edit.Items, "5 Fotos zusammenfügen…");
        item.IsEnabled.Should().BeTrue();
        item.Command.Execute(null);
        started.Should().Be(1);

        // ein Foto: „Zusammenfügen…“ (Zeit-Nachbarn im Fenster), auch in der Lightbox
        PhotoContextMenuBuilder.Build(menu.Items, new PhotoMenuContext(PhotoMenuSource.Lightbox, PhotoMenuTestKit.Jpeg(), [7]), host, PhotoMenuTestKit.Actions());
        PhotoMenuTestKit.Headers(PhotoMenuTestKit.Item(menu.Items, "Bearbeiten").Items).Should().Contain("Zusammenfügen…");

        // gemischte Bibliotheken: gesperrt mit Grund als Tooltip
        var blocked = host with { MergeBlockedReason = MergeTexts.BlockedMixedLibraries };
        PhotoContextMenuBuilder.Build(menu.Items, new PhotoMenuContext(PhotoMenuSource.List, PhotoMenuTestKit.Jpeg(), [7, 8]), blocked, PhotoMenuTestKit.Actions());
        var off = PhotoMenuTestKit.Item(PhotoMenuTestKit.Item(menu.Items, "Bearbeiten").Items, "2 Fotos zusammenfügen…");
        off.IsEnabled.Should().BeFalse();
        off.ToolTip.Should().Be(MergeTexts.BlockedMixedLibraries);

        // ohne Anbindung kein Eintrag
        PhotoContextMenuBuilder.Build(menu.Items, new PhotoMenuContext(PhotoMenuSource.Gallery, PhotoMenuTestKit.Jpeg(), [7]), PhotoMenuTestKit.GalleryHost(), PhotoMenuTestKit.Actions());
        PhotoMenuTestKit.Headers(PhotoMenuTestKit.Item(menu.Items, "Bearbeiten").Items).Should().NotContain(h => h.Contains("zusammenfügen", StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Suchen_zeigt_bei_einem_Ergebnis_Quellen_und_bei_einer_Quelle_das_Ergebnis() => OnUiAsync(async () =>
    {
        var store = new FakeStore { Marks = { [7] = new MergedPhotoMark(7, MergeMethod.Panorama, PanoramaProjection.Cylindrical, 3, 3) }, Sources = { [7] = [1, 2, 3] }, Results = { [2] = [7] } };
        var applied = new List<RelatedPhotos>();
        var host = PhotoMenuTestKit.GalleryHost() with { ApplySearch = r => { applied.Add(r); return Task.CompletedTask; } };
        var actions = ActionsWith(store);
        var menu = new ContextMenu();
        PhotoContextMenuBuilder.Build(menu.Items, new PhotoMenuContext(PhotoMenuSource.Gallery, PhotoMenuTestKit.Jpeg(7), [7]), host, actions);
        var search = PhotoMenuTestKit.Item(menu.Items, "Suchen");
        await PhotoContextMenuBuilder.FillSubmenuAsync(search);
        PhotoMenuTestKit.Headers(search.Items).Should().Contain("Quellen").And.NotContain("Zusammengefügtes Ergebnis");
        PhotoMenuTestKit.Item(search.Items, "Quellen").Command.Execute(null);
        await WaitUntilAsync(() => applied.Count == 1);
        applied[0].FileIds.Should().Equal(7, 1, 2, 3);
        applied[0].Label.Should().Be("Quellen von urlaub.jpg");

        PhotoContextMenuBuilder.Build(menu.Items, new PhotoMenuContext(PhotoMenuSource.Gallery, PhotoMenuTestKit.Jpeg(2), [2]), host, actions);
        search = PhotoMenuTestKit.Item(menu.Items, "Suchen");
        await PhotoContextMenuBuilder.FillSubmenuAsync(search);
        PhotoMenuTestKit.Item(search.Items, "Zusammengefügtes Ergebnis").Command.Execute(null);
        await WaitUntilAsync(() => applied.Count == 2);
        applied[1].FileIds.Should().Equal(2, 7);
    });

    [Fact]
    public void Ergebnis_Marke_nennt_Verfahren_und_Quellenzahl()
    {
        var item = new GalleryItem(new GalleryFileDto(5, 1, "a_panorama.jpg", ".jpg", 100, 0, 0, false, null, false, @"C:\Fotos"));
        item.IsMergeResult.Should().BeFalse();
        item.MergeMarkText.Should().BeEmpty();
        item.MergeMark = new MergedPhotoMark(5, MergeMethod.Panorama, PanoramaProjection.Spherical360, 26, 24);
        item.IsMergeResult.Should().BeTrue();
        item.MergeMarkText.Should().Be("Panorama · aus 26 Fotos");
        item.MergeMarkToolTip.Should().Contain("aus 26 Fotos zusammengefügt").And.Contain("2 nicht mehr in der Bibliothek");
    }

    [Fact]
    public void Lightbox_Zeile_und_Abschnitt_Zusammengefuegt_bei_einem_Ergebnis()
    {
        TestUiThread.Run(() =>
        {
            var recipe = new MergeRecipe(MergeRecipe.CurrentVersion, MergeMethod.Panorama, new MergeOptions(PanoramaProjection.Auto, MergeSphereForm.Full, MergeCrop.Straight, ColorBoost: 0),
                PanoramaProjection.Spherical360, MergeSphereForm.Full, [], new AlignmentSpec { Cameras = [] }, 1, "Lumora 0.5.47", 0);
            var store = new FakeStore();
            store.Infos[5] = new MergedPhotoInfo(5, MergeMethod.Panorama, PanoramaProjection.Spherical360, 26,
                [new MergedSourceEntry(1, 0, false), new MergedSourceEntry(2, 1, true)], "merge:panorama", recipe.ToJson(), DateTime.UtcNow, 3, null);
            var actions = new MergeGalleryActions(new ServiceCollection().BuildServiceProvider(), store, null!, new FakeWindows());
            using var vm = new LightboxViewModel(new LibVlcHost(NullLogger<LibVlcHost>.Instance), contextFactory: null!, NullLogger<LightboxViewModel>.Instance, merge: actions);
            vm.SetMergeAlbum(4, "Ausflüge");
            vm.Load([Entry(5), Entry(6)], 0);
            Pump(() => vm.HasMergeInfo);
            vm.MergeRowText.Should().Be("Zusammengefügt aus 26 Fotos · Panorama, Kugel 360°");
            vm.MergeInfoRows.Select(r => r.Label).Should().Contain(["Verfahren", "Aufnahmeart", "Farbe kräftiger", "Quellen", "Erstellt"]);
            vm.MergeInfoRows.Single(r => r.Label == "Aufnahmeart").Value.Should().Be("Kugel 360° · 2 : 1 (Rundum-Bild)");
            vm.MergeInfoRows.Single(r => r.Label == "Farbe kräftiger").Value.Should().Be("aus");
            vm.MergeInfoRows.Single(r => r.Label == "Quellen").Value.Should().Be("26 · 25 nicht mehr in der Bibliothek");
            vm.MergeAlbumId.Should().Be(4, "die Lightbox übernimmt das Album der Galerie");

            vm.NextCommand.Execute(null);
            Pump(() => !vm.HasMergeInfo);
            vm.MergeInfoRows.Should().BeEmpty("kein Ergebnis");
            return 0;
        }, Timeout);
    }

    [Fact]
    public void Sperre_bei_gemischten_Bibliotheken_nur_Videos_und_nicht_verfuegbarem_Rechen_Prozess()
    {
        var actions = new MergeGalleryActions(new ServiceCollection().BuildServiceProvider(), new FakeStore(), null!, new FakeWindows());
        actions.BlockReason([(1, MediaType.Image, false), (1, MediaType.Image, false)]).Should().BeNull();
        actions.BlockReason([(1, MediaType.Image, false), (2, MediaType.Image, false)]).Should().Be(MergeTexts.BlockedMixedLibraries);
        actions.BlockReason([(1, MediaType.Video, false), (1, MediaType.Archive, false)]).Should().Be(MergeTexts.BlockedNoPhotos);
        actions.BlockReason([(1, MediaType.Image, true)]).Should().Be(MergeTexts.BlockedNoPhotos, "fehlende Datei");
        actions.BlockReason([(1, MediaType.Image, false), (2, MediaType.Video, false)]).Should().BeNull("Videos werden im Fenster ausgelassen");
    }

    [Fact]
    public async Task Aktivitaet_Zusammenfuegen_zeigt_Oeffnen_und_oeffnet_das_Ergebnis()
    {
        using var center = new Lumora.Services.Activity.ActivityCenter();
        var navigator = new FakeNavigator();
        var vm = TestUiThread.Run(() => new Lumora.Shell.Activity.ActivityStatusViewModel(center, navigator: navigator), Timeout);
        try
        {
            center.Start(new Lumora.Services.Activity.ActivityStartInfo(Lumora.Services.Activity.ActivityKind.Merge, "Panorama zusammenfügen", "3 Fotos", UserInitiated: true))
                .Complete(new Lumora.Services.Activity.ActivityCompletion("Panorama gespeichert", Actions: [Lumora.Services.Activity.ActivityAction.ShowResult([77])]));
            await WaitUntilAsync(() => TestUiThread.Run(() => vm.IsToastVisible, Timeout));
            TestUiThread.Run(() => vm.ToastHasShowResult && vm.ToastShowResultLabel == "Öffnen", Timeout).Should().BeTrue();
            var item = TestUiThread.Run(() => { vm.Refresh(); return vm.Recent.First(); }, Timeout);
            TestUiThread.Run(() => item.HasShowResult && item.ShowResultLabel == "Öffnen", Timeout).Should().BeTrue();

            TestUiThread.Run(() => { vm.ShowResultToastCommand.Execute(null); return 0; }, Timeout);
            navigator.Shown.Should().ContainSingle().Which.Should().Equal(77);
            TestUiThread.Run(() => { item.ShowResultCommand.Execute(null); return 0; }, Timeout);
            navigator.Shown.Should().HaveCount(2);
        }
        finally
        {
            TestUiThread.Run(() => { vm.Dispose(); return 0; }, Timeout);
        }
    }

    private sealed class FakeNavigator : Lumora.Shell.Activity.IActivityNavigator
    {
        public List<IReadOnlyList<int>> Shown { get; } = [];

        public void ShowMissingFiles()
        {
        }

        public void ShowFiles(IReadOnlyList<int> fileIds) => Shown.Add(fileIds);
    }

    private static PhotoActions ActionsWith(IMergedPhotoStore store)
        => new(new ScriptedDialogs(), NullLogger<PhotoActions>.Instance, related: NullService.Create<IRelatedPhotoSearch>(), mergedPhotos: store);

    private static LightboxEntry Entry(int id) => new(id, Path.Combine(Path.GetTempPath(), "lumora-merge-test-gibt-es-nicht", $"f{id}.jpg"), $"f{id}.jpg", MediaType.Image, false, null, 1000, DateTime.UtcNow);

    private static void Pump(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException("Bedingung nicht erreicht");
            }

            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    // ---- Fakes ---------------------------------------------------------------------------------------------------------

    internal sealed class FakeMergeService(string root) : IMergeService
    {
        public IReadOnlyList<MergeSourceInfo> Sources { get; set; } = [];

        public IReadOnlyList<MergeSourceInfo> Neighbors { get; set; } = [];

        public bool Mixed { get; set; }

        public bool Passed { get; set; } = true;

        public bool HoldSave { get; set; }

        public List<FakeSession> Sessions { get; } = [];

        public List<MergeSettings> Saved { get; } = [];

        public MergeSettings Settings { get; set; } = MergeSettings.Default;

        public Task<MergeAvailability> GetAvailabilityAsync(CancellationToken ct = default) => Task.FromResult(new MergeAvailability(true, null, "4.13"));

        public Task<MergeAnalysis> AnalyzeAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        {
            var methods = Enum.GetValues<MergeMethod>().Select(m => m == MergeMethod.Panorama
                ? new MergeMethodInfo(m, MergeTexts.Method(m), true, true, "passt · überlappende Fotos einer Szene")
                : new MergeMethodInfo(m, MergeTexts.Method(m), false, false, "kommt später")).ToList();
            var notes = Mixed ? new List<MergeNote> { new("mixed-libraries", MergeTexts.BlockedMixedLibraries) } : [];
            return Task.FromResult(new MergeAnalysis(Sources, Neighbors, Mixed ? null : 1, Mixed, methods, MergeMethod.Panorama, PanoramaProjection.Auto, notes, 0, 0));
        }

        public IMergePreviewSession OpenPreviewSession(MergeRequest request)
        {
            var session = new FakeSession(this, request, root);
            Sessions.Add(session);
            return session;
        }

        public Task<MergeSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Settings);

        public Task SaveSettingsAsync(MergeSettings settings, CancellationToken ct = default)
        {
            Saved.Add(settings);
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeSession(FakeMergeService owner, MergeRequest request, string root) : IMergePreviewSession
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MergeRequest Request { get; } = request;

        public MergeAnalysis? Analysis => null;

        public MergePreview? Current { get; private set; }

        public MergeOptions Options { get; private set; } = request.Options ?? new MergeOptions();

        public bool IsBusy => false;

        public int StartCalls { get; private set; }

        public List<MergeOptions> Updates { get; } = [];

        public List<MergeSaveOptions> SaveOptions { get; } = [];

        public bool Backgrounded { get; private set; }

        public event Action<MergeProgress>? ProgressChanged;

        public Task<MergePreview> StartAsync(CancellationToken ct = default)
        {
            StartCalls++;
            return Task.FromResult(Make(Options));
        }

        public Task<MergePreview> UpdateAsync(MergeOptions options, CancellationToken ct = default)
        {
            Updates.Add(options);
            Options = options;
            return Task.FromResult(Make(options));
        }

        public MergeEstimate Estimate() => new(2000, 800, 1, null, 2_000_000_000, 9_000_000_000, TimeSpan.FromSeconds(40), 400_000);

        public async Task<MergeSaveResult> SaveAsync(MergeSaveOptions? options = null, CancellationToken ct = default)
        {
            SaveOptions.Add(options!);
            ProgressChanged?.Invoke(new MergeProgress(true, WorkerStep.Seams, "Nähte suchen", 1, 1, 0.4));
            if (owner.HoldSave)
            {
                await _release.Task;
            }

            return new MergeSaveResult(true, 77, 5, Path.Combine(root, "IMG_0001_panorama.jpg"), 2000, 800, 1, [], null);
        }

        public void ReleaseSave() => _release.TrySetResult();

        public void ContinueInBackground() => Backgrounded = true;

        public void Cancel()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private MergePreview Make(MergeOptions options)
        {
            var path = Path.Combine(root, $"preview-{Guid.NewGuid():N}.jpg");
            TestImages.SaveJpeg(path, TestImages.LeftRedRightBlue(100, 50));
            MergePreviewFrame? frame = null;
            if (options.Crop == MergeCrop.Straight)
            {
                var framePath = Path.Combine(root, $"frame-{Guid.NewGuid():N}.jpg");
                TestImages.SaveJpeg(framePath, TestImages.LeftRedRightBlue(120, 62));
                frame = new MergePreviewFrame(framePath, 120, 62, 10, 6, 100, 50);
            }

            var quality = owner.Passed
                ? new MergeQuality(true, [], 0.5, 0.8, 1.2, Request.FileIds.Count, Request.FileIds.Count)
                : new MergeQuality(false, [MergeTexts.Problem("tilted")], 3, 0.1, 0.3, Request.FileIds.Count, Request.FileIds.Count);
            var projection = options.Projection == PanoramaProjection.Auto ? PanoramaProjection.Cylindrical : options.Projection;
            Current = new MergePreview(path, 100, 50, 2000, 800, projection, PanoramaProjection.Cylindrical, options.SphereForm ?? MergeSphereForm.Band, quality, [], [], TimeSpan.FromSeconds(1.5), frame);
            return Current;
        }
    }

    private sealed class FakeResults : IMergeResultActions
    {
        public List<int> Opened { get; } = [];

        public List<int> Undone { get; } = [];

        public bool IsExplorerBlocked => true;

        public Task OpenResultAsync(int resultFileId, Window? owner)
        {
            Opened.Add(resultFileId);
            return Task.CompletedTask;
        }

        public Task ShowInGalleryAsync(int resultFileId, string name) => Task.CompletedTask;

        public Task<string> UndoAsync(int batchId)
        {
            Undone.Add(batchId);
            return Task.FromResult("Ergebnis in den Papierkorb");
        }

        public Task EnhanceAsync(int resultFileId, Window? owner) => Task.CompletedTask;

        public string? ShowInExplorer(string path) => "gesperrt";
    }

    private sealed class FakeStore : IMergedPhotoStore
    {
        public Dictionary<int, MergedPhotoMark> Marks { get; } = [];

        public Dictionary<int, IReadOnlyList<int>> Sources { get; } = [];

        public Dictionary<int, IReadOnlyList<int>> Results { get; } = [];

        public Dictionary<int, MergedPhotoInfo> Infos { get; } = [];

        public Task<IReadOnlyDictionary<int, MergedPhotoMark>> GetMarksAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<int, MergedPhotoMark>>(Marks.Where(m => fileIds.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value));

        public Task<IReadOnlySet<int>> GetSourceFileIdsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<int>>(fileIds.Where(Results.ContainsKey).ToHashSet());

        public Task<MergedPhotoInfo?> GetInfoAsync(int resultFileId, CancellationToken ct = default) => Task.FromResult(Infos.GetValueOrDefault(resultFileId));

        public Task<IReadOnlyList<int>> GetSourcesAsync(int resultFileId, CancellationToken ct = default) => Task.FromResult(Sources.GetValueOrDefault(resultFileId) ?? []);

        public Task<IReadOnlyList<int>> GetResultsOfSourceAsync(int sourceFileId, CancellationToken ct = default) => Task.FromResult(Results.GetValueOrDefault(sourceFileId) ?? []);
    }

    private sealed class FakeWindows : IMergeWindowService
    {
        public bool IsSaving => false;

        public MergeWindow? Current => null;

        public void Show(MergeStart start, Window? owner = null)
        {
        }
    }

    private sealed class BindingErrors : TraceListener
    {
        public BindingErrors()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }

        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
