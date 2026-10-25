using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Exclusions;
using Lumora.Removal;
using Lumora.Services.Scanning.Exclusions;

namespace Lumora.Folders;

/// <summary>
/// Ausschlüsse beim Einlesen im Ordnerbaum (ADR-0066): ausgeschlossene Ordner (Name aus der allgemeinen Liste in jeder Tiefe bzw.
/// Ordner der Bibliothek) erscheinen nicht im Baum — dezent zählt „n Ordner ausgeschlossen“ unter dem Kopf. Rechtsklick › „Ordner
/// ausschließen…“ (nicht an der Wurzel, nicht in „Alle verknüpften“) läuft über <see cref="ScanExclusionActions"/> mit Rückfrage;
/// Rückgängig im Ergebnisfeld bringt die Einträge zurück und hebt den Ausschluss wieder auf.
/// </summary>
public sealed partial class FolderTreeViewModel
{
    private readonly ScanExclusionActions? _exclusionActions;

    /// <summary>„n Ordner ausgeschlossen“ (null, wenn keiner) — dezenter Hinweis unter dem Seitenkopf.</summary>
    [ObservableProperty]
    private string? _excludedFoldersText;

    /// <summary>Menüeintrag nur, wenn der Dienst angebunden ist (Tests ohne ihn).</summary>
    public bool HasExclusionActions => _exclusionActions is not null;

    /// <summary>
    /// Ausgeschlossene Ordner (und alles darunter) aus den geladenen Zeilen nehmen; Wurzeln bleiben immer. Zählt für den Hinweis
    /// die obersten ausgeschlossenen Ordner im Katalog plus ausgeschlossene Ordner der Bibliothek, die (noch) keine Zeile haben.
    /// </summary>
    private async Task<List<FolderRow>> HideExcludedFoldersAsync(List<FolderRow> rows, IReadOnlyList<int> libraryIds)
    {
        if (_exclusionActions is null)
        {
            ExcludedFoldersText = null;
            return rows;
        }

        try
        {
            var sets = new Dictionary<int, ScanExclusionSet>();
            foreach (var libraryId in libraryIds)
            {
                sets[libraryId] = await _exclusionActions.Service.Store.LoadSetAsync(libraryId).ConfigureAwait(true);
            }

            var hidden = rows.Where(r => sets.TryGetValue(r.LibraryId, out var set) && set.IsExcludedPath(r.Path)).Select(r => r.Id).ToHashSet();
            var top = rows.Count(r => hidden.Contains(r.Id) && (r.ParentId is not { } p || !hidden.Contains(p)));
            var catalogPaths = rows.Where(r => hidden.Contains(r.Id)).Select(r => r.Path.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var withoutRow = sets.Values.SelectMany(s => s.FolderPaths).Count(p => !catalogPaths.Contains(p));
            var count = top + withoutRow;
            ExcludedFoldersText = count switch
            {
                0 => null,
                1 => "1 Ordner ausgeschlossen",
                _ => $"{count:N0} Ordner ausgeschlossen",
            };
            return hidden.Count == 0 ? rows : rows.Where(r => !hidden.Contains(r.Id)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ordnerbaum: Ausschlüsse konnten nicht gelesen werden");
            ExcludedFoldersText = null;
            return rows;
        }
    }

    private bool CanExcludeFolder(FolderNode? node) => _exclusionActions is not null && CanRunFolderAction(node) && node is { CanExclude: true };

    [RelayCommand(CanExecute = nameof(CanExcludeFolder))]
    private async Task ExcludeFolderAsync(FolderNode? node)
    {
        if (node is not { CanExclude: true } || _exclusionActions is not { } actions)
        {
            return;
        }

        if (_loadedScope.IsAllLinked)
        {
            ShowRemovalResult(new RemovalActionResult($"Ordner ausschließen: {RemovalActions.AllLinkedHint}", true));
            return;
        }

        IsFolderActionRunning = true;
        ErrorMessage = null;
        try
        {
            var libraryId = _libraryOfFolder.GetValueOrDefault(node.Id, _libraryId);
            var result = await actions.ExcludeFolderAsync(libraryId, node.Path, node.Name).ConfigureAwait(true);
            if (result is not null)
            {
                ShowUndoableResult(result.ToRemovalResult(), result.UndoBatchId is null ? null : id => UndoExclusionAsync(actions, id, result.Undo));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner ausschließen fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Ordner ausschließen fehlgeschlagen (FolderId {FolderId})", node.Id);
        }
        finally
        {
            IsFolderActionRunning = false;
        }
    }

    private static async Task<RemovalActionResult> UndoExclusionAsync(ScanExclusionActions actions, int batchId, ExclusionUndo? undo)
        => (await actions.UndoAsync(batchId, undo).ConfigureAwait(true)).ToRemovalResult();

    /// <summary>Ergebnis zeigen und „Rückgängig“ an den passenden Ablauf binden (Ausschließen, Verschieben).</summary>
    private void ShowUndoableResult(RemovalActionResult result, Func<int, Task<RemovalActionResult>>? undo)
    {
        _undoHandler = result.UndoBatchId is null ? null : undo;
        RemovalUndoBatchId = result.UndoBatchId;
        ShowRemovalResult(result);
    }
}
