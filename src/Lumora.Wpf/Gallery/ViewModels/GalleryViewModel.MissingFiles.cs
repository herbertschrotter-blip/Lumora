using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Data;
using Lumora.Removal;
using Lumora.Services.Removal;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Galerie: fehlende Dateien aus der Bibliothek entfernen (ADR-0065). Filter „Ablage: Nur fehlende“ (<see cref="GalleryLocation.Missing"/>)
/// zeigt sie; „Aus der Bibliothek entfernen“ in der Aktionsleiste gilt für die Auswahl — nur wenn alle gewählten fehlen
/// (gemischte Auswahl → Hinweis), mit Bestätigung samt Zahlen; in „Alle verknüpften“ gesperrt wie die anderen Datei-Aktionen.
/// „Rückgängig“ läuft über die gemeinsame Taste (der RemoveFromCatalog-Batch ist rückgängig machbar).
/// </summary>
public sealed partial class GalleryViewModel
{
    private readonly RemovalActions? _removalActions;

    /// <summary>Fehlende Dateien in der Auswahl (auf der aktuellen Seite).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemoveMissingAction))]
    [NotifyCanExecuteChangedFor(nameof(RemoveMissingSelectionCommand))]
    private int _selectedMissingCount;

    /// <summary>Knopf „Aus der Bibliothek entfernen“ sichtbar: Filter „Nur fehlende“ oder fehlende in der Auswahl.</summary>
    public bool ShowRemoveMissingAction => _removalActions is not null && (LocationFilter == GalleryLocation.Missing || SelectedMissingCount > 0);

    private bool CanRemoveMissingSelection() => _removalActions is not null && CanActOnSelection;

    partial void OnSelectedCountChanged(int value) => RemoveMissingSelectionCommand.NotifyCanExecuteChanged();

    partial void OnIsOperationRunningChanged(bool value) => RemoveMissingSelectionCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanRemoveMissingSelection))]
    private async Task RemoveMissingSelectionAsync()
    {
        const string what = "Aus der Bibliothek entfernen";
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0 || _removalActions is null || await RejectInAllLinkedViewAsync(what))
        {
            return;
        }

        await RunOperationAsync(what, async () =>
        {
            var result = await _removalActions.RemoveMissingAsync(new MissingTarget.Selection(ids), "in der Auswahl");
            if (result is null)
            {
                return; // abgebrochen
            }

            SetResultStatus(result.Message);
            if (result.IsWarning && result.UndoBatchId is null)
            {
                await _dialogs.ShowErrorsAsync(what, result.Message, []);
            }

            _logger.LogInformation("Galerie: Aus der Bibliothek entfernen — {Count} gewählt, Batch {BatchId}", ids.Count, result.UndoBatchId);
            ScheduleReload();
        });
    }
}
