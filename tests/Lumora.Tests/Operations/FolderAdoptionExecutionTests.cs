using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Catalog;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Operations;

/// <summary>
/// „Ordner in die Bibliothek übernehmen“ mit echtem Dateisystem und Katalog (ADR-0034): ein AdoptFolder-Batch, stabile
/// FileIds (Bewertung/Tag wandern mit), Folders.RootId = Bibliotheksordner, FTS, Nicht-Mediendateien, Papierkorb des
/// Quellordners nur ohne Rest, Eingang austragen, Rückgängig (Dateien zurück, Eingang wieder eingetragen, leere Zielordner
/// entfernt), Teilübernahme eines Unterordners und Eingang behalten. Dazu „Ordner bewerten“ über die vorhandenen Dienste.
/// </summary>
public sealed class FolderAdoptionExecutionTests
{
    private sealed class Env : IDisposable
    {
        public required TempLibraryFixture Fx { get; init; }
        public required int LibraryId { get; init; }
        public required int InboxRootId { get; init; }
        public required int ArchiveRootId { get; init; }
        public required FileOperationService FileOps { get; init; }
        public required FakeRecycleBin RecycleBin { get; init; }
        public required FolderAdoptionService Adoption { get; init; }

        public void Dispose() => Fx.Dispose();
    }

    private static async Task<Env> CreateAsync()
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var inbox = await fx.AddInboxRootAsync(lib.Id);
        fx.WriteLibraryFile("vorhanden.jpg");
        fx.WriteInboxFile(@"leer\leer2\inhalt\a.jpg");
        fx.WriteInboxFile(@"Reise\Tag1\b.jpg");
        fx.WriteInboxFile(@"Reise\Tag2\c.jpg");
        File.WriteAllText(Path.Combine(fx.InboxDir, "Reise", "notiz.txt"), "Notiz");
        fx.WriteInboxFile(@"Reise\.thumbs\b_thumb.jpg"); // Systemordner (V2) — nicht übernehmen, nicht als Rest zählen
        await fx.ScanAsync(lib.Id);
        var recycle = new FakeRecycleBin();
        var fileOps = fx.CreateFileOperationService(recycle);
        var archiveRootId = (await fx.GetRootsAsync(lib.Id)).Single(r => r.Role == LibraryRootRole.Archive).Id;
        var adoption = new FolderAdoptionService(fx.ContextFactory, fileOps, fx.CreateLibraryService(), NullLogger<FolderAdoptionService>.Instance);
        return new Env
        {
            Fx = fx,
            LibraryId = lib.Id,
            InboxRootId = inbox.Id,
            ArchiveRootId = archiveRootId,
            FileOps = fileOps,
            RecycleBin = recycle,
            Adoption = adoption,
        };
    }

    private static async Task<(FolderAdoptionContext Context, FolderAdoptionPlan Plan)> PlanAsync(Env env, string source, string target, bool collapse = true)
    {
        var context = await env.Adoption.GetContextAsync(source);
        context.Should().NotBeNull();
        var snapshot = await env.Adoption.ReadSourceAsync(source);
        return (context!, env.Adoption.BuildPlan(snapshot, target, collapse));
    }

    [Fact]
    public async Task Ganzer_Eingang_wird_mit_Struktur_uebernommen_Quelle_in_den_Papierkorb_Eingang_ausgetragen_und_Rueckgaengig_stellt_alles_wieder_her()
    {
        using var env = await CreateAsync();
        var fx = env.Fx;
        var a = (await fx.FindFileAsync("a.jpg"))!;
        var b = (await fx.FindFileAsync("b.jpg"))!;
        await fx.CreateCatalogUpdateService().SetRatingAsync(new[] { a.Id }, 4);
        var tags = fx.CreateTagService();
        var tag = await tags.CreateAsync("Urlaub");
        await tags.AssignAsync(new[] { b.Id }, new[] { tag.Id });
        var target = Path.Combine(fx.LibraryRoot, "2024");
        var (context, plan) = await PlanAsync(env, fx.InboxDir, target);
        context.IsWholeInbox.Should().BeTrue();
        context.ArchiveRootId.Should().Be(env.ArchiveRootId);
        plan.IgnoredEntries.Should().ContainSingle(e => e.EndsWith(".thumbs", StringComparison.Ordinal));

        var outcome = await env.Adoption.ExecuteAsync(context, plan, FolderAdoptionOptions.Default);

        var inboxName = Path.GetFileName(fx.InboxDir);
        var newA = Path.Combine(target, inboxName, "inhalt", "a.jpg");
        var newB = Path.Combine(target, inboxName, "Reise", "Tag1", "b.jpg");
        var newNote = Path.Combine(target, inboxName, "Reise", "notiz.txt");
        outcome.Execution.Moved.Should().Be(4, "3 Medien + notiz.txt");
        outcome.Execution.Failed.Should().Be(0);
        outcome.Execution.SourceRecycled.Should().BeTrue();
        outcome.InboxUnregistered.Should().BeTrue();
        outcome.Summary.Should().Contain("Quellordner im Papierkorb").And.Contain("ausgetragen");
        File.Exists(newA).Should().BeTrue();
        File.Exists(newB).Should().BeTrue();
        File.Exists(newNote).Should().BeTrue("Nicht-Mediendateien wandern mit");
        Directory.Exists(fx.InboxDir).Should().BeFalse("Quellordner liegt im Papierkorb");
        env.RecycleBin.Recycled.Should().ContainSingle().Which.Should().Be(fx.InboxDir);
        (await fx.GetRootsAsync(env.LibraryId)).Should().ContainSingle("Eingang ausgetragen");

        var movedA = (await fx.FindFileByIdAsync(a.Id))!;
        movedA.Rating.Should().Be(4, "FileId bleibt — Bewertung wandert mit");
        (await tags.GetTagIdsForFilesAsync(new[] { b.Id }))[b.Id].Should().Equal(tag.Id);
        var folderA = (await fx.FindFolderByIdAsync(movedA.FolderId))!;
        folderA.Path.Should().Be(Path.GetDirectoryName(newA));
        folderA.RootId.Should().Be(env.ArchiveRootId);
        (await fx.FindFolderAsync(Path.Combine(target, inboxName)))!.RootId.Should().Be(env.ArchiveRootId, "Verzweigungsordner ohne Dateien ebenfalls");
        fx.FtsRowIds("a").Should().Contain(a.Id);
        await using (var ctx = fx.CreateContext())
        {
            (await ctx.Folders.CountAsync(f => f.RootId == env.InboxRootId)).Should().Be(0);
        }

        var batch = (await fx.GetBatchAsync(outcome.Execution.BatchId))!;
        batch.OperationType.Should().Be(OperationType.AdoptFolder);
        batch.Status.Should().Be(BatchStatus.Applied);
        var ops = await fx.GetOperationsAsync(batch.Id);
        ops.Count(o => o.Action == OperationType.Move).Should().Be(4);
        ops.Should().Contain(o => o.Action == OperationType.MkDir && o.SourcePath == target);
        ops.Should().ContainSingle(o => o.Action == OperationType.Recycle && o.Status == FileOperationStatus.Applied);
        fx.Messages.OfKind(FilesChangeKind.Moved).SelectMany(m => m.FileIds).Should().Contain(new[] { a.Id, b.Id });
        (await env.FileOps.GetRecentBatchesAsync(1)).Single().CanUndo.Should().BeTrue();

        // Rückgängig: Dateien zurück, Eingang wieder eingetragen, leere Zielordner weg
        var undo = await env.FileOps.UndoLastAsync();

        undo!.Failed.Should().Be(0);
        File.Exists(Path.Combine(fx.InboxDir, "leer", "leer2", "inhalt", "a.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(fx.InboxDir, "Reise", "notiz.txt")).Should().BeTrue();
        Directory.Exists(target).Should().BeFalse("vom Batch angelegte, wieder leere Zielordner werden entfernt");
        var roots = await fx.GetRootsAsync(env.LibraryId);
        var reInbox = roots.Should().ContainSingle(r => r.Role == LibraryRootRole.Inbox).Subject;
        reInbox.Path.Should().Be(fx.InboxDir);
        var backA = (await fx.FindFileByIdAsync(a.Id))!;
        backA.Rating.Should().Be(4);
        (await fx.FindFolderByIdAsync(backA.FolderId))!.RootId.Should().Be(reInbox.Id);
        (await fx.GetBatchAsync(batch.Id))!.Status.Should().Be(BatchStatus.Undone);
        await using (var ctx = fx.CreateContext())
        {
            (await ctx.Folders.AnyAsync(f => f.Path.StartsWith(target))).Should().BeFalse("Folders-Zeilen der Zielordner entfernt");
        }
    }

    [Fact]
    public async Task Unterordner_uebernehmen_laesst_den_Eingang_eingetragen_und_raeumt_seine_Ordnerzeilen()
    {
        using var env = await CreateAsync();
        var fx = env.Fx;
        var source = Path.Combine(fx.InboxDir, "Reise");
        var target = Path.Combine(fx.LibraryRoot, "Reisen");
        var (context, plan) = await PlanAsync(env, source, target);
        context.IsWholeInbox.Should().BeFalse();

        var outcome = await env.Adoption.ExecuteAsync(context, plan, FolderAdoptionOptions.Default);

        outcome.Execution.Moved.Should().Be(3);
        outcome.InboxUnregistered.Should().BeFalse();
        File.Exists(Path.Combine(target, "Reise", "Tag2", "c.jpg")).Should().BeTrue();
        Directory.Exists(source).Should().BeFalse();
        Directory.Exists(fx.InboxDir).Should().BeTrue();
        (await fx.GetRootsAsync(env.LibraryId)).Should().HaveCount(2);
        (await fx.FindFolderAsync(Path.Combine(source, "Tag1"))).Should().BeNull("leere Ordnerzeilen des Quellordners entfernt");
        (await fx.FindFileAsync("a.jpg")).Should().NotBeNull("der Rest des Eingangs bleibt");
    }

    [Fact]
    public async Task Ganzer_Eingang_ohne_Austragen_behaelt_den_leeren_Eingangsordner()
    {
        using var env = await CreateAsync();
        var fx = env.Fx;
        var (context, plan) = await PlanAsync(env, fx.InboxDir, Path.Combine(fx.LibraryRoot, "Import"));

        var outcome = await env.Adoption.ExecuteAsync(context, plan, FolderAdoptionOptions.Default with { UnregisterInbox = false });

        outcome.Execution.SourceRecycled.Should().BeTrue();
        outcome.InboxUnregistered.Should().BeFalse();
        Directory.Exists(fx.InboxDir).Should().BeTrue("der Eingang bleibt als leerer Ordner bestehen");
        Directory.EnumerateFileSystemEntries(fx.InboxDir).Should().BeEmpty();
        (await fx.GetRootsAsync(env.LibraryId)).Should().HaveCount(2);
        (await fx.FindFolderAsync(fx.InboxDir)).Should().NotBeNull("Ordnerzeile der Eingangs-Wurzel bleibt");
    }

    [Fact]
    public async Task Rest_Dateien_verhindern_Papierkorb_und_Austragen()
    {
        using var env = await CreateAsync();
        var fx = env.Fx;
        var target = Path.Combine(fx.LibraryRoot, "2025");
        var (context, plan) = await PlanAsync(env, fx.InboxDir, target);
        var blocked = plan.Files.Single(f => f.SourcePath.EndsWith("c.jpg", StringComparison.Ordinal)).DestPath;
        TempLibraryFixture.WriteFile(blocked); // Ziel nach der Vorschau belegt → Item scheitert

        var outcome = await env.Adoption.ExecuteAsync(context, plan, FolderAdoptionOptions.Default);

        outcome.Execution.Failed.Should().Be(1);
        outcome.HasProblems.Should().BeTrue();
        outcome.Execution.SourceRecycled.Should().BeFalse();
        outcome.Execution.RemainingFiles.Should().ContainSingle().Which.Should().EndWith("c.jpg");
        outcome.InboxUnregistered.Should().BeFalse();
        env.RecycleBin.Recycled.Should().BeEmpty();
        File.Exists(Path.Combine(fx.InboxDir, "Reise", "Tag2", "c.jpg")).Should().BeTrue();
        (await fx.GetRootsAsync(env.LibraryId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Ohne_verschobene_Datei_bleiben_keine_leeren_Zielordner_zurueck()
    {
        using var env = await CreateAsync();
        var fx = env.Fx;
        var source = Path.Combine(fx.InboxDir, "Reise");
        var target = Path.Combine(fx.LibraryRoot, "Neu", "Tiefer");
        var (context, plan) = await PlanAsync(env, source, target);
        foreach (var file in plan.Files)
        {
            File.Delete(file.SourcePath); // nach der Vorschau verschwunden → jedes Item scheitert
        }

        var outcome = await env.Adoption.ExecuteAsync(context, plan, FolderAdoptionOptions.Default);

        outcome.Execution.Moved.Should().Be(0);
        outcome.HasProblems.Should().BeTrue();
        (await fx.GetBatchAsync(outcome.Execution.BatchId))!.Status.Should().Be(BatchStatus.Failed);
        Directory.Exists(Path.Combine(fx.LibraryRoot, "Neu")).Should().BeFalse("angelegte Zielordner werden wieder entfernt");
        (await fx.FindFolderAsync(target)).Should().BeNull();
    }

    [Fact]
    public async Task Gescheiterter_Papierkorb_wird_als_Problem_gemeldet()
    {
        using var env = await CreateAsync();
        var fx = env.Fx;
        env.RecycleBin.ThrowOnRecycle = new IOException("gesperrt");
        var (context, plan) = await PlanAsync(env, Path.Combine(fx.InboxDir, "Reise"), Path.Combine(fx.LibraryRoot, "R"));

        var outcome = await env.Adoption.ExecuteAsync(context, plan, FolderAdoptionOptions.Default);

        outcome.Execution.Moved.Should().Be(3);
        outcome.Execution.SourceRecycled.Should().BeFalse();
        outcome.Execution.RecycleFailed.Should().BeTrue();
        outcome.HasProblems.Should().BeTrue();
        outcome.Summary.Should().Contain("gesperrt");
    }

    [Fact]
    public async Task Namenskonflikt_im_vorhandenen_Zielordner_benennt_um_und_haelt_FTS_aktuell()
    {
        using var env = await CreateAsync();
        var fx = env.Fx;
        fx.WriteLibraryFile(@"Ziel\inhalt\a.jpg");
        await fx.CreateScanService().ScanAsync(env.LibraryId, Lumora.Services.Scanning.ScanMode.Incremental, null, CancellationToken.None);
        var source = Path.Combine(fx.InboxDir, "leer");
        var a = (await fx.FindFileAsync("a.jpg"))!;
        await using (var ctx = fx.CreateContext())
        {
            a = await ctx.Files.AsNoTracking().Join(ctx.Folders, f => f.FolderId, d => d.Id, (f, d) => new { f, d })
                .Where(x => x.d.RootId == env.InboxRootId && x.f.Name == "a.jpg").Select(x => x.f).SingleAsync();
        }

        var (context, plan) = await PlanAsync(env, source, Path.Combine(fx.LibraryRoot, "Ziel"));
        plan.Files.Single().DestPath.Should().Be(Path.Combine(fx.LibraryRoot, "Ziel", "inhalt", "a_1.jpg"));
        plan.Folders.Single().MergesExisting.Should().BeTrue();

        var outcome = await env.Adoption.ExecuteAsync(context, plan, FolderAdoptionOptions.Default);

        outcome.Execution.Renamed.Should().Be(1);
        var moved = (await fx.FindFileByIdAsync(a.Id))!;
        moved.Name.Should().Be("a_1.jpg");
        fx.FtsRowIds("a_1").Should().Contain(a.Id);

        await env.FileOps.UndoLastAsync();
        (await fx.FindFileByIdAsync(a.Id))!.Name.Should().Be("a.jpg", "Rückgängig benennt zurück");
        File.Exists(Path.Combine(fx.LibraryRoot, "Ziel", "inhalt", "a.jpg")).Should().BeTrue("vorhandener Zielordner bleibt mit seiner Datei");
    }

    [Fact]
    public async Task Ordner_im_Bibliotheksordner_hat_keinen_Uebernahme_Kontext()
    {
        using var env = await CreateAsync();
        (await env.Adoption.GetContextAsync(env.Fx.LibraryRoot)).Should().BeNull();
        (await env.Adoption.GetContextAsync(env.Fx.OutsideDir)).Should().BeNull();
    }

    [Fact]
    public async Task Ordner_bewerten_setzt_Bewertung_Favorit_Tags_und_Kategorie_fuer_alle_Dateien_darunter()
    {
        using var env = await CreateAsync();
        var fx = env.Fx;
        var tags = fx.CreateTagService();
        var categories = fx.CreateCategoryService();
        var service = new FolderMetadataService(fx.ContextFactory, fx.CreateCatalogUpdateService(), tags, categories, NullLogger<FolderMetadataService>.Instance);
        var reise = (await fx.FindFolderAsync(Path.Combine(fx.InboxDir, "Reise", "Tag1")))!;
        var parentId = reise.ParentId!.Value;

        var ids = await service.GetFileIdsUnderAsync(parentId);

        ids.Should().HaveCount(2, "b.jpg und c.jpg unter Reise (rekursiv) — .thumbs liest der Scanner seit 0.4.33 nicht mehr ein (ADR-0066)");
        await service.SetRatingAsync(ids, 5);
        await service.SetFavoriteAsync(ids, true);
        var tag = await tags.CreateAsync("Reise");
        (await service.ChangeTagsAsync(ids, new[] { tag.Id }, Array.Empty<int>())).Assigned.Should().Be(2);
        var category = await categories.CreateAsync("Projekt", new[] { "Nord", "Süd" });
        (await service.SetCategoryValueAsync(ids, category.Id, category.Values[0].Id)).Should().Be(2);

        foreach (var id in ids)
        {
            var file = (await fx.FindFileByIdAsync(id))!;
            file.Rating.Should().Be(5);
            file.IsFavorite.Should().BeTrue();
        }

        (await categories.GetValuesForFilesAsync(ids)).Should().HaveCount(2);
        (await fx.FindFileAsync("a.jpg"))!.Rating.Should().BeNull("Dateien außerhalb des Ordners bleiben unberührt");
        fx.Messages.OfKind(FilesChangeKind.Metadata).Should().NotBeEmpty();
        await service.SetRatingAsync(ids, null);
        (await fx.FindFileByIdAsync(ids[0]))!.Rating.Should().BeNull();
    }
}
