using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Removal;
using Lumora.Shell;

namespace Lumora.Removal;

/// <summary>
/// Karte „Fehlende Dateien“ in Einstellungen › Speicher &amp; Wartung (ADR-0065): Zahl der als fehlend markierten Dateien der
/// aktuellen Bibliothek und „Alle fehlenden entfernen (n)“ mit Bestätigung und Rückgängig. In „Alle verknüpften“ (ADR-0048)
/// gesperrt. Zählt neu beim Anzeigen und nach Scans, Datei-Änderungen und Bibliothekswechsel.
/// </summary>
public sealed partial class MissingFilesSettingsViewModel : ObservableObject
{
    private readonly RemovalActions _actions;
    private readonly ILibraryScope _scope;
    private readonly ILogger<MissingFilesSettingsViewModel> _logger;
    private int _libraryId;

    public MissingFilesSettingsViewModel(RemovalActions actions, ILibraryScope scope, ILogger<MissingFilesSettingsViewModel> logger, IMessenger? messenger = null)
    {
        _actions = actions;
        _scope = scope;
        _logger = logger;
        var bus = messenger ?? WeakReferenceMessenger.Default;
        bus.Register<MissingFilesSettingsViewModel, ScanCompletedMessage>(this, static (r, m) => UiThread.Post(() => _ = r.RefreshAsync()));
        bus.Register<MissingFilesSettingsViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => _ = r.RefreshAsync()));
        bus.Register<MissingFilesSettingsViewModel, FilesChangedMessage>(this, static (r, msg) =>
        {
            if (msg.Kind != FilesChangeKind.Metadata || r.MissingCount > 0)
            {
                UiThread.Post(() => _ = r.RefreshAsync());
            }
        });
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText), nameof(SummaryText))]
    [NotifyCanExecuteChangedFor(nameof(RemoveAllCommand))]
    private int _missingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyCanExecuteChangedFor(nameof(RemoveAllCommand))]
    private bool _isAllLinked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveAllCommand), nameof(UndoCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private int? _undoBatchId;

    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private bool _resultIsWarning;

    public string ButtonText => $"Alle fehlenden entfernen ({MissingCount:N0})";

    public bool CanUndo => UndoBatchId is not null;

    public string SummaryText => IsAllLinked
        ? RemovalActions.AllLinkedHint
        : MissingCount switch
        {
            0 => "Keine fehlenden Dateien in der aktuellen Bibliothek.",
            1 => "1 Datei der aktuellen Bibliothek gibt es auf der Platte nicht mehr.",
            _ => $"{MissingCount:N0} Dateien der aktuellen Bibliothek gibt es auf der Platte nicht mehr.",
        };

    /// <summary>Zahl neu lesen (nur die aktuelle Bibliothek).</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var scope = await _scope.GetCurrentAsync().ConfigureAwait(true);
            _libraryId = scope.LibraryId;
            IsAllLinked = scope.IsAllLinked;
            MissingCount = scope.HasLibrary
                ? (await _actions.Service.CountMissingByFolderAsync([scope.LibraryId]).ConfigureAwait(true)).Values.Sum()
                : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fehlende Dateien: Zahl nicht lesbar");
        }
    }

    private bool CanRemoveAll() => !IsBusy && !IsAllLinked && MissingCount > 0 && _libraryId != 0;

    [RelayCommand(CanExecute = nameof(CanRemoveAll))]
    private async Task RemoveAllAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _actions.RemoveMissingAsync(new MissingTarget.InLibraries([_libraryId]), "in dieser Bibliothek").ConfigureAwait(true);
            if (result is not null)
            {
                UndoBatchId = result.UndoBatchId;
                ResultText = result.Message;
                ResultIsWarning = result.IsWarning;
            }
        }
        catch (Exception ex)
        {
            ResultText = $"Fehlende entfernen fehlgeschlagen: {ex.Message}";
            ResultIsWarning = true;
            _logger.LogError(ex, "Einstellungen: Fehlende entfernen fehlgeschlagen");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private bool CanUndoNow() => !IsBusy && UndoBatchId is not null;

    [RelayCommand(CanExecute = nameof(CanUndoNow))]
    private async Task UndoAsync()
    {
        if (UndoBatchId is not { } batchId)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _actions.UndoAsync(batchId).ConfigureAwait(true);
            UndoBatchId = null;
            ResultText = result.Message;
            ResultIsWarning = result.IsWarning;
        }
        catch (Exception ex)
        {
            ResultText = $"Rückgängig fehlgeschlagen: {ex.Message}";
            ResultIsWarning = true;
            _logger.LogError(ex, "Einstellungen: Rückgängig von Batch {BatchId} fehlgeschlagen", batchId);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }
}
