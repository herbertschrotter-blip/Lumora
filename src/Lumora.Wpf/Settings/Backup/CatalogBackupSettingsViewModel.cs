using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Database.Backup;
using Lumora.Services.Infrastructure;
using Lumora.Settings.Info;
using Lumora.Shell;

namespace Lumora.Settings.Backup;

/// <summary>Eine Zeile der Sicherungsliste: Datum, Version, Größe, Anlass.</summary>
public sealed class CatalogBackupRow(CatalogBackupInfo info, bool isScheduled)
{
    public CatalogBackupInfo Info { get; } = info;

    public string DateText => Info.CreatedLocal.ToString("dd.MM.yyyy HH:mm");

    public string VersionText => $"Version {Info.Version}";

    public string SizeText => Formatting.Bytes(Info.SizeBytes);

    public string KindText => Info.Kind switch
    {
        CatalogBackupKind.Manual => "von Hand",
        CatalogBackupKind.BeforeRestore => "vor Wiederherstellung",
        _ => "vor Aktualisierung",
    };

    /// <summary>Für den nächsten Start zum Wiederherstellen vorgemerkt.</summary>
    public bool IsScheduled { get; } = isScheduled;
}

/// <summary>
/// Karte „Katalog-Sicherung“ in Einstellungen › Speicher &amp; Wartung (ADR-0069): „Katalog jetzt sichern“, Liste der
/// Sicherungen (Datum, Version, Größe), „Ordner öffnen“ und „Wiederherstellen…“ — nur nach Rückfrage; Lumora merkt die
/// Sicherung vor, beendet sich und ersetzt den Katalog beim nächsten Start. Vorschaubilder werden nicht gesichert.
/// In einer versteckten Bibliothek liegen die Sicherungen im Container (<c>.lumora\backups</c>).
/// </summary>
public sealed partial class CatalogBackupSettingsViewModel : ObservableObject
{
    private readonly ICatalogBackupService _backup;
    private readonly IDialogService _dialogs;
    private readonly IInfoActions _actions;
    private readonly IAppShutdownService _shutdown;
    private readonly ILogger<CatalogBackupSettingsViewModel> _logger;
    private readonly IDatabaseAccessGate? _gate;

    public CatalogBackupSettingsViewModel(
        ICatalogBackupService backup,
        IDialogService dialogs,
        IInfoActions actions,
        IAppShutdownService shutdown,
        ILogger<CatalogBackupSettingsViewModel> logger,
        ILibraryContextFactory? factory = null,
        ICatalogContext? catalog = null)
    {
        _backup = backup;
        _dialogs = dialogs;
        _actions = actions;
        _shutdown = shutdown;
        _logger = logger;
        _gate = factory as IDatabaseAccessGate;
        IsHidden = catalog?.IsHidden == true;
    }

    public ObservableCollection<CatalogBackupRow> Backups { get; } = [];

    public bool IsHidden { get; }

    public string BackupFolder => _backup.BackupFolder;

    /// <summary>Erklärung unter dem Titel.</summary>
    public string ExplanationText => IsHidden
        ? "Gesichert wird der Katalog dieser versteckten Bibliothek (Bewertungen, Tags, Alben, Personen, Orte …) — nur im Container (.lumora\\backups). Vorschaubilder werden nicht gesichert: Lumora erzeugt sie bei Bedarf neu. Die Fotos selbst sichert diese Funktion nicht."
        : "Gesichert wird der Katalog (Bewertungen, Tags, Alben, Personen, Orte …). Vor jeder Aktualisierung des Katalogs durch eine neue Version legt Lumora automatisch eine Sicherung an (die letzten 5 bleiben). Vorschaubilder werden nicht gesichert: Lumora erzeugt sie bei Bedarf neu. Die Fotos selbst sichert diese Funktion nicht.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackupNowCommand), nameof(RestoreCommand), nameof(CancelRestoreCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _statusIsWarning;

    /// <summary>„Wiederherstellung beim nächsten Start vorgemerkt: …“; null = nichts vorgemerkt.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRestoreCommand))]
    private string? _scheduledRestoreText;

    public bool HasBackups => Backups.Count > 0;

    /// <summary>Liste und Vormerkung neu lesen (beim Anzeigen der Seite).</summary>
    public Task RefreshAsync()
    {
        try
        {
            var scheduled = _backup.ScheduledRestore;
            var list = _backup.List();
            Backups.Clear();
            foreach (var info in list)
            {
                Backups.Add(new CatalogBackupRow(info, scheduled is not null && string.Equals(info.FileName, scheduled.FileName, StringComparison.OrdinalIgnoreCase)));
            }

            ScheduledRestoreText = scheduled is null
                ? null
                : $"Beim nächsten Start wird der Katalog aus der Sicherung vom {scheduled.CreatedLocal:dd.MM.yyyy HH:mm} (Version {scheduled.Version}) wiederhergestellt.";
            if (StatusText is null && _backup.LastRestoreProblem is { } problem)
            {
                SetStatus($"Wiederherstellung beim Start nicht ausgeführt: {problem}", warning: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Katalog-Sicherungen nicht lesbar");
            SetStatus($"Sicherungen nicht lesbar: {ex.Message}", warning: true);
        }

        OnPropertyChanged(nameof(HasBackups));
        RestoreCommand.NotifyCanExecuteChanged(); // Knöpfe der neuen Zeilen (CommandParameter erst nach dem ersten Abfragen gesetzt)
        return Task.CompletedTask;
    }

    private bool CanBackupNow() => !IsBusy;

    /// <summary>„Katalog jetzt sichern“; liefert die neue Sicherung (Rauchtest, Tests) oder null bei Fehler.</summary>
    [RelayCommand(CanExecute = nameof(CanBackupNow))]
    public async Task<CatalogBackupInfo?> BackupNowAsync()
    {
        if (_gate?.IsSuspended == true)
        {
            SetStatus("Der Katalog ist gerade gesperrt (Umzug) — bitte später sichern.", warning: true);
            return null;
        }

        IsBusy = true;
        SetStatus("Katalog wird gesichert …", warning: false);
        try
        {
            var info = await _backup.CreateBackupAsync(CatalogBackupKind.Manual).ConfigureAwait(true);
            SetStatus($"Gesichert: {info.CreatedLocal:dd.MM.yyyy HH:mm} · {Formatting.Bytes(info.SizeBytes)}", warning: false);
            return info;
        }
        catch (CatalogBackupException ex)
        {
            _logger.LogWarning(ex, "Katalog-Sicherung von Hand fehlgeschlagen");
            SetStatus(ex.Message, warning: true);
            return null;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            _actions.OpenFolder(_backup.BackupFolder);
        }
        catch (Exception ex)
        {
            SetStatus($"Ordner konnte nicht geöffnet werden: {ex.Message}", warning: true);
        }
    }

    private bool CanRestore(CatalogBackupRow? row) => !IsBusy && row is not null;

    /// <summary>Rückfrage → vormerken → Lumora beendet sich; der Austausch passiert beim nächsten Start.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync(CatalogBackupRow? row)
    {
        if (row is null)
        {
            return;
        }

        IsBusy = true; // keine zweite Sicherung/Wiederherstellung, solange die Rückfrage offen ist
        try
        {
            await RestoreCoreAsync(row).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreCoreAsync(CatalogBackupRow row)
    {
        var where = IsHidden ? "dieser versteckten Bibliothek" : "der Bibliothek";
        var confirmed = await _dialogs.ConfirmAsync(
            "Katalog wiederherstellen?",
            $"Der Katalog {where} wird durch die Sicherung vom {row.DateText} ({row.VersionText}) ersetzt.\n\n" +
            "Lumora beendet sich jetzt und tauscht den Katalog beim nächsten Start aus. Der aktuelle Katalog bleibt als Sicherung " +
            "„vor Wiederherstellung“ erhalten. Änderungen seit der Sicherung (Bewertungen, Tags, Alben, Personen …) fehlen danach; " +
            "die Fotos selbst bleiben unberührt. Die Vorschaubilder werden verworfen und entstehen bei Bedarf neu.",
            "Wiederherstellen und beenden",
            isDestructive: true).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            _backup.ScheduleRestore(row.Info);
        }
        catch (Exception ex) when (ex is CatalogBackupException or System.IO.IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Wiederherstellung ließ sich nicht vormerken");
            SetStatus(ex.Message, warning: true);
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
        _logger.LogInformation("Wiederherstellung vorgemerkt — Lumora wird beendet");
        _shutdown.Shutdown();
    }

    private bool CanCancelRestore() => !IsBusy && ScheduledRestoreText is not null;

    [RelayCommand(CanExecute = nameof(CanCancelRestore))]
    private async Task CancelRestoreAsync()
    {
        _backup.CancelScheduledRestore();
        SetStatus("Wiederherstellung abgebrochen.", warning: false);
        await RefreshAsync().ConfigureAwait(true);
    }

    private void SetStatus(string text, bool warning)
    {
        StatusText = text;
        StatusIsWarning = warning;
    }
}
