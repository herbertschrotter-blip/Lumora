using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Services;
using Lumora.Lightbox;
using Lumora.Services.Duplicates;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Shell;

namespace Lumora.Duplicates;

/// <summary>
/// Seite „Doppelte“ (ADR-0053): Umschalter „Exakt gleich“ / „Ähnlich“ (mit Schwelle), Filter „nur Eingang ↔ Bibliothek“,
/// Gruppen mit Vorschlag „behalten“ (Regel sichtbar, Auswahl änderbar), „Übrige in den Papierkorb“ nur nach Bestätigung,
/// „Angaben übertragen“ (Standard an), „Rückgängig“ der letzten Aufräumaktion. Alles im aktuellen Bereich
/// (<see cref="ILibraryScope"/>, ADR-0048); neu geladen wird beim Öffnen, nach Bibliothekswechsel und nach neuen
/// Fingerabdrücken (nur, solange die Seite sichtbar ist).
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private readonly IDuplicateService _duplicates;
    private readonly IDuplicateCleanupService _cleanup;
    private readonly IDuplicateSettingsStore _settings;
    private readonly ILibraryScope _scope;
    private readonly IGalleryDialogService _dialogs;
    private readonly IDuplicateImageSource _images;
    private readonly ILightboxService? _lightbox;
    private readonly ILogger<DuplicatesViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly Dictionary<int, DuplicateMemberViewModel> _membersById = new();
    private CancellationTokenSource? _loadCts;
    private bool _isActive;
    private bool _stale = true;
    private bool _settingsLoaded;
    private bool _suppressReload;

    public DuplicatesViewModel(
        IDuplicateService duplicates,
        IDuplicateCleanupService cleanup,
        IDuplicateSettingsStore settings,
        ILibraryScope scope,
        IGalleryDialogService dialogs,
        IDuplicateImageSource images,
        ILogger<DuplicatesViewModel> logger,
        ILightboxService? lightbox = null,
        IMessenger? messenger = null)
    {
        _duplicates = duplicates;
        _cleanup = cleanup;
        _settings = settings;
        _scope = scope;
        _dialogs = dialogs;
        _images = images;
        _lightbox = lightbox;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _images.ImagesReady += ApplyImages;

        _messenger.Register<DuplicatesViewModel, ActiveLibraryChangedMessage>(this, static (r, _) => UiThread.Post(r.MarkStale));
        _messenger.Register<DuplicatesViewModel, DuplicatesChangedMessage>(this, static (r, _) => UiThread.Post(r.MarkStale));
        _messenger.Register<DuplicatesViewModel, FilesChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind is FilesChangeKind.Removed or FilesChangeKind.Moved)
            {
                UiThread.Post(r.MarkStale);
            }
        });
    }

    /// <summary>Die Regel „behalten“ in Worten.</summary>
    public string RuleText => "Vorschlag „behalten“: " + KeeperRule.Description;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExactMode), nameof(IsSimilarMode))]
    private DuplicateMode _mode = DuplicateMode.Exact;

    /// <summary>Höchster pHash-Abstand für „ähnlich“ (1–12).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThresholdText))]
    private int _threshold = DuplicateSettings.DefaultSimilarThreshold;

    /// <summary>Nur Gruppen mit einer Datei im Eingang und einer in der Bibliothek.</summary>
    [ObservableProperty] private bool _inboxOnly;

    /// <summary>Bewertung, Favorit, Tags, Kategorien, Personen auf die behaltene Datei übertragen (Standard an).</summary>
    [ObservableProperty] private bool _transferMetadata = true;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _summaryText = string.Empty;

    /// <summary>Hinweis, solange der Hintergrund-Index noch nicht fertig ist (null = fertig).</summary>
    [ObservableProperty] private string? _pendingText;

    /// <summary>Ergebnis der letzten Aktion (null = keins).</summary>
    [ObservableProperty] private string? _statusText;

    [ObservableProperty] private string _emptyText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private DuplicateUndoInfo? _undoInfo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanUpCommand))]
    private int _deleteCount;

    [ObservableProperty] private string _cleanUpText = "Übrige in den Papierkorb";

    public bool IsExactMode
    {
        get => Mode == DuplicateMode.Exact;
        set
        {
            if (value)
            {
                Mode = DuplicateMode.Exact;
            }
        }
    }

    public bool IsSimilarMode
    {
        get => Mode == DuplicateMode.Similar;
        set
        {
            if (value)
            {
                Mode = DuplicateMode.Similar;
            }
        }
    }

    public int MinThreshold => DuplicateSettings.MinThreshold;

    public int MaxThreshold => DuplicateSettings.MaxThreshold;

    public string ThresholdText => Threshold switch
    {
        <= 4 => $"Abstand ≤ {Threshold} · nur fast gleiche",
        <= 8 => $"Abstand ≤ {Threshold} · verkleinerte und neu gespeicherte Kopien",
        _ => $"Abstand ≤ {Threshold} · auch Serienaufnahmen und Ausschnitte",
    };

    public bool HasGroups => Groups.Count > 0;

    public string UndoText => UndoInfo is { } u ? $"Rückgängig ({u.FileCount} {(u.FileCount == 1 ? "Datei" : "Dateien")})" : "Rückgängig";

    partial void OnUndoInfoChanged(DuplicateUndoInfo? value) => OnPropertyChanged(nameof(UndoText));

    partial void OnModeChanged(DuplicateMode value) => ReloadIfActive();

    partial void OnThresholdChanged(int value)
    {
        SaveSettings();
        if (Mode == DuplicateMode.Similar)
        {
            ReloadIfActive();
        }
    }

    partial void OnInboxOnlyChanged(bool value) => ReloadIfActive();

    partial void OnTransferMetadataChanged(bool value) => SaveSettings();

    /// <summary>Seite sichtbar (Code-Behind): laden, falls veraltet.</summary>
    public async Task ActivateAsync()
    {
        _isActive = true;
        if (!_settingsLoaded)
        {
            await LoadSettingsAsync();
        }

        if (_stale)
        {
            await LoadAsync();
        }
    }

    public void Deactivate() => _isActive = false;

    /// <summary>Statusleisten-Hinweis „anzeigen“: exakte Doppelte zwischen Eingang und Bibliothek.</summary>
    public void ShowInboxDuplicates()
    {
        _suppressReload = true;
        try
        {
            Mode = DuplicateMode.Exact;
            InboxOnly = true;
        }
        finally
        {
            _suppressReload = false;
        }

        _stale = true;
        ReloadIfActive();
    }

    private void MarkStale()
    {
        _stale = true;
        ReloadIfActive();
    }

    private void ReloadIfActive()
    {
        if (_suppressReload)
        {
            return;
        }

        _stale = true;
        if (_isActive)
        {
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Gruppen neu lesen (öffentlich für Tests und Rauchtest).</summary>
    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _stale = false;
        IsLoading = true;
        try
        {
            var scope = await _scope.GetCurrentAsync(cts.Token);
            if (!scope.HasLibrary)
            {
                SetGroups([], scope);
                SummaryText = "Keine Bibliothek gewählt.";
                PendingText = null;
                return;
            }

            var result = await _duplicates.GetGroupsAsync(new DuplicateQuery(scope.LibraryIds, Mode, Threshold, InboxOnly), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            SetGroups(result.Groups, scope);
            SummaryText = result.Groups.Count == 0
                ? "Keine Doppelten gefunden"
                : $"{result.Groups.Count:N0} {(result.Groups.Count == 1 ? "Gruppe" : "Gruppen")} · {result.FileCount:N0} Dateien · "
                  + $"{result.RedundantCount:N0} überzählig ({Formatting.Bytes(result.RedundantBytes)})";
            PendingText = Mode == DuplicateMode.Exact
                ? result.PendingContent > 0 ? $"Noch {result.PendingContent:N0} Prüfsummen offen — das Ergebnis ist noch unvollständig." : null
                : result.PendingImages > 0 ? $"Noch {result.PendingImages:N0} Bild-Fingerabdrücke offen — das Ergebnis ist noch unvollständig." : null;
            EmptyText = InboxOnly
                ? "Keine Datei im Eingang liegt schon in der Bibliothek."
                : Mode == DuplicateMode.Exact
                    ? "Keine byte-gleichen Dateien in diesem Bereich."
                    : "Keine ähnlichen Bilder bei dieser Schwelle.";
            UndoInfo = await _cleanup.GetUndoableAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // neuere Abfrage läuft
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Doppelte: Gruppen konnten nicht gelesen werden");
            StatusText = $"Doppelte konnten nicht gelesen werden: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }
    }

    private void SetGroups(IReadOnlyList<DuplicateGroup> groups, LibraryScopeInfo scope)
    {
        Groups.Clear();
        _membersById.Clear();
        foreach (var group in groups)
        {
            var vm = new DuplicateGroupViewModel(group, scope.IsAllLinked, UpdateDeleteCount);
            foreach (var member in vm.Members)
            {
                member.Image = _images.TryGet(member.FileId);
                _membersById[member.FileId] = member;
            }

            Groups.Add(vm);
        }

        OnPropertyChanged(nameof(HasGroups));
        UpdateDeleteCount();
    }

    /// <summary>Vom Code-Behind, sobald eine Gruppe sichtbar wird: ihre Vorschaubilder anfordern.</summary>
    public void EnsureImages(DuplicateGroupViewModel group)
        => _images.Request(group.Members.Where(m => m.Image is null).Select(m => m.Member));

    private void ApplyImages(IReadOnlyList<int> fileIds)
    {
        foreach (var id in fileIds)
        {
            if (_membersById.TryGetValue(id, out var member))
            {
                member.Image = _images.TryGet(id);
            }
        }
    }

    private void UpdateDeleteCount()
    {
        var included = Groups.Where(g => g.IsIncluded).ToList();
        DeleteCount = included.Sum(g => g.ToDelete.Count());
        var bytes = included.Sum(g => g.ToDelete.Sum(m => m.Member.Size));
        CleanUpText = DeleteCount == 0
            ? "Übrige in den Papierkorb"
            : $"Übrige in den Papierkorb ({DeleteCount:N0} · {Formatting.Bytes(bytes)})";
    }

    private bool CanCleanUp() => DeleteCount > 0 && !IsBusy;

    /// <summary>„Übrige in den Papierkorb“ — nur nach Bestätigung, über die Datei-Operationen (Protokoll, Rückgängig).</summary>
    [RelayCommand(CanExecute = nameof(CanCleanUp))]
    private async Task CleanUpAsync()
    {
        var groups = Groups
            .Where(g => g.IsIncluded && g.Keeper is not null)
            .Select(g => new DuplicateCleanupGroup(g.Keeper!.FileId, g.ToDelete.Select(m => m.FileId).ToList(), g.Group.Mode == DuplicateMode.Exact))
            .Where(g => g.DeleteFileIds.Count > 0)
            .ToList();
        if (groups.Count == 0)
        {
            return;
        }

        var files = groups.Sum(g => g.DeleteFileIds.Count);
        var transfer = TransferMetadata
            ? "Bewertung, Favorit, Tags, Kategorien und Personen werden vorher auf die behaltene Datei übertragen."
            : "Angaben der gelöschten Dateien werden nicht übertragen.";
        var message = $"{files:N0} {(files == 1 ? "Datei" : "Dateien")} aus {groups.Count:N0} {(groups.Count == 1 ? "Gruppe" : "Gruppen")} kommen in den Papierkorb. "
                      + $"Je Gruppe bleibt die als „Behalten“ markierte Datei.\n\n{transfer}\n\n„Rückgängig“ auf dieser Seite holt sie zurück.";
        if (!await _dialogs.ConfirmAsync("Übrige in den Papierkorb", message, "In den Papierkorb", isDestructive: true))
        {
            return;
        }

        IsBusy = true;
        CleanUpCommand.NotifyCanExecuteChanged();
        try
        {
            var result = await _cleanup.CleanUpAsync(groups, TransferMetadata);
            var text = $"{result.Recycled:N0} {(result.Recycled == 1 ? "Datei" : "Dateien")} im Papierkorb";
            if (result.Transferred > 0)
            {
                text += $" · {result.Transferred:N0} Angaben übertragen";
            }

            if (result.PersonsNotTransferred > 0)
            {
                text += $" · {result.PersonsNotTransferred:N0} Personen nicht übertragbar (kein passendes Gesicht)";
            }

            StatusText = text;
            if (result.Failed > 0)
            {
                await _dialogs.ShowErrorsAsync("Übrige in den Papierkorb", $"{result.Failed:N0} Dateien konnten nicht in den Papierkorb.", result.Errors);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Doppelte: Aufräumen fehlgeschlagen");
            await _dialogs.ShowErrorsAsync("Übrige in den Papierkorb", ex.Message, []);
        }
        finally
        {
            IsBusy = false;
            CleanUpCommand.NotifyCanExecuteChanged();
        }

        await LoadAsync();
    }

    private bool CanUndo() => UndoInfo is not null && !IsBusy;

    /// <summary>Letzte Aufräumaktion zurücknehmen: Dateien aus dem Papierkorb mit allen Angaben zurück.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (UndoInfo is not { } info)
        {
            return;
        }

        IsBusy = true;
        UndoCommand.NotifyCanExecuteChanged();
        try
        {
            var result = await _cleanup.UndoAsync(info.BatchId);
            StatusText = result is null
                ? "Nichts mehr zurückzunehmen."
                : $"{result.Restored:N0} {(result.Restored == 1 ? "Datei" : "Dateien")} aus dem Papierkorb zurückgeholt";
            if (result is { Failed: > 0 })
            {
                await _dialogs.ShowErrorsAsync("Rückgängig", $"{result.Failed:N0} Dateien konnten nicht zurückgeholt werden.", result.Errors);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Doppelte: Rückgängig fehlgeschlagen");
            await _dialogs.ShowErrorsAsync("Rückgängig", ex.Message, []);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    /// <summary>„Eingang-Filter aufheben“ im Hinweis.</summary>
    [RelayCommand]
    private void ClearInboxFilter() => InboxOnly = false;

    /// <summary>Doppelklick auf eine Karte: Lightbox mit den Dateien der Gruppe.</summary>
    public void Open(DuplicateMemberViewModel member)
    {
        var group = Groups.FirstOrDefault(g => g.Members.Contains(member));
        if (_lightbox is null || group is null)
        {
            return;
        }

        var entries = group.Members
            .Select(m => new LightboxEntry(m.FileId, m.FullPath, m.Name, m.Member.MediaType, m.Member.IsFavorite, m.Member.Rating, m.Member.Size, m.Member.ModifiedAtUtc))
            .ToList();
        _lightbox.Open(entries, group.Members.ToList().IndexOf(member), System.Windows.Application.Current?.MainWindow, Lumora.Services.Usage.UsageViewContext.None); // Vergleichen zählt nicht (ADR-0072)
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _settings.LoadAsync();
            _suppressReload = true;
            try
            {
                Threshold = settings.SimilarThreshold;
                TransferMetadata = settings.TransferMetadata;
            }
            finally
            {
                _suppressReload = false;
            }

            _settingsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Doppelte: Einstellungen nicht lesbar — Standard");
            _settingsLoaded = true;
        }
    }

    private void SaveSettings()
    {
        if (!_settingsLoaded || _suppressReload)
        {
            return;
        }

        var settings = new DuplicateSettings(Threshold, TransferMetadata);
        _ = Task.Run(async () =>
        {
            try
            {
                await _settings.SaveAsync(settings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Doppelte: Einstellungen nicht gespeichert");
            }
        });
    }
}
