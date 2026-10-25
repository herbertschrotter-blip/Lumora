using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Removal;

namespace Lumora.Folders;

/// <summary>
/// „Ordner verschieben“ im Ordnerbaum (ADR-0066): Rechtsklick › „Verschieben nach…“ (Windows-Ordnerdialog, beginnt in der Wurzel)
/// und Ziehen eines Ordners auf einen anderen (Bestätigung, Drop-Markierung am Ziel, Esc bricht ab — <see cref="FolderTreePage"/>).
/// Nicht an Wurzeln, nicht in „Alle verknüpften“. Die Vorprüfung beim Ziehen ist nur eine schnelle Lage-Prüfung im Speicher; die
/// verbindlichen Regeln prüft der Dienst und meldet einen Grund. Rückgängig im Ergebnisfeld bzw. über die Galerie-Taste.
/// </summary>
public sealed partial class FolderTreeViewModel
{
    private readonly FolderMoveActions? _moveActions;

    /// <summary>Menüeintrag und Ziehen nur, wenn der Dienst angebunden ist (Tests ohne ihn).</summary>
    public bool HasMoveActions => _moveActions is not null;

    private bool CanMoveFolder(FolderNode? node) => _moveActions is not null && CanRunFolderAction(node) && node is { CanMove: true };

    /// <summary>„Verschieben nach…“: Ziel wählen, dann verschieben (ohne weitere Rückfrage — die Auswahl ist die Bestätigung).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveFolder))]
    private async Task MoveFolderAsync(FolderNode? node)
    {
        if (node is not { CanMove: true } || _moveActions is null)
        {
            return;
        }

        if (_loadedScope.IsAllLinked)
        {
            ShowRemovalResult(new RemovalActionResult($"Verschieben: {RemovalActions.AllLinkedHint}", true));
            return;
        }

        var target = _moveActions.PickTarget(node.Name, RootPathOf(node) ?? Path.GetDirectoryName(node.Path));
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await MoveFolderToAsync(node, target, confirm: false).ConfigureAwait(true);
    }

    /// <summary>Schnelle Vorprüfung beim Ziehen: Ordner (keine Wurzel) auf einen anderen Ordner derselben Bibliothek, nicht in sich selbst, nicht dorthin, wo er liegt.</summary>
    public bool CanDropFolder(FolderNode? source, FolderNode? target)
    {
        if (_moveActions is null || IsFolderActionRunning || _loadedScope.IsAllLinked
            || source is not { CanMove: true } || target is not { IsFolder: true } || ReferenceEquals(source, target))
        {
            return false;
        }

        if (_libraryOfFolder.GetValueOrDefault(source.Id, -1) != _libraryOfFolder.GetValueOrDefault(target.Id, -2))
        {
            return false;
        }

        var sourcePath = source.Path.TrimEnd('\\', '/');
        var targetPath = target.Path.TrimEnd('\\', '/');
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)
            || targetPath.StartsWith(sourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false; // in sich selbst
        }

        var parent = Path.GetDirectoryName(sourcePath);
        return !string.Equals(parent?.TrimEnd('\\', '/'), targetPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ablegen eines gezogenen Ordners: mit Rückfrage verschieben.</summary>
    public Task DropFolderAsync(FolderNode source, FolderNode target)
        => CanDropFolder(source, target) ? MoveFolderToAsync(source, target.Path, confirm: true) : Task.CompletedTask;

    internal async Task MoveFolderToAsync(FolderNode node, string targetPath, bool confirm)
    {
        if (_moveActions is not { } actions)
        {
            return;
        }

        IsFolderActionRunning = true;
        ErrorMessage = null;
        try
        {
            var result = await actions.MoveAsync(node.Id, node.Name, targetPath, confirm).ConfigureAwait(true);
            if (result is not null)
            {
                ShowUndoableResult(result, actions.UndoAsync);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Verschieben fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Ordner verschieben fehlgeschlagen (FolderId {FolderId})", node.Id);
        }
        finally
        {
            IsFolderActionRunning = false;
        }
    }

    /// <summary>Wurzel des Ordners (Startort der Zielauswahl).</summary>
    private string? RootPathOf(FolderNode node)
        => _rootsById.Values
            .Where(r => IsSameOrBelow(node.Path, r.Path))
            .OrderByDescending(r => r.Path.Length)
            .Select(r => r.Path)
            .FirstOrDefault();

    private static bool IsSameOrBelow(string path, string root)
    {
        var p = path.TrimEnd('\\', '/');
        var r = root.TrimEnd('\\', '/');
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
