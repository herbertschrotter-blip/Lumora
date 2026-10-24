using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Services.HiddenLibraries.SecurityCheck;
using Wpf.Ui.Controls;

namespace Lumora.Shell.HiddenLibraries.SecurityCheck;

/// <summary>
/// Reiter „Sicherheits-Check“ (ADR-0064, Mockup 14) — nur in der geöffneten versteckten Sitzung. Zeigt die Prüfungen mit
/// Wertung „x von y erfüllt“, Gefahren-Stufe, „Erneut prüfen“, „Erfüllte ausblenden“, je Punkt „Einstellung öffnen“ und
/// Anleitung sowie die Lumora-Option „nach dem Zusperren ganz beenden“. Lumora ändert keine Windows-Einstellung.
/// </summary>
public sealed partial class SecurityCheckViewModel : ObservableObject
{
    private readonly ISecurityCheckService _service;
    private readonly ISecuritySettingsLauncher _launcher;
    private readonly ICatalogSessionManager _sessions;
    private readonly ILogger<SecurityCheckViewModel> _logger;
    private bool _applying;

    public SecurityCheckViewModel(
        ISecurityCheckService service,
        ISecuritySettingsLauncher launcher,
        ICatalogSessionManager sessions,
        ILogger<SecurityCheckViewModel> logger)
    {
        _service = service;
        _launcher = launcher;
        _sessions = sessions;
        _logger = logger;
        Levels =
        [
            new(this, ThreatLevel.PoweredOff, "Ausgeschalteter PC", "Diebstahl, Reparatur, Verkauf der Festplatte"),
            new(this, ThreatLevel.SharedUser, "Mitbenutzer", "Familie oder jemand kurz am PC, eigenes Windows-Konto"),
            new(this, ThreatLevel.Forensic, "Fachmännische Untersuchung", "auch unter Druck Kennwörter herausgeben müssen"),
        ];
        _level = service.Settings.Level;
        _exitAfterLock = service.Settings.ExitAfterLock;
        foreach (var option in Levels)
        {
            option.Sync(_level);
        }

        if (service.Last is { } last)
        {
            Apply(last);
        }
    }

    public bool IsHiddenModeEnabled => _sessions.IsHiddenModeEnabled;

    public bool IsHiddenSessionActive => _sessions.IsHiddenSessionActive;

    public string Lead => SecurityCheckCatalog.Lead;

    public string InfoTitle => SecurityCheckCatalog.InfoTitle;

    public string InfoText => SecurityCheckCatalog.InfoText;

    public ObservableCollection<SecurityCheckRowViewModel> Rows { get; } = new();

    public System.Collections.Generic.IReadOnlyList<ThreatLevelOption> Levels { get; }

    [ObservableProperty]
    private ThreatLevel _level;

    [ObservableProperty]
    private bool _exitAfterLock;

    [ObservableProperty]
    private bool _hideFulfilled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _fulfilledCount;

    [ObservableProperty]
    private int _countedTotal;

    /// <summary>Hinweis nach einem Klick (z. B. Ziel auf diesem PC nicht vorhanden); null = keiner.</summary>
    [ObservableProperty]
    private string? _message;

    public string ScoreText => CountedTotal == 0 ? "–" : $"{FulfilledCount}/{CountedTotal}";

    /// <summary>0–100 für den Ring.</summary>
    public double ScorePercent => CountedTotal == 0 ? 0 : 100.0 * FulfilledCount / CountedTotal;

    /// <summary>Endwinkel des Rings (Grad, höchstens knapp unter 360 — ein voller Kreis wäre als Bogen leer).</summary>
    public double ScoreAngle => Math.Min(359.9, ScorePercent * 3.6);

    /// <summary>Mindestens ein Punkt erfüllt — sonst nur der graue Ring.</summary>
    public bool HasScore => FulfilledCount > 0;

    public bool IsAllFulfilled => CountedTotal > 0 && FulfilledCount == CountedTotal;

    public string SummaryText => CountedTotal == 0 ? "Noch nicht geprüft." : $"{FulfilledCount} von {CountedTotal} erfüllt";

    public string LevelNote => SecurityCheckRating.LevelNote(Level);

    partial void OnFulfilledCountChanged(int value) => NotifyScore();

    partial void OnCountedTotalChanged(int value) => NotifyScore();

    partial void OnLevelChanged(ThreatLevel value)
    {
        OnPropertyChanged(nameof(LevelNote));
        foreach (var option in Levels)
        {
            option.Sync(value);
        }

        if (!_applying)
        {
            _ = SaveLevelAsync(value);
        }
    }

    partial void OnExitAfterLockChanged(bool value)
    {
        if (!_applying)
        {
            _ = SaveExitAfterLockAsync(value);
        }
    }

    partial void OnHideFulfilledChanged(bool value)
    {
        foreach (var row in Rows)
        {
            row.HideIfFulfilled = value;
        }
    }

    /// <summary>Beim Anzeigen des Reiters: letztes Ergebnis zeigen, sonst prüfen.</summary>
    public Task LoadAsync() => _service.Last is { } last ? ApplyOnUiAsync(last) : RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Message = null;
        try
        {
            var report = await _service.RunAsync().ConfigureAwait(true);
            Apply(report);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sicherheits-Check: Prüfung fehlgeschlagen ({Type})", ex.GetType().Name);
            Message = "Die Prüfung ist fehlgeschlagen. Bitte später erneut prüfen.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleHideFulfilled() => HideFulfilled = !HideFulfilled;

    internal void Open(SecurityCheckRowViewModel row)
    {
        Message = null;
        if (row.Target == SettingsTarget.None)
        {
            return;
        }

        if (!_launcher.Open(row.Target))
        {
            row.IsHowtoOpen = true;
            Message = $"„{row.Title}“: Die Einstellung lässt sich auf diesem PC nicht direkt öffnen — bitte der Anleitung folgen.";
        }
    }

    private async Task SaveLevelAsync(ThreatLevel level)
    {
        try
        {
            if (await _service.SetLevelAsync(level).ConfigureAwait(true) is { } report)
            {
                Apply(report);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sicherheits-Check: Gefahren-Stufe nicht gespeichert ({Type})", ex.GetType().Name);
            Message = "Die Gefahren-Stufe wurde nicht gespeichert.";
        }
    }

    private async Task SaveExitAfterLockAsync(bool value)
    {
        try
        {
            if (await _service.SetExitAfterLockAsync(value).ConfigureAwait(true) is { } report)
            {
                Apply(report);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sicherheits-Check: Option „ganz beenden“ nicht gespeichert ({Type})", ex.GetType().Name);
            Message = "Die Einstellung wurde nicht gespeichert.";
        }
    }

    private Task ApplyOnUiAsync(SecurityCheckReport report)
    {
        Apply(report);
        return Task.CompletedTask;
    }

    /// <summary>Übernimmt ein Ergebnis (Zeilen werden nur beim ersten Mal angelegt, danach aktualisiert).</summary>
    internal void Apply(SecurityCheckReport report)
    {
        _applying = true;
        try
        {
            Level = report.Level;
            ExitAfterLock = _service.Settings.ExitAfterLock;
            var context = _service.LastContext;
            foreach (var finding in report.Findings)
            {
                var row = Rows.FirstOrDefault(r => r.Id == finding.Id);
                if (row is null)
                {
                    var definition = SecurityCheckCatalog.Get(finding.Id);
                    row = new SecurityCheckRowViewModel(this, definition, definition.Target != SettingsTarget.None && _launcher.IsAvailable(definition.Target));
                    Rows.Add(row);
                }

                row.Update(finding, report.Level, SecurityCheckCatalog.TitleFor(finding.Id, context), HideFulfilled);
            }

            FulfilledCount = report.FulfilledCount;
            CountedTotal = report.CountedTotal;
            NotifyScore();
        }
        finally
        {
            _applying = false;
        }
    }

    private void NotifyScore()
    {
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(ScorePercent));
        OnPropertyChanged(nameof(ScoreAngle));
        OnPropertyChanged(nameof(HasScore));
        OnPropertyChanged(nameof(IsAllFulfilled));
        OnPropertyChanged(nameof(SummaryText));
    }
}

/// <summary>Eine Wahl der Gefahren-Stufe (Karte mit Titel und Erklärung).</summary>
public sealed partial class ThreatLevelOption : ObservableObject
{
    private readonly SecurityCheckViewModel _owner;

    internal ThreatLevelOption(SecurityCheckViewModel owner, ThreatLevel level, string title, string description)
    {
        _owner = owner;
        Level = level;
        Title = title;
        Description = description;
    }

    public ThreatLevel Level { get; }

    public string Title { get; }

    public string Description { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && _owner.Level != Level)
        {
            _owner.Level = Level;
        }
    }

    internal void Sync(ThreatLevel selected) => IsSelected = selected == Level;
}

/// <summary>Darstellung eines Punkts: Kreis (grün/rot/gelb/grau), Etikett, Satz, Knopf, Anleitung.</summary>
public enum SecurityCheckDot
{
    Ok,
    Critical,
    Warning,
    Grey,
    Unknown,
}

/// <summary>Eine Zeile des Sicherheits-Checks.</summary>
public sealed partial class SecurityCheckRowViewModel : ObservableObject
{
    private readonly SecurityCheckViewModel _owner;

    internal SecurityCheckRowViewModel(SecurityCheckViewModel owner, SecurityCheckDefinition definition, bool canOpen)
    {
        _owner = owner;
        Id = definition.Id;
        Target = definition.Target;
        Howto = definition.Howto;
        OpenLabel = definition.OpenLabel;
        CanOpen = canOpen;
        _title = definition.Title;
        RecommendedTag = definition.RecommendedTag;
    }

    public SecurityCheckId Id { get; }

    public SettingsTarget Target { get; }

    public string? Howto { get; }

    public bool HasHowto => !string.IsNullOrEmpty(Howto) && !IsToggle;

    public string OpenLabel { get; }

    /// <summary>„Einstellung öffnen“ nur, wenn es das Ziel auf diesem PC gibt (sonst gilt die Anleitung).</summary>
    public bool CanOpen { get; }

    public bool ShowOpenButton => Target != SettingsTarget.None;

    /// <summary>Die Lumora-Option hat statt Knopf einen Schalter.</summary>
    public bool IsToggle => Id == SecurityCheckId.ExitAfterLock;

    private string RecommendedTag { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private SecurityCheckStatus _status;

    [ObservableProperty]
    private SecurityCheckDot _dot = SecurityCheckDot.Unknown;

    [ObservableProperty]
    private SymbolRegular _symbol = SymbolRegular.Question24;

    /// <summary>Etikett neben dem Titel; null = keins.</summary>
    [ObservableProperty]
    private string? _tag;

    /// <summary>„Critical“ (rot), „Recommended“ (gelb) oder „Neutral“ (grau).</summary>
    [ObservableProperty]
    private string _tagKind = "Neutral";

    [ObservableProperty]
    private bool _isHowtoOpen;

    [ObservableProperty]
    private bool _isVisible = true;

    public bool IsFulfilled => Status == SecurityCheckStatus.Fulfilled;

    internal bool HideIfFulfilled
    {
        set => IsVisible = !(value && IsFulfilled);
    }

    [RelayCommand]
    private void ToggleHowto() => IsHowtoOpen = !IsHowtoOpen;

    [RelayCommand]
    private void Open() => _owner.Open(this);

    internal void Update(SecurityCheckFinding finding, ThreatLevel level, string title, bool hideFulfilled)
    {
        Title = title;
        Detail = finding.Detail;
        Status = finding.Status;
        OnPropertyChanged(nameof(IsFulfilled));
        var weight = SecurityCheckRating.WeightOf(finding.Id, level);
        var counted = SecurityCheckRating.Counts(finding, level);
        (Dot, Symbol) = finding.Status switch
        {
            SecurityCheckStatus.Fulfilled => (SecurityCheckDot.Ok, SymbolRegular.Checkmark24),
            SecurityCheckStatus.NotApplicable => (SecurityCheckDot.Grey, SymbolRegular.Subtract24),
            SecurityCheckStatus.Unknown => (SecurityCheckDot.Unknown, SymbolRegular.Question24),
            _ when !counted => (SecurityCheckDot.Grey, SymbolRegular.Info24),
            _ when weight == SecurityCheckWeight.Critical => (SecurityCheckDot.Critical, SymbolRegular.Dismiss24),
            _ => (SecurityCheckDot.Warning, SymbolRegular.Warning24),
        };

        if (IsToggle)
        {
            (Tag, TagKind) = ("Lumora-Einstellung", "Neutral");
        }
        else if (finding.Status is SecurityCheckStatus.Fulfilled or SecurityCheckStatus.NotApplicable)
        {
            (Tag, TagKind) = (null, "Neutral");
        }
        else if (!counted)
        {
            (Tag, TagKind) = ("Zusatz", "Neutral");
        }
        else
        {
            (Tag, TagKind) = (RecommendedTag, weight == SecurityCheckWeight.Critical ? "Critical" : "Recommended");
        }

        HideIfFulfilled = hideFulfilled;
    }
}
