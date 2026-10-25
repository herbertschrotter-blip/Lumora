using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumora.Services.Activity;
using Wpf.Ui.Controls;

namespace Lumora.Shell.Activity;

/// <summary>
/// Eine Zeile im Aufklapp-Fenster der Aktivitäten (Mockup 15): Titel mit Zahlen, zweite Zeile (Ziel, Phase, Restzeit bzw.
/// Grund oder Ergebnis mit Zeitpunkt), Balken, Pause/Fortsetzen/Abbrechen bzw. Rückgängig/Fehlende zeigen/Details. Wird in
/// place aktualisiert (gleiche Id → gleiche Zeile), damit die UI-Automation nicht bei jeder Meldung neue Elemente sieht.
/// </summary>
public sealed partial class ActivityItemViewModel : ObservableObject
{
    private readonly ActivityStatusViewModel _owner;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _showBar;
    [ObservableProperty] private SymbolRegular _icon = SymbolRegular.ArrowSync16;
    [ObservableProperty] private ActivityState _state;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(ResumeCommand))]
    [NotifyPropertyChangedFor(nameof(ShowPause), nameof(ShowResume))]
    private bool _canPause;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPause), nameof(ShowResume))]
    private bool _isPaused;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _hasUndo;

    [ObservableProperty] private bool _hasShowMissing;
    [ObservableProperty] private bool _hasDetails;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isWarning;
    [ObservableProperty] private bool _isSuccess;
    [ObservableProperty] private bool _isWaiting;

    public ActivityItemViewModel(ActivityStatusViewModel owner, ActivitySnapshot snapshot, DateTime nowUtc)
    {
        _owner = owner;
        Id = snapshot.Id;
        Update(snapshot, nowUtc);
    }

    public long Id { get; }

    public ActivitySnapshot Snapshot { get; private set; } = null!;

    public bool ShowPause => CanPause && !IsPaused;

    public bool ShowResume => CanPause && IsPaused;

    /// <summary>Name für die UI-Automation: Titel + zweite Zeile.</summary>
    public string AutomationName => Subtitle is null ? Title : $"{Title}, {Subtitle}";

    public void Update(ActivitySnapshot s, DateTime nowUtc)
    {
        Snapshot = s;
        State = s.State;
        Icon = IconFor(s);
        IsWaiting = s.State == ActivityState.Waiting;
        IsPaused = s.State == ActivityState.Paused;
        IsError = s.State == ActivityState.Failed;
        IsWarning = s.State is ActivityState.Warning or ActivityState.Canceled;
        IsSuccess = s.State == ActivityState.Succeeded;
        Title = TitleOf(s);
        Subtitle = SubtitleOf(s, nowUtc);
        ShowBar = s.State is ActivityState.Running or ActivityState.Paused;
        IsIndeterminate = s.IsIndeterminate && s.State == ActivityState.Running;
        Fraction = s.Fraction;
        CanCancel = s.CanCancel;
        CanPause = s.CanPause;
        HasUndo = s.HasAction(ActivityActionKind.Undo);
        HasShowMissing = s.HasAction(ActivityActionKind.ShowMissing);
        HasDetails = s.HasAction(ActivityActionKind.Details) && !string.IsNullOrEmpty(s.Details);
        OnPropertyChanged(nameof(AutomationName));
    }

    /// <summary>„Verschieben: 120 von 340“ (mit Menge), „Motive erkennen — wartet“, sonst der Titel.</summary>
    internal static string TitleOf(ActivitySnapshot s) => s.State switch
    {
        ActivityState.Waiting => $"{s.Title} — wartet",
        ActivityState.Running or ActivityState.Paused when s.Total > 0 => $"{s.Title}: {s.Done:N0} von {s.Total:N0}",
        _ => s.Title,
    };

    /// <summary>Zweite Zeile: laufend Ziel · Phase · Restzeit; wartend der Grund; fertig Zeitpunkt · Ergebnis · Ziel.</summary>
    internal static string? SubtitleOf(ActivitySnapshot s, DateTime nowUtc)
    {
        var parts = new List<string>(4);
        switch (s.State)
        {
            case ActivityState.Waiting:
                return s.WaitReason;
            case ActivityState.Running:
            case ActivityState.Paused:
                Add(parts, s.Target);
                Add(parts, s.Detail);
                if (s.State == ActivityState.Paused)
                {
                    parts.Add("angehalten");
                }
                else if (s.Remaining is { } remaining)
                {
                    parts.Add(RemainingText(remaining));
                }

                break;
            default:
                parts.Add(Ago(s.FinishedUtc ?? nowUtc, nowUtc));
                Add(parts, s.Result);
                Add(parts, s.Target);
                break;
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>„noch etwa 20 s“, „noch etwa 1 min“, „noch etwa 1 h 5 min“.</summary>
    internal static string RemainingText(TimeSpan remaining)
    {
        if (remaining.TotalSeconds < 60)
        {
            return $"noch etwa {Math.Max(5, (int)Math.Ceiling(remaining.TotalSeconds / 5) * 5)} s";
        }

        if (remaining.TotalMinutes < 60)
        {
            return $"noch etwa {(int)Math.Round(remaining.TotalMinutes)} min";
        }

        return $"noch etwa {(int)remaining.TotalHours} h {remaining.Minutes} min";
    }

    /// <summary>„gerade eben“, „vor 2 min“, „vor 3 h“.</summary>
    internal static string Ago(DateTime whenUtc, DateTime nowUtc)
    {
        var span = nowUtc - whenUtc;
        if (span.TotalMinutes < 1)
        {
            return "gerade eben";
        }

        return span.TotalHours < 1 ? $"vor {(int)span.TotalMinutes} min" : $"vor {(int)span.TotalHours} h";
    }

    internal static SymbolRegular IconFor(ActivitySnapshot s) => s.State switch
    {
        ActivityState.Succeeded => SymbolRegular.CheckmarkCircle24,
        ActivityState.Warning => SymbolRegular.Warning24,
        ActivityState.Failed => SymbolRegular.ErrorCircle16,
        ActivityState.Canceled => SymbolRegular.Dismiss24,
        ActivityState.Waiting => SymbolRegular.Clock16,
        _ => KindIcon(s.Kind),
    };

    internal static SymbolRegular KindIcon(ActivityKind kind) => kind switch
    {
        ActivityKind.FileOperation => SymbolRegular.FolderArrowRight24,
        ActivityKind.Adoption => SymbolRegular.MailInbox24,
        ActivityKind.Sorter => SymbolRegular.ArrowSortDown24,
        ActivityKind.Archive => SymbolRegular.FolderZip24,
        ActivityKind.GpsTracks => SymbolRegular.LocationArrow20,
        ActivityKind.Thumbnails => SymbolRegular.Image24,
        ActivityKind.Duplicates => SymbolRegular.DocumentCopy24,
        ActivityKind.Faces => SymbolRegular.PersonCircle24,
        ActivityKind.Motifs => SymbolRegular.ImageSearch24,
        ActivityKind.CaptureDates => SymbolRegular.CalendarClock24,
        ActivityKind.Places => SymbolRegular.Location24,
        ActivityKind.Quality => SymbolRegular.Sparkle24,
        ActivityKind.Stories => SymbolRegular.BookOpen24,
        ActivityKind.CatalogWrite => SymbolRegular.Clock16,
        ActivityKind.ModelDownload => SymbolRegular.ArrowDownload24, // ADR-0070
        _ => SymbolRegular.ArrowSync16, // Einlesen, Ordnerüberwachung
    };

    private static void Add(List<string> parts, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(text);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _owner.Cancel(this);

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _owner.SetPaused(this, true);

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Resume() => _owner.SetPaused(this, false);

    [RelayCommand(CanExecute = nameof(HasUndo))]
    private System.Threading.Tasks.Task UndoAsync() => _owner.UndoAsync(this);

    [RelayCommand]
    private void ShowMissing() => _owner.ShowMissing();

    [RelayCommand]
    private System.Threading.Tasks.Task ShowDetailsAsync() => _owner.ShowDetailsAsync(this);
}
