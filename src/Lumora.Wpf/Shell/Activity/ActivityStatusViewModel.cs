using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Activity;

namespace Lumora.Shell.Activity;

/// <summary>Öffnet Ziele der Aktivitäts-Anzeige außerhalb der Statusleiste (Galerie „Nur fehlende“).</summary>
public interface IActivityNavigator
{
    void ShowMissingFiles();
}

/// <summary>
/// Aktivitäts-Anzeige ganz rechts in der Statusleiste (ADR-0067, Mockup 15): wichtigster Vorgang mit Drehsymbol, Text und
/// kleinem Balken, Zähler „+n“, ohne Vorgang kurz „Alles erledigt“, danach „Keine Aktivität“. Klick klappt die Liste auf
/// (Läuft / Wartet / Zuletzt fertig); beim Fertigwerden eines vom Benutzer gestarteten Vorgangs eine kurze Meldung über der
/// Statusleiste, mit „Rückgängig“ wo möglich. Liest die <see cref="IActivityCenter"/> nach deren (gedrosselter)
/// Benachrichtigung — höchstens ein Auftrag mit Background-Priorität steht am UI-Thread aus; Zeilen werden in place
/// aktualisiert.
/// </summary>
public sealed partial class ActivityStatusViewModel : ObservableObject, IDisposable
{
    /// <summary>So lange steht „Alles erledigt“ nach dem letzten Ende.</summary>
    public static readonly TimeSpan AllDoneDuration = TimeSpan.FromSeconds(8);

    /// <summary>So lange steht die kurze Meldung.</summary>
    public static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan AgeRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IActivityCenter _center;
    private readonly IDialogService? _dialogs;
    private readonly IActivityNavigator? _navigator;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly DispatcherTimer _allDoneTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _ageTimer;
    private int _refreshQueued;
    private long? _toastActivityId;

    [ObservableProperty] private string _statusText = "Keine Aktivität";
    [ObservableProperty] private string? _statusToolTip;
    [ObservableProperty] private bool _hasActive;
    [ObservableProperty] private bool _primaryIsWaiting;
    [ObservableProperty] private bool _showAllDone;
    [ObservableProperty] private bool _isIdle = true;
    [ObservableProperty] private double _primaryFraction;
    [ObservableProperty] private bool _primaryIsIndeterminate;
    [ObservableProperty] private bool _showPrimaryBar;
    [ObservableProperty] private string? _othersText;
    [ObservableProperty] private string _headerSummary = "nichts aktiv";
    [ObservableProperty] private bool _hasRunning;
    [ObservableProperty] private bool _hasWaiting;
    [ObservableProperty] private bool _hasRecent;
    [ObservableProperty] private bool _isEmpty = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToastOverlay))]
    private bool _isFlyoutOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToastOverlay))]
    private bool _isToastVisible;

    [ObservableProperty] private string? _toastTitle;
    [ObservableProperty] private string? _toastSubtitle;
    [ObservableProperty] private bool _toastIsError;
    [ObservableProperty] private bool _toastIsWarning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoToastCommand))]
    private bool _toastHasUndo;

    /// <param name="utcNow">Optional (Tests); sonst die Uhr.</param>
    public ActivityStatusViewModel(
        IActivityCenter center,
        IDialogService? dialogs = null,
        IActivityNavigator? navigator = null,
        ILogger<ActivityStatusViewModel>? logger = null,
        Func<DateTime>? utcNow = null,
        Lumora.Services.Background.IBackgroundPlanner? planner = null)
    {
        _center = center;
        _planner = planner;
        _dialogs = dialogs;
        _navigator = navigator;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _allDoneTimer = new DispatcherTimer(AllDoneDuration, DispatcherPriority.Background, (sender, _) => { (sender as DispatcherTimer)?.Stop(); UpdateStatus(_center.GetOverview()); }, dispatcher);
        StopTimer(_allDoneTimer);
        _toastTimer = new DispatcherTimer(ToastDuration, DispatcherPriority.Background, (_, _) => DismissToast(), dispatcher);
        StopTimer(_toastTimer);
        _ageTimer = new DispatcherTimer(AgeRefreshInterval, DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
        StopTimer(_ageTimer);
        _center.Changed += OnCenterChanged;
        _center.Finished += OnCenterFinished;
        if (_planner is not null)
        {
            _isAllPaused = _planner.AllPaused;
            _planner.Changed += () => dispatcher.BeginInvoke(() => IsAllPaused = _planner.AllPaused, DispatcherPriority.Background);
        }

        Refresh();
    }

    private readonly Lumora.Services.Background.IBackgroundPlanner? _planner;

    /// <summary>„Alle pausieren“ im Kopf der Liste (ADR-0074): hält alle Hintergrund-Aufgaben an, nicht das Einlesen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllPausedLabel))]
    private bool _isAllPaused;

    public bool CanPauseAll => _planner is not null;

    public string AllPausedLabel => IsAllPaused ? "Alle fortsetzen" : "Alle pausieren";

    [RelayCommand]
    private void ToggleAllPaused()
    {
        if (_planner is null)
        {
            return;
        }

        var paused = !_planner.AllPaused;
        _logger.LogInformation("Aktivitäten: alle Hintergrund-Aufgaben {State}", paused ? "pausiert" : "fortgesetzt");
        _planner.SetAllPaused(paused);
        IsAllPaused = paused;
    }

    public ObservableCollection<ActivityItemViewModel> Running { get; } = new();

    public ObservableCollection<ActivityItemViewModel> Waiting { get; } = new();

    public ObservableCollection<ActivityItemViewModel> Recent { get; } = new();

    /// <summary>Die kurze Meldung nur, solange die Liste zu ist (sonst überdecken sich beide).</summary>
    public bool ShowToastOverlay => IsToastVisible && !IsFlyoutOpen;

    /// <summary>Liest die Zentrale und aktualisiert Statusleiste und Liste (UI-Thread).</summary>
    public void Refresh()
    {
        Volatile.Write(ref _refreshQueued, 0);
        var overview = _center.GetOverview();
        var now = _utcNow();
        Sync(Running, overview.Running, now);
        Sync(Waiting, overview.Waiting, now);
        Sync(Recent, overview.Recent, now);
        HasRunning = Running.Count > 0;
        HasWaiting = Waiting.Count > 0;
        HasRecent = Recent.Count > 0;
        IsEmpty = !HasRunning && !HasWaiting && !HasRecent;
        HeaderSummary = Summary(overview.Running.Count, overview.Waiting.Count);
        UpdateStatus(overview);
    }

    partial void OnIsFlyoutOpenChanged(bool value)
    {
        if (value)
        {
            Refresh(); // Zeitangaben („vor 2 min“) frisch
            _ageTimer.Start();
        }
        else
        {
            StopTimer(_ageTimer);
        }
    }

    internal static string Summary(int running, int waiting)
    {
        var parts = new List<string>(2);
        if (running > 0)
        {
            parts.Add(running == 1 ? "1 läuft" : $"{running} laufen");
        }

        if (waiting > 0)
        {
            parts.Add(waiting == 1 ? "1 wartet" : $"{waiting} warten");
        }

        return parts.Count == 0 ? "nichts aktiv" : string.Join(" · ", parts);
    }

    private void UpdateStatus(ActivityOverview overview)
    {
        var primary = overview.Primary;
        HasActive = primary is not null;
        PrimaryIsWaiting = primary is { State: ActivityState.Waiting };
        if (primary is not null)
        {
            StopTimer(_allDoneTimer);
            ShowAllDone = false;
            IsIdle = false;
            StatusText = ActivityItemViewModel.TitleOf(primary);
            StatusToolTip = ActivityItemViewModel.SubtitleOf(primary, _utcNow());
            ShowPrimaryBar = primary.State is ActivityState.Running or ActivityState.Paused;
            PrimaryIsIndeterminate = primary.IsIndeterminate && primary.State == ActivityState.Running;
            PrimaryFraction = primary.Fraction;
            OthersText = overview.OthersCount > 0 ? $"+{overview.OthersCount}" : null;
            return;
        }

        ShowPrimaryBar = false;
        OthersText = null;
        StatusToolTip = null;
        var sinceLast = overview.LastFinishedUtc is { } last ? _utcNow() - last : (TimeSpan?)null;
        if (sinceLast is { } since && since < AllDoneDuration)
        {
            ShowAllDone = true;
            IsIdle = false;
            StatusText = "Alles erledigt";
            _allDoneTimer.Interval = AllDoneDuration - since + TimeSpan.FromMilliseconds(50);
            _allDoneTimer.Start();
        }
        else
        {
            ShowAllDone = false;
            IsIdle = true;
            StatusText = "Keine Aktivität";
        }
    }

    /// <summary>Gleiche Id → gleiche Zeile (in place), Reihenfolge wie die Zentrale.</summary>
    private void Sync(ObservableCollection<ActivityItemViewModel> target, IReadOnlyList<ActivitySnapshot> source, DateTime now)
    {
        var existing = target.ToDictionary(i => i.Id);
        for (var i = 0; i < source.Count; i++)
        {
            var snapshot = source[i];
            if (existing.Remove(snapshot.Id, out var item))
            {
                item.Update(snapshot, now);
                var index = target.IndexOf(item);
                if (index != i)
                {
                    target.Move(index, i);
                }
            }
            else
            {
                target.Insert(i, new ActivityItemViewModel(this, snapshot, now));
            }
        }

        foreach (var gone in existing.Values)
        {
            target.Remove(gone);
        }
    }

    private void OnCenterChanged()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return; // ein Auftrag steht schon aus und liest den jüngsten Stand
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Refresh();
            return;
        }

        _ = dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
    }

    private void OnCenterFinished(ActivitySnapshot snapshot)
    {
        if (!snapshot.UserInitiated || snapshot.State == ActivityState.Canceled)
        {
            return; // Hintergrund-Indizes und Einlesen ohne Meldung; Abbrechen hat der Benutzer selbst ausgelöst
        }

        UiThread.Post(() => ShowToast(snapshot));
    }

    internal void ShowToast(ActivitySnapshot snapshot)
    {
        _toastActivityId = snapshot.Id;
        ToastTitle = snapshot.Result ?? snapshot.Title;
        ToastSubtitle = snapshot.Target ?? (snapshot.Result is null ? null : snapshot.Title);
        ToastIsError = snapshot.State == ActivityState.Failed;
        ToastIsWarning = snapshot.State == ActivityState.Warning;
        ToastHasUndo = snapshot.HasAction(ActivityActionKind.Undo);
        IsToastVisible = true;
        StopTimer(_toastTimer);
        _toastTimer.Start();
    }

    /// <summary>Kurze Meldung ohne Eintrag (Ergebnis von „Rückgängig“ aus Liste oder Meldung).</summary>
    internal void ShowMessage(string text)
    {
        _toastActivityId = null;
        ToastTitle = text;
        ToastSubtitle = null;
        ToastIsError = false;
        ToastIsWarning = false;
        ToastHasUndo = false;
        IsToastVisible = true;
        StopTimer(_toastTimer);
        _toastTimer.Start();
    }

    [RelayCommand]
    private void DismissToast()
    {
        StopTimer(_toastTimer);
        IsToastVisible = false;
        _toastActivityId = null;
    }

    [RelayCommand(CanExecute = nameof(ToastHasUndo))]
    private async Task UndoToastAsync()
    {
        if (_toastActivityId is not { } id)
        {
            return;
        }

        DismissToast();
        await RunUndoAsync(id).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleFlyout() => IsFlyoutOpen = !IsFlyoutOpen;

    [RelayCommand]
    private void CloseFlyout() => IsFlyoutOpen = false;

    [RelayCommand]
    private void ClearRecent() => _center.ClearRecent();

    internal void Cancel(ActivityItemViewModel item)
    {
        _logger.LogInformation("Aktivitäten: Abbrechen für Vorgang {Id} ({Kind})", item.Id, item.Snapshot.Kind);
        _center.Cancel(item.Id);
        Refresh();
    }

    internal void SetPaused(ActivityItemViewModel item, bool paused)
    {
        _center.SetPaused(item.Id, paused);
        Refresh();
    }

    internal Task UndoAsync(ActivityItemViewModel item) => RunUndoAsync(item.Id);

    private async Task RunUndoAsync(long id)
    {
        try
        {
            var text = await _center.UndoAsync(id).ConfigureAwait(true);
            if (text is null)
            {
                // schon zurückgenommen bzw. läuft gerade
            }
            else if (text.StartsWith("Rückgängig fehlgeschlagen", StringComparison.Ordinal))
            {
                if (_dialogs is not null)
                {
                    await _dialogs.ShowErrorAsync("Rückgängig", text).ConfigureAwait(true);
                }
            }
            else
            {
                ShowMessage(text); // kurze Meldung „Rückgängig: 340 Dateien zurückverschoben“ (kein eigener Eintrag)
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aktivitäten: Rückgängig für Vorgang {Id} fehlgeschlagen", id);
        }

        Refresh();
    }

    internal void ShowMissing()
    {
        IsFlyoutOpen = false;
        _navigator?.ShowMissingFiles();
    }

    internal async Task ShowDetailsAsync(ActivityItemViewModel item)
    {
        if (_dialogs is null || string.IsNullOrEmpty(item.Snapshot.Details))
        {
            return;
        }

        await _dialogs.ShowErrorAsync(item.Snapshot.Title, item.Snapshot.Details).ConfigureAwait(true);
    }

    private static void StopTimer(DispatcherTimer timer) => timer.Stop();

    public void Dispose()
    {
        _center.Changed -= OnCenterChanged;
        _center.Finished -= OnCenterFinished;
        StopTimer(_allDoneTimer);
        StopTimer(_toastTimer);
        StopTimer(_ageTimer);
    }
}
