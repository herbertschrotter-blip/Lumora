using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Activity;
using Lumora.Services.Enhance;
using Lumora.Services.Scanning;

namespace Lumora.Tests.Enhance;

/// <summary>
/// „Foto verbessern“ (ADR-0071): Vorschau → neue Datei daneben, verknüpft, Original unverändert, Rückgängig in den Papierkorb,
/// Metadaten-Übernahme, fehlendes Modell = kein Lauf, mehrere Fotos mit Abbruch, versteckte Sitzung schreibt nichts nach außen.
/// Fake-Modelle (kleine Bildoperationen), kein echtes Modell.
/// </summary>
public sealed class PhotoEnhanceServiceTests
{
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Speichern_legt_neue_Datei_daneben_an_verknuepft_und_laesst_das_Original_unveraendert()
    {
        await using var w = await EnhanceWorld.CreateAsync(["Garten.jpg"]);
        var id = await w.IdAsync("Garten.jpg");
        var original = w.PathOf("Garten.jpg");
        var before = Hash(original);

        var preview = await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true, UpscaleFactor = 2, Brightness = 0.2 });
        preview.Notes.Should().Contain("Natürlichkeit 35 %, Körnung 20 %.", "ohne Angabe gelten die Einstellungen (0.5.6)");

        preview.Width.Should().Be(FakeEnhanceCodec.DefaultWidth * 2);
        preview.AppliedTools.Should().Equal(EnhanceTool.Adjust, EnhanceTool.Upscale);
        preview.ToolsKey.Should().Be("adjust+upscale");
        preview.ResultPath.Should().StartWith(w.Service.TempFolder, "Zwischenstand im Temp-Ordner des Katalogs");
        File.Exists(w.PathOf("Garten_verbessert.jpg")).Should().BeFalse("gespeichert wird erst auf Klick");

        var used = new List<Lumora.Services.Messaging.UsageSignalMessage>();
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Register<Lumora.Services.Messaging.UsageSignalMessage>(w.Fx.Messenger, used, (_, m) => used.Add(m));
        var saved = await w.Service.SaveAsync(preview);

        saved.Success.Should().BeTrue(saved.Error);
        used.Should().ContainSingle().Which.FileIds.Should().Equal(id); // „Verbessern“ = Verwendung des Originals (ADR-0072)
        saved.Path.Should().Be(w.PathOf("Garten_verbessert.jpg"));
        File.Exists(saved.Path).Should().BeTrue();
        File.Exists(preview.ResultPath).Should().BeFalse("der Zwischenstand wurde verschoben");
        Hash(original).Should().Be(before, "das Original bleibt IMMER unverändert");
        await using var ctx = w.Fx.CreateContext();
        var row = await ctx.Files.AsNoTracking().SingleAsync(f => f.Id == saved.NewFileId);
        row.Name.Should().Be("Garten_verbessert.jpg");
        row.Width.Should().Be(FakeEnhanceCodec.DefaultWidth * 2);
        var link = await ctx.PhotoVariants.AsNoTracking().SingleAsync();
        link.FileId.Should().Be(saved.NewFileId);
        link.OriginalFileId.Should().Be(id);
        link.Tools.Should().Be("adjust+upscale");
        link.BatchId.Should().Be(saved.BatchId);
        (await w.Fx.GetBatchAsync(saved.BatchId))!.OperationType.Should().Be(OperationType.CreateVariant);
        w.Fx.FtsRowIds("verbessert").Should().Contain(saved.NewFileId);
    }

    [Fact]
    public async Task Rueckgaengig_legt_die_neue_Datei_in_den_Papierkorb_und_raeumt_den_Katalog_auf()
    {
        await using var w = await EnhanceWorld.CreateAsync(["Garten.jpg"]);
        var id = await w.IdAsync("Garten.jpg");
        var saved = await w.Service.SaveAsync(await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true }));

        var undo = await w.Ops.UndoBatchAsync(saved.BatchId);

        undo!.Succeeded.Should().Be(1);
        w.Bin.Recycled.Should().Equal(saved.Path);
        File.Exists(saved.Path).Should().BeFalse();
        File.Exists(w.PathOf("Garten.jpg")).Should().BeTrue();
        (await w.Fx.FindFileByIdAsync(saved.NewFileId)).Should().BeNull();
        (await w.Fx.FindFileByIdAsync(id)).Should().NotBeNull();
        await using var ctx = w.Fx.CreateContext();
        (await ctx.PhotoVariants.CountAsync()).Should().Be(0);
        w.Fx.FtsRowIds("verbessert").Should().BeEmpty();
        (await w.Fx.GetBatchAsync(saved.BatchId))!.Status.Should().Be(BatchStatus.Undone);
    }

    [Fact]
    public async Task Belegter_Name_wird_nummeriert_und_nie_ueberschrieben()
    {
        await using var w = await EnhanceWorld.CreateAsync(["Garten.jpg", "Garten_verbessert.jpg"]);
        var existing = Hash(w.PathOf("Garten_verbessert.jpg"));
        var id = await w.IdAsync("Garten.jpg");
        (await w.Service.SuggestFileNameAsync(id)).Should().Be("Garten_verbessert (2).jpg");

        var saved = await w.Service.SaveAsync(await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true }));

        saved.Path.Should().Be(w.PathOf("Garten_verbessert (2).jpg"));
        Hash(w.PathOf("Garten_verbessert.jpg")).Should().Be(existing);
    }

    [Fact]
    public async Task Metadaten_Sterne_und_Tags_werden_nach_Einstellung_uebernommen()
    {
        await using var w = await EnhanceWorld.CreateAsync(["Scan.jpg", "Zweit.jpg"]);
        var id = await w.IdAsync("Scan.jpg");
        var id2 = await w.IdAsync("Zweit.jpg");
        var captured = new DateTime(1987, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        await using (var ctx = w.Fx.CreateContext())
        {
            foreach (var f in await ctx.Files.Where(f => f.Id == id || f.Id == id2).ToListAsync())
            {
                f.CapturedAt = captured;
                f.CapturedAtSource = CaptureDateSource.Manual;
                f.CapturedAtOffset = 120;
                f.Latitude = 47.07;
                f.Longitude = 15.44;
                f.GeoSource = GeoSource.Manual;
                f.Rating = 4;
                f.IsFavorite = true;
            }

            var tag = new Tag { Name = "Familie", Color = "#336699", CreatedAtUtc = DateTime.UtcNow };
            ctx.Tags.Add(tag);
            await ctx.SaveChangesAsync();
            ctx.FileTags.Add(new FileTag { FileId = id, TagId = tag.Id });
            ctx.FileTags.Add(new FileTag { FileId = id2, TagId = tag.Id });
            await ctx.SaveChangesAsync();
        }

        var withAll = await w.Service.SaveAsync(await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true }));
        await w.Settings.SaveAsync(EnhanceSettings.Default with { CopyRatingAndTags = false, CopyMetadata = false });
        var without = await w.Service.SaveAsync(await w.Service.RenderAsync(id2, new EnhanceOptions { Upscale = true }));

        var a = (await w.Fx.FindFileByIdAsync(withAll.NewFileId))!;
        a.CapturedAt.Should().Be(captured);
        a.CapturedAtSource.Should().Be(CaptureDateSource.Manual);
        a.CapturedAtOffset.Should().Be(120);
        a.Latitude.Should().Be(47.07);
        a.Longitude.Should().Be(15.44);
        a.GeoSource.Should().Be(GeoSource.Manual);
        a.Rating.Should().Be(4);
        a.IsFavorite.Should().BeTrue();
        var b = (await w.Fx.FindFileByIdAsync(without.NewFileId))!;
        b.Rating.Should().BeNull();
        b.IsFavorite.Should().BeFalse();
        b.Latitude.Should().BeNull();
        b.CapturedAtSource.Should().BeNull("der Hintergrund liest das Datum dann aus der Datei");
        await using var check = w.Fx.CreateContext();
        (await check.FileTags.CountAsync(t => t.FileId == withAll.NewFileId)).Should().Be(1);
        (await check.FileTags.CountAsync(t => t.FileId == without.NewFileId)).Should().Be(0);
    }

    [Fact]
    public async Task EXIF_des_JPEG_Originals_kommt_in_die_Fassung_mit_Ausrichtung_1()
    {
        await using var w = await EnhanceWorld.CreateAsync(["Alt.jpg"]);
        var original = w.PathOf("Alt.jpg");
        await File.WriteAllBytesAsync(original, EnhanceWorld.JpegWithExif(FakeEnhanceCodec.Gradient(20, 10), orientation: 6));
        var id = await w.IdAsync("Alt.jpg");
        var before = Hash(original);

        var saved = await w.Service.SaveAsync(await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true }));

        var exif = JpegExifCopier.ReadExifSegment(await File.ReadAllBytesAsync(saved.Path));
        exif.Should().NotBeNull("Aufnahmedatum und GPS bleiben in der Datei");
        System.Text.Encoding.ASCII.GetString(exif!).Should().Contain("1987:07:12");
        // Ausrichtung im neuen Block = 1 (Wert direkt nach Tag/Typ/Anzahl des ersten Eintrags)
        exif![10 + 8 + 2 + 8].Should().Be(1);
        Hash(original).Should().Be(before);
        w.Codec.Load(saved.Path, 10_000).Width.Should().Be(40, "Bilddaten bleiben nach dem Einfügen lesbar");
    }

    [Fact]
    public async Task Modell_nicht_geladen_es_wird_nichts_berechnet_und_nichts_geschrieben()
    {
        await using var w = await EnhanceWorld.CreateAsync(["Garten.jpg"], ready: []);
        var id = await w.IdAsync("Garten.jpg");
        var filesBefore = Directory.GetFiles(w.Fx.LibraryRoot, "*", SearchOption.AllDirectories).Length;

        var single = () => w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true });
        var batch = () => w.Service.RunBatchAsync([id], new EnhanceOptions { Colorize = true });

        (await single.Should().ThrowAsync<EnhanceModelMissingException>()).Which.Tool.Should().Be(EnhanceTool.Upscale);
        (await batch.Should().ThrowAsync<EnhanceModelMissingException>()).Which.Message.Should().Contain("Modell fehlt");
        w.Engines.Created.Should().BeEmpty();
        Directory.Exists(w.Service.TempFolder).Should().BeFalse();
        Directory.GetFiles(w.Fx.LibraryRoot, "*", SearchOption.AllDirectories).Length.Should().Be(filesBefore);
        w.Activities.GetOverview().Recent.Should().BeEmpty("kein Lauf, kein Vorgang");
        (await w.Service.GetToolStatusAsync()).Single(s => s.Tool == EnhanceTool.Upscale)
            .Should().Match<EnhanceToolStatus>(s => !s.Ready && s.CanDownload && s.StateText == "Modell fehlt");
    }

    [Fact]
    public async Task Modell_Natuerlich_rechnet_mit_general_x4v3_und_verlangt_dessen_Modell()
    {
        // 0.5.7: zweites Hochskalier-Modell; das Ergebnis zählt als „Hochskalieren“
        await using var w = await EnhanceWorld.CreateAsync(["Garten.jpg"], ready: [EnhanceTool.Upscale, EnhanceTool.UpscaleNatural]);
        var id = await w.IdAsync("Garten.jpg");

        var preview = await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true, UpscaleModel = EnhanceUpscaleModel.Natural });

        w.Engines.Created.Should().Equal(EnhanceTool.UpscaleNatural);
        preview.AppliedTools.Should().Equal(EnhanceTool.Upscale);
        preview.ToolsKey.Should().Be("upscale");
        preview.Width.Should().Be(FakeEnhanceCodec.DefaultWidth * 2);
        preview.Notes.Should().Contain("Modell „Natürlich“ (Real-ESRGAN general x4v3).");
        (await w.Service.GetToolStatusAsync()).Select(s => s.Tool).Should().Contain(EnhanceTool.UpscaleNatural);

        // ohne Angabe gilt die Einstellung
        await w.Settings.SaveAsync(EnhanceSettings.Default with { UpscaleModel = EnhanceUpscaleModel.Natural });
        await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true });
        w.Engines.Processed.Should().OnlyContain(t => t == EnhanceTool.UpscaleNatural);
    }

    [Fact]
    public async Task Weitere_Modelle_je_Foto_waehlbar_und_Automatisch_weicht_auf_ein_geladenes_aus()
    {
        // 0.5.11: Klar, Klar weich, Foto, Siax, Haut — je eine eigene Modelldatei, Ergebnis zählt als „Hochskalieren“
        await using var w = await EnhanceWorld.CreateAsync(["Garten.jpg"], ready: [EnhanceTool.UpscaleSkin, EnhanceTool.UpscaleClear]);
        var id = await w.IdAsync("Garten.jpg");

        var skin = await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true, UpscaleModel = EnhanceUpscaleModel.Skin });
        var clear = await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true, UpscaleModel = EnhanceUpscaleModel.Clear });

        w.Engines.Created.Should().Equal(EnhanceTool.UpscaleSkin, EnhanceTool.UpscaleClear);
        skin.AppliedTools.Should().Equal(EnhanceTool.Upscale);
        skin.ToolsKey.Should().Be("upscale");
        skin.Notes.Should().Contain("Modell „Haut“ (4x foolhardy Remacri).");
        clear.Notes.Should().Contain("Modell „Klar“ (4x-ClearRealityV1).");
        var status = await w.Service.GetToolStatusAsync();
        status.Select(s => s.Tool).Should().Contain(EnhanceUpscaleModels.Engines);
        status.Single(s => s.Tool == EnhanceTool.UpscaleSiax).Should().Match<EnhanceToolStatus>(s => !s.Ready && s.ModelId == EnhanceModelCatalog.UpscaleSiaxId);

        // gewählt „Scharf“, aber nur Klar/Haut geladen: Automatisch nimmt das erste geladene in Knopf-Reihenfolge
        var auto = await w.Service.RenderAsync(id, new EnhanceOptions { Auto = true, UpscaleModel = EnhanceUpscaleModel.Sharp });
        w.Engines.Processed.Last().Should().Be(EnhanceTool.UpscaleClear);
        auto.Notes.Should().Contain(n => n.Contains("Modell fehlt — Hochskalieren (Klar) verwendet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Modell_Natuerlich_fehlt_nur_Automatisch_weicht_auf_Scharf_aus()
    {
        await using var w = await EnhanceWorld.CreateAsync(["Garten.jpg"], ready: [EnhanceTool.Upscale]);
        var id = await w.IdAsync("Garten.jpg");

        var explicitNatural = () => w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true, UpscaleModel = EnhanceUpscaleModel.Natural });
        (await explicitNatural.Should().ThrowAsync<EnhanceModelMissingException>()).Which.Tool.Should().Be(EnhanceTool.UpscaleNatural);
        w.Engines.Created.Should().BeEmpty();
        (await w.Service.GetToolStatusAsync()).Single(s => s.Tool == EnhanceTool.UpscaleNatural)
            .Should().Match<EnhanceToolStatus>(s => !s.Ready && s.CanDownload && s.ModelId == EnhanceModelCatalog.UpscaleNaturalId);

        // „Automatisch“ bei einem kleinen Bild: rechnet mit dem geladenen Modell und sagt es
        var preview = await w.Service.RenderAsync(id, new EnhanceOptions { Auto = true, UpscaleModel = EnhanceUpscaleModel.Natural });
        w.Engines.Created.Should().Equal(EnhanceTool.Upscale);
        preview.Notes.Should().Contain(n => n.Contains("Modell fehlt — Hochskalieren verwendet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gesichter_ohne_Gesichtserkennung_gelten_als_nicht_bereit()
    {
        await using var w = await EnhanceWorld.CreateAsync(["P.jpg"], ready: [EnhanceTool.Faces], faces: new FakeFaceLocator { IsAvailable = false });
        var status = (await w.Service.GetToolStatusAsync()).Single(s => s.Tool == EnhanceTool.Faces);
        status.Ready.Should().BeFalse();
        status.StateText.Should().Be("braucht Gesichtserkennung");
        var id = await w.IdAsync("P.jpg");
        var act = () => w.Service.RenderAsync(id, new EnhanceOptions { Faces = true });
        await act.Should().ThrowAsync<EnhanceModelMissingException>();
    }

    [Fact]
    public async Task Mehrere_Fotos_mit_Abbruch_speichern_die_fertigen_in_einem_Batch_mit_Rueckgaengig()
    {
        await using var w = await EnhanceWorld.CreateAsync(["a.jpg", "b.jpg", "c.jpg", "film.mp4"]);
        var ids = new List<int> { await w.IdAsync("a.jpg"), await w.IdAsync("b.jpg"), await w.IdAsync("film.mp4"), await w.IdAsync("c.jpg") };
        var calls = 0;
        w.Engines.BeforeProcess = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                // „Abbrechen“ in der Aktivitäts-Anzeige während des zweiten Fotos
                var running = w.Activities.GetOverview().Running.Single(r => r.Kind == ActivityKind.Enhance);
                w.Activities.Cancel(running.Id).Should().BeTrue();
                throw new OperationCanceledException();
            }
        };

        var result = await w.Service.RunBatchAsync(ids, new EnhanceOptions { Upscale = true });

        result.Canceled.Should().BeTrue();
        result.Saved.Should().Be(1);
        result.BatchId.Should().BeGreaterThan(0);
        File.Exists(w.PathOf("a_verbessert.jpg")).Should().BeTrue();
        File.Exists(w.PathOf("b_verbessert.jpg")).Should().BeFalse();
        File.Exists(w.PathOf("c_verbessert.jpg")).Should().BeFalse();
        Directory.GetFiles(w.Service.TempFolder).Should().BeEmpty("keine Zwischenstände bleiben liegen");
        var entry = w.Activities.GetOverview().Recent.Single(r => r.Kind == ActivityKind.Enhance);
        entry.State.Should().Be(ActivityState.Warning);
        entry.Result.Should().Contain("1 Foto verbessert").And.Contain("abgebrochen");
        entry.HasAction(ActivityActionKind.Undo).Should().BeTrue();
        w.Activities.GetOverview().Recent.Should().NotContain(r => r.Kind == ActivityKind.FileOperation, "das Speichern läuft im Vorgang „Foto verbessern“");

        var undoText = await w.Activities.UndoAsync(entry.Id);

        undoText.Should().Contain("1 verbesserte Fassungen in den Papierkorb");
        File.Exists(w.PathOf("a_verbessert.jpg")).Should().BeFalse();
    }

    [Fact]
    public async Task Mehrere_Fotos_ohne_Abbruch_ueberspringen_Videos_und_nichts_zu_tun()
    {
        await using var w = await EnhanceWorld.CreateAsync(["a.jpg", "b.jpg", "film.mp4"], ready: [EnhanceTool.Upscale]);
        var ids = new List<int> { await w.IdAsync("a.jpg"), await w.IdAsync("b.jpg"), await w.IdAsync("film.mp4") };

        var result = await w.Service.RunBatchAsync(ids, new EnhanceOptions { Upscale = true, UpscaleFactor = 4 });

        result.Should().Match<EnhanceBatchResult>(r => r.Saved == 2 && r.Skipped == 1 && r.Failed == 0 && !r.Canceled);
        await using var ctx = w.Fx.CreateContext();
        (await ctx.PhotoVariants.CountAsync(v => v.BatchId == result.BatchId)).Should().Be(2);
        (await w.Fx.GetOperationsAsync(result.BatchId)).Should().HaveCount(2).And.OnlyContain(o => o.Action == OperationType.CreateVariant);
        w.Activities.GetOverview().Recent.Single(r => r.Kind == ActivityKind.Enhance).State.Should().Be(ActivityState.Succeeded);
    }

    [Fact]
    public async Task Versteckte_Sitzung_schreibt_nichts_ausserhalb_des_Containers()
    {
        await using var w = await EnhanceWorld.CreateAsync(["geheim.jpg"], hidden: true);
        var outside = Path.Combine(w.Fx.RootDir, "appdata");
        var before = Directory.GetFiles(outside, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal) && !f.EndsWith(".db", StringComparison.Ordinal)).ToHashSet();
        var id = await w.IdAsync("geheim.jpg");

        var preview = await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true });
        preview.ResultPath.Should().StartWith(Path.Combine(w.Fx.LibraryRoot, ".lumora"), "Zwischenstand im Container");
        var saved = await w.Service.SaveAsync(preview);

        saved.Path.Should().StartWith(w.Fx.LibraryRoot);
        var after = Directory.GetFiles(outside, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal) && !f.EndsWith(".db", StringComparison.Ordinal)).ToHashSet();
        after.Should().BeEquivalentTo(before, "außerhalb des Containers entsteht keine Datei (außer dem Test-Katalog selbst)");
        Directory.GetFiles(w.Fx.OutsideDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Ziel_ausserhalb_der_Bibliothek_wird_abgelehnt()
    {
        await using var w = await EnhanceWorld.CreateAsync(["a.jpg"]);
        var id = await w.IdAsync("a.jpg");
        var preview = await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true });

        var result = await w.Ops.CreateVariantsAsync([new Lumora.Services.Operations.VariantCreateRequest(id, preview.ResultPath, Path.Combine(w.Fx.OutsideDir, "x.jpg"), "upscale")]);

        result.Succeeded.Should().Be(0);
        result.Items.Single().Error.Should().Contain("außerhalb der Bibliothek");
        File.Exists(Path.Combine(w.Fx.OutsideDir, "x.jpg")).Should().BeFalse();
    }

    [Fact]
    public async Task Unterordner_nach_Einstellung_wird_angelegt_und_im_Katalog_gebucht()
    {
        await using var w = await EnhanceWorld.CreateAsync([Path.Combine("2020", "a.jpg")]);
        await w.Settings.SaveAsync(EnhanceSettings.Default with { Location = EnhanceSaveLocation.Subfolder, SubfolderName = "besser", FileNamePattern = "{name}-hd" });
        var id = await w.IdAsync("a.jpg");

        var saved = await w.Service.SaveAsync(await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true }));

        saved.Path.Should().Be(w.PathOf("2020", "besser", "a-hd.jpg"));
        (await w.Fx.FindFolderAsync(w.PathOf("2020", "besser"))).Should().NotBeNull();
        (await w.Service.SuggestFileNameAsync(id)).Should().Be(Path.Combine("besser", "a-hd (2).jpg"));
    }

    [Fact]
    public async Task Automatisch_faerbt_Graustufen_ein_und_ueberspringt_fehlende_Modelle_mit_Hinweis()
    {
        await using var w = await EnhanceWorld.CreateAsync(["sw.jpg"], ready: [EnhanceTool.Colorize]);
        w.Codec.NextImage = FakeEnhanceCodec.Gray(2400, 1800);
        var id = await w.IdAsync("sw.jpg");

        var preview = await w.Service.RenderAsync(id, new EnhanceOptions { Auto = true });

        preview.AppliedTools.Should().Equal(EnhanceTool.Colorize);
        w.Engines.Processed.Should().Equal(EnhanceTool.Colorize);
        w.Service.Discard(preview);
        File.Exists(preview.ResultPath).Should().BeFalse("Verwerfen löscht den Zwischenstand");

        w.Codec.NextImage = FakeEnhanceCodec.Gradient(2400, 1800); // farbig, groß: nichts zu tun
        var act = () => w.Service.RenderAsync(id, new EnhanceOptions { Auto = true });
        await act.Should().ThrowAsync<EnhanceNothingToDoException>();
    }

    [Fact]
    public async Task Fassungen_bleiben_ueber_Fehlende_entfernen_und_Rueckgaengig_sowie_Neu_aufbauen_erhalten()
    {
        await using var w = await EnhanceWorld.CreateAsync(["a.jpg"]);
        var id = await w.IdAsync("a.jpg");
        var saved = await w.Service.SaveAsync(await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true }));

        // Neu aufbauen (neue FileIds): Verknüpfung über UserDataCarryOver mitgenommen
        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);
        var newOriginal = await w.IdAsync("a.jpg");
        var newVariant = await w.IdAsync("a_verbessert.jpg");
        await using (var ctx = w.Fx.CreateContext())
        {
            var link = await ctx.PhotoVariants.AsNoTracking().SingleAsync();
            (link.FileId, link.OriginalFileId).Should().Be((newVariant, newOriginal));
        }

        // Fassung fehlt → aus dem Katalog entfernen (Verknüpfung gesichert) → Rückgängig stellt sie wieder her
        File.Move(w.PathOf("a_verbessert.jpg"), Path.Combine(w.Fx.OutsideDir, "geparkt.jpg"));
        await using (var ctx = w.Fx.CreateContext())
        {
            await ctx.Files.Where(f => f.Id == newVariant).ExecuteUpdateAsync(s => s.SetProperty(f => f.Missing, true));
        }

        var removed = await w.Ops.RemoveMissingAsync([newVariant]);
        removed.Removed.Should().Be(1);
        await using (var ctx = w.Fx.CreateContext())
        {
            (await ctx.PhotoVariants.CountAsync()).Should().Be(0);
        }

        await w.Ops.UndoBatchAsync(removed.BatchId);
        await using (var ctx = w.Fx.CreateContext())
        {
            (await ctx.PhotoVariants.AsNoTracking().SingleAsync()).OriginalFileId.Should().Be(newOriginal);
        }

        saved.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Zeitschaetzung_nennt_Grafikkarte_bzw_Prozessor_und_fehlende_Modelle()
    {
        await using var w = await EnhanceWorld.CreateAsync(["a.jpg", "b.jpg"]);
        var ids = new List<int> { await w.IdAsync("a.jpg"), await w.IdAsync("b.jpg") };

        var gpu = await w.Service.EstimateAsync(ids, new EnhanceOptions { Upscale = true, Faces = true });
        await w.Settings.SaveAsync(EnhanceSettings.Default with { UseGpu = false });
        var cpu = await w.Service.EstimateAsync(ids, new EnhanceOptions { Upscale = true });

        gpu.Photos.Should().Be(2);
        gpu.OnGpu.Should().BeTrue();
        gpu.Text.Should().Contain("auf der Grafikkarte");
        gpu.MissingModels.Should().Equal(EnhanceTool.Faces);
        cpu.Text.Should().Contain("auf dem Prozessor");
        cpu.Duration.Should().BeGreaterThan(gpu.Duration);
    }

    [Fact]
    public async Task Papierkorb_der_Fassung_sichert_die_Verknuepfung_mit_vollem_Pfad_und_Rueckkehr_stellt_sie_her()
    {
        await using var w = await EnhanceWorld.CreateAsync(["a.jpg"]);
        var id = await w.IdAsync("a.jpg");
        var saved = await w.Service.SaveAsync(await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true }));

        var recycled = await w.Ops.RecycleAsync([saved.NewFileId]);

        await using var ctx = w.Fx.CreateContext();
        var undo = await ctx.FileLinkUndo.AsNoTracking().SingleAsync();
        undo.Kind.Should().Be(FileLinkKind.VariantOf);
        undo.TargetId.Should().Be(id);
        undo.SourcePath.Should().Be(saved.Path, "Rückkehr an genau diesen Pfad stellt die Verknüpfung wieder her");
        (await ctx.PhotoVariants.CountAsync()).Should().Be(0);

        // Rückkehr (neue Zeile für dieselbe Datei) → Verknüpfung wieder da
        var op = (await w.Fx.GetOperationsAsync(recycled.BatchId)).Single();
        var back = new MediaFile { LibraryId = w.LibraryId, FolderId = (await w.Fx.FindFileByIdAsync(id))!.FolderId, Name = "a_verbessert.jpg", Extension = ".jpg", Size = 1, CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow };
        ctx.Files.Add(back);
        await ctx.SaveChangesAsync();
        (await Lumora.Services.Albums.FileLinkUndoStore.RestoreAsync(ctx, op.Id, back.Id, CancellationToken.None)).Should().Be(1);
        (await ctx.PhotoVariants.AsNoTracking().SingleAsync()).Should().Match<PhotoVariant>(v => v.FileId == back.Id && v.OriginalFileId == id);
    }

    [Fact]
    public async Task Veraltete_Katalogzeile_am_Ziel_wird_ersetzt_und_Fassung_einer_Fassung_haengt_am_Original()
    {
        await using var w = await EnhanceWorld.CreateAsync(["a.jpg"]);
        var id = await w.IdAsync("a.jpg");
        var first = await w.Service.SaveAsync(await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true }));
        File.Delete(first.Path); // im Explorer gelöscht, Zeile bleibt (fehlt)
        await using (var ctx = w.Fx.CreateContext())
        {
            await ctx.Files.Where(f => f.Id == first.NewFileId).ExecuteUpdateAsync(x => x.SetProperty(f => f.Missing, true));
        }

        var second = await w.Service.SaveAsync(await w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true }));

        second.Success.Should().BeTrue(second.Error);
        second.Path.Should().Be(first.Path, "der Name ist auf der Platte wieder frei");
        (await w.Fx.FindFileByIdAsync(first.NewFileId)).Should().BeNull("die veraltete Zeile ist ersetzt");
        (await w.Fx.FindFileByIdAsync(second.NewFileId))!.Missing.Should().BeFalse();

        var third = await w.Service.SaveAsync(await w.Service.RenderAsync(second.NewFileId, new EnhanceOptions { Upscale = true }));

        third.Path.Should().EndWith("a_verbessert_verbessert.jpg");
        await using var check = w.Fx.CreateContext();
        (await check.PhotoVariants.AsNoTracking().OrderBy(v => v.FileId).Select(v => v.OriginalFileId).ToListAsync()).Should().Equal(id, id);
        (await new PhotoVariantStore(w.Fx.ContextFactory).GetGroupsAsync([third.NewFileId]))[third.NewFileId].Count.Should().Be(3);
    }

    [Fact]
    public async Task Modelle_werden_erst_nach_laufenden_Berechnungen_freigegeben()
    {
        await using var w = await EnhanceWorld.CreateAsync(["a.jpg"]);
        var id = await w.IdAsync("a.jpg");
        using var entered = new SemaphoreSlim(0);
        using var proceed = new SemaphoreSlim(0);
        w.Engines.BeforeProcess = (_, _) =>
        {
            entered.Release();
            proceed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        };

        var render = w.Service.RenderAsync(id, new EnhanceOptions { Upscale = true });
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        w.Service.ReleaseModels(); // z. B. Leiste geschlossen, während ein Stapel rechnet
        w.Engines.Disposed.Should().Be(0, "das Modell wird gerade benutzt");
        proceed.Release();
        var preview = await render;

        preview.AppliedTools.Should().Contain(EnhanceTool.Upscale);
        w.Engines.Disposed.Should().Be(1, "nach dem Lauf freigegeben");
    }
}
