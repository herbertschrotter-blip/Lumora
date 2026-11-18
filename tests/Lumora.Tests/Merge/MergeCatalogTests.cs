using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Merge;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Merge;

/// <summary>
/// Katalog-Teil von „Zusammenfügen“ (ADR-0078 Punkt 10/11): CreateMergedAsync legt das Ergebnis neben das erste Einzelbild,
/// setzt Zeit/Ort/Kamera/Bewertung/gemeinsame Tags und verknüpft die Quellen; Rückgängig räumt Datei, Zeilen, FTS und
/// Vorschaubilder weg; Papierkorb von Ergebnis bzw. Quelle sichert die Verknüpfung (FileLinkUndo 6/7); „Neu aufbauen“ trägt
/// sie mit. Ohne OpenCV: das „Ergebnis“ ist eine JPEG-förmige Datei.
/// </summary>
public sealed class MergeCatalogTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2024, 7, 12, 8, 0, 0, DateTimeKind.Utc);
    private TempLibraryFixture _fx = null!;
    private FileOperationService _ops = null!;
    private readonly FakeRecycleBin _bin = new() { DeleteFiles = true };
    private int _libraryId;

    public async Task InitializeAsync()
    {
        _fx = await TempLibraryFixture.CreateAsync();
        _libraryId = (await _fx.CreateLibraryAsync()).Id;
        Directory.CreateDirectory(Path.Combine(_fx.LibraryRoot, "Tour"));
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
        {
            File.WriteAllBytes(Path.Combine(_fx.LibraryRoot, "Tour", name), [.. ExifTestJpeg.Plain()]);
        }

        await _fx.ScanAsync(_libraryId);
        _ops = _fx.CreateFileOperationService(_bin);
    }

    public Task DisposeAsync()
    {
        _ops.Dispose();
        _fx.Dispose();
        return Task.CompletedTask;
    }

    private async Task<int> IdAsync(string name) => (await _fx.FindFileAsync(name))!.Id;

    private string ResultFile()
    {
        var temp = Path.Combine(_fx.RootDir, "appdata", "temp", "merge", Guid.NewGuid().ToString("N") + ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllBytes(temp, ExifTestJpeg.Plain());
        return temp;
    }

    /// <summary>a (10 s, kein Ort, 2 Sterne, Tags T1+T2), b (0 s = früheste, geschätzter Ort, 4 Sterne, T1), c (20 s, echtes GPS, Favorit, T1).</summary>
    private async Task<(int A, int B, int C, int T1, int T2)> SeedAsync()
    {
        int a = await IdAsync("a.jpg"), b = await IdAsync("b.jpg"), c = await IdAsync("c.jpg");
        await using var ctx = _fx.CreateContext();
        var files = await ctx.Files.Where(f => f.Id == a || f.Id == b || f.Id == c).ToListAsync();
        void Set(int id, Action<MediaFile> change) => change(files.Single(f => f.Id == id));
        Set(a, f =>
        {
            f.CapturedAt = T0.AddSeconds(10);
            f.CapturedAtSource = CaptureDateSource.Exif;
            f.CapturedAtOffset = 120;
            f.Rating = 2;
            f.CameraModel = "TestCam TC-1";
            f.GeoSource = GeoSource.None;
        });
        Set(b, f =>
        {
            f.CapturedAt = T0;
            f.CapturedAtSource = CaptureDateSource.Exif;
            f.CapturedAtOffset = 120;
            f.Rating = 4;
            f.CameraModel = "TestCam TC-1";
            f.Latitude = 47.1;
            f.Longitude = 13.1;
            f.GeoSource = GeoSource.Exif;
            f.GeoAccuracy = GeoAccuracy.Estimated;
            f.GeoAccuracyReason = GeoAccuracyReason.NoAltitudeNoTime;
        });
        Set(c, f =>
        {
            f.CapturedAt = T0.AddSeconds(20);
            f.CapturedAtSource = CaptureDateSource.Exif;
            f.IsFavorite = true;
            f.CameraModel = "TestCam TC-1";
            f.Latitude = 47.2;
            f.Longitude = 13.2;
            f.GeoSource = GeoSource.Exif;
            f.GeoAccuracy = GeoAccuracy.Exact;
            f.GpsTimeUtc = T0.AddSeconds(19);
        });
        var t1 = new Tag { Name = "Gipfel", Color = "#ff0000", CreatedAtUtc = DateTime.UtcNow };
        var t2 = new Tag { Name = "Nur a", Color = "#00ff00", CreatedAtUtc = DateTime.UtcNow };
        ctx.Tags.AddRange(t1, t2);
        await ctx.SaveChangesAsync();
        ctx.FileTags.AddRange(new FileTag { FileId = a, TagId = t1.Id }, new FileTag { FileId = b, TagId = t1.Id }, new FileTag { FileId = c, TagId = t1.Id }, new FileTag { FileId = a, TagId = t2.Id });
        await ctx.SaveChangesAsync();
        return (a, b, c, t1.Id, t2.Id);
    }

    private MergeCreateRequest Request(IReadOnlyList<int> sources, string? target = null)
        => new(sources, ResultFile(), target ?? Path.Combine(_fx.LibraryRoot, "Tour", "b_panorama.jpg"), MergeMethod.Panorama, PanoramaProjection.Cylindrical,
            "merge:panorama", "{\"version\":1}", Width: 12000, Height: 3000);

    [Fact]
    public async Task Ergebnis_liegt_neben_dem_ersten_Einzelbild_mit_Zeit_Ort_Bewertung_Tags_und_Quellen_und_Rueckgaengig_raeumt_alles_weg()
    {
        var (a, b, c, t1, _) = await SeedAsync();
        var before = File.ReadAllBytes(Path.Combine(_fx.LibraryRoot, "Tour", "a.jpg"));

        var result = await _ops.CreateMergedAsync([Request([b, a, c])]);

        result.Succeeded.Should().Be(1);
        var item = result.Items.Single();
        item.DestPath.Should().Be(Path.Combine(_fx.LibraryRoot, "Tour", "b_panorama.jpg"));
        File.Exists(item.DestPath).Should().BeTrue();
        var batch = await _fx.GetBatchAsync(result.BatchId);
        batch!.OperationType.Should().Be(OperationType.CreateMerged);
        batch.Description.Should().Be("Panorama aus 3 Fotos").And.NotContain(".jpg", "das Protokoll nennt keine Dateinamen");

        var row = (await _fx.FindFileByIdAsync(item.FileId))!;
        row.CapturedAt.Should().Be(T0, "Zeit der frühesten Quelle mit Zeit");
        row.CapturedAtOffset.Should().Be(120);
        (row.Latitude, row.Longitude, row.GeoAccuracy).Should().Be((47.2, 13.2, GeoAccuracy.Exact), "echtes GPS vor geschätztem");
        row.GpsTimeUtc.Should().Be(T0.AddSeconds(19));
        row.CameraModel.Should().Be("TestCam TC-1");
        row.Rating.Should().Be((byte)4, "höchste Sternezahl");
        row.IsFavorite.Should().BeTrue("eine Quelle ist Favorit");
        (row.Width, row.Height).Should().Be((12000, 3000));
        await using (var ctx = _fx.CreateContext())
        {
            (await ctx.FileTags.Where(t => t.FileId == item.FileId).Select(t => t.TagId).ToListAsync()).Should().Equal(t1);
            var merged = await ctx.MergedPhotos.AsNoTracking().SingleAsync();
            (merged.FileId, merged.Method, merged.Projection, merged.SourceCount, merged.BatchId).Should().Be((item.FileId, MergeMethod.Panorama, PanoramaProjection.Cylindrical, 3, result.BatchId));
            (await ctx.MergedPhotoSources.AsNoTracking().OrderBy(s => s.Position).Select(s => s.SourceFileId).ToListAsync()).Should().Equal(b, a, c);
        }

        _fx.FtsRowIds("b_panorama").Should().Contain(item.FileId);

        // Vorschaubild des Ergebnisses anlegen — Rückgängig räumt Zeile und Cache-Datei weg
        var thumbDir = _fx.Paths.GetLibraryThumbnailsPath(_libraryId);
        Directory.CreateDirectory(thumbDir);
        var thumb = Path.Combine(thumbDir, $"{item.FileId}_400_still.jpg");
        File.WriteAllBytes(thumb, [1]);
        await using (var ctx = _fx.CreateContext())
        {
            ctx.Thumbnails.Add(new Thumbnail { FileId = item.FileId, Size = 400, Format = "jpg", CachePath = thumb, Status = ThumbnailStatus.Generated });
            await ctx.SaveChangesAsync();
        }

        var undo = await _ops.UndoBatchAsync(result.BatchId);

        undo!.Succeeded.Should().Be(1);
        File.Exists(item.DestPath).Should().BeFalse();
        _bin.Recycled.Should().Contain(item.DestPath);
        (await _fx.FindFileByIdAsync(item.FileId)).Should().BeNull();
        File.Exists(thumb).Should().BeFalse();
        (await _fx.GetThumbnailRowsAsync(item.FileId)).Should().BeEmpty();
        _fx.FtsRowIds("b_panorama").Should().NotContain(item.FileId);
        await using (var ctx = _fx.CreateContext())
        {
            (await ctx.MergedPhotos.CountAsync()).Should().Be(0);
            (await ctx.MergedPhotoSources.CountAsync()).Should().Be(0);
        }

        File.ReadAllBytes(Path.Combine(_fx.LibraryRoot, "Tour", "a.jpg")).Should().Equal(before, "Einzelbilder bleiben Byte für Byte gleich");
        (await _fx.FindFileByIdAsync(a)).Should().NotBeNull();
    }

    [Fact]
    public async Task Belegter_Name_wird_nummeriert_und_nie_ueberschrieben()
    {
        var (a, b, c, _, _) = await SeedAsync();
        File.WriteAllBytes(Path.Combine(_fx.LibraryRoot, "Tour", "b_panorama.jpg"), [9, 9]);

        var result = await _ops.CreateMergedAsync([Request([b, a, c])]);

        result.Items.Single().DestPath.Should().Be(Path.Combine(_fx.LibraryRoot, "Tour", "b_panorama (2).jpg"));
        File.ReadAllBytes(Path.Combine(_fx.LibraryRoot, "Tour", "b_panorama.jpg")).Should().Equal(9, 9);
    }

    [Fact]
    public async Task Quellen_aus_zwei_Bibliotheken_werden_abgelehnt()
    {
        var (a, b, _, _, _) = await SeedAsync();
        await using (var ctx = _fx.CreateContext())
        {
            var other = new Library { Name = "Zweite", RootPath = _fx.OutsideDir, CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
            ctx.Libraries.Add(other);
            await ctx.SaveChangesAsync();
            await ctx.Files.Where(f => f.Id == a).ExecuteUpdateAsync(s => s.SetProperty(f => f.LibraryId, other.Id));
        }

        var result = await _ops.CreateMergedAsync([Request([b, a])]);

        result.Succeeded.Should().Be(0);
        result.Items.Single().Error.Should().Contain("verschiedenen Bibliotheken");
        File.Exists(Path.Combine(_fx.LibraryRoot, "Tour", "b_panorama.jpg")).Should().BeFalse();
    }

    [Fact]
    public async Task Ziel_ausserhalb_der_Bibliothek_wird_abgelehnt()
    {
        var (a, b, _, _, _) = await SeedAsync();

        var result = await _ops.CreateMergedAsync([Request([b, a], Path.Combine(_fx.OutsideDir, "x.jpg"))]);

        result.Succeeded.Should().Be(0);
        File.Exists(Path.Combine(_fx.OutsideDir, "x.jpg")).Should().BeFalse();
    }

    [Fact]
    public async Task Ohne_Zeit_und_Ort_in_den_Quellen_bleiben_beide_leer_nie_das_Erstelldatum()
    {
        int a = await IdAsync("a.jpg"), b = await IdAsync("b.jpg");

        var result = await _ops.CreateMergedAsync([Request([a, b])]);

        var row = (await _fx.FindFileByIdAsync(result.Items.Single().FileId))!;
        row.CapturedAt.Should().BeNull();
        row.CapturedAtSource.Should().Be(CaptureDateSource.Unknown);
        row.Latitude.Should().BeNull();
        row.GeoSource.Should().Be(GeoSource.None);
        row.Rating.Should().BeNull();
        row.IsFavorite.Should().BeFalse();
    }

    [Fact]
    public async Task Papierkorb_des_Ergebnisses_sichert_die_Quellen_und_Rueckgaengig_stellt_sie_in_alter_Reihenfolge_her()
    {
        var (a, b, c, _, _) = await SeedAsync();
        var created = await _ops.CreateMergedAsync([Request([b, a, c])]);
        var resultId = created.Items.Single().FileId;

        var recycled = await _ops.RecycleAsync([resultId]);

        await using (var ctx = _fx.CreateContext())
        {
            var undo = await ctx.FileLinkUndo.AsNoTracking().OrderBy(u => u.Id).ToListAsync();
            undo.Select(u => (u.Kind, u.TargetId)).Should().Equal((FileLinkKind.MergedFrom, b), (FileLinkKind.MergedFrom, a), (FileLinkKind.MergedFrom, c));
            (await ctx.MergedPhotos.CountAsync()).Should().Be(0);
            (await ctx.MergedPhotoUndo.AsNoTracking().SingleAsync()).RecipeJson.Should().Be("{\"version\":1}", "Verfahren, Aufnahmeart und Rezept sind mitgesichert");
        }

        // Datei kehrt zurück (Rückgängig des Papierkorbs legt eine neue Zeile an)
        _bin.DeleteFiles.Should().BeTrue();
        File.WriteAllBytes(Path.Combine(_fx.LibraryRoot, "Tour", "b_panorama.jpg"), ExifTestJpeg.Plain());
        var op = (await _fx.GetOperationsAsync(recycled.BatchId)).Single();
        await using (var ctx = _fx.CreateContext())
        {
            var back = new MediaFile { LibraryId = _libraryId, FolderId = (await _fx.FindFileByIdAsync(a))!.FolderId, Name = "b_panorama.jpg", Extension = ".jpg", Size = 1, CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow };
            ctx.Files.Add(back);
            await ctx.SaveChangesAsync();
            (await Lumora.Services.Albums.FileLinkUndoStore.RestoreAsync(ctx, op.Id, back.Id, CancellationToken.None)).Should().BeGreaterThan(0);
            var merged = await ctx.MergedPhotos.AsNoTracking().SingleAsync();
            (merged.FileId, merged.SourceCount, merged.Method, merged.Projection, merged.RecipeJson, merged.BatchId)
                .Should().Be((back.Id, 3, MergeMethod.Panorama, PanoramaProjection.Cylindrical, "{\"version\":1}", created.BatchId), "die gesicherte Zeile kommt zurück, nicht nur die Quellen");
            (await ctx.MergedPhotoSources.AsNoTracking().OrderBy(s => s.Position).Select(s => s.SourceFileId).ToListAsync()).Should().Equal(b, a, c);
            (await ctx.MergedPhotoUndo.CountAsync()).Should().Be(0, "die Sicherung ist verbraucht");
        }
    }

    [Fact]
    public async Task Neu_aufbauen_schreibt_gesicherte_Verknuepfungen_entfernter_Dateien_auf_neue_FileIds_um()
    {
        var (a, b, c, _, _) = await SeedAsync();
        var created = await _ops.CreateMergedAsync([Request([b, a, c])]);
        var resultId = created.Items.Single().FileId;
        await _ops.RecycleAsync([a]); // Kind 7 → Ergebnis
        _bin.DeleteFiles.Should().BeTrue("die Quelle ist von der Platte weg");

        await _fx.CreateScanService().ScanAsync(_libraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);

        var nr = await IdAsync("b_panorama.jpg");
        nr.Should().NotBe(resultId);
        await using var ctx = _fx.CreateContext();
        var undo = await ctx.FileLinkUndo.AsNoTracking().SingleAsync();
        (undo.Kind, undo.TargetId).Should().Be((FileLinkKind.MergeSourceOf, nr), "die Sicherung zeigt auf die neue FileId des Ergebnisses, nie auf eine fremde Datei");
    }

    [Fact]
    public async Task Papierkorb_einer_Quelle_sichert_ihre_Verknuepfung_und_Rueckkehr_haengt_sie_wieder_an()
    {
        var (a, b, c, _, _) = await SeedAsync();
        var created = await _ops.CreateMergedAsync([Request([b, a, c])]);
        var resultId = created.Items.Single().FileId;
        var folderId = (await _fx.FindFileByIdAsync(a))!.FolderId;

        var recycled = await _ops.RecycleAsync([a]);

        await using (var ctx = _fx.CreateContext())
        {
            var undo = await ctx.FileLinkUndo.AsNoTracking().SingleAsync();
            (undo.Kind, undo.TargetId).Should().Be((FileLinkKind.MergeSourceOf, resultId));
            (await ctx.MergedPhotoSources.CountAsync()).Should().Be(2);
            (await ctx.MergedPhotos.AsNoTracking().SingleAsync()).SourceCount.Should().Be(3, "SourceCount bleibt stehen");
        }

        // Rückkehr aus dem Papierkorb (Windows „Wiederherstellen“ + Scan): neue Zeile, Verknüpfung über die Operation
        var op = (await _fx.GetOperationsAsync(recycled.BatchId)).Single();
        await using (var ctx = _fx.CreateContext())
        {
            var back = new MediaFile { LibraryId = _libraryId, FolderId = folderId, Name = "a.jpg", Extension = ".jpg", Size = 1, CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow };
            ctx.Files.Add(back);
            await ctx.SaveChangesAsync();
            (await Lumora.Services.Albums.FileLinkUndoStore.RestoreAsync(ctx, op.Id, back.Id, CancellationToken.None)).Should().Be(1);
            var sources = await ctx.MergedPhotoSources.AsNoTracking().Where(s => s.ResultFileId == resultId).OrderBy(s => s.Position).ToListAsync();
            sources.Select(s => s.SourceFileId).Should().Equal(b, c, back.Id);
            sources[^1].Position.Should().Be(3, "zurückkehrende Quelle hängt sich hinten an");
        }
    }

    // ---- Befunde der Prüfung (0.5.48) ----------------------------------------------------------------------------------

    private async Task<(int Results, int Sources)> MergedCountsAsync()
    {
        await using var ctx = _fx.CreateContext();
        return (await ctx.MergedPhotos.CountAsync(), await ctx.MergedPhotoSources.CountAsync());
    }

    private async Task<List<int>> SourcesOfAsync(int resultId)
    {
        await using var ctx = _fx.CreateContext();
        return await ctx.MergedPhotoSources.AsNoTracking().Where(s => s.ResultFileId == resultId).OrderBy(s => s.Position).Select(s => s.SourceFileId).ToListAsync();
    }

    [Fact]
    public async Task Ordner_mit_dem_Ergebnis_in_den_Papierkorb_und_Rueckgaengig_bringt_die_Quellen_mit()
    {
        var (a, b, c, _, _) = await SeedAsync();
        using var ops = _fx.CreateFileOperationService(new Lumora.Tests.Operations.MovingRecycleBin(Path.Combine(_fx.RootDir, "papierkorb")));
        var created = await ops.CreateMergedAsync([Request([b, a, c], Path.Combine(_fx.LibraryRoot, "Tour", "zusammengefügt", "b_panorama.jpg"))]);
        var resultId = created.Items.Single().FileId;
        var folderId = (await _fx.FindFileByIdAsync(resultId))!.FolderId;

        var recycled = await ops.RecycleFolderAsync(folderId);
        recycled.Success.Should().BeTrue(recycled.Error);
        (await MergedCountsAsync()).Should().Be((0, 0));

        var undo = await ops.UndoBatchAsync(recycled.BatchId);

        undo!.Failed.Should().Be(0);
        (await _fx.FindFileByIdAsync(resultId)).Should().NotBeNull("dieselbe FileId");
        (await SourcesOfAsync(resultId)).Should().Equal([b, a, c], "die Quellen kommen mit dem Ergebnis zurück, in alter Reihenfolge");
        await using var ctx = _fx.CreateContext();
        (await ctx.FileLinkUndo.CountAsync()).Should().Be(0);
        (await ctx.MergedPhotoUndo.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Abzug_bringt_Ergebnis_und_Quelle_in_beiden_Reihenfolgen_zurueck(bool resultFirst)
    {
        // „Ordner in den Papierkorb“, „Fehlende entfernen“, „Aus dem Katalog entfernen“, „Doppelte aufräumen“: gleiche FileIds
        var (a, b, c, _, _) = await SeedAsync();
        var resultId = (await _ops.CreateMergedAsync([Request([b, a, c])])).Items.Single().FileId;
        using var connection = _fx.ContextFactory.OpenSqliteConnection();
        var snapshots = new Dictionary<int, (string Json, int FolderId, long Size, long Modified)>();
        foreach (var id in new[] { resultId, a })
        {
            var row = (await _fx.FindFileByIdAsync(id))!;
            snapshots[id] = (Lumora.Services.Duplicates.CatalogRowSnapshot.Capture(connection, null, id), row.FolderId, row.Size,
                new DateTimeOffset(DateTime.SpecifyKind(row.ModifiedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
        }

        await using (var ctx = _fx.CreateContext())
        {
            await ctx.Files.Where(f => f.Id == resultId || f.Id == a).ExecuteDeleteAsync();
        }

        (await MergedCountsAsync()).Should().Be((0, 0));
        using (var tx = connection.BeginTransaction())
        {
            foreach (var id in resultFirst ? new[] { resultId, a } : new[] { a, resultId })
            {
                var (json, folderId, size, modified) = snapshots[id];
                var snapshot = Lumora.Services.Duplicates.CatalogRowSnapshot.Parse(json);
                Lumora.Services.Duplicates.CatalogRowSnapshot.RestoreFileRow(connection, tx, snapshot, folderId, size, modified).Should().BeTrue();
                Lumora.Services.Duplicates.CatalogRowSnapshot.RestoreDependents(connection, tx, snapshot);
            }

            tx.Commit();
        }

        (await SourcesOfAsync(resultId)).Should().Equal([b, a, c], resultFirst ? "Quelle nach dem Ergebnis" : "Quelle vor dem Ergebnis — die Quellen stehen im Abzug des Ergebnisses");
        (await MergedCountsAsync()).Should().Be((1, 3));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Ergebnis_und_Quelle_kehren_mit_neuen_FileIds_zurueck_in_jeder_Reihenfolge(bool resultRemovedFirst, bool resultReturnsFirst)
    {
        // Serie samt Panorama im Papierkorb, in Windows wiederhergestellt und eingelesen (bzw. Verschieben nach außen + Rückgängig)
        var (a, b, c, _, _) = await SeedAsync();
        var resultId = (await _ops.CreateMergedAsync([Request([b, a, c])])).Items.Single().FileId;
        var originals = new Dictionary<int, MediaFile> { [resultId] = (await _fx.FindFileByIdAsync(resultId))!, [a] = (await _fx.FindFileByIdAsync(a))! };

        var recycled = await _ops.RecycleAsync(resultRemovedFirst ? [resultId, a] : [a, resultId]);
        recycled.Succeeded.Should().Be(2);
        var operations = (await _fx.GetOperationsAsync(recycled.BatchId)).ToDictionary(o => o.FileId!.Value, o => o.Id);

        async Task<int> ReturnAsync(int oldId)
        {
            var o = originals[oldId];
            await using var ctx = _fx.CreateContext();
            var back = new MediaFile { LibraryId = o.LibraryId, FolderId = o.FolderId, Name = o.Name, Extension = o.Extension, MediaType = o.MediaType, Size = o.Size, CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = o.ModifiedAtUtc };
            ctx.Files.Add(back);
            await ctx.SaveChangesAsync();
            await Lumora.Services.Albums.FileLinkUndoStore.RestoreAsync(ctx, operations[oldId], back.Id, CancellationToken.None);
            return back.Id;
        }

        int newResult, newA;
        if (resultReturnsFirst)
        {
            newResult = await ReturnAsync(resultId);
            newA = await ReturnAsync(a);
        }
        else
        {
            newA = await ReturnAsync(a);
            newResult = await ReturnAsync(resultId);
        }

        var sources = await SourcesOfAsync(newResult);
        sources.Should().BeEquivalentTo([b, newA, c], "alle Quellen hängen wieder am Ergebnis, gleich in welcher Reihenfolge entfernt und zurückgekehrt");
        if (resultRemovedFirst)
        {
            sources.Should().Equal([b, newA, c], "die Sicherung des Ergebnisses kennt die alte Position");
        }

        await using var check = _fx.CreateContext();
        var merged = await check.MergedPhotos.AsNoTracking().SingleAsync();
        (merged.FileId, merged.SourceCount, merged.RecipeJson).Should().Be((newResult, 3, "{\"version\":1}"));
        (await check.FileLinkUndo.CountAsync()).Should().Be(0, "alles verknüpft — keine offenen Sicherungen");
        (await check.MergedPhotoUndo.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Altes_Rueckgaengig_legt_kein_neueres_Ergebnis_am_selben_Pfad_in_den_Papierkorb()
    {
        var (a, b, c, _, _) = await SeedAsync();
        var first = await _ops.CreateMergedAsync([Request([b, a, c])]);
        var firstItem = first.Items.Single();
        await _ops.RecycleAsync([firstItem.FileId]); // Ergebnis in der Galerie in den Papierkorb — der Name ist wieder frei
        var secondBatch = await _ops.CreateMergedAsync([Request([b, a, c])]);
        var second = secondBatch.Items.Single();
        second.DestPath.Should().Be(firstItem.DestPath);
        var recycledBefore = _bin.Recycled.Count;

        var undo = await _ops.UndoBatchAsync(first.BatchId);

        undo!.Items.Single().Error.Should().Be(FileOperationService.NotOursHint);
        File.Exists(second.DestPath).Should().BeTrue("das neuere Ergebnis bleibt");
        _bin.Recycled.Count.Should().Be(recycledBefore, "nichts in den Papierkorb");
        (await _fx.FindFileByIdAsync(second.FileId)).Should().NotBeNull();
        (await MergedCountsAsync()).Should().Be((1, 3));

        // das eigene Rückgängig des neueren Ergebnisses wirkt weiter — und sichert seine Quellen zur Operation (kehrt die Datei
        // aus dem Papierkorb zurück, kommt die Verknüpfung mit)
        (await _ops.UndoBatchAsync(secondBatch.BatchId))!.Succeeded.Should().Be(1);
        File.Exists(second.DestPath).Should().BeFalse();
        var secondOp = (await _fx.GetOperationsAsync(secondBatch.BatchId)).Single().Id;
        await using var ctx = _fx.CreateContext();
        (await ctx.FileLinkUndo.Where(u => u.FileOperationId == secondOp).OrderBy(u => u.Id).Select(u => u.TargetId).ToListAsync()).Should().Equal(b, a, c);
        (await ctx.MergedPhotoUndo.CountAsync(u => u.FileOperationId == secondOp)).Should().Be(1);
    }

    [Fact]
    public async Task Neu_aufbauen_traegt_Ergebnis_Quellen_und_Rezept_mit()
    {
        var (a, b, c, _, _) = await SeedAsync();
        await _ops.CreateMergedAsync([Request([b, a, c])]);

        await _fx.CreateScanService().ScanAsync(_libraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);

        int na = await IdAsync("a.jpg"), nb = await IdAsync("b.jpg"), nc = await IdAsync("c.jpg"), nr = await IdAsync("b_panorama.jpg");
        new[] { na, nb, nc, nr }.Should().NotIntersectWith(new[] { a, b, c }, "neue FileIds nach „Neu aufbauen“");
        await using var ctx = _fx.CreateContext();
        var merged = await ctx.MergedPhotos.AsNoTracking().SingleAsync();
        (merged.FileId, merged.SourceCount, merged.RecipeJson, merged.Projection).Should().Be((nr, 3, "{\"version\":1}", PanoramaProjection.Cylindrical));
        (await ctx.MergedPhotoSources.AsNoTracking().OrderBy(s => s.Position).Select(s => s.SourceFileId).ToListAsync()).Should().Equal(nb, na, nc);
        (await _fx.FindFileByIdAsync(nr))!.Rating.Should().Be((byte)4, "Bewertung des Ergebnisses überlebt wie bei jeder Datei");
    }

    [Fact]
    public async Task Abfragen_fuer_Marke_Lightbox_und_Suchen_liefern_Ergebnis_und_Quellen()
    {
        var (a, b, c, _, _) = await SeedAsync();
        var resultId = (await _ops.CreateMergedAsync([Request([b, a, c])])).Items.Single().FileId;
        await using (var ctx = _fx.CreateContext())
        {
            await ctx.Files.Where(f => f.Id == c).ExecuteUpdateAsync(s => s.SetProperty(f => f.Missing, true));
        }

        var store = new MergedPhotoStore(_fx.ContextFactory);

        var marks = await store.GetMarksAsync([a, resultId]);
        marks.Keys.Should().Equal(resultId);
        (marks[resultId].SourceCount, marks[resultId].PresentSources).Should().Be((3, 2));
        (await store.GetSourceFileIdsAsync([a, b, c, resultId])).Should().BeEquivalentTo([a, b, c]);
        (await store.GetSourcesAsync(resultId)).Should().Equal(b, a, c);
        (await store.GetResultsOfSourceAsync(a)).Should().Equal(resultId);
        var info = (await store.GetInfoAsync(resultId))!;
        info.MissingSources.Should().Be(1);
        info.Projection.Should().Be(PanoramaProjection.Cylindrical);
        (await store.GetInfoAsync(a)).Should().BeNull();
    }
}
