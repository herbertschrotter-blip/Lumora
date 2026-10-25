using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Removal;
using Lumora.Services.Removal;

namespace Lumora.Folders;

/// <summary>
/// Kontextmenü des Ordnerbaums (ADR-0065): „In den Papierkorb…“ (Ordner samt Inhalt, Rückgängig im Ergebnisfeld) und
/// „Fehlende entfernen (n)“ für den Ordner samt Unterordnern (nur bei n &gt; 0; leere, verschwundene Ordner gehen mit aus dem
/// Baum). Die Abläufe mit Bestätigung macht <see cref="RemovalActions"/>; in „Alle verknüpften“ (ADR-0048) gesperrt wie die
/// anderen Datei-Aktionen. Zähler „fehlt“ je Ordner (samt Unterordnern) kommen beim Laden des Baums mit.
/// </summary>
public sealed partial class FolderTreeViewModel
{
    private readonly RemovalActions? _removalActions;
    private Dictionary<int, int> _directMissing = new();
    private Dictionary<int, int> _totalMissing = new();

    /// <summary>Wer „Rückgängig“ im Ergebnisfeld ausführt; null = <see cref="RemovalActions.UndoAsync"/> (ADR-0066: auch Verschieben/Ausschließen).</summary>
    private Func<int, Task<RemovalActionResult>>? _undoHandler;

    /// <summary>Batch der letzten Lösch-/Entfernen-Aktion, die sich noch zurücknehmen lässt (Knopf „Rückgängig“ im Ergebnisfeld).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoRemoval))]
    [NotifyCanExecuteChangedFor(nameof(UndoRemovalCommand))]
    private int? _removalUndoBatchId;

    public bool CanUndoRemoval => RemovalUndoBatchId is not null && !IsFolderActionRunning;

    /// <summary>Menüeinträge nur, wenn der Dienst angebunden ist (Tests ohne ihn).</summary>
    public bool HasRemovalActions => _removalActions is not null;

    partial void OnIsFolderActionRunningChanged(bool value)
    {
        RecycleFolderCommand.NotifyCanExecuteChanged();
        RemoveMissingInFolderCommand.NotifyCanExecuteChanged();
        UndoRemovalCommand.NotifyCanExecuteChanged();
        ExcludeFolderCommand.NotifyCanExecuteChanged(); // ADR-0066
        MoveFolderCommand.NotifyCanExecuteChanged(); // ADR-0066
        OnPropertyChanged(nameof(CanUndoRemoval));
    }

    /// <summary>Fehlende je Ordner samt Unterordnern aus den direkten Zählern (nach <see cref="BuildIndex"/>).</summary>
    private void BuildMissingIndex(Dictionary<int, int> direct)
    {
        _directMissing = direct;
        _totalMissing = new Dictionary<int, int>();
        foreach (var root in _rootRows)
        {
            Total(root.Id);
        }

        int Total(int folderId)
        {
            var total = _directMissing.GetValueOrDefault(folderId);
            if (_childrenByParent.TryGetValue(folderId, out var children))
            {
                foreach (var child in children)
                {
                    total += Total(child.Id);
                }
            }

            _totalMissing[folderId] = total;
            return total;
        }
    }

    private bool CanRunRemoval(FolderNode? node) => _removalActions is not null && CanRunFolderAction(node);

    [RelayCommand(CanExecute = nameof(CanRunRemoval))]
    private Task RecycleFolderAsync(FolderNode? node)
        => node is { IsFolder: true } && _removalActions is { } actions
            ? RunRemovalAsync("Papierkorb", node, () => actions.RecycleFolderAsync(node.Id))
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRunRemoval))]
    private Task RemoveMissingInFolderAsync(FolderNode? node)
        => node is { HasMissing: true } && _removalActions is { } actions
            ? RunRemovalAsync("Fehlende entfernen", node, () => actions.RemoveMissingAsync(new MissingTarget.InFolder(node.Id), $"in „{node.Name}“ (mit Unterordnern)"))
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanUndoRemoval))]
    private async Task UndoRemovalAsync()
    {
        var handler = _undoHandler ?? (_removalActions is { } removal ? removal.UndoAsync : null);
        if (RemovalUndoBatchId is not { } batchId || handler is null)
        {
            return;
        }

        IsFolderActionRunning = true;
        try
        {
            var result = await handler(batchId).ConfigureAwait(true);
            _undoHandler = null;
            RemovalUndoBatchId = null;
            ShowRemovalResult(result);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Rückgängig fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Ordnerbaum: Rückgängig von Batch {BatchId} fehlgeschlagen", batchId);
        }
        finally
        {
            IsFolderActionRunning = false;
        }
    }

    private async Task RunRemovalAsync(string what, FolderNode node, Func<Task<RemovalActionResult?>> action)
    {
        if (_loadedScope.IsAllLinked)
        {
            ShowRemovalResult(new RemovalActionResult($"{what}: {RemovalActions.AllLinkedHint}", true));
            return;
        }

        IsFolderActionRunning = true;
        ErrorMessage = null;
        try
        {
            var result = await action().ConfigureAwait(true);
            if (result is not null)
            {
                _undoHandler = null;
                RemovalUndoBatchId = result.UndoBatchId;
                ShowRemovalResult(result);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{what} fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Ordner-Aktion {What} fehlgeschlagen (FolderId {FolderId})", what, node.Id);
        }
        finally
        {
            IsFolderActionRunning = false;
        }
    }

    private void ShowRemovalResult(RemovalActionResult result)
    {
        ResultMessage = result.IsWarning ? null : result.Message;
        ResultWarning = result.IsWarning ? result.Message : null;
    }
}
