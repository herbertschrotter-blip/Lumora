using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Services.Watch;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Operations;

/// <summary>
/// „Ordner verschieben“ (ADR-0066): FileIds bleiben, Pfade/Elternordner/Wurzel der Folders-Zeilen werden nachgezogen,
/// Verknüpfungen und Angaben bleiben, Rückgängig stellt alles wieder her; Ablehnungen mit klarer Meldung ohne Änderung.
/// </summary>
public sealed class FolderMoveTests
{
    private sealed record World(TempLibraryFixture Fx, int LibraryId, int ReiseId, int ZielId, int Alpha, int Bravo, FileOperationService Ops) : IDisposable
    {
        public string Reise => Path.Combine(Fx.LibraryRoot, "Reise");

        public string Ziel => Path.Combine(Fx.LibraryRoot, "Ziel");

        public void Dispose()
        {
            Ops.Dispose();
            Fx.Dispose();
        }
    }

    /// <summary>Bibliothek mit Reise\alpha.jpg, Reise\Tag2\bravo.jpg, Reise\notiz.txt, Ziel\z.jpg und wurzel.jpg.</summary>
    private static async Task<World> ArrangeAsync(IOwnChangeRegistry? own = null)
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("Reise", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("Reise", "Tag2", "bravo.jpg"));
        File.WriteAllText(Path.Combine(fx.LibraryRoot, "Reise", "notiz.txt"), "kein Medium");
        fx.WriteLibraryFile(Path.Combine("Ziel", "z.jpg"));
        fx.WriteLibraryFile("wurzel.jpg");
        await fx.ScanAsync(library.Id);
        var reise = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "Reise")))!;
        var ziel = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "Ziel")))!;
        var ops = fx.CreateFileOperationService(ownChanges: own);
        return new World(fx, library.Id, reise.Id, ziel.Id, (await fx.FindFileAsync("alpha.jpg"))!.Id, (await fx.FindFileAsync("bravo.jpg"))!.Id, ops);
    }

    [Fact]
    public async Task Verschieben_behaelt_FileIds_und_Angaben_und_zieht_die_Ordnerzeilen_nach()
    {
        using var w = await ArrangeAsync();
        var (tagId, albumId, _) = await RemovalTestKit.DecorateAsync(w.Fx, w.LibraryId, w.Alpha, rating: 4);

        var result = await w.Ops.MoveFolderAsync(w.ReiseId, w.Ziel);

        result.Success.Should().BeTrue(result.Error);
        var newPath = Path.Combine(w.Ziel, "Reise");
        result.Path.Should().Be(newPath);
        Directory.Exists(w.Reise).Should().BeFalse();
        File.Exists(Path.Combine(newPath, "Tag2", "bravo.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(newPath, "notiz.txt")).Should().BeTrue("Nicht-Medien gehen mit");

        var folder = (await w.Fx.FindFolderByIdAsync(w.ReiseId))!;
        folder.Path.Should().Be(newPath);
        folder.ParentId.Should().Be(w.ZielId);
        (await w.Fx.FindFolderAsync(Path.Combine(newPath, "Tag2")))!.ParentId.Should().Be(w.ReiseId);
        var alpha = (await w.Fx.FindFileByIdAsync(w.Alpha))!;
        alpha.FolderId.Should().Be(w.ReiseId, "dieselbe FileId, dieselbe Ordnerzeile");
        alpha.Rating.Should().Be(4);
        (await w.Fx.FindFileByIdAsync(w.Bravo)).Should().NotBeNull();
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM FileTags WHERE FileId = {w.Alpha} AND TagId = {tagId}")).Should().Be(1);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM AlbumItems WHERE FileId = {w.Alpha} AND AlbumId = {albumId}")).Should().Be(1);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM Thumbnails WHERE FileId = {w.Alpha}")).Should().Be(1, "Vorschaubilder sind FileId-basiert");

        var batch = (await w.Fx.GetBatchAsync(result.BatchId))!;
        batch.OperationType.Should().Be(OperationType.MoveFolder);
        batch.Status.Should().Be(BatchStatus.Applied);
        var ops = await w.Fx.GetOperationsAsync(result.BatchId);
        ops.Should().ContainSingle(o => o.FileId == null && o.Action == OperationType.MoveFolder && o.DestPath == newPath);
        ops.Where(o => o.FileId != null).Should().HaveCount(2).And.OnlyContain(o => o.Action == OperationType.Move);
        w.Fx.Messages.OfKind(FilesChangeKind.Moved).SelectMany(m => m.FileIds).Should().BeEquivalentTo([w.Alpha, w.Bravo]);
    }

    [Fact]
    public async Task Rueckgaengig_stellt_Pfade_Elternordner_und_Verknuepfungen_wieder_her()
    {
        using var w = await ArrangeAsync();
        var (tagId, _, _) = await RemovalTestKit.DecorateAsync(w.Fx, w.LibraryId, w.Bravo);
        var rootFolder = (await w.Fx.FindFolderAsync(w.Fx.LibraryRoot))!;
        var moved = await w.Ops.MoveFolderAsync(w.ReiseId, w.Ziel);

        var undo = await w.Ops.UndoBatchAsync(moved.BatchId);

        undo!.Failed.Should().Be(0);
        Directory.Exists(w.Reise).Should().BeTrue();
        Directory.Exists(Path.Combine(w.Ziel, "Reise")).Should().BeFalse();
        var folder = (await w.Fx.FindFolderByIdAsync(w.ReiseId))!;
        folder.Path.Should().Be(w.Reise);
        folder.ParentId.Should().Be(rootFolder.Id);
        (await w.Fx.FindFolderAsync(Path.Combine(w.Reise, "Tag2"))).Should().NotBeNull();
        (await w.Fx.FindFileByIdAsync(w.Bravo))!.FolderId.Should().Be((await w.Fx.FindFolderAsync(Path.Combine(w.Reise, "Tag2")))!.Id);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM FileTags WHERE FileId = {w.Bravo} AND TagId = {tagId}")).Should().Be(1);
        (await w.Fx.GetBatchAsync(moved.BatchId))!.Status.Should().Be(BatchStatus.Undone);
    }

    [Fact]
    public async Task Galerie_Taste_nimmt_das_Verschieben_zurueck()
    {
        using var w = await ArrangeAsync();
        var moved = await w.Ops.MoveFolderAsync(w.ReiseId, w.Ziel);

        var undo = await w.Ops.UndoLastAsync();

        undo!.BatchId.Should().Be(moved.BatchId);
        Directory.Exists(w.Reise).Should().BeTrue();
    }

    [Fact]
    public async Task Ablehnungen_aendern_nichts_und_nennen_den_Grund()
    {
        using var w = await ArrangeAsync();
        var root = (await w.Fx.FindFolderAsync(w.Fx.LibraryRoot))!;
        var tag2 = (await w.Fx.FindFolderAsync(Path.Combine(w.Reise, "Tag2")))!;
        Directory.CreateDirectory(Path.Combine(w.Fx.LibraryRoot, "Belegt", "Reise"));

        (await w.Ops.MoveFolderAsync(root.Id, w.Ziel)).Error.Should().Contain("Wurzel");
        (await w.Ops.MoveFolderAsync(w.ReiseId, w.Reise)).Error.Should().Contain("in sich selbst");
        (await w.Ops.MoveFolderAsync(w.ReiseId, Path.Combine(w.Reise, "Tag2"))).Error.Should().Contain("in sich selbst");
        (await w.Ops.MoveFolderAsync(tag2.Id, w.Reise)).Error.Should().Contain("liegt bereits");
        (await w.Ops.MoveFolderAsync(w.ReiseId, Path.Combine(w.Fx.LibraryRoot, "Belegt"))).Error.Should().Contain("Zusammenführen");
        (await w.Ops.MoveFolderAsync(w.ReiseId, w.Fx.OutsideDir)).Error.Should().Contain("außerhalb dieser Bibliothek");
        (await w.Ops.MoveFolderAsync(w.ReiseId, Path.Combine(w.Fx.LibraryRoot, "gibtsnicht"))).Error.Should().Contain("nicht vorhanden");
        Directory.CreateDirectory(Path.Combine(w.Fx.LibraryRoot, ".thumbs"));
        (await w.Ops.MoveFolderAsync(w.ReiseId, Path.Combine(w.Fx.LibraryRoot, ".thumbs"))).Error.Should().Contain("ausgeschlossen");

        Directory.Exists(w.Reise).Should().BeTrue();
        (await w.Fx.FindFolderByIdAsync(w.ReiseId))!.Path.Should().Be(w.Reise);
        await using var ctx = w.Fx.CreateContext();
        (await ctx.OperationBatches.CountAsync()).Should().Be(0, "abgelehnt wird vor jedem Protokoll");
    }

    [Fact]
    public async Task Ziel_in_einer_anderen_Bibliothek_wird_abgelehnt()
    {
        using var w = await ArrangeAsync();
        await Scanning.ScanExclusionTests.AddLibraryAsync(w.Fx, "Andere", w.Fx.InboxDir);

        var result = await w.Ops.MoveFolderAsync(w.ReiseId, w.Fx.InboxDir);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("außerhalb dieser Bibliothek");
    }

    [Fact]
    public async Task Ordner_aus_einem_Eingang_ins_Archiv_derselben_Bibliothek_bekommt_die_neue_Wurzel()
    {
        using var w = await ArrangeAsync();
        var inbox = await w.Fx.AddInboxRootAsync(w.LibraryId);
        w.Fx.WriteInboxFile(Path.Combine("Neu", "n.jpg"));
        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.Incremental, null, CancellationToken.None);
        var neu = (await w.Fx.FindFolderAsync(Path.Combine(w.Fx.InboxDir, "Neu")))!;
        neu.RootId.Should().Be(inbox.Id);

        var result = await w.Ops.MoveFolderAsync(neu.Id, w.Ziel);

        result.Success.Should().BeTrue(result.Error);
        var moved = (await w.Fx.FindFolderByIdAsync(neu.Id))!;
        moved.RootId.Should().NotBe(inbox.Id, "liegt jetzt unter der Archiv-Wurzel");
        moved.ParentId.Should().Be(w.ZielId);

        (await w.Ops.UndoBatchAsync(result.BatchId))!.Failed.Should().Be(0);
        (await w.Fx.FindFolderByIdAsync(neu.Id))!.RootId.Should().Be(inbox.Id);
    }

    [Fact]
    public async Task Quelle_und_Ziel_gelten_der_Ordnerueberwachung_als_eigen()
    {
        var own = new OwnChangeRegistry();
        using var w = await ArrangeAsync(own);

        var result = await w.Ops.MoveFolderAsync(w.ReiseId, w.Ziel);

        result.Success.Should().BeTrue();
        own.IsOwn(w.Reise).Should().BeTrue();
        own.IsOwn(Path.Combine(w.Ziel, "Reise")).Should().BeTrue();
        own.IsOwn(Path.Combine(w.Ziel, "Reise", "Tag2", "bravo.jpg")).Should().BeTrue("ganzer Baum, kein „fehlt“-Sturm");
    }

    [Fact]
    public async Task Ausschluesse_unter_dem_Ordner_ziehen_mit()
    {
        using var w = await ArrangeAsync();
        var store = new ScanExclusionStore(w.Fx.ContextFactory, w.Fx.Messenger);
        await store.AddFolderAsync(w.LibraryId, Path.Combine(w.Reise, "Privat"));

        (await w.Ops.MoveFolderAsync(w.ReiseId, w.Ziel)).Success.Should().BeTrue();

        (await store.GetFoldersAsync(w.LibraryId)).Single().FullPath.Should().BeEquivalentTo(Path.Combine(w.Ziel, "Reise", "Privat"));
    }
}
