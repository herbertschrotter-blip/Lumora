using System.Collections.Concurrent;
using Lumora.Domain.Enums;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;

namespace Lumora.Services.Activity;

/// <summary>
/// Hängt jeden Datei-Vorgang in die Aktivitäts-Anzeige (ADR-0067), ohne den <see cref="FileOperationService"/> zu ändern:
/// Verschieben, Umbenennen, Papierkorb, Ordner umbenennen/verschieben/in den Papierkorb, „In die Bibliothek übernehmen“,
/// Fehlende/Ausgeschlossene entfernen, Rückgängig. Jeder Aufruf ist ein Vorgang des Benutzers (kurze Meldung) mit
/// „Abbrechen“ über ein verknüpftes CancellationToken (bereits erledigte Dateien bleiben, Rückgängig möglich) und — wo der
/// Batch rückgängig machbar ist — „Rückgängig“ über <see cref="IFileOperationService.UndoBatchAsync"/> (kein zweiter Weg).
/// Ordner anlegen (sofort fertig) und Abfragen laufen ohne Eintrag durch. Läuft der Aufruf schon in einem Vorgang
/// (<see cref="ActivityScope.IsOwned"/>, z. B. Sortier-Lauf, Hintergrund-Papierkorb), entsteht kein eigener Eintrag.
/// Texte nennen Ordner nur mit Namen, nie mit Pfad (Bibliotheks-Trennung, versteckte Sitzung).
/// </summary>
public sealed class ActivityTrackingFileOperationService : IFileOperationService
{
    private const int UndoMapLimit = 64;

    private readonly IFileOperationService _inner;
    private readonly IActivityCenter _center;
    private readonly Func<int, CancellationToken, Task<string?>>? _folderName;

    /// <summary>Batch → Eintrag, damit „Rückgängig“ auf anderem Weg (Galerie-Taste) den Eintrag nachzieht.</summary>
    private readonly ConcurrentDictionary<int, long> _entryByBatch = new();

    /// <param name="folderName">Name eines Katalog-Ordners für die Texte (optional, Tests ohne Katalog).</param>
    public ActivityTrackingFileOperationService(IFileOperationService inner, IActivityCenter center, Func<int, CancellationToken, Task<string?>>? folderName = null)
    {
        _inner = inner;
        _center = center;
        _folderName = folderName;
    }

    /// <summary>Der eigentliche Dienst (Tests).</summary>
    public IFileOperationService Inner => _inner;

    public Task<FileOperationBatchResult> RecycleAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => TrackAsync(
            new ActivityStartInfo(ActivityKind.FileOperation, "In den Papierkorb", UserInitiated: true),
            fileIds.Count,
            ct,
            token => _inner.RecycleAsync(fileIds, token),
            r => Batch(r, $"{Files(r.Succeeded)} in den Papierkorb", undoable: false));

    public Task<FileOperationBatchResult> MoveAsync(IReadOnlyList<int> fileIds, string targetFolderPath, CancellationToken ct = default)
    {
        var target = FolderLabel(targetFolderPath);
        return TrackAsync(
            new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", $"nach {target}", UserInitiated: true),
            fileIds.Count,
            ct,
            token => _inner.MoveAsync(fileIds, targetFolderPath, token),
            r => Batch(r, $"{Files(r.Succeeded)} verschoben", undoable: true));
    }

    public Task<FileOperationBatchResult> RenameAsync(int fileId, string newName, CancellationToken ct = default)
        => TrackAsync(
            new ActivityStartInfo(ActivityKind.FileOperation, "Umbenennen", $"in „{newName}“", UserInitiated: true),
            0,
            ct,
            token => _inner.RenameAsync(fileId, newName, token),
            r => Batch(r, r.Succeeded > 0 ? "Datei umbenannt" : "Umbenennen fehlgeschlagen", undoable: true));

    /// <summary>Sofort fertig — kein Eintrag (eine wartende Scan-Sperre zeigt sich trotzdem unter „Wartet“).</summary>
    public Task<FolderOperationResult> CreateFolderAsync(int libraryId, string parentFolderPath, string name, CancellationToken ct = default)
        => _inner.CreateFolderAsync(libraryId, parentFolderPath, name, ct);

    public async Task<FolderOperationResult> RenameFolderAsync(int folderId, string newName, CancellationToken ct = default)
    {
        var name = await FolderNameAsync(folderId, ct).ConfigureAwait(false);
        return await TrackAsync(
            new ActivityStartInfo(ActivityKind.FileOperation, name is null ? "Ordner umbenennen" : $"Ordner „{name}“ umbenennen", $"in „{newName}“", UserInitiated: true),
            0,
            ct,
            token => _inner.RenameFolderAsync(folderId, newName, token),
            r => FolderOp(r, $"Ordner in „{newName}“ umbenannt")).ConfigureAwait(false);
    }

    public Task<FolderAdoptionExecution> AdoptFolderAsync(
        int libraryId,
        FolderAdoptionPlan plan,
        bool recycleSource,
        bool keepSourceFolder = false,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var total = plan.FileCount;
        return TrackAsync(
            new ActivityStartInfo(ActivityKind.Adoption, "In die Bibliothek übernehmen",
                $"aus {FolderLabel(plan.SourceFolder)} → {FolderLabel(plan.TargetFolder)}", UserInitiated: true),
            total,
            ct,
            token =>
            {
                // Fortschritt der Übernahme an Aufrufer UND Anzeige (der Batch meldet je Datei über IProgress<int>)
                var handle = ActivityScope.Current;
                var both = new ForwardProgress(progress, done => handle?.Report(done, total));
                return _inner.AdoptFolderAsync(libraryId, plan, recycleSource, keepSourceFolder, both, token);
            },
            r =>
            {
                var text = $"{Files(r.Moved)} übernommen";
                var problems = r.Failed > 0 || r.RemainingFiles.Count > 0 || r.RecycleFailed;
                var details = problems
                    ? string.Join(Environment.NewLine, new[]
                    {
                        r.Failed > 0 ? $"{Files(r.Failed)} nicht übernommen" : null,
                        r.RemainingFiles.Count > 0 ? $"{Files(r.RemainingFiles.Count)} blieben im Eingang" : null,
                        r.RecycleNote,
                    }.Where(s => !string.IsNullOrEmpty(s)))
                    : null;
                return Completion(r.BatchId, r.Moved > 0, text + (r.Failed > 0 ? $" · {r.Failed:N0} nicht" : string.Empty), problems, details);
            });
    }

    public async Task<FileOperationBatchResult?> UndoLastAsync(CancellationToken ct = default)
    {
        var result = await TrackAsync<FileOperationBatchResult?>(
            new ActivityStartInfo(ActivityKind.FileOperation, "Rückgängig", UserInitiated: true),
            0,
            ct,
            token => _inner.UndoLastAsync(token),
            UndoCompletion).ConfigureAwait(false);
        NoteUndone(result);
        return result;
    }

    public async Task<FileOperationBatchResult?> UndoBatchAsync(int batchId, CancellationToken ct = default)
    {
        var result = await TrackAsync<FileOperationBatchResult?>(
            new ActivityStartInfo(ActivityKind.FileOperation, "Rückgängig", UserInitiated: true),
            0,
            ct,
            token => _inner.UndoBatchAsync(batchId, token),
            UndoCompletion).ConfigureAwait(false);
        NoteUndone(result);
        return result;
    }

    public Task<IReadOnlyList<OperationBatchInfo>> GetRecentBatchesAsync(int take = 50, CancellationToken ct = default)
        => _inner.GetRecentBatchesAsync(take, ct);

    /// <summary>Zurückholen aus dem Papierkorb (Seite „Doppelte“); ohne eigenes „Rückgängig“.</summary>
    public Task<FileOperationBatchResult?> RestoreRecycledAsync(int batchId, RecycleRestoreCatalog? restoreCatalog = null, CancellationToken ct = default)
        => TrackAsync<FileOperationBatchResult?>(
            new ActivityStartInfo(ActivityKind.FileOperation, "Aus dem Papierkorb zurückholen", UserInitiated: true),
            0,
            ct,
            token => _inner.RestoreRecycledAsync(batchId, restoreCatalog, token),
            r => r is null
                ? new ActivityCompletion("Nichts zurückzuholen", ActivityState.Warning)
                : Batch(r, $"{Files(r.Succeeded)} zurückgeholt", undoable: false));

    public async Task<FolderRecycleResult> RecycleFolderAsync(int folderId, bool allowPermanentDelete = false, CancellationToken ct = default)
    {
        var name = await FolderNameAsync(folderId, ct).ConfigureAwait(false);
        var label = name is null ? "Ordner" : $"Ordner „{name}“";
        return await TrackAsync(
            new ActivityStartInfo(ActivityKind.FileOperation, $"In den Papierkorb: {label}", UserInitiated: true),
            0,
            ct,
            token => _inner.RecycleFolderAsync(folderId, allowPermanentDelete, token),
            r =>
            {
                if (r.NeedsPermanentConsent)
                {
                    return new ActivityCompletion("Kein Papierkorb auf dem Laufwerk — nichts geändert", ActivityState.Warning, [ActivityAction.Details], r.NoRecycleBinReason);
                }

                if (!r.Success && r.BatchId == 0)
                {
                    return new ActivityCompletion(r.Error ?? "Nicht möglich", ActivityState.Failed, r.Error is null ? null : [ActivityAction.Details], r.Error);
                }

                var text = r.Permanent ? $"{label} endgültig gelöscht ({Files(r.FilesRemoved)})" : $"{label} im Papierkorb ({Files(r.FilesRemoved)})";
                var problems = !r.Success || r.FilesFailed > 0;
                return Completion(r.BatchId, r.CanUndoPartially, text, problems, problems ? r.Error ?? $"{Files(r.FilesFailed)} blieben (z. B. gesperrt)" : null);
            }).ConfigureAwait(false);
    }

    public Task<MissingRemovalBatchResult> RemoveMissingAsync(IReadOnlyList<int> fileIds, int? pruneUnderFolderId = null, CancellationToken ct = default)
        => TrackAsync(
            new ActivityStartInfo(ActivityKind.FileOperation, $"Fehlende entfernen: {Files(fileIds.Count)}", UserInitiated: true),
            0,
            ct,
            token => _inner.RemoveMissingAsync(fileIds, pruneUnderFolderId, token),
            r =>
            {
                var skipped = r.Reappeared + r.SkippedOffline + r.SkippedNotMissing + r.Failed;
                var details = skipped > 0
                    ? string.Join(Environment.NewLine, new[]
                    {
                        r.Reappeared > 0 ? $"{Files(r.Reappeared)} wieder da (nicht entfernt)" : null,
                        r.SkippedOffline > 0 ? $"{Files(r.SkippedOffline)} auf nicht erreichbaren Laufwerken (gelten nicht als fehlend)" : null,
                        r.SkippedNotMissing > 0 ? $"{Files(r.SkippedNotMissing)} nicht als fehlend markiert" : null,
                        r.Failed > 0 ? $"{Files(r.Failed)} nicht gebucht" : null,
                    }.Where(s => s is not null))
                    : null;
                return Completion(r.BatchId, r.CanUndo, $"{Files(r.Removed)} aus der Bibliothek entfernt", r.Failed > 0 || r.SkippedOffline > 0, details);
            });

    public Task<ExclusionRemovalResult> RemoveExcludedAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => TrackAsync(
            new ActivityStartInfo(ActivityKind.FileOperation, $"Ausgeschlossene entfernen: {Files(fileIds.Count)}", UserInitiated: true),
            0,
            ct,
            token => _inner.RemoveExcludedAsync(fileIds, token),
            r => Completion(r.BatchId, r.CanUndo, $"{Files(r.Removed)} aus dem Katalog genommen", r.Failed > 0,
                r.Failed > 0 ? $"{Files(r.Failed)} nicht gebucht" : null));

    public async Task<FolderOperationResult> MoveFolderAsync(int folderId, string targetFolderPath, CancellationToken ct = default)
    {
        var name = await FolderNameAsync(folderId, ct).ConfigureAwait(false);
        return await TrackAsync(
            new ActivityStartInfo(ActivityKind.FileOperation, name is null ? "Ordner verschieben" : $"Ordner „{name}“ verschieben", $"nach {FolderLabel(targetFolderPath)}", UserInitiated: true),
            0,
            ct,
            token => _inner.MoveFolderAsync(folderId, targetFolderPath, token),
            r => FolderOp(r, "Ordner verschoben")).ConfigureAwait(false);
    }


    // ------------------------------------------------------------------ Rahmen

    /// <summary>
    /// Vorgang starten (mit verknüpftem Abbruch), den Aufruf im <see cref="ActivityScope"/> ausführen, Ergebnis eintragen.
    /// <paramref name="total"/> &gt; 0: der Dienst meldet Datei-Fortschritt über <see cref="ActivityScope.ReportItems"/>.
    /// </summary>
    private async Task<T> TrackAsync<T>(ActivityStartInfo info, int total, CancellationToken ct, Func<CancellationToken, Task<T>> run, Func<T, ActivityCompletion> complete)
    {
        if (ActivityScope.IsOwned)
        {
            return await run(ct).ConfigureAwait(false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // CancelAsync: der Klick kommt vom UI-Thread — Fortsetzungen des Batches sollen dort nicht inline laufen (wie ScanGate.CancelWait)
        var handle = _center.Start(info with { Cancel = () => _ = linked.CancelAsync() });
        if (total > 0)
        {
            handle.Report(0, total);
        }

        try
        {
            T result;
            using (ActivityScope.Enter(handle, acceptsItemProgress: total > 0))
            {
                result = await run(linked.Token).ConfigureAwait(false);
            }

            var completion = complete(result);
            handle.Complete(completion);
            if (result is { } r && BatchIdOf(r) is int batchId && batchId > 0 && completion.Actions?.Any(a => a.Kind == ActivityActionKind.Undo) == true)
            {
                Remember(batchId, handle.Id);
            }

            return result;
        }
        catch (ScanGateWaitCanceledException ex)
        {
            handle.Canceled(ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            handle.Canceled(total > 0 ? "Abgebrochen — bereits erledigte bleiben (Rückgängig über die Galerie)" : "Abgebrochen");
            throw;
        }
        catch (Exception ex)
        {
            handle.Fail($"Fehlgeschlagen: {ex.Message}");
            throw;
        }
    }

    private static int? BatchIdOf(object result) => result switch
    {
        FileOperationBatchResult b => b.BatchId,
        FolderOperationResult f => f.BatchId,
        FolderAdoptionExecution a => a.BatchId,
        FolderRecycleResult fr => fr.BatchId,
        MissingRemovalBatchResult m => m.BatchId,
        ExclusionRemovalResult e => e.BatchId,
        _ => null,
    };

    private void Remember(int batchId, long entryId)
    {
        _entryByBatch[batchId] = entryId;
        if (_entryByBatch.Count > UndoMapLimit)
        {
            foreach (var old in _entryByBatch.Keys.OrderBy(k => k).Take(_entryByBatch.Count - UndoMapLimit))
            {
                _entryByBatch.TryRemove(old, out _);
            }
        }
    }

    /// <summary>Rückgängig auf beliebigem Weg: der ursprüngliche Eintrag verliert „Rückgängig“.</summary>
    private void NoteUndone(FileOperationBatchResult? result)
    {
        if (result is { Succeeded: > 0 } && _entryByBatch.TryRemove(result.BatchId, out var entryId))
        {
            _center.MarkUndone(entryId, "rückgängig gemacht");
        }
    }

    private ActivityCompletion Batch(FileOperationBatchResult r, string text, bool undoable)
    {
        var failed = r.Items.Where(i => !i.Success).ToList();
        var details = failed.Count > 0
            ? string.Join(Environment.NewLine, failed.GroupBy(i => i.Error ?? "unbekannter Fehler").Select(g => $"{Files(g.Count())}: {g.Key}"))
            : null;
        if (r.Succeeded == 0 && failed.Count > 0)
        {
            return new ActivityCompletion($"Nichts erledigt: {Files(failed.Count)} übersprungen", ActivityState.Failed, [ActivityAction.Details], details);
        }

        var line = failed.Count > 0 ? $"{text} · {Files(failed.Count)} übersprungen" : text;
        return Completion(r.BatchId, undoable && r.Succeeded > 0, line, failed.Count > 0, details);
    }

    private ActivityCompletion FolderOp(FolderOperationResult r, string text)
        => r.Success
            ? Completion(r.BatchId, r.BatchId > 0, text, false, null)
            : new ActivityCompletion(r.Error ?? "Nicht möglich", ActivityState.Failed, r.Error is null ? null : [ActivityAction.Details], r.Error);

    /// <summary>Ergebnis mit „Rückgängig“ (über <see cref="IFileOperationService.UndoBatchAsync"/>) und ggf. „Details“.</summary>
    private ActivityCompletion Completion(int batchId, bool canUndo, string text, bool warning, string? details)
    {
        var actions = new List<ActivityAction>(2);
        if (canUndo && batchId > 0)
        {
            actions.Add(ActivityAction.Undo(ct => UndoTextAsync(batchId, ct)));
        }

        if (!string.IsNullOrEmpty(details))
        {
            actions.Add(ActivityAction.Details);
        }

        return new ActivityCompletion(text, warning ? ActivityState.Warning : ActivityState.Succeeded, actions, details);
    }

    /// <summary>
    /// „Rückgängig“ aus der Anzeige: derselbe Weg wie überall (<see cref="IFileOperationService.UndoBatchAsync"/> des Dienstes),
    /// aber ohne eigenen Eintrag — der ursprüngliche Eintrag bekommt „rückgängig gemacht“, die Oberfläche meldet die Zeile.
    /// Scheitert es, bleibt „Rückgängig“ am Eintrag stehen.
    /// </summary>
    private async Task<string> UndoTextAsync(int batchId, CancellationToken ct)
    {
        var result = await _inner.UndoBatchAsync(batchId, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Der Vorgang ist nicht mehr im Protokoll.");
        if (result.Succeeded == 0)
        {
            throw new InvalidOperationException(result.Items.FirstOrDefault()?.Error ?? "Nichts zurückgenommen.");
        }

        _entryByBatch.TryRemove(batchId, out _);
        return UndoLine(result);
    }

    private static ActivityCompletion UndoCompletion(FileOperationBatchResult? r)
    {
        if (r is null)
        {
            return new ActivityCompletion("Nichts rückgängig zu machen", ActivityState.Warning);
        }

        if (r.Succeeded == 0)
        {
            var reason = r.Items.FirstOrDefault()?.Error;
            return new ActivityCompletion("Rückgängig nicht möglich", ActivityState.Failed, reason is null ? null : [ActivityAction.Details], reason);
        }

        var failed = r.Items.Where(i => !i.Success).ToList();
        return failed.Count > 0
            ? new ActivityCompletion($"{UndoLine(r)} · {Files(failed.Count)} nicht", ActivityState.Warning, [ActivityAction.Details],
                string.Join(Environment.NewLine, failed.GroupBy(i => i.Error ?? "unbekannter Fehler").Select(g => $"{Files(g.Count())}: {g.Key}")))
            : new ActivityCompletion(UndoLine(r));
    }

    private static string UndoLine(FileOperationBatchResult r) => r.Type switch
    {
        OperationType.Move or OperationType.MoveFolder or OperationType.AdoptFolder => $"Rückgängig: {Files(r.Succeeded)} zurückverschoben",
        OperationType.Rename or OperationType.RenameFolder => "Rückgängig: Name zurückgesetzt",
        OperationType.RecycleFolder => $"Rückgängig: {Files(r.Succeeded)} aus dem Papierkorb zurück",
        OperationType.RemoveFromCatalog or OperationType.ExcludeFromCatalog => $"Rückgängig: {Files(r.Succeeded)} wieder in der Bibliothek",
        _ => $"Rückgängig: {Files(r.Succeeded)}",
    };

    private async Task<string?> FolderNameAsync(int folderId, CancellationToken ct)
    {
        if (_folderName is null || ActivityScope.IsOwned)
        {
            return null;
        }

        try
        {
            return await _folderName(folderId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // nur Text — der Vorgang selbst meldet echte Fehler
        }
    }

    /// <summary>„Urlaub 2024“ aus einem Ordnerpfad (nur der Name, Bibliotheks-Trennung/versteckte Sitzung).</summary>
    internal static string FolderLabel(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path ?? string.Empty);
        var name = Path.GetFileName(trimmed);
        return $"„{(string.IsNullOrEmpty(name) ? trimmed : name)}“";
    }

    internal static string Files(int count) => count == 1 ? "1 Datei" : $"{count:N0} Dateien";

    /// <summary>Leitet Fortschritt an zwei Empfänger weiter (Aufrufer + Anzeige).</summary>
    private sealed class ForwardProgress(IProgress<int>? first, Action<int> second) : IProgress<int>
    {
        public void Report(int value)
        {
            first?.Report(value);
            second(value);
        }
    }
}
