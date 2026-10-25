using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace Lumora.Persons.ViewModels;

/// <summary>
/// Titelbild der Person (ADR-0068, Mockup 17): Umschalter „Automatisch / Selbst gewählt“ in der Detailansicht und der Klick auf
/// den Vorschläge-Chip einer Personen-Kachel, der die Person direkt im Reiter „Vorschläge“ öffnet. Automatisch = bestes Gesicht
/// (Gesichts- und Foto-Güte); die eigene Wahl gewinnt immer und wird nie automatisch ersetzt.
/// </summary>
public sealed partial class PersonsViewModel
{
    /// <summary>Titelbild wird automatisch gewählt (keine eigene Wahl).</summary>
    public bool IsCoverAutomatic => CurrentPerson is { CoverIsManual: false };

    /// <summary>Titelbild ist selbst gewählt.</summary>
    public bool IsCoverManual => CurrentPerson is { CoverIsManual: true };

    /// <summary>„automatisch gewählt: bestes von 231 Gesichtern“ bzw. „selbst gewählt“.</summary>
    public string CoverModeText => CurrentPerson switch
    {
        null => string.Empty,
        { CoverIsManual: true } => "selbst gewählt — Rechtsklick auf ein Gesicht › „Als Titelbild für“ ändert es",
        { FaceCount: 0 } => "noch kein bestätigtes Gesicht",
        var p => $"automatisch gewählt: bestes von {N(p.FaceCount)} Gesichtern",
    };

    partial void OnCurrentPersonChanged(Lumora.Services.Faces.Assignment.PersonSummary? value)
    {
        OnPropertyChanged(nameof(IsCoverAutomatic));
        OnPropertyChanged(nameof(IsCoverManual));
        OnPropertyChanged(nameof(CoverModeText));
        SetCoverAutomaticCommand.NotifyCanExecuteChanged();
        SetCoverManualCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Umschalter „Automatisch“: eigenes Titelbild lösen.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnPerson))]
    private Task SetCoverAutomaticAsync()
    {
        if (CurrentPerson is not { CoverIsManual: true } person)
        {
            OnPropertyChanged(nameof(IsCoverAutomatic)); // Umschalter bleibt, wie er ist
            return Task.CompletedTask;
        }

        return RunAsync("Titelbild zurücksetzen", async () =>
        {
            await _service.ClearCoverAsync(person.Id).ConfigureAwait(true);
            ShowInfo("Titelbild wird wieder automatisch gewählt (bestes Gesicht).");
        });
    }

    /// <summary>
    /// Umschalter „Selbst gewählt“: mit genau einem gewählten bestätigten Gesicht wird es Titelbild, sonst Hinweis und Wechsel auf
    /// „Bestätigt“.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActOnPerson))]
    private Task SetCoverManualAsync()
    {
        if (CurrentPerson is null)
        {
            return Task.CompletedTask;
        }

        if (SelectedConfirmedCount == 1 && Confirmed.Any(t => t.IsSelected))
        {
            return SetCoverCommand.ExecuteAsync(null);
        }

        DetailTab = 1;
        OnPropertyChanged(nameof(IsCoverManual));
        OnPropertyChanged(nameof(IsCoverAutomatic));
        ShowInfo("Wähle unter „Bestätigt“ genau ein Gesicht und klicke „Als Titelbild“ — oder Rechtsklick auf ein Gesicht bzw. Foto › „Als Titelbild für“.");
        return Task.CompletedTask;
    }

    /// <summary>Meldung der Seite setzen (Rechtsklick-Menü auf Gesichtern, ADR-0068).</summary>
    public void ShowStatusMessage(string text) => ShowInfo(text);

    /// <summary>Klick auf den Vorschläge-Chip einer Kachel: Person direkt im Reiter „Vorschläge“ öffnen.</summary>
    [RelayCommand]
    private async Task OpenPersonSuggestionsAsync(PersonTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        await LoadPersonAsync(tile.PersonId, keepState: false).ConfigureAwait(true);
        if (CurrentPerson?.Id == tile.PersonId)
        {
            DetailTab = 0;
        }
    }
}
