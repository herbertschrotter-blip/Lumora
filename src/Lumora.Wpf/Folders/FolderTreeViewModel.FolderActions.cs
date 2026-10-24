using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Lumora.Folders;

/// <summary>
/// Kontextmenü des Ordnerbaums (ADR-0034): „In die Bibliothek übernehmen…“ für Ordner unter einem Eingang und
/// „Bewertung / Favorit / Kategorie / Tags für alle Dateien“ (rekursiv). Die Arbeit macht <see cref="FolderActions"/>;
/// das Ergebnis erscheint als InfoBar über dem Baum (Hinweis bzw. Warnung bei Rest-Dateien).
/// </summary>
public sealed partial class FolderTreeViewModel
{
    /// <summary>Ergebnis der letzten Ordner-Aktion ohne Probleme.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    private string? _resultMessage;

    /// <summary>Ergebnis mit Problemen (Rest-Dateien, Fehler) — Hinweisfeld in Warnfarbe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText), nameof(ResultIsWarning))]
    private string? _resultWarning;

    /// <summary>Angezeigter Ergebnistext (Warnung vor Hinweis); null = Feld ausgeblendet.</summary>
    public string? ResultText => ResultWarning ?? ResultMessage;

    public bool ResultIsWarning => ResultWarning is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AdoptFolderCommand), nameof(EditFolderTagsCommand), nameof(EditFolderCategoriesCommand), nameof(SetFolderCaptureDateCommand))]
    private bool _isFolderActionRunning;

    [RelayCommand(CanExecute = nameof(CanRunFolderAction))]
    private Task AdoptFolderAsync(FolderNode? node)
        => node is { IsPlaceholder: false, IsInInbox: true }
            ? RunFolderActionAsync("Übernehmen", node, () => _folderActions.AdoptAsync(node.Path))
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRunFolderAction))]
    private Task EditFolderTagsAsync(FolderNode? node)
        => node is { IsPlaceholder: false }
            ? RunFolderActionAsync("Tags", node, () => _folderActions.EditTagsAsync(node.Id, node.Name))
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRunFolderAction))]
    private Task EditFolderCategoriesAsync(FolderNode? node)
        => node is { IsPlaceholder: false }
            ? RunFolderActionAsync("Kategorie", node, () => _folderActions.EditCategoriesAsync(node.Id, node.Name))
            : Task.CompletedTask;

    [RelayCommand]
    private void DismissResult()
    {
        ResultMessage = null;
        ResultWarning = null;
        RemovalUndoBatchId = null; // „Rückgängig“ verschwindet mit dem Ergebnis (ADR-0065)
    }

    /// <summary>„Bewertung für alle Dateien“ (vom Knoten-Command aufgerufen); null entfernt die Bewertung.</summary>
    internal Task RateFolderAsync(FolderNode node, byte? rating)
        => IsFolderActionRunning ? Task.CompletedTask : RunFolderActionAsync("Bewertung", node, () => _folderActions.RateAsync(node.Id, node.Name, rating));

    /// <summary>„Favorit für alle“ (vom Knoten-Command aufgerufen).</summary>
    internal Task FavoriteFolderAsync(FolderNode node, bool isFavorite)
        => IsFolderActionRunning ? Task.CompletedTask : RunFolderActionAsync("Favorit", node, () => _folderActions.FavoriteAsync(node.Id, node.Name, isFavorite));

    private bool CanRunFolderAction(FolderNode? node) => !IsFolderActionRunning && HasLibrary && node is not { IsLibraryHeader: true };

    private async Task RunFolderActionAsync(string what, FolderNode node, Func<Task<FolderActionResult?>> action)
    {
        IsFolderActionRunning = true;
        ErrorMessage = null;
        try
        {
            var result = await action().ConfigureAwait(true);
            if (result is null)
            {
                return;
            }

            RemovalUndoBatchId = null; // ein anderes Ergebnis ersetzt das Rückgängig-Angebot (ADR-0065)

            ResultMessage = result.IsWarning ? null : result.Message;
            ResultWarning = result.IsWarning ? result.Message : null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{what} fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Ordner-Aktion {What} für {Path} fehlgeschlagen", what, node.Path);
        }
        finally
        {
            IsFolderActionRunning = false;
        }
    }
}
