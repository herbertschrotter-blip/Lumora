using System.Windows.Controls;
using Lumora.PhotoMenu;
using Lumora.Services.Covers;
using Lumora.Services.Export;
using Lumora.Services.Tags;
using Lumora.Wpf.Tests.Fixtures;
using static Lumora.Wpf.Tests.PhotoMenu.PhotoMenuTestKit;

namespace Lumora.Wpf.Tests.PhotoMenu;

/// <summary>
/// Neu gegliedertes Rechtsklick-Menü (ADR-0068, Mockup 17): gleiche Struktur für Galerie-Kachel, Listenzeile und Lightbox,
/// Archiv-Einträge oben, Tastenkürzel, Mehrfachauswahl nur mit sinnvollen Einträgen, Drehen nur JPEG, Weg nach außen in der
/// versteckten Bibliothek gesperrt, „Als Titelbild für ▸“ mit Gruppen und Häkchen.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PhotoContextMenuBuilderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task OnUi(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private static ContextMenu Build(PhotoMenuContext context, PhotoMenuHost host, PhotoActions actions)
    {
        var menu = new ContextMenu();
        PhotoContextMenuBuilder.Build(menu.Items, context, host, actions);
        return menu;
    }

    [Fact]
    public void Galerie_Einzelfoto_hat_die_abgenommene_Gliederung_mit_Kuerzeln()
    {
        TestUiThread.Run(() =>
        {
            var menu = Build(new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7]), GalleryHost(), Actions());

            Headers(menu.Items).Should().Equal(
                "Öffnen", "Diashow ab hier", "—",
                "Bewerten", "Tags", "Kategorien", "Personen", "Alben", "Story", "Als Titelbild für", "—",
                "Bearbeiten", "Suchen", "—",
                "Datei", "In den Papierkorb", "—",
                "Eigenschaften");
            Item(menu.Items, "Öffnen").InputGestureText.Should().Be("Enter");
            Item(menu.Items, "In den Papierkorb").InputGestureText.Should().Be("Entf");
            Item(menu.Items, "Eigenschaften").InputGestureText.Should().Be("Alt+Enter");

            var file = Item(menu.Items, "Datei");
            Headers(file.Items).Should().Equal(
                "Kopieren (in die Zwischenablage)", "Kopieren nach…", "Verschieben nach…", "Umbenennen…", "In die Bibliothek…", "—",
                "Im Explorer anzeigen", "Pfad kopieren", "Mit Standard-App öffnen", "—", "Exportieren (verkleinert)");
            Item(file.Items, "Kopieren (in die Zwischenablage)").InputGestureText.Should().Be("Strg+C");
            Item(file.Items, "In die Bibliothek…").InputGestureText.Should().Be("Strg+M");
            Item(file.Items, "Exportieren (verkleinert)").Items.Count.Should().Be(PhotoExportOptions.Sizes.Count);

            var edit = Item(menu.Items, "Bearbeiten");
            Headers(edit.Items).Should().Equal("Datum setzen…", "Ort setzen…", "—", "Nach links drehen", "Nach rechts drehen", "—", "Foto verbessern…");
            Item(edit.Items, "Nach rechts drehen").IsEnabled.Should().BeTrue();
            Item(edit.Items, "Foto verbessern…").IsEnabled.Should().BeFalse("ohne Anbindung (Host.Enhance) ausgegraut — ADR-0071");
            return true;
        }, Timeout);
    }

    [Fact]
    public void Fehlendes_Foto_laesst_sich_aus_der_Bibliothek_entfernen()
    {
        TestUiThread.Run(() =>
        {
            var removed = 0;
            var context = new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg() with { IsMissing = true }, [7]);
            var menu = Build(context, GalleryHost() with { RemoveMissing = () => removed++ }, Actions());

            var headers = Headers(menu.Items);
            headers.IndexOf("Aus der Bibliothek entfernen").Should().Be(headers.IndexOf("In den Papierkorb") + 1);
            Item(menu.Items, "Aus der Bibliothek entfernen").Command.Execute(null);
            removed.Should().Be(1);

            Headers(Build(context, GalleryHost(), Actions()).Items).Should().NotContain("Aus der Bibliothek entfernen", "ohne fehlende Dateien kein Eintrag");
            return true;
        }, Timeout);
    }

    [Fact]
    public void Mehrfachauswahl_zeigt_nur_sinnvolle_Eintraege()
    {
        TestUiThread.Run(() =>
        {
            var menu = Build(new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7, 8, 9]), GalleryHost(), Actions());

            var headers = Headers(menu.Items);
            headers.Should().NotContain(["Öffnen", "Personen", "Als Titelbild für", "Suchen", "Eigenschaften"]);
            headers.Should().Contain(["Bewerten", "Tags", "Kategorien", "Alben", "Story", "Bearbeiten", "Datei", "In den Papierkorb"]);
            Headers(Item(menu.Items, "Datei").Items).Should().NotContain(["Umbenennen…", "Im Explorer anzeigen", "Pfad kopieren", "Mit Standard-App öffnen"]);
            return true;
        }, Timeout);
    }

    [Fact]
    public void Lightbox_ohne_Oeffnen_und_Suchen_mit_ihren_Kuerzeln()
    {
        TestUiThread.Run(() =>
        {
            var host = GalleryHost(search: false) with { Open = null };
            var context = new PhotoMenuContext(PhotoMenuSource.Lightbox, Jpeg(), [7]);
            var menu = Build(context, host, Actions());

            Headers(menu.Items).Should().NotContain(["Öffnen", "Suchen"]);
            Item(menu.Items, "Diashow ab hier").InputGestureText.Should().Be("S");
            PhotoMenuGestures.Rating(context, 3).Should().Be("Strg+3");
            PhotoMenuGestures.Favorite(context).Should().Be("Strg+D");
            PhotoMenuGestures.Rating(new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7]), 3).Should().BeNull("in der Galerie gibt es kein Kürzel dafür");
            return true;
        }, Timeout);
    }

    [Fact]
    public void Archiv_hat_Inhalt_Entpacken_und_In_die_Bibliothek_ganz_oben()
    {
        TestUiThread.Run(() =>
        {
            var menu = Build(new PhotoMenuContext(PhotoMenuSource.Gallery, Zip(), [9]), GalleryHost(), Actions());

            Headers(menu.Items).Take(4).Should().Equal("Inhalt anzeigen", "Entpacken…", "In die Bibliothek…", "—");
            Headers(menu.Items).Should().NotContain(["Öffnen", "Als Titelbild für", "Suchen", "Personen"]);
            return true;
        }, Timeout);
    }

    [Fact]
    public void Drehen_nur_fuer_JPEG_und_versteckt_kein_Weg_nach_aussen()
    {
        TestUiThread.Run(() =>
        {
            var png = Build(new PhotoMenuContext(PhotoMenuSource.List, Png(), [8]), GalleryHost(), Actions());
            var rotate = Item(Item(png.Items, "Bearbeiten").Items, "Nach links drehen");
            rotate.IsEnabled.Should().BeFalse();
            ((string)rotate.ToolTip).Should().Contain("JPEG");

            var hidden = Build(new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7]), GalleryHost(), Actions(blocked: true));
            var file = Item(hidden.Items, "Datei");
            var copy = Item(file.Items, "Kopieren (in die Zwischenablage)");
            copy.IsEnabled.Should().BeFalse();
            copy.ToolTip.Should().Be(ExternalFileSet.BlockedText);
            Item(file.Items, "Exportieren (verkleinert)").IsEnabled.Should().BeFalse();
            Item(file.Items, "Kopieren nach…").IsEnabled.Should().BeTrue("innerhalb der Bibliothek bleibt Kopieren erlaubt");
            return true;
        }, Timeout);
    }

    [Fact]
    public async Task Als_Titelbild_fuer_zeigt_Gruppen_Haekchen_und_setzt_das_Ziel()
    {
        var (covers, script) = ScriptedService.Create<ICoverService>();
        var targets = new List<CoverTarget>
        {
            new(CoverTargetKind.Person, 3, "Anna", IsCurrent: false, IsManual: false, FaceId: 31),
            new(CoverTargetKind.Album, 5, "Sommer", IsCurrent: true, IsManual: true),
            new(CoverTargetKind.Folder, 11, "Reise", IsCurrent: true, IsManual: false),
        };
        script.Handlers[nameof(ICoverService.GetTargetsAsync)] = _ => Task.FromResult<IReadOnlyList<CoverTarget>>(targets);
        CoverTarget? set = null;
        script.Handlers[nameof(ICoverService.SetAsync)] = args =>
        {
            set = (CoverTarget)args[0]!;
            return Task.CompletedTask;
        };
        var status = new List<string>();

        await OnUi(async () =>
        {
            var menu = Build(new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7]), GalleryHost(status), Actions(covers: covers));
            var cover = Item(menu.Items, "Als Titelbild für");
            await PhotoContextMenuBuilder.FillSubmenuAsync(cover);

            Headers(cover.Items).Should().Equal("Personen im Foto", "Anna", "Alben", "Sommer", "Ordner", "Reise", "—", "Wieder automatisch wählen lassen…");
            Item(cover.Items, "Anna").IsChecked.Should().BeFalse();
            Item(cover.Items, "Sommer").IsChecked.Should().BeTrue();
            Item(cover.Items, "Reise").IsChecked.Should().BeTrue("automatisch gewählt ist auch ein Häkchen");
            var reset = Item(cover.Items, "Wieder automatisch wählen lassen…");
            reset.IsEnabled.Should().BeTrue();
            ((IReadOnlyList<CoverTarget>)reset.Tag).Should().ContainSingle().Which.Name.Should().Be("Sommer", "nur selbst gewählte gehen zurück auf automatisch");

            Item(cover.Items, "Anna").Command.Execute(null);
            await Task.Delay(50);
        });

        set.Should().Be(targets[0]);
        status.Should().ContainSingle().Which.Should().Contain("Person „Anna“");
    }

    [Fact]
    public async Task Tags_zeigen_Zustand_ueber_die_Auswahl()
    {
        var (tags, script) = ScriptedService.Create<ITagService>();
        script.Handlers[nameof(ITagService.GetAllAsync)] = _ => Task.FromResult<IReadOnlyList<TagInfo>>(
            [new TagInfo(1, "Urlaub", "#336699", 0), new TagInfo(2, "Familie", "#993366", 1), new TagInfo(3, "Leer", "#999999", 2)]);
        script.Handlers[nameof(ITagService.GetTagIdsForFilesAsync)] = _ => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(
            new Dictionary<int, IReadOnlyList<int>> { [7] = [1, 2], [8] = [1] });

        await OnUi(async () =>
        {
            var menu = Build(new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7, 8]), GalleryHost(), Actions(tags: tags));
            var tagMenu = Item(menu.Items, "Tags");
            await PhotoContextMenuBuilder.FillSubmenuAsync(tagMenu);

            Headers(tagMenu.Items).Should().Equal("Urlaub", "Familie  (teilweise)", "Leer", "—", "Neuer Tag…", "Tags verwalten…");
            Item(tagMenu.Items, "Urlaub").IsChecked.Should().BeTrue();
            Item(tagMenu.Items, "Leer").IsChecked.Should().BeFalse();
            Item(tagMenu.Items, "Neuer Tag…").InputGestureText.Should().Be("Strg+T");
        });
    }

    [Fact]
    public void Foto_verbessern_ist_mit_Anbindung_frei_und_nennt_bei_Mehrfachauswahl_die_Zahl()
    {
        // ADR-0071: „Bearbeiten ▸ Foto verbessern…“ — eines → Leiste in der Lightbox, mehrere → Dialog; Videos ausgegraut
        TestUiThread.Run(() =>
        {
            var calls = 0;
            var host = GalleryHost() with { Enhance = () => calls++ };
            var single = Item(Build(new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7]), host, Actions()).Items, "Bearbeiten");
            Item(single.Items, "Foto verbessern…").IsEnabled.Should().BeTrue();
            Item(single.Items, "Foto verbessern…").Command.Execute(null);
            calls.Should().Be(1);

            var multi = Item(Build(new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7, 8, 9]), host, Actions()).Items, "Bearbeiten");
            Item(multi.Items, "3 Fotos verbessern…").IsEnabled.Should().BeTrue();

            var video = new PhotoMenuTarget(7, "film.mp4", @"C:\Bibliothek\film.mp4", ".mp4", Lumora.Domain.Enums.MediaType.Video, false);
            var videoEdit = Item(Build(new PhotoMenuContext(PhotoMenuSource.Gallery, video, [7]), host, Actions()).Items, "Bearbeiten");
            Item(videoEdit.Items, "Foto verbessern…").IsEnabled.Should().BeFalse("nur Fotos");
            return true;
        }, Timeout);
    }
}
