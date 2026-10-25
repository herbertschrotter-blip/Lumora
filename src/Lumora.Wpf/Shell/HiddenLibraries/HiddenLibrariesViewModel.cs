using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Services.HiddenLibraries;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>
/// Versteckte Bibliotheken in der Oberfläche (ADR-0049): Einträge im Bibliotheks-Umschalter und der Reiter
/// „Versteckte Bibliotheken“ der Einstellungen — beides nur mit Startschalter <c>_hidden</c>. Öffnen fragt das
/// Lumora-Kennwort, Anlegen Laufwerk/Ordner/Name/Kennwort; der Wechsel selbst baut die Oberfläche neu auf.
/// </summary>
public sealed partial class HiddenLibrariesViewModel : ObservableObject
{
    private readonly ICatalogSessionManager _sessions;
    private readonly IHiddenLibraryDialogs _dialogs;
    private readonly IDialogService _messages;
    private readonly ILogger<HiddenLibrariesViewModel> _logger;
    private readonly IContainerAssistant? _assistant;

    /// <param name="assistant">Container-Assistent vor dem Anlegen (ADR-0061); null = direkt zum Anlegen-Dialog.</param>
    public HiddenLibrariesViewModel(
        ICatalogSessionManager sessions,
        IHiddenLibraryDialogs dialogs,
        IDialogService messages,
        ILogger<HiddenLibrariesViewModel> logger,
        IContainerAssistant? assistant = null)
    {
        _sessions = sessions;
        _dialogs = dialogs;
        _messages = messages;
        _logger = logger;
        _assistant = assistant;
        if (sessions.ActiveManifest is { } manifest)
        {
            _autoLockMinutes = manifest.AutoLockMinutes;
            _dismountOnLock = manifest.DismountOnLock;
        }
    }

    /// <summary>Startschalter <c>_hidden</c> gesetzt — sonst zeigt die Oberfläche nichts davon.</summary>
    public bool IsHiddenModeEnabled => _sessions.IsHiddenModeEnabled;

    public bool IsHiddenSessionActive => _sessions.IsHiddenSessionActive;

    /// <summary>Für den Reiter: Liste und Anlegen nur außerhalb einer versteckten Sitzung.</summary>
    public bool IsNormalSession => IsHiddenModeEnabled && !IsHiddenSessionActive;

    public string? ActiveLibraryName => _sessions.ActiveLibrary?.Name;

    /// <summary>„Laufwerk X: · VeraCrypt“ bzw. „Laufwerk X:“.</summary>
    public string ActiveLocationText
    {
        get
        {
            if (_sessions.ActiveLibrary is not { } library)
            {
                return string.Empty;
            }

            var drive = library.DriveRoot.TrimEnd('\\');
            return _sessions.Containers.Detect(library.DriveRoot) == ContainerKind.VeraCrypt
                ? $"Laufwerk {drive} · VeraCrypt-Container"
                : $"Laufwerk {drive}";
        }
    }

    /// <summary>Gefundene versteckte Bibliotheken (nur im Speicher, bei jedem Öffnen des Menüs neu gesucht).</summary>
    public ObservableCollection<HiddenLibraryInfo> Found { get; } = new();

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private int _autoLockMinutes = HiddenLibraryManifest.DefaultAutoLockMinutes;

    [ObservableProperty]
    private bool _dismountOnLock;

    /// <summary>Auswahl für „Automatisch zusperren nach … Minuten“ (enthält immer den gespeicherten Wert).</summary>
    public System.Collections.Generic.IReadOnlyList<int> AutoLockChoices
        => new[] { 1, 2, 5, 10, 15, 30, 60, 120, 240 }.Append(AutoLockMinutes).Distinct().Order().ToList();

    partial void OnAutoLockMinutesChanged(int value) => SaveIfActive();

    partial void OnDismountOnLockChanged(bool value) => SaveIfActive();

    private bool _saving;

    private void SaveIfActive()
    {
        if (IsHiddenSessionActive && !_saving)
        {
            SaveSettings();
        }
    }

    private DateTime _lastSearchUtc = DateTime.MinValue;

    /// <summary>
    /// Für das Umschalter-Menü: sucht nur neu, wenn die letzte Suche älter als 15 s ist (jede Suche liest die Wurzeln aller
    /// lokalen Laufwerke; schlafende Platten sollen nicht bei jedem Klick anlaufen).
    /// </summary>
    public Task RefreshIfStaleAsync()
        => DateTime.UtcNow - _lastSearchUtc > TimeSpan.FromSeconds(15) ? RefreshAsync() : Task.CompletedTask;

    /// <summary>Sucht auf allen eingehängten lokalen Laufwerken (Wurzel und eine Ebene darunter).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!IsHiddenModeEnabled || IsHiddenSessionActive)
        {
            Found.Clear();
            return;
        }

        IsSearching = true;
        try
        {
            var found = await _sessions.FindAsync().ConfigureAwait(true);
            _lastSearchUtc = DateTime.UtcNow;
            Found.Clear();
            foreach (var library in found)
            {
                Found.Add(library);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Suche nach versteckten Bibliotheken fehlgeschlagen ({Type})", ex.GetType().Name);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task OpenAsync(HiddenLibraryInfo? library)
    {
        if (library is null || !IsNormalSession)
        {
            return;
        }

        var canReset = HiddenLibraryStore.HasResetRequest(library.FolderPath);
        var answer = _dialogs.AskUnlock(library, canReset, password => _sessions.VerifyPasswordAsync(library, password));
        if (answer is null)
        {
            return;
        }

        var result = answer.IsReset
            ? await _sessions.ResetPasswordAndOpenAsync(library, answer.Password).ConfigureAwait(true)
            : await _sessions.OpenAsync(library, answer.Password).ConfigureAwait(true);
        if (!result.Success)
        {
            await ShowAsync("Versteckte Bibliothek öffnen", result.Message).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!IsNormalSession)
        {
            return;
        }

        // ADR-0061: zuerst „Container ist schon eingehängt“ oder „Neuen verschlüsselten Container erstellen“
        string? newDrive = null;
        if (_assistant is not null)
        {
            var container = _assistant.Run();
            if (container.Outcome == ContainerAssistantOutcome.Cancelled)
            {
                await ShowAsync("Versteckte Bibliothek anlegen", container.Message).ConfigureAwait(true);
                return;
            }

            newDrive = container.DriveRoot;
        }

        var answer = _dialogs.AskCreate(PreferDrive(_sessions.GetCandidateDrives(), newDrive), _sessions.ValidateNewFolderAsync);
        if (answer is null)
        {
            return;
        }

        var result = await _sessions.CreateAsync(answer.Folder, answer.Name, answer.Password).ConfigureAwait(true);
        if (!result.Success)
        {
            await ShowAsync("Versteckte Bibliothek anlegen", result.Message).ConfigureAwait(true);
        }
    }

    /// <summary>Zurück zur normalen Bibliothek; der versteckte Katalog wird vollständig geschlossen.</summary>
    [RelayCommand]
    private async Task LockAsync()
    {
        if (IsHiddenSessionActive)
        {
            await _sessions.LockAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task MountContainerAsync()
    {
        if (!IsNormalSession)
        {
            return;
        }

        var used = DriveInfoLetters();
        var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !used.Contains(c)).ToList();
        var answer = _dialogs.AskMountContainer(free);
        if (answer is null)
        {
            return;
        }

        // BitLocker-VHDX (0.5.4): Windows hängt ein und fragt das Kennwort; VeraCrypt nur für andere Containerdateien
        if (!IContainerTools.IsVirtualDiskFile(answer.ContainerFile) && !_sessions.Containers.IsVeraCryptInstalled)
        {
            await ShowAsync("Container öffnen", "VeraCrypt ist nicht installiert. Den Container bitte selbst einhängen, danach „Suchen“.").ConfigureAwait(true);
            return;
        }

        var result = IContainerTools.IsVirtualDiskFile(answer.ContainerFile)
            ? await _sessions.MountVirtualDiskAsync(answer.ContainerFile).ConfigureAwait(true)
            : await _sessions.MountContainerAsync(answer.ContainerFile, answer.DriveLetter).ConfigureAwait(true);
        if (!result.Success)
        {
            await ShowAsync("Container öffnen", result.Message).ConfigureAwait(true);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _saving = true;
        try
        {
            if (_sessions.UpdateSettings(AutoLockMinutes, DismountOnLock) is { } saved)
            {
                AutoLockMinutes = saved.AutoLockMinutes;
                DismountOnLock = saved.DismountOnLock;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ = ShowAsync("Versteckte Bibliothek", $"Einstellungen nicht gespeichert: {ex.Message}");
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>Das neue Laufwerk aus dem Container-Assistenten zuerst (der Anlegen-Dialog wählt das erste vor).</summary>
    internal static System.Collections.Generic.IReadOnlyList<string> PreferDrive(System.Collections.Generic.IReadOnlyList<string> drives, string? preferred)
    {
        if (string.IsNullOrEmpty(preferred))
        {
            return drives;
        }

        return drives.Where(d => !string.Equals(d, preferred, StringComparison.OrdinalIgnoreCase)).Prepend(preferred).ToList();
    }

    private static System.Collections.Generic.HashSet<char> DriveInfoLetters()
        => DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();

    private Task ShowAsync(string title, string? message)
        => string.IsNullOrEmpty(message) ? Task.CompletedTask : _messages.ShowErrorAsync(title, message);
}
