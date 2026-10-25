using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Services.Usage;
using Lumora.Shell;

namespace Lumora.Usage;

/// <summary>
/// Karte „Nutzung lernen“ in Einstellungen › Storys (ADR-0072, Mockup 20): Schalter „Nutzung lernen“ (Standard an) und
/// „Vergessene Schätze anbieten“, Zahlen zum Gelernten, „Gelernte Nutzung löschen…“ mit Rückfrage. Alles je Katalog
/// (die versteckte Bibliothek hat ihre eigene Karte mit eigenen Daten).
/// </summary>
public sealed partial class UsageSettingsViewModel : ObservableObject
{
    internal const string EraseTitle = "Gelernte Nutzung löschen?";

    private readonly IUsageService _usage;
    private readonly IDialogService? _dialogs;
    private readonly ILogger<UsageSettingsViewModel> _logger;
    private bool _loading;

    public UsageSettingsViewModel(IUsageService usage, ILogger<UsageSettingsViewModel> logger, IDialogService? dialogs = null)
    {
        _usage = usage;
        _logger = logger;
        _dialogs = dialogs;
    }

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _forgottenEnabled = true;
    [ObservableProperty] private UsageSummary _summary = UsageSummary.Empty;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isLoaded;

    public string FilesText => Summary.Files.ToString("N0", CultureInfo.CurrentCulture);

    public string DaysText => Summary.Days.ToString("N0", CultureInfo.CurrentCulture);

    public string LovedText => Summary.Loved.ToString("N0", CultureInfo.CurrentCulture);

    public string SinceText => Summary.Since is { } since ? since.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) : "–";

    public bool HasData => !Summary.IsEmpty;

    /// <summary>Text der Rückfrage (Tests, Rauchtest).</summary>
    public static string EraseMessage(UsageSummary summary)
        => $"Lumora vergisst alles, was es in diesem Katalog über Ihre Nutzung gelernt hat: {summary.Files.ToString("N0", CultureInfo.CurrentCulture)} "
           + (summary.Files == 1 ? "Foto" : "Fotos") + $" an {summary.Days.ToString("N0", CultureInfo.CurrentCulture)} "
           + (summary.Days == 1 ? "Tag" : "Tagen")
           + ". Highlights, „Lieblinge“ und „Vergessene Schätze“ beruhen danach wieder nur auf Güte, Favoriten und Sternen. "
           + "Favoriten, Sterne, Alben und Storys bleiben unverändert. Das lässt sich nicht rückgängig machen.";

    partial void OnSummaryChanged(UsageSummary value)
    {
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(DaysText));
        OnPropertyChanged(nameof(LovedText));
        OnPropertyChanged(nameof(SinceText));
        OnPropertyChanged(nameof(HasData));
        EraseCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadAsync()
    {
        try
        {
            var settings = await _usage.GetSettingsAsync().ConfigureAwait(true);
            var summary = await _usage.GetSummaryAsync().ConfigureAwait(true);
            _loading = true;
            try
            {
                IsEnabled = settings.Enabled;
                ForgottenEnabled = settings.ForgottenEnabled;
                Summary = summary;
            }
            finally
            {
                _loading = false;
            }

            IsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellungen › Nutzung: nicht lesbar");
            StatusText = "Nutzung konnte nicht gelesen werden: " + ex.Message;
        }
    }

    partial void OnIsEnabledChanged(bool value) => Save();

    partial void OnForgottenEnabledChanged(bool value) => Save();

    private void Save()
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        _ = SaveAsync(new UsageSettings(IsEnabled, ForgottenEnabled));
    }

    private async Task SaveAsync(UsageSettings settings)
    {
        try
        {
            await _usage.SaveSettingsAsync(settings).ConfigureAwait(true);
            StatusText = settings.Enabled ? "Gespeichert — Lumora lernt aus der Nutzung." : "Gespeichert — Lumora lernt nichts mehr und verwendet das Gelernte nicht (bleibt bis „löschen“).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellungen › Nutzung: nicht gespeichert");
            StatusText = "Nicht gespeichert: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasData))]
    private async Task EraseAsync()
    {
        if (_dialogs is not null
            && !await _dialogs.ConfirmAsync(EraseTitle, EraseMessage(Summary), "Löschen", isDestructive: true).ConfigureAwait(true))
        {
            return;
        }

        await EraseConfirmedAsync().ConfigureAwait(true);
    }

    /// <summary>Löschen ohne Rückfrage (nach Bestätigung; Tests, Rauchtest).</summary>
    public async Task EraseConfirmedAsync()
    {
        try
        {
            var files = await _usage.EraseAsync().ConfigureAwait(true);
            StatusText = $"Gelernte Nutzung gelöscht ({files.ToString("N0", CultureInfo.CurrentCulture)} {(files == 1 ? "Foto" : "Fotos")}).";
            Summary = await _usage.GetSummaryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gelernte Nutzung: nicht gelöscht");
            StatusText = "Nicht gelöscht: " + ex.Message;
        }
    }
}
