using Lumora.Removal;
using Lumora.Services.Operations;
using Lumora.Services.Removal;

namespace Lumora.Wpf.Tests.Removal;

/// <summary>Removal-Dienst mit vorgegebenen Antworten (ADR-0065); merkt sich die Aufrufe.</summary>
internal sealed class FakeRemovalService : IRemovalService
{
    public FolderRecyclePreview? Preview { get; set; }

    public Queue<FolderRecycleResult> RecycleResults { get; } = new();

    public MissingSummary Summary { get; set; } = MissingSummary.Empty;

    public MissingRemovalBatchResult RemoveResult { get; set; } = MissingRemovalBatchResult.Empty;

    public Dictionary<int, int> MissingByFolder { get; set; } = new();

    public List<(int FolderId, bool Permanent)> RecycleCalls { get; } = [];

    public List<MissingTarget> RemoveCalls { get; } = [];

    public List<MissingTarget> SummaryCalls { get; } = [];

    public List<int> UndoCalls { get; } = [];

    public Task<FolderRecyclePreview?> PreviewFolderRecycleAsync(int folderId, CancellationToken ct = default) => Task.FromResult(Preview);

    public Task<FolderRecycleResult> RecycleFolderAsync(int folderId, bool allowPermanentDelete, CancellationToken ct = default)
    {
        RecycleCalls.Add((folderId, allowPermanentDelete));
        return Task.FromResult(RecycleResults.Count > 0 ? RecycleResults.Dequeue() : new FolderRecycleResult(7, true, allowPermanentDelete, 2, 0, 1, null));
    }

    public Task<MissingSummary> SummarizeMissingAsync(MissingTarget target, CancellationToken ct = default)
    {
        SummaryCalls.Add(target);
        return Task.FromResult(Summary);
    }

    public Task<MissingRemovalBatchResult> RemoveMissingAsync(MissingTarget target, CancellationToken ct = default)
    {
        RemoveCalls.Add(target);
        return Task.FromResult(RemoveResult);
    }

    public Task<IReadOnlyDictionary<int, int>> CountMissingByFolderAsync(IReadOnlyList<int> libraryIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<int, int>>(MissingByFolder);

    public Task<FileOperationBatchResult?> UndoAsync(int batchId, CancellationToken ct = default)
    {
        UndoCalls.Add(batchId);
        return Task.FromResult<FileOperationBatchResult?>(new FileOperationBatchResult(batchId, Lumora.Domain.Enums.OperationType.RecycleFolder, 2, 0,
            [new FileOperationItemResult(1, "a", null, true, null), new FileOperationItemResult(2, "b", null, true, null)]));
    }

    public static FolderRecyclePreview PreviewOf(int folderId, string? blocked = null, string? noBin = null, UserDataCounts? userData = null)
        => new(folderId, 1, "Reise", @"X:\Fotos\Reise", blocked, 3, 4, 1, userData ?? new UserDataCounts(3, 2, 1, 1, 0), noBin);
}

/// <summary>Bestätigungen mit fester Antwort; merkt sich Titel und Text.</summary>
internal sealed class FakeRemovalDialogs : IRemovalDialogs
{
    public bool Answer { get; set; } = true;

    public List<(string Title, string Message, string Confirm)> Requests { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Requests.Add((title, message, confirmText));
        return Task.FromResult(Answer);
    }
}
