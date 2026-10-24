using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Duplicates;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Watch;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Operations;

/// <summary>„Ordner in den Papierkorb“ samt Rückgängig (ADR-0065) — mit Fake-Papierkorb in Temp, nie dem echten.</summary>
public sealed class FolderRecycleTests
{
    private sealed record World(TempLibraryFixture Fx, int LibraryId, int FolderId, int SubFolderId, int Alpha, int Bravo, MovingRecycleBin Bin, FileOperationService Ops) : IDisposable
    {
        public string FolderPath => Path.Combine(Fx.LibraryRoot, "Reise");

        public void Dispose()
        {
            Ops.Dispose();
            Fx.Dispose();
        }
    }

    /// <summary>Bibliothek mit Reise\alpha.jpg, Reise\Tag2\bravo.jpg, Reise\notiz.txt und bleibt\charlie.jpg.</summary>
    private static async Task<World> ArrangeAsync(IOwnChangeRegistry? own = null)
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("Reise", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("Reise", "Tag2", "bravo.jpg"));
        File.WriteAllText(Path.Combine(fx.LibraryRoot, "Reise", "notiz.txt"), "kein Medium");
        fx.WriteLibraryFile(Path.Combine("bleibt", "charlie.jpg"));
        fx.WriteLibraryFile("wurzel.jpg"); // damit die Wurzel eine Folders-Zeile hat
        await fx.ScanAsync(library.Id);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!.Id;
        var bravo = (await fx.FindFileAsync("bravo.jpg"))!.Id;
        var folder = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "Reise")))!;
        var sub = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "Reise", "Tag2")))!;
        var bin = new MovingRecycleBin(Path.Combine(fx.RootDir, "papierkorb"));
        var ops = fx.CreateFileOperationService(bin, ownChanges: own);
        return new World(fx, library.Id, folder.Id, sub.Id, alpha, bravo, bin, ops);
    }

    [Fact]
    public async Task Ordner_kommt_in_den_Papierkorb_und_der_Katalog_wird_sauber()
    {
        using var w = await ArrangeAsync();
        var (_, _, thumb) = await RemovalTestKit.DecorateAsync(w.Fx, w.LibraryId, w.Alpha);

        var result = await w.Ops.RecycleFolderAsync(w.FolderId);

        result.Success.Should().BeTrue(result.Error);
        result.Permanent.Should().BeFalse();
        result.FilesRemoved.Should().Be(2);
        result.FoldersRemoved.Should().Be(2);
        result.CanUndo.Should().BeTrue();
        Directory.Exists(w.FolderPath).Should().BeFalse();
        w.Bin.Contains(w.FolderPath).Should().BeTrue();

        (await w.Fx.FindFileByIdAsync(w.Alpha)).Should().BeNull();
        (await w.Fx.FindFileByIdAsync(w.Bravo)).Should().BeNull();
        (await w.Fx.FindFileAsync("charlie.jpg")).Should().NotBeNull("anderer Ordner bleibt");
        (await w.Fx.FindFolderByIdAsync(w.FolderId)).Should().BeNull();
        (await w.Fx.FindFolderByIdAsync(w.SubFolderId)).Should().BeNull();
        (await w.Fx.GetThumbnailRowsAsync(w.Alpha)).Should().BeEmpty();
        File.Exists(thumb).Should().BeFalse("Cache-Datei mit entfernt");
        w.Fx.FtsRowIds("alpha").Should().BeEmpty();
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM FileTags WHERE FileId = {w.Alpha}")).Should().Be(0, "Kaskade");
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM AlbumItems WHERE FileId = {w.Alpha}")).Should().Be(0, "Kaskade");
        (await RemovalTestKit.CountAsync(w.Fx, "SELECT COUNT(*) FROM FileLinkUndo")).Should().Be(1, "Album-Verknüpfung zur Operation gesichert (ADR-0054)");

        var batch = (await w.Fx.GetBatchAsync(result.BatchId))!;
        batch.OperationType.Should().Be(OperationType.RecycleFolder);
        batch.Status.Should().Be(BatchStatus.Applied);
        var ops = await w.Fx.GetOperationsAsync(result.BatchId);
        ops.Should().HaveCount(3);
        ops.Should().ContainSingle(o => o.FileId == null && o.Action == OperationType.RecycleFolder && o.Status == FileOperationStatus.Applied);
        ops.Where(o => o.FileId != null).Should().OnlyContain(o => o.Action == OperationType.Recycle && o.Status == FileOperationStatus.Applied);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM DuplicateCleanupUndo WHERE BatchId = {result.BatchId} AND KeptFileId = 0")).Should().Be(3, "2 Datei-Abzüge + 1 Ordner-Abzug");
        w.Fx.Messages.OfKind(FilesChangeKind.Removed).SelectMany(m => m.FileIds).Should().BeEquivalentTo([w.Alpha, w.Bravo]);
    }

    [Fact]
    public async Task Rueckgaengig_holt_Ordner_und_Katalogzeilen_mit_denselben_Ids_zurueck()
    {
        using var w = await ArrangeAsync();
        var (tagId, albumId, _) = await RemovalTestKit.DecorateAsync(w.Fx, w.LibraryId, w.Alpha, rating: 5);
        var recycled = await w.Ops.RecycleFolderAsync(w.FolderId);
        recycled.Success.Should().BeTrue();

        var undo = await w.Ops.UndoLastAsync();

        undo.Should().NotBeNull();
        undo!.Failed.Should().Be(0);
        Directory.Exists(w.FolderPath).Should().BeTrue();
        File.Exists(Path.Combine(w.FolderPath, "notiz.txt")).Should().BeTrue("Nicht-Medien kommen mit dem Ordner zurück");
        var alpha = (await w.Fx.FindFileByIdAsync(w.Alpha))!;
        alpha.Rating.Should().Be(5);
        alpha.Missing.Should().BeFalse();
        alpha.FolderId.Should().Be(w.FolderId, "Ordnerzeile mit alter Id zurück");
        (await w.Fx.FindFileByIdAsync(w.Bravo))!.FolderId.Should().Be(w.SubFolderId);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM FileTags WHERE FileId = {w.Alpha} AND TagId = {tagId}")).Should().Be(1);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM AlbumItems WHERE FileId = {w.Alpha} AND AlbumId = {albumId}")).Should().Be(1);
        (await RemovalTestKit.CountAsync(w.Fx, "SELECT COUNT(*) FROM FileLinkUndo")).Should().Be(0, "Sicherung verworfen, Abzug hat sie zurückgebracht");
        w.Fx.FtsRowIds("alpha").Should().Equal(w.Alpha);
        (await w.Fx.GetBatchAsync(recycled.BatchId))!.Status.Should().Be(BatchStatus.Undone);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM DuplicateCleanupUndo WHERE BatchId = {recycled.BatchId} AND UndoneAtUtc IS NULL")).Should().Be(0);
        w.Fx.Messages.OfKind(FilesChangeKind.Added).SelectMany(m => m.FileIds).Should().BeEquivalentTo([w.Alpha, w.Bravo]);
    }

    [Fact]
    public async Task Wurzel_und_Ordner_mit_Wurzel_darin_werden_abgelehnt()
    {
        using var w = await ArrangeAsync();
        var root = (await w.Fx.FindFolderAsync(w.Fx.LibraryRoot))!;

        var rootResult = await w.Ops.RecycleFolderAsync(root.Id);

        rootResult.Success.Should().BeFalse();
        rootResult.BatchId.Should().Be(0);
        rootResult.Error.Should().Contain("Wurzel").And.Contain("Einstellungen");

        // Eingang innerhalb von „Reise“ → „Reise“ darf nicht weg
        await w.Fx.AddInboxRootAsync(w.LibraryId, Path.Combine(w.FolderPath, "Tag2"));
        var nested = await w.Ops.RecycleFolderAsync(w.FolderId);
        nested.Success.Should().BeFalse();
        nested.Error.Should().Contain("liegt die Wurzel");
        Directory.Exists(w.FolderPath).Should().BeTrue();
        (await w.Fx.FindFileByIdAsync(w.Alpha)).Should().NotBeNull();
    }

    [Fact]
    public async Task Ohne_Papierkorb_erst_Rueckfrage_dann_endgueltig_und_nicht_rueckgaengig()
    {
        using var w = await ArrangeAsync();
        w.Bin.UnavailableReason = "Wechseldatenträger ohne Papierkorb.";

        var ask = await w.Ops.RecycleFolderAsync(w.FolderId);

        ask.NeedsPermanentConsent.Should().BeTrue();
        ask.NoRecycleBinReason.Should().Contain("Wechseldatenträger");
        ask.BatchId.Should().Be(0);
        Directory.Exists(w.FolderPath).Should().BeTrue("ohne Zustimmung passiert nichts");
        (await w.Fx.FindFileByIdAsync(w.Alpha)).Should().NotBeNull();

        var deleted = await w.Ops.RecycleFolderAsync(w.FolderId, allowPermanentDelete: true);

        deleted.Success.Should().BeTrue(deleted.Error);
        deleted.Permanent.Should().BeTrue();
        deleted.CanUndo.Should().BeFalse();
        Directory.Exists(w.FolderPath).Should().BeFalse();
        w.Bin.RecycledDirectories.Should().BeEmpty("endgültig gelöscht, nicht über den Papierkorb");
        (await w.Fx.GetBatchAsync(deleted.BatchId))!.OperationType.Should().Be(OperationType.DeleteFolder);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM DuplicateCleanupUndo WHERE BatchId = {deleted.BatchId}")).Should().Be(0);
        var batches = await w.Ops.GetRecentBatchesAsync();
        batches.Single(b => b.Id == deleted.BatchId).CanUndo.Should().BeFalse();
        (await w.Ops.UndoLastAsync()).Should().BeNull("nichts Rückgängig-fähiges");
    }

    [Fact]
    public async Task Offline_Laufwerk_und_fehlender_Ordner_werden_abgelehnt()
    {
        using var w = await ArrangeAsync();
        var offlineRoot = Path.Combine(w.Fx.RootDir, "abgesteckt");
        var root = await w.Fx.AddInboxRootAsync(w.LibraryId, offlineRoot);
        int offlineFolderId;
        await using (var ctx = w.Fx.CreateContext())
        {
            var f = new Lumora.Domain.Entities.Folder { LibraryId = w.LibraryId, RootId = root.Id, Path = Path.Combine(offlineRoot, "x"), Name = "x", ScannedAt = DateTime.UtcNow };
            ctx.Folders.Add(f);
            await ctx.SaveChangesAsync();
            offlineFolderId = f.Id;
        }

        var offline = await w.Ops.RecycleFolderAsync(offlineFolderId);
        offline.Success.Should().BeFalse();
        offline.Error.Should().Contain("Laufwerk nicht erreichbar");

        Directory.Delete(Path.Combine(w.FolderPath, "Tag2"), recursive: true);
        var vanished = await w.Ops.RecycleFolderAsync(w.SubFolderId);
        vanished.Success.Should().BeFalse();
        vanished.Error.Should().Contain("Fehlende entfernen");
        (await w.Fx.FindFileByIdAsync(w.Bravo)).Should().NotBeNull("abgelehnt heißt: nichts geändert");
    }

    [Fact]
    public async Task Papierkorb_Fehler_aendert_nichts_und_verwirft_die_Abzuege()
    {
        using var w = await ArrangeAsync();
        w.Bin.ThrowOnDirectory = new IOException("gesperrt");

        var result = await w.Ops.RecycleFolderAsync(w.FolderId);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("gesperrt");
        Directory.Exists(w.FolderPath).Should().BeTrue();
        (await w.Fx.FindFileByIdAsync(w.Alpha)).Should().NotBeNull();
        (await w.Fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Failed);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM DuplicateCleanupUndo WHERE BatchId = {result.BatchId}")).Should().Be(0);
    }

    [Fact]
    public async Task Von_der_Shell_endgueltig_geloescht_wird_nicht_als_rueckgaengig_machbar_gebucht()
    {
        using var w = await ArrangeAsync();
        w.Bin.NotInBin = true;

        var result = await w.Ops.RecycleFolderAsync(w.FolderId);

        result.Permanent.Should().BeTrue();
        result.CanUndo.Should().BeFalse();
        (await w.Fx.GetBatchAsync(result.BatchId))!.OperationType.Should().Be(OperationType.DeleteFolder);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM DuplicateCleanupUndo WHERE BatchId = {result.BatchId}")).Should().Be(0);
        (await RemovalTestKit.CountAsync(w.Fx, "SELECT COUNT(*) FROM FileLinkUndo")).Should().Be(0, "endgültig gelöscht: keine Sicherung");
    }

    [Fact]
    public async Task Papierkorb_Fehler_bei_schon_fehlender_Datei_aendert_ebenfalls_nichts()
    {
        using var w = await ArrangeAsync();
        File.Delete(Path.Combine(w.FolderPath, "alpha.jpg"));
        await RemovalTestKit.MarkMissingAsync(w.Fx, w.Alpha);
        w.Bin.ThrowOnDirectory = new IOException("gesperrt");

        var result = await w.Ops.RecycleFolderAsync(w.FolderId);

        result.FilesRemoved.Should().Be(0);
        (await w.Fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Failed);
        (await w.Fx.FindFileByIdAsync(w.Alpha)).Should().NotBeNull("die schon fehlende Datei bleibt, der Ordner ging nicht weg");
    }

    [Fact]
    public async Task Rueckgaengig_nach_Katalogfehler_holt_beim_zweiten_Versuch_nur_den_Katalog_nach()
    {
        using var w = await ArrangeAsync();
        var recycled = await w.Ops.RecycleFolderAsync(w.FolderId);
        string original;
        await using (var ctx = w.Fx.CreateContext())
        {
            original = (await ctx.DuplicateCleanupUndo.SingleAsync(u => u.BatchId == recycled.BatchId && u.FileId == 0)).Snapshot;
            await ctx.DuplicateCleanupUndo.Where(u => u.BatchId == recycled.BatchId && u.FileId == 0).ExecuteUpdateAsync(s => s.SetProperty(u => u.Snapshot, "kaputt"));
        }

        var first = async () => await w.Ops.UndoLastAsync();
        await first.Should().ThrowAsync<Exception>();
        Directory.Exists(w.FolderPath).Should().BeTrue("der Ordner ist schon zurück");
        (await w.Fx.FindFileByIdAsync(w.Alpha)).Should().BeNull();

        await using (var ctx = w.Fx.CreateContext())
        {
            await ctx.DuplicateCleanupUndo.Where(u => u.BatchId == recycled.BatchId && u.FileId == 0).ExecuteUpdateAsync(s => s.SetProperty(u => u.Snapshot, original));
        }

        var second = await w.Ops.UndoLastAsync();

        second!.Failed.Should().Be(0);
        (await w.Fx.FindFileByIdAsync(w.Alpha))!.FolderId.Should().Be(w.FolderId);
        (await w.Fx.FindFileByIdAsync(w.Bravo)).Should().NotBeNull();
        (await w.Fx.GetBatchAsync(recycled.BatchId))!.Status.Should().Be(BatchStatus.Undone);
    }

    [Fact]
    public async Task Ordnerueberwachung_sieht_den_ganzen_Baum_als_eigen()
    {
        var own = new OwnChangeRegistry();
        using var w = await ArrangeAsync(own);

        var result = await w.Ops.RecycleFolderAsync(w.FolderId);

        result.Success.Should().BeTrue();
        own.IsOwn(w.FolderPath).Should().BeTrue("Nachlaufzeit: Ereignisse des Ordners lösen keinen Scan aus");
        own.IsOwn(Path.Combine(w.FolderPath, "Tag2", "bravo.jpg")).Should().BeTrue();
        own.IsOwn(Path.Combine(w.Fx.LibraryRoot, "bleibt", "charlie.jpg")).Should().BeFalse();
    }

    [Fact]
    public async Task Seite_Doppelte_bietet_die_Abzuege_nicht_als_eigenes_Rueckgaengig_an()
    {
        using var w = await ArrangeAsync();
        (await w.Ops.RecycleFolderAsync(w.FolderId)).Success.Should().BeTrue();
        var duplicates = new DuplicateCleanupService(w.Fx.ContextFactory, w.Ops, NullLogger<DuplicateCleanupService>.Instance, messenger: w.Fx.Messenger);

        (await duplicates.GetUndoableAsync()).Should().BeNull();
    }
}
