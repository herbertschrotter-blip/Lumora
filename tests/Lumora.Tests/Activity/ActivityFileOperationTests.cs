using Lumora.Domain.Enums;
using Lumora.Services.Activity;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Activity;

/// <summary>
/// Datei-Vorgänge in der Aktivitäts-Anzeige (ADR-0067) mit dem echten <see cref="FileOperationService"/>: Fortschritt je
/// Datei, „Zuletzt fertig“ mit Rückgängig über UndoBatchAsync, Rückgängig auf anderem Weg zieht den Eintrag nach,
/// verschachtelte Aufrufe ohne eigenen Eintrag, Abbrechen über das verknüpfte Token, wartende Scan-Sperre als „wartet“.
/// </summary>
public sealed class ActivityFileOperationTests
{
    private static async Task<(TempLibraryFixture Fx, int[] Ids)> ArrangeAsync(int files = 3)
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        for (var i = 0; i < files; i++)
        {
            fx.WriteLibraryFile(Path.Combine("a", $"f{i}.jpg"));
        }

        await fx.ScanAsync(library.Id);
        var ids = new int[files];
        for (var i = 0; i < files; i++)
        {
            ids[i] = (await fx.FindFileAsync($"f{i}.jpg"))!.Id;
        }

        return (fx, ids);
    }

    [Fact]
    public async Task Verschieben_meldet_Fortschritt_und_endet_mit_Rueckgaengig_ueber_UndoBatch()
    {
        var (fx, ids) = await ArrangeAsync();
        using var _ = fx;
        var time = new ManualTimerTime();
        using var center = new ActivityCenter(time);
        using var inner = fx.CreateFileOperationService();
        var ops = new ActivityTrackingFileOperationService(inner, center);
        var finished = new List<ActivitySnapshot>();
        center.Finished += finished.Add;
        var target = Path.Combine(fx.LibraryRoot, "Urlaub 2024");

        var result = await ops.MoveAsync(ids, target);

        result.Succeeded.Should().Be(3);
        var entry = center.GetOverview().Recent.Should().ContainSingle().Subject;
        entry.Title.Should().Be("Verschieben");
        entry.Target.Should().Be("nach „Urlaub 2024“", "nur der Ordnername, kein Pfad");
        entry.Done.Should().Be(3, "der Dienst meldet je Datei über ActivityScope.ReportItems");
        entry.Result.Should().Be("3 Dateien verschoben");
        entry.UserInitiated.Should().BeTrue();
        entry.HasAction(ActivityActionKind.Undo).Should().BeTrue();
        finished.Should().ContainSingle().Which.Id.Should().Be(entry.Id);

        var undo = await center.UndoAsync(entry.Id);

        undo.Should().Be("Rückgängig: 3 Dateien zurückverschoben");
        File.Exists(Path.Combine(fx.LibraryRoot, "a", "f0.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "f0.jpg")).Should().BeFalse();
        var overview = center.GetOverview();
        var single = overview.Recent.Should().ContainSingle("Rückgängig aus der Liste legt keinen zweiten Eintrag an").Subject;
        single.HasAction(ActivityActionKind.Undo).Should().BeFalse();
        single.Result.Should().Be("3 Dateien verschoben · rückgängig gemacht");
    }

    [Fact]
    public async Task Rueckgaengig_ueber_die_Galerie_Taste_zieht_den_Eintrag_nach()
    {
        var (fx, ids) = await ArrangeAsync(1);
        using var _ = fx;
        using var center = new ActivityCenter(new ManualTimerTime());
        using var inner = fx.CreateFileOperationService();
        var ops = new ActivityTrackingFileOperationService(inner, center);
        await ops.MoveAsync(ids, Path.Combine(fx.LibraryRoot, "b"));
        var moveEntry = center.GetOverview().Recent.Single();

        var undo = await ops.UndoLastAsync();

        undo!.Succeeded.Should().Be(1);
        var recent = center.GetOverview().Recent;
        recent.Single(r => r.Id == moveEntry.Id).HasAction(ActivityActionKind.Undo).Should().BeFalse();
        (await center.UndoAsync(moveEntry.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Verschachtelt_im_Vorgang_entsteht_kein_eigener_Eintrag()
    {
        var (fx, ids) = await ArrangeAsync(2);
        using var _ = fx;
        using var center = new ActivityCenter(new ManualTimerTime());
        using var inner = fx.CreateFileOperationService();
        var ops = new ActivityTrackingFileOperationService(inner, center);
        var outer = center.Start(new ActivityStartInfo(ActivityKind.Sorter, "Sortieren", UserInitiated: true));

        using (ActivityScope.Enter(outer))
        {
            (await ops.MoveAsync([ids[0]], Path.Combine(fx.LibraryRoot, "x"))).Succeeded.Should().Be(1);
        }

        using (ActivityScope.Suppress())
        {
            (await ops.RecycleAsync([ids[1]])).Succeeded.Should().Be(1, "Fake-Papierkorb der Fixture");
        }

        var overview = center.GetOverview();
        overview.Running.Should().ContainSingle().Which.Id.Should().Be(outer.Id);
        overview.Running.Single().Done.Should().Be(0, "ohne acceptsItemProgress kein Datei-Fortschritt am Sortier-Lauf");
        overview.Recent.Should().BeEmpty();
    }

    [Fact]
    public async Task Abbrechen_im_Flyout_bricht_ueber_das_verknuepfte_Token_ab()
    {
        using var center = new ActivityCenter(new ManualTimerTime());
        var inner = new BlockingOperations();
        var ops = new ActivityTrackingFileOperationService(inner, center);

        var move = ops.MoveAsync([1, 2], @"C:\Ziel\Neu");
        await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = center.GetOverview().Running.Single();
        running.CanCancel.Should().BeTrue();
        running.Target.Should().Be("nach „Neu“");

        center.Cancel(running.Id).Should().BeTrue();

        await FluentActions.Awaiting(() => move).Should().ThrowAsync<OperationCanceledException>();
        var entry = center.GetOverview().Recent.Single();
        entry.State.Should().Be(ActivityState.Canceled);
        entry.Result.Should().StartWith("Abgebrochen — bereits erledigte bleiben");
    }

    [Fact]
    public async Task Wartende_Scan_Sperre_zeigt_den_Datei_Vorgang_unter_Wartet_mit_Abbrechen()
    {
        var (fx, ids) = await ArrangeAsync(1);
        using var _ = fx;
        using var center = new ActivityCenter(new ManualTimerTime());
        var gate = new ScanGate(fx.Messenger);
        using var bridge = new ActivityBridge(center, fx.Messenger, gate);
        bridge.Start();
        using var inner = fx.CreateFileOperationService(scanGate: gate);
        var ops = new ActivityTrackingFileOperationService(inner, center);
        var libraryId = (await fx.FindFileByIdAsync(ids[0]))!.LibraryId;

        Task<FileOperationBatchResult> move;
        using (await gate.AcquireAsync(libraryId, CancellationToken.None))
        {
            move = ops.MoveAsync(ids, Path.Combine(fx.LibraryRoot, "b"));
            await WaitUntilAsync(() => center.GetOverview().Waiting.Count == 1);
            var waiting = center.GetOverview().Waiting.Single();
            waiting.Title.Should().Be("Verschieben", "derselbe Eintrag wartet — kein zweiter");
            waiting.WaitReason.Should().Be("wartet auf laufendes Einlesen derselben Bibliothek");
            waiting.CanCancel.Should().BeTrue();
        }

        (await move).Succeeded.Should().Be(1);
        center.GetOverview().Waiting.Should().BeEmpty();
        center.GetOverview().Recent.Single().Result.Should().Be("1 Datei verschoben");
    }

    [Fact]
    public async Task Ordner_in_den_Papierkorb_nennt_den_Ordnernamen_und_bietet_Rueckgaengig()
    {
        using var center = new ActivityCenter(new ManualTimerTime());
        var inner = new BlockingOperations { RecycleFolderResult = new FolderRecycleResult(7, true, false, 58, 0, 1, null) };
        var ops = new ActivityTrackingFileOperationService(inner, center, (_, _) => Task.FromResult<string?>("alt"));
        inner.Release();

        await ops.RecycleFolderAsync(3);

        var entry = center.GetOverview().Recent.Single();
        entry.Title.Should().Be("In den Papierkorb: Ordner „alt“");
        entry.Result.Should().Be("Ordner „alt“ im Papierkorb (58 Dateien)");
        entry.HasAction(ActivityActionKind.Undo).Should().BeTrue();
        await center.UndoAsync(entry.Id);
        inner.UndoneBatches.Should().Equal(7);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Bedingung nicht erreicht");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Datei-Dienst, der beim Verschieben auf Freigabe bzw. Abbruch wartet; Undo merkt sich die Batches.</summary>
    private sealed class BlockingOperations : IFileOperationService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> UndoneBatches { get; } = [];

        public FolderRecycleResult? RecycleFolderResult { get; init; }

        public void Release() => _release.TrySetResult();

        public async Task<FileOperationBatchResult> MoveAsync(IReadOnlyList<int> fileIds, string targetFolderPath, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return new FileOperationBatchResult(1, OperationType.Move, fileIds.Count, 0, []);
        }

        public Task<FolderRecycleResult> RecycleFolderAsync(int folderId, bool allowPermanentDelete = false, CancellationToken ct = default)
            => Task.FromResult(RecycleFolderResult!);

        public Task<FileOperationBatchResult?> UndoBatchAsync(int batchId, CancellationToken ct = default)
        {
            UndoneBatches.Add(batchId);
            return Task.FromResult<FileOperationBatchResult?>(new FileOperationBatchResult(batchId, OperationType.RecycleFolder, 58, 0, []));
        }

        public Task<FileOperationBatchResult> RecycleAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FileOperationBatchResult> RenameAsync(int fileId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FolderOperationResult> CreateFolderAsync(int libraryId, string parentFolderPath, string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FolderOperationResult> RenameFolderAsync(int folderId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FolderAdoptionExecution> AdoptFolderAsync(int libraryId, FolderAdoptionPlan plan, bool recycleSource, bool keepSourceFolder = false, IProgress<int>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<FileOperationBatchResult?> UndoLastAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<OperationBatchInfo>> GetRecentBatchesAsync(int take = 50, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
