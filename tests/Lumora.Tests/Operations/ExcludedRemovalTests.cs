using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Operations;

/// <summary>
/// Vorhandene Dateien in ausgeschlossenen Ordnern aus der Bibliothek entfernen (ADR-0066): nur nach gespeichertem Ausschluss,
/// Katalog sauber (Thumbnails, Cache-Datei, FTS, leere ausgeschlossene Ordnerzeilen), Dateien bleiben auf der Platte, Rückgängig
/// mit denselben FileIds und Angaben; Vorschau mit Zahlen über den Dienst.
/// </summary>
public sealed class ExcludedRemovalTests
{
    private sealed record World(TempLibraryFixture Fx, int LibraryId, int Thumb1, int Thumb2, int Normal, ScanExclusionStore Store, ScanExclusionService Service) : IDisposable
    {
        public void Dispose() => Fx.Dispose();
    }

    /// <summary>Bibliothek mit a.jpg, 2020\b.jpg und zwei Bildern in Ordnern „Vorschau“ (vor dem Ausschluss eingelesen).</summary>
    private static async Task<World> ArrangeAsync()
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile(Path.Combine("2020", "b.jpg"));
        fx.WriteLibraryFile(Path.Combine("Vorschau", "v1.jpg"));
        fx.WriteLibraryFile(Path.Combine("2020", "Tief", "Vorschau", "v2.jpg"));
        await fx.ScanAsync(library.Id);
        var store = new ScanExclusionStore(fx.ContextFactory, fx.Messenger);
        var ops = fx.CreateFileOperationService();
        var service = new ScanExclusionService(fx.ContextFactory, store, ops, fx.CreateScanService(), NullLogger<ScanExclusionService>.Instance);
        return new World(fx, library.Id, (await fx.FindFileAsync("v1.jpg"))!.Id, (await fx.FindFileAsync("v2.jpg"))!.Id, (await fx.FindFileAsync("b.jpg"))!.Id, store, service);
    }

    [Fact]
    public async Task Vorschau_zaehlt_Dateien_und_Benutzerdaten_fuer_einen_neuen_Namen()
    {
        using var w = await ArrangeAsync();
        await RemovalTestKit.DecorateAsync(w.Fx, w.LibraryId, w.Thumb1, rating: 3);

        var summary = await w.Service.SummarizeAsync(new ExclusionScope.Name("vorschau"));

        summary.FileIds.Should().BeEquivalentTo([w.Thumb1, w.Thumb2], "in jeder Tiefe, Groß/Klein egal");
        summary.UserData.RatedOrFavorite.Should().Be(1);
        summary.UserData.InAlbums.Should().Be(1);
        summary.UserData.WithTagsOrCategories.Should().Be(1);
        (await w.Service.SummarizeAsync(new ExclusionScope.Current([w.LibraryId]))).Count.Should().Be(0, "noch nicht gespeichert");
        (await w.Service.SummarizeAsync(new ExclusionScope.Folder(w.LibraryId, Path.Combine(w.Fx.LibraryRoot, "2020")))).FileIds.Should().BeEquivalentTo([w.Normal, w.Thumb2]);
    }

    [Fact]
    public async Task Entfernen_nach_Bestaetigung_raeumt_den_Katalog_und_laesst_die_Dateien_liegen()
    {
        using var w = await ArrangeAsync();
        var (_, _, thumbPath) = await RemovalTestKit.DecorateAsync(w.Fx, w.LibraryId, w.Thumb1);
        await w.Store.SetFolderNamesAsync(["Vorschau"]);
        var summary = await w.Service.SummarizeAsync(new ExclusionScope.Current([w.LibraryId]));

        var result = await w.Service.RemoveFromCatalogAsync(summary.FileIds);

        result.Removed.Should().Be(2);
        result.CanUndo.Should().BeTrue();
        result.FoldersRemoved.Should().Be(2, "beide Vorschau-Ordner sind danach leer");
        (await w.Fx.FindFileByIdAsync(w.Thumb1)).Should().BeNull();
        (await w.Fx.FindFileByIdAsync(w.Thumb2)).Should().BeNull();
        (await w.Fx.FindFileByIdAsync(w.Normal)).Should().NotBeNull();
        File.Exists(Path.Combine(w.Fx.LibraryRoot, "Vorschau", "v1.jpg")).Should().BeTrue("keine Datei wird angefasst");
        File.Exists(thumbPath).Should().BeFalse("Cache-Datei mit entfernt");
        w.Fx.FtsRowIds("v1").Should().BeEmpty();
        (await w.Fx.FindFolderAsync(Path.Combine(w.Fx.LibraryRoot, "Vorschau"))).Should().BeNull();
        (await w.Fx.FindFolderAsync(Path.Combine(w.Fx.LibraryRoot, "2020", "Tief"))).Should().NotBeNull("nicht ausgeschlossen — bleibt im Baum");
        var batch = (await w.Fx.GetBatchAsync(result.BatchId))!;
        batch.OperationType.Should().Be(OperationType.ExcludeFromCatalog);
        batch.Status.Should().Be(BatchStatus.Applied);
        (await w.Fx.GetOperationsAsync(result.BatchId)).Should().OnlyContain(o => o.Action == OperationType.ExcludeFromCatalog && o.Status == FileOperationStatus.Applied);
        w.Fx.Messages.OfKind(FilesChangeKind.Removed).SelectMany(m => m.FileIds).Should().BeEquivalentTo([w.Thumb1, w.Thumb2]);
    }

    [Fact]
    public async Task Rueckgaengig_bringt_dieselben_FileIds_mit_allen_Angaben_zurueck()
    {
        using var w = await ArrangeAsync();
        var (tagId, albumId, _) = await RemovalTestKit.DecorateAsync(w.Fx, w.LibraryId, w.Thumb1, rating: 5);
        await w.Store.AddFolderAsync(w.LibraryId, Path.Combine(w.Fx.LibraryRoot, "Vorschau"));
        var removed = await w.Service.RemoveFromCatalogAsync([w.Thumb1, w.Thumb2]);
        removed.Removed.Should().Be(1, "nur der Ordner dieser Bibliothek ist ausgeschlossen");
        removed.SkippedNotExcluded.Should().Be(1);

        var undo = await w.Service.UndoRemovalAsync(removed.BatchId);

        undo!.Failed.Should().Be(0);
        var back = (await w.Fx.FindFileByIdAsync(w.Thumb1))!;
        back.Rating.Should().Be(5);
        back.Missing.Should().BeFalse("die Datei ist da");
        (await w.Fx.FindFolderAsync(Path.Combine(w.Fx.LibraryRoot, "Vorschau"))).Should().NotBeNull("Ordnerzeile zurück");
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM FileTags WHERE FileId = {w.Thumb1} AND TagId = {tagId}")).Should().Be(1);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM AlbumItems WHERE FileId = {w.Thumb1} AND AlbumId = {albumId}")).Should().Be(1);
        w.Fx.FtsRowIds("v1").Should().Contain(w.Thumb1);
        (await w.Fx.GetBatchAsync(removed.BatchId))!.Status.Should().Be(BatchStatus.Undone);
    }

    [Fact]
    public async Task Galerie_Taste_nimmt_das_Entfernen_ebenfalls_zurueck()
    {
        using var w = await ArrangeAsync();
        await w.Store.SetFolderNamesAsync(["Vorschau"]);
        var removed = await w.Service.RemoveFromCatalogAsync([w.Thumb1, w.Thumb2]);

        var undo = await w.Fx.CreateFileOperationService().UndoLastAsync();

        undo!.BatchId.Should().Be(removed.BatchId);
        (await w.Fx.FindFileByIdAsync(w.Thumb2)).Should().NotBeNull();
    }

    [Fact]
    public async Task Ohne_gespeicherten_Ausschluss_wird_nichts_entfernt()
    {
        using var w = await ArrangeAsync();

        var result = await w.Service.RemoveFromCatalogAsync([w.Thumb1, w.Normal]);

        result.BatchId.Should().Be(0);
        result.SkippedNotExcluded.Should().Be(2);
        (await w.Fx.FindFileByIdAsync(w.Thumb1)).Should().NotBeNull();
        await using var ctx = w.Fx.CreateContext();
        (await ctx.OperationBatches.CountAsync()).Should().Be(0);
    }
}
