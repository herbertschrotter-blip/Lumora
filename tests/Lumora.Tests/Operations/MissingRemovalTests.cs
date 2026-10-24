using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Removal;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Operations;

/// <summary>„Fehlende aus der Bibliothek entfernen“ an Ordner, Auswahl und Bibliothek (ADR-0065): Guard, „wieder da“, Zahlen, Katalog sauber, Rückgängig.</summary>
public sealed class MissingRemovalTests : IAsyncLifetime
{
    private TempLibraryFixture _fx = null!;
    private FileOperationService _ops = null!;
    private MovingRecycleBin _bin = null!;
    private RemovalService _removal = null!;
    private int _libraryId;

    public async Task InitializeAsync()
    {
        _fx = await TempLibraryFixture.CreateAsync();
        var library = await _fx.CreateLibraryAsync();
        _libraryId = library.Id;
        _fx.WriteLibraryFile("wurzel.jpg");
        _fx.WriteLibraryFile(Path.Combine("2020", "weg1.jpg"));
        _fx.WriteLibraryFile(Path.Combine("2020", "Feier", "weg2.jpg"));
        _fx.WriteLibraryFile(Path.Combine("2021", "da.jpg"));
        await _fx.ScanAsync(library.Id);
        _bin = new MovingRecycleBin(Path.Combine(_fx.RootDir, "papierkorb"));
        _ops = _fx.CreateFileOperationService(_bin);
        _removal = RemovalTestKit.CreateRemovalService(_fx, _ops, _bin);
    }

    public Task DisposeAsync()
    {
        _ops.Dispose();
        _fx.Dispose();
        return Task.CompletedTask;
    }

    private string P(params string[] parts) => Path.Combine([_fx.LibraryRoot, .. parts]);

    private async Task<int> Id(string name) => (await _fx.FindFileAsync(name))!.Id;

    /// <summary>Ordner 2020 samt Unterordner auf der Platte löschen und die beiden Dateien als fehlend markieren.</summary>
    private async Task<(int Weg1, int Weg2, int Folder2020, int FolderFeier)> Delete2020Async()
    {
        var weg1 = await Id("weg1.jpg");
        var weg2 = await Id("weg2.jpg");
        var f2020 = (await _fx.FindFolderAsync(P("2020")))!.Id;
        var feier = (await _fx.FindFolderAsync(P("2020", "Feier")))!.Id;
        Directory.Delete(P("2020"), recursive: true);
        await RemovalTestKit.MarkMissingAsync(_fx, weg1, weg2);
        return (weg1, weg2, f2020, feier);
    }

    [Fact]
    public async Task Vorschau_am_Ordner_zaehlt_Fehlende_samt_Unterordnern_und_ihre_Benutzerdaten()
    {
        var (weg1, weg2, f2020, _) = await Delete2020Async();
        await RemovalTestKit.DecorateAsync(_fx, _libraryId, weg1);
        await using (var ctx = _fx.CreateContext())
        {
            (await ctx.Files.FirstAsync(f => f.Id == weg2)).IsFavorite = true;
            await ctx.SaveChangesAsync();
        }

        var summary = await _removal.SummarizeMissingAsync(new MissingTarget.InFolder(f2020));

        summary.FileIds.Should().BeEquivalentTo([weg1, weg2]);
        summary.Offline.Should().Be(0);
        summary.UserData.RatedOrFavorite.Should().Be(2);
        summary.UserData.InAlbums.Should().Be(1);
        summary.UserData.WithTagsOrCategories.Should().Be(1);
        summary.UserData.WithPersons.Should().Be(0);
        (await _removal.CountMissingByFolderAsync([_libraryId])).Values.Sum().Should().Be(2);
    }

    [Fact]
    public async Task Entfernen_am_Ordner_raeumt_Katalog_auf_und_entfernt_leere_verschwundene_Ordner()
    {
        var (weg1, weg2, f2020, feier) = await Delete2020Async();
        var (_, _, thumb) = await RemovalTestKit.DecorateAsync(_fx, _libraryId, weg1);

        var result = await _removal.RemoveMissingAsync(new MissingTarget.InFolder(f2020));

        result.Removed.Should().Be(2);
        result.FoldersRemoved.Should().Be(2, "2020 und Feier gibt es nicht mehr und sie sind leer");
        result.CanUndo.Should().BeTrue();
        (await _fx.FindFileByIdAsync(weg1)).Should().BeNull();
        (await _fx.FindFileByIdAsync(weg2)).Should().BeNull();
        (await _fx.GetThumbnailRowsAsync(weg1)).Should().BeEmpty();
        File.Exists(thumb).Should().BeFalse();
        _fx.FtsRowIds("weg1").Should().BeEmpty();
        (await RemovalTestKit.CountAsync(_fx, $"SELECT COUNT(*) FROM FileTags WHERE FileId = {weg1}")).Should().Be(0);
        (await RemovalTestKit.CountAsync(_fx, $"SELECT COUNT(*) FROM AlbumItems WHERE FileId = {weg1}")).Should().Be(0);
        (await RemovalTestKit.CountAsync(_fx, "SELECT COUNT(*) FROM FileLinkUndo")).Should().Be(1, "Verknüpfung gesichert (ADR-0054)");
        (await _fx.FindFolderByIdAsync(f2020)).Should().BeNull();
        (await _fx.FindFolderByIdAsync(feier)).Should().BeNull();
        (await _fx.FindFolderAsync(_fx.LibraryRoot)).Should().NotBeNull("Wurzel bleibt");
        (await _fx.FindFileAsync("da.jpg")).Should().NotBeNull();
        Directory.Exists(P("2021")).Should().BeTrue();
        _bin.RecycledDirectories.Should().BeEmpty("es wird keine Datei angefasst");

        var batch = (await _fx.GetBatchAsync(result.BatchId))!;
        batch.OperationType.Should().Be(OperationType.RemoveFromCatalog);
        batch.Status.Should().Be(BatchStatus.Applied);
        (await _fx.GetOperationsAsync(result.BatchId)).Should().HaveCount(2).And.OnlyContain(o => o.Action == OperationType.RemoveFromCatalog && o.Status == FileOperationStatus.Applied);
        _fx.Messages.OfKind(FilesChangeKind.Removed).SelectMany(m => m.FileIds).Should().BeEquivalentTo([weg1, weg2]);
    }

    [Fact]
    public async Task Rueckgaengig_bringt_Zeilen_als_fehlend_mit_denselben_Ids_und_Ordnern_zurueck()
    {
        var (weg1, weg2, f2020, feier) = await Delete2020Async();
        var (tagId, albumId, _) = await RemovalTestKit.DecorateAsync(_fx, _libraryId, weg1, rating: 3);
        var removed = await _removal.RemoveMissingAsync(new MissingTarget.InFolder(f2020));

        var undo = await _removal.UndoAsync(removed.BatchId);

        undo!.Failed.Should().Be(0);
        undo.Succeeded.Should().Be(2);
        var row = (await _fx.FindFileByIdAsync(weg1))!;
        row.Missing.Should().BeTrue("die Datei fehlt weiterhin");
        row.Rating.Should().Be(3);
        row.FolderId.Should().Be(f2020);
        (await _fx.FindFileByIdAsync(weg2))!.FolderId.Should().Be(feier);
        (await _fx.FindFolderByIdAsync(feier))!.ParentId.Should().Be(f2020);
        (await RemovalTestKit.CountAsync(_fx, $"SELECT COUNT(*) FROM FileTags WHERE FileId = {weg1} AND TagId = {tagId}")).Should().Be(1);
        (await RemovalTestKit.CountAsync(_fx, $"SELECT COUNT(*) FROM AlbumItems WHERE FileId = {weg1} AND AlbumId = {albumId}")).Should().Be(1);
        (await RemovalTestKit.CountAsync(_fx, "SELECT COUNT(*) FROM FileLinkUndo")).Should().Be(0);
        _fx.FtsRowIds("weg1").Should().Equal(weg1);
        (await _fx.GetBatchAsync(removed.BatchId))!.Status.Should().Be(BatchStatus.Undone);
        (await _ops.GetRecentBatchesAsync()).Single(b => b.Id == removed.BatchId).CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task Datei_wieder_da_wird_nicht_entfernt_und_fehlt_nicht_mehr()
    {
        var da = await Id("da.jpg");
        await RemovalTestKit.MarkMissingAsync(_fx, da); // fälschlich als fehlend markiert, Datei liegt aber da

        var result = await _ops.RemoveMissingAsync([da]);

        result.Removed.Should().Be(0);
        result.Reappeared.Should().Be(1);
        result.BatchId.Should().Be(0);
        (await _fx.FindFileByIdAsync(da))!.Missing.Should().BeFalse();
        _fx.Messages.OfKind(FilesChangeKind.Metadata).SelectMany(m => m.FileIds).Should().Contain(da);
    }

    [Fact]
    public async Task Offline_Wurzel_Dateien_gelten_als_offline_und_bleiben()
    {
        var offlineRoot = Path.Combine(_fx.RootDir, "usb-platte");
        Directory.CreateDirectory(offlineRoot);
        TempLibraryFixture.WriteFile(Path.Combine(offlineRoot, "usb.jpg"));
        await _fx.AddInboxRootAsync(_libraryId, offlineRoot);
        await _fx.CreateScanService().ScanAsync(_libraryId, Lumora.Services.Scanning.ScanMode.Incremental, null, CancellationToken.None);
        var usb = await Id("usb.jpg");
        await RemovalTestKit.MarkMissingAsync(_fx, usb);
        Directory.Delete(offlineRoot, recursive: true); // Laufwerk abgesteckt

        var summary = await _removal.SummarizeMissingAsync(new MissingTarget.InLibraries([_libraryId]));
        var result = await _removal.RemoveMissingAsync(new MissingTarget.InLibraries([_libraryId]));
        var direct = await _ops.RemoveMissingAsync([usb]);

        summary.OnlyOffline.Should().BeTrue();
        summary.OfflineRoots.Should().ContainSingle();
        result.Removed.Should().Be(0);
        result.SkippedOffline.Should().Be(1);
        direct.Removed.Should().Be(0, "auch der Dienst selbst prüft die Wurzel");
        direct.SkippedOffline.Should().Be(1);
        (await _fx.FindFileByIdAsync(usb))!.Missing.Should().BeTrue();
    }

    [Fact]
    public async Task Auswahl_gemischt_wird_erkannt_und_nicht_fehlende_bleiben()
    {
        var (weg1, _, _, _) = await Delete2020Async();
        var da = await Id("da.jpg");

        var summary = await _removal.SummarizeMissingAsync(new MissingTarget.Selection([weg1, da]));
        summary.Present.Should().Be(1);
        summary.FileIds.Should().Equal(weg1);

        var result = await _ops.RemoveMissingAsync([weg1, da]);
        result.Removed.Should().Be(1);
        result.SkippedNotMissing.Should().Be(1);
        (await _fx.FindFileByIdAsync(da)).Should().NotBeNull();
    }

    [Fact]
    public async Task Bibliothek_zaehlt_nur_ihre_eigenen_Fehlenden()
    {
        var (weg1, weg2, _, _) = await Delete2020Async();
        int otherFile;
        await using (var ctx = _fx.CreateContext())
        {
            var other = new Library { Name = "Andere", RootPath = Path.Combine(_fx.RootDir, "andere"), CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
            ctx.Libraries.Add(other);
            await ctx.SaveChangesAsync();
            Directory.CreateDirectory(other.RootPath);
            var root = new LibraryRoot { LibraryId = other.Id, Path = other.RootPath, Role = LibraryRootRole.Archive, Name = "andere" };
            ctx.LibraryRoots.Add(root);
            await ctx.SaveChangesAsync();
            var folder = new Folder { LibraryId = other.Id, RootId = root.Id, Path = other.RootPath, Name = "andere", ScannedAt = DateTime.UtcNow };
            ctx.Folders.Add(folder);
            await ctx.SaveChangesAsync();
            var file = new MediaFile { LibraryId = other.Id, FolderId = folder.Id, Name = "fremd.jpg", Extension = ".jpg", Missing = true, CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow };
            ctx.Files.Add(file);
            await ctx.SaveChangesAsync();
            otherFile = file.Id;
        }

        var summary = await _removal.SummarizeMissingAsync(new MissingTarget.InLibraries([_libraryId]));
        summary.FileIds.Should().BeEquivalentTo([weg1, weg2]);

        (await _removal.RemoveMissingAsync(new MissingTarget.InLibraries([_libraryId]))).Removed.Should().Be(2);
        (await _fx.FindFileByIdAsync(otherFile)).Should().NotBeNull("andere Bibliothek bleibt unberührt (ADR-0048)");
    }

    [Fact]
    public async Task Verschwundener_Ordner_mit_Verknuepfung_bleibt_im_Baum()
    {
        var (weg1, _, f2020, feier) = await Delete2020Async();
        var da = await Id("da.jpg");
        await using (var ctx = _fx.CreateContext())
        {
            ctx.FolderLinks.Add(new FolderLink { FolderId = feier, FileId = da, AddedAtUtc = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var result = await _removal.RemoveMissingAsync(new MissingTarget.InFolder(f2020));

        result.Removed.Should().Be(2);
        (await _fx.FindFolderByIdAsync(feier)).Should().NotBeNull("Verknüpfung hält den Ordner");
        (await _fx.FindFolderByIdAsync(f2020)).Should().NotBeNull("hat noch ein Kind");
        (await _fx.FindFileByIdAsync(weg1)).Should().BeNull();
    }
}
