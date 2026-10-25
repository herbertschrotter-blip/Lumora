using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Shell;

namespace Lumora.Exclusions;

/// <summary>Eintrag „ausgeschlossener Ordner“ in den Einstellungen.</summary>
public sealed record ExcludedFolderItem(int LibraryId, string FullPath, string RelativePath, string RootName)
{
    public string Display => $"{RootName} › {RelativePath}";
}

/// <summary>
/// Karte „Ausschlüsse beim Einlesen“ in Einstellungen › Bibliotheken &amp; Eingänge (ADR-0066): allgemeine Liste (Ordnernamen für
/// alle Bibliotheken des Katalogs) mit Hinzufügen/Entfernen, ausgeschlossene Ordner der aktuellen Bibliothek mit „Ausschluss
/// aufheben“, Zahl der schon katalogisierten Dateien in ausgeschlossenen Ordnern mit „Aus der Bibliothek entfernen…“ und
/// Rückgängig. Lädt neu beim Anzeigen, nach Änderungen der Ausschlüsse, Scans und Bibliothekswechsel.
/// </summary>
public sealed partial class ScanExclusionsSettingsViewModel : ObservableObject
{
    private readonly ScanExclusionActions _actions;
    private readonly ILibraryScope _scope;
    private readonly ILibraryService _libraries;
    private readonly ILogger<ScanExclusionsSettingsViewModel> _logger;
    private ExclusionUndo? _undo;

    public ScanExclusionsSettingsViewModel(ScanExclusionActions actions, ILibraryScope scope, ILibraryService libraries, ILogger<ScanExclusionsSettingsViewModel> logger, IMessenger? messenger = null)
    {
        _actions = actions;
        _scope = scope;
        _libraries = libraries;
        _logger = logger;
        var bus = messenger ?? WeakReferenceMessenger.Default;
        bus.Register<ScanExclusionsSettingsViewModel, ScanExclusionsChangedMessage>(this, static (r, m) => UiThread.Post(() => _ = r.RefreshAsync()));
        bus.Register<ScanExclusionsSettingsViewModel, ScanCompletedMessage>(this, static (r, m) => UiThread.Post(() => _ = r.RefreshAsync()));
        bus.Register<ScanExclusionsSettingsViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => _ = r.RefreshAsync()));
    }

    public ObservableCollection<string> FolderNames { get; } = new();

    public ObservableCollection<ExcludedFolderItem> Folders { get; } = new();

    public ScanExclusionActions Actions => _actions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddNameCommand))]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string? _libraryName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolders))]
    private int _folderCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCatalogedExcluded), nameof(CatalogedText))]
    [NotifyCanExecuteChangedFor(nameof(RemoveExistingCommand))]
    private int _catalogedExcluded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddNameCommand), nameof(RemoveNameCommand), nameof(LiftFolderCommand), nameof(RemoveExistingCommand), nameof(UndoCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private int? _undoBatchId;

    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private bool _resultIsWarning;

    private int _libraryId;

    public bool HasFolders => FolderCount > 0;

    public bool HasCatalogedExcluded => CatalogedExcluded > 0;

    public bool CanUndo => UndoBatchId is not null;

    public string CatalogedText => CatalogedExcluded == 1
        ? "1 Datei in ausgeschlossenen Ordnern ist noch in der Bibliothek."
        : $"{CatalogedExcluded:N0} Dateien in ausgeschlossenen Ordnern sind noch in der Bibliothek.";

    /// <summary>Listen und Zahl neu lesen (aktuelle Bibliothek).</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var scope = await _scope.GetCurrentAsync().ConfigureAwait(true);
            _libraryId = scope.LibraryId;
            LibraryName = _libraryId > 0 ? (await _libraries.GetAsync(_libraryId).ConfigureAwait(true))?.Name : null;
            var names = await _actions.Service.Store.GetFolderNamesAsync().ConfigureAwait(true);
            Replace(FolderNames, names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
            if (_libraryId > 0)
            {
                var folders = await _actions.Service.Store.GetFoldersAsync(_libraryId).ConfigureAwait(true);
                Replace(Folders, folders.Select(f => new ExcludedFolderItem(f.LibraryId, f.FullPath, f.RelativePath, f.RootName)));
                CatalogedExcluded = (await _actions.Service.SummarizeAsync(new ExclusionScope.Current([_libraryId])).ConfigureAwait(true)).Count;
            }
            else
            {
                Folders.Clear();
                CatalogedExcluded = 0;
            }

            FolderCount = Folders.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ausschlüsse konnten nicht gelesen werden");
        }
    }

    private bool CanAddName() => !IsBusy && !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanAddName))]
    private Task AddNameAsync() => RunAsync(async () =>
    {
        var result = await _actions.AddFolderNameAsync(NewName).ConfigureAwait(true);
        if (result is not null && !result.IsWarning)
        {
            NewName = string.Empty;
        }

        return result;
    });

    private bool CanEdit() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RemoveNameAsync(string? name)
        => string.IsNullOrWhiteSpace(name) ? Task.CompletedTask : RunAsync(async () => await _actions.RemoveFolderNameAsync(name, _libraryId > 0 ? _libraryId : null).ConfigureAwait(true));

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task LiftFolderAsync(ExcludedFolderItem? item)
        => item is null ? Task.CompletedTask : RunAsync(async () => await _actions.LiftFolderAsync(item.LibraryId, item.FullPath).ConfigureAwait(true));

    private bool CanRemoveExisting() => !IsBusy && CatalogedExcluded > 0 && _libraryId > 0;

    [RelayCommand(CanExecute = nameof(CanRemoveExisting))]
    private Task RemoveExistingAsync() => RunAsync(() => _actions.RemoveExistingAsync(_libraryId));

    private bool CanRunUndo() => !IsBusy && UndoBatchId is not null;

    [RelayCommand(CanExecute = nameof(CanRunUndo))]
    private async Task UndoAsync()
    {
        if (UndoBatchId is not { } batchId)
        {
            return;
        }

        var undo = _undo;
        await RunAsync(async () => await _actions.UndoAsync(batchId, undo).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async Task RunAsync(Func<Task<ExclusionActionResult?>> action)
    {
        IsBusy = true;
        try
        {
            var result = await action().ConfigureAwait(true);
            if (result is not null)
            {
                ResultText = result.Message;
                ResultIsWarning = result.IsWarning;
                UndoBatchId = result.UndoBatchId;
                _undo = result.Undo;
            }
        }
        catch (Exception ex)
        {
            ResultText = $"Fehlgeschlagen: {ex.Message}";
            ResultIsWarning = true;
            UndoBatchId = null;
            _logger.LogError(ex, "Ausschlüsse: Aktion fehlgeschlagen");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private static void Replace<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
