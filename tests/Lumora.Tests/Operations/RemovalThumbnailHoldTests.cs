using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Operations;
using Lumora.Services.Removal;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Services.Thumbnails;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Operations;

/// <summary>
/// #21: Vorschaubilder entfernter Einträge kommen mit „Rückgängig“ zurück — „Fehlende entfernen“ (auch wenn die Datei weiter fehlt),
/// „Ausgeschlossene entfernen“ und „Ordner in den Papierkorb“; abgelaufene Ablagen werden beim nächsten Entfernen weggeräumt.
/// </summary>
public sealed class RemovalThumbnailHoldTests
{
    private static string ThumbDir(TempLibraryFixture fx, int libraryId) => fx.Paths.GetLibraryThumbnailsPath(libraryId);

    private static string HoldDir(TempLibraryFixture fx, int libraryId, int batchId)
        => Path.Combine(ThumbDir(fx, libraryId), FileOperationService.ThumbnailHoldFolderName, batchId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Nach Rückgängig: Cache-Datei am üblichen Platz und Zeile „Generated“ mit diesem Pfad, Ablage leer.</summary>
    private static async Task ShouldHaveThumbnailBackAsync(TempLibraryFixture fx, int libraryId, int fileId, string thumbPath, int batchId)
    {
        File.Exists(thumbPath).Should().BeTrue("die Cache-Datei ist zurück");
        var rows = await fx.GetThumbnailRowsAsync(fileId);
        rows.Should().ContainSingle();
        rows[0].Status.Should().Be(ThumbnailStatus.Generated);
        rows[0].CachePath.Should().Be(thumbPath);
        Directory.Exists(HoldDir(fx, libraryId, batchId)).Should().BeFalse("die Ablage des Batches ist leer und weggeräumt");
    }

    [Fact]
    public async Task Fehlende_entfernen_und_Rueckgaengig_bringt_das_Vorschaubild_obwohl_die_Datei_fehlt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("2020", "weg.jpg"));
        fx.WriteLibraryFile("da.jpg");
        await fx.ScanAsync(library.Id);
        var bin = new MovingRecycleBin(Path.Combine(fx.RootDir, "papierkorb"));
        using var ops = fx.CreateFileOperationService(bin);
        var removal = RemovalTestKit.CreateRemovalService(fx, ops, bin);
        var weg = (await fx.FindFileAsync("weg.jpg"))!.Id;
        var (_, _, thumb) = await RemovalTestKit.DecorateAsync(fx, library.Id, weg);
        File.Delete(Path.Combine(fx.LibraryRoot, "2020", "weg.jpg"));
        await RemovalTestKit.MarkMissingAsync(fx, weg);

        var removed = await removal.RemoveMissingAsync(new MissingTarget.Selection([weg]));

        removed.Removed.Should().Be(1);
        File.Exists(thumb).Should().BeFalse("am üblichen Platz ist nach dem Entfernen nichts mehr (Repo-Regel)");
        (await fx.GetThumbnailRowsAsync(weg)).Should().BeEmpty();
        File.Exists(Path.Combine(HoldDir(fx, library.Id, removed.BatchId), Path.GetFileName(thumb))).Should().BeTrue("aufbewahrt für Rückgängig");

        var undo = await removal.UndoAsync(removed.BatchId);

        undo!.Failed.Should().Be(0);
        (await fx.FindFileByIdAsync(weg))!.Missing.Should().BeTrue("die Datei fehlt weiterhin");
        await ShouldHaveThumbnailBackAsync(fx, library.Id, weg, thumb, removed.BatchId);

        // Die Galerie fragt wie gewohnt beim Vorschaubild-Dienst an: er meldet das zurückgeholte Bild (die Quelle fehlt ja)
        using var thumbs = new ThumbnailService(fx.ContextFactory, fx.Paths, new FakeImageRenderer(), new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance);
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        thumbs.ThumbnailReady += e => ready.TrySetResult(e.ThumbnailPath);
        var row = (await fx.GetThumbnailRowsAsync(weg)).Single();
        await thumbs.RequestThumbnailsAsync([new ThumbnailRequest(library.Id, weg, Path.Combine(fx.LibraryRoot, "2020", "weg.jpg"), MediaType.Image, row.SourceModifiedAtUtc)]);
        (await ready.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(thumb);
    }

    [Fact]
    public async Task Ausgeschlossene_entfernen_und_Rueckgaengig_bringt_das_Vorschaubild()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("Vorschau", "v.jpg"));
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(library.Id);
        var store = new ScanExclusionStore(fx.ContextFactory, fx.Messenger);
        using var ops = fx.CreateFileOperationService();
        var service = new ScanExclusionService(fx.ContextFactory, store, ops, fx.CreateScanService(), NullLogger<ScanExclusionService>.Instance);
        var v = (await fx.FindFileAsync("v.jpg"))!.Id;
        var (_, _, thumb) = await RemovalTestKit.DecorateAsync(fx, library.Id, v);
        await store.SetFolderNamesAsync(["Vorschau"]);

        var removed = await service.RemoveFromCatalogAsync([v]);
        File.Exists(thumb).Should().BeFalse();
        var undo = await service.UndoRemovalAsync(removed.BatchId);

        undo!.Failed.Should().Be(0);
        await ShouldHaveThumbnailBackAsync(fx, library.Id, v, thumb, removed.BatchId);
    }

    [Fact]
    public async Task Ordner_in_den_Papierkorb_und_Rueckgaengig_bringt_das_Vorschaubild()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("Reise", "alpha.jpg"));
        fx.WriteLibraryFile("wurzel.jpg");
        await fx.ScanAsync(library.Id);
        var bin = new MovingRecycleBin(Path.Combine(fx.RootDir, "papierkorb"));
        using var ops = fx.CreateFileOperationService(bin);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!.Id;
        var (_, _, thumb) = await RemovalTestKit.DecorateAsync(fx, library.Id, alpha);
        var folder = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "Reise")))!.Id;

        var recycled = await ops.RecycleFolderAsync(folder);
        recycled.Success.Should().BeTrue(recycled.Error);
        File.Exists(thumb).Should().BeFalse();
        var undo = await ops.UndoBatchAsync(recycled.BatchId);

        undo!.Failed.Should().Be(0);
        await ShouldHaveThumbnailBackAsync(fx, library.Id, alpha, thumb, recycled.BatchId);
    }

    [Fact]
    public async Task Abgelaufene_Ablage_wird_beim_naechsten_Entfernen_weggeraeumt_und_Rueckgaengig_bringt_nur_die_Zeile()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("alt.jpg");
        fx.WriteLibraryFile("neu.jpg");
        fx.WriteLibraryFile("da.jpg");
        await fx.ScanAsync(library.Id);
        var bin = new MovingRecycleBin(Path.Combine(fx.RootDir, "papierkorb"));
        using var ops = fx.CreateFileOperationService(bin);
        var removal = RemovalTestKit.CreateRemovalService(fx, ops, bin);
        var alt = (await fx.FindFileAsync("alt.jpg"))!.Id;
        var neu = (await fx.FindFileAsync("neu.jpg"))!.Id;
        var (_, _, altThumb) = await RemovalTestKit.DecorateAsync(fx, library.Id, alt);
        File.Delete(Path.Combine(fx.LibraryRoot, "alt.jpg"));
        File.Delete(Path.Combine(fx.LibraryRoot, "neu.jpg"));
        await RemovalTestKit.MarkMissingAsync(fx, alt, neu);
        var first = await removal.RemoveMissingAsync(new MissingTarget.Selection([alt]));
        var hold = HoldDir(fx, library.Id, first.BatchId);
        Directory.SetCreationTimeUtc(hold, DateTime.UtcNow - FileOperationService.ThumbnailHoldMaxAge - TimeSpan.FromDays(1));

        var second = await removal.RemoveMissingAsync(new MissingTarget.Selection([neu]));
        second.Removed.Should().Be(1);
        Directory.Exists(hold).Should().BeFalse("älter als die Aufbewahrungszeit");

        var undo = await removal.UndoAsync(first.BatchId);
        undo!.Failed.Should().Be(0);
        (await fx.FindFileByIdAsync(alt)).Should().NotBeNull("die Zeile kommt trotzdem zurück");
        (await fx.GetThumbnailRowsAsync(alt)).Should().BeEmpty("ohne Ablage kein Vorschaubild — die Galerie zeigt den Platzhalter mit „fehlt“");
        File.Exists(altThumb).Should().BeFalse();
    }

    [Fact]
    public async Task Ohne_Vorschaubild_entsteht_keine_Ablage()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("weg.jpg");
        fx.WriteLibraryFile("da.jpg");
        await fx.ScanAsync(library.Id);
        var bin = new MovingRecycleBin(Path.Combine(fx.RootDir, "papierkorb"));
        using var ops = fx.CreateFileOperationService(bin);
        var removal = RemovalTestKit.CreateRemovalService(fx, ops, bin);
        var weg = (await fx.FindFileAsync("weg.jpg"))!.Id;
        File.Delete(Path.Combine(fx.LibraryRoot, "weg.jpg"));
        await RemovalTestKit.MarkMissingAsync(fx, weg);

        var removed = await removal.RemoveMissingAsync(new MissingTarget.Selection([weg]));

        removed.Removed.Should().Be(1);
        Directory.Exists(HoldDir(fx, library.Id, removed.BatchId)).Should().BeFalse();
        (await removal.UndoAsync(removed.BatchId))!.Failed.Should().Be(0);
        (await fx.GetThumbnailRowsAsync(weg)).Should().BeEmpty();
    }

    [Fact]
    public async Task Neu_aufbauen_laesst_die_Ablage_stehen_und_raeumt_den_uebrigen_Cache()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("weg.jpg");
        fx.WriteLibraryFile("da.jpg");
        await fx.ScanAsync(library.Id);
        var bin = new MovingRecycleBin(Path.Combine(fx.RootDir, "papierkorb"));
        using var ops = fx.CreateFileOperationService(bin);
        var removal = RemovalTestKit.CreateRemovalService(fx, ops, bin);
        var weg = (await fx.FindFileAsync("weg.jpg"))!.Id;
        var da = (await fx.FindFileAsync("da.jpg"))!.Id;
        var (_, _, wegThumb) = await RemovalTestKit.DecorateAsync(fx, library.Id, weg);
        var (_, _, daThumb) = await RemovalTestKit.DecorateAsync(fx, library.Id, da);
        File.Delete(Path.Combine(fx.LibraryRoot, "weg.jpg"));
        await RemovalTestKit.MarkMissingAsync(fx, weg);
        var removed = await removal.RemoveMissingAsync(new MissingTarget.Selection([weg]));

        await fx.CreateScanService().ScanAsync(library.Id, Lumora.Services.Scanning.ScanMode.ClearAndRescan, null, CancellationToken.None);

        File.Exists(daThumb).Should().BeFalse("Neu aufbauen verwirft die Vorschaubilder der alten FileIds");
        File.Exists(Path.Combine(HoldDir(fx, library.Id, removed.BatchId), Path.GetFileName(wegThumb))).Should().BeTrue("die Ablage gehört zum Entfernen-Batch, nicht zu den aktuellen FileIds");
    }
}
