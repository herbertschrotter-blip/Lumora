using Lumora.Services.Activity;

namespace Lumora.Services.Sorter;

/// <summary>
/// Sortier-/Flatten-Lauf in der Aktivitäts-Anzeige (ADR-0067): ein Lauf ist EIN Vorgang (die vielen Datei-Batches darin legen
/// über <see cref="ActivityScope"/> keine eigenen Einträge an), Fortschritt aus <see cref="SorterProgress"/>, „Abbrechen“ über
/// ein verknüpftes CancellationToken, „Rückgängig“ über <see cref="UndoLastRunAsync"/> (derselbe Weg wie die Taste
/// „Rückgängig (letzter Lauf)“). Ohne Zentrale (Tests) unverändert.
/// </summary>
public sealed partial class SorterMoveExecutor
{
    private readonly IActivityCenter? _activities;

    /// <summary>Eintrag des letzten Laufs je Lauf-Schlüssel: nur der letzte Lauf ist rückgängig machbar (SorterRunLog).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastRunEntry = new();

    public async Task<SorterExecutionResult> ExecuteAsync(
        SorterPlan plan,
        string runKey,
        IProgress<SorterProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_activities is null || ActivityScope.IsOwned)
        {
            return await ExecuteCoreAsync(plan, runKey, progress, ct).ConfigureAwait(false);
        }

        var title = plan.Kind == SorterPlan.KindFlatten ? "Flatten" : "Sortieren";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var handle = _activities.Start(new ActivityStartInfo(ActivityKind.Sorter, title, UserInitiated: true, Cancel: () => _ = linked.CancelAsync()));
        var forward = new ForwardProgress(progress, p => handle.Report(p.Completed, p.Total));
        try
        {
            SorterExecutionResult result;
            using (ActivityScope.Enter(handle))
            {
                result = await ExecuteCoreAsync(plan, runKey, forward, linked.Token).ConfigureAwait(false);
            }

            var text = $"{Files(result.Moved)} verschoben";
            if (result.Failed > 0)
            {
                text += $" · {result.Failed:N0} fehlgeschlagen";
            }

            var actions = new List<ActivityAction>();
            if (result.BatchIds.Count > 0 && result.Moved > 0)
            {
                actions.Add(ActivityAction.Undo(async token =>
                {
                    var undo = await UndoLastRunCoreAsync(runKey, null, token).ConfigureAwait(false)
                               ?? throw new InvalidOperationException("Der Lauf ist schon zurückgenommen.");
                    return $"Rückgängig: {Files(undo.Restored)} zurückverschoben";
                }));
            }

            var failedDetails = result.Details.Where(d => !d.Success).Take(20).Select(d => $"{d.FileName}: {d.Message}").ToList();
            if (failedDetails.Count > 0)
            {
                actions.Add(ActivityAction.Details);
            }

            handle.Complete(new ActivityCompletion(text, result.Failed > 0 ? ActivityState.Warning : ActivityState.Succeeded, actions,
                failedDetails.Count > 0 ? string.Join(Environment.NewLine, failedDetails) : null));
            if (result.BatchIds.Count > 0)
            {
                // Ein neuer Lauf ersetzt den letzten im Lauf-Protokoll — der ältere Eintrag verliert „Rückgängig“
                if (_lastRunEntry.TryGetValue(runKey, out var previous))
                {
                    _activities.MarkUndone(previous, "nicht mehr rückgängig (neuerer Lauf)");
                }

                _lastRunEntry[runKey] = handle.Id;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            handle.Canceled("Abgebrochen — bereits verschobene bleiben (Rückgängig über „Rückgängig (letzter Lauf)“)");
            throw;
        }
        catch (Exception ex)
        {
            handle.Fail($"Fehlgeschlagen: {ex.Message}");
            throw;
        }
    }

    public async Task<SorterUndoResult?> UndoLastRunAsync(string runKey, IProgress<SorterProgress>? progress, CancellationToken ct)
    {
        if (_activities is null || ActivityScope.IsOwned)
        {
            return await UndoLastRunCoreAsync(runKey, progress, ct).ConfigureAwait(false);
        }

        var handle = _activities.Start(new ActivityStartInfo(ActivityKind.Sorter, "Rückgängig (Sortier-Lauf)", UserInitiated: true));
        var forward = new ForwardProgress(progress, p => handle.Report(p.Completed, p.Total));
        try
        {
            SorterUndoResult? result;
            using (ActivityScope.Enter(handle))
            {
                result = await UndoLastRunCoreAsync(runKey, forward, ct).ConfigureAwait(false);
            }

            if (result is not null && _lastRunEntry.TryRemove(runKey, out var runEntry))
            {
                _activities.MarkUndone(runEntry, "rückgängig gemacht"); // Rückgängig über die Taste der Seite
            }

            handle.Complete(result is null
                ? new ActivityCompletion("Nichts rückgängig zu machen", ActivityState.Warning)
                : new ActivityCompletion($"Rückgängig: {Files(result.Restored)} zurückverschoben" + (result.Failed > 0 ? $" · {result.Failed:N0} nicht" : string.Empty),
                    result.Failed > 0 ? ActivityState.Warning : ActivityState.Succeeded));
            return result;
        }
        catch (OperationCanceledException)
        {
            handle.Canceled();
            throw;
        }
        catch (Exception ex)
        {
            handle.Fail($"Fehlgeschlagen: {ex.Message}");
            throw;
        }
    }

    private static string Files(int count) => count == 1 ? "1 Datei" : $"{count:N0} Dateien";

    private sealed class ForwardProgress(IProgress<SorterProgress>? first, Action<SorterProgress> second) : IProgress<SorterProgress>
    {
        public void Report(SorterProgress value)
        {
            first?.Report(value);
            second(value);
        }
    }
}
