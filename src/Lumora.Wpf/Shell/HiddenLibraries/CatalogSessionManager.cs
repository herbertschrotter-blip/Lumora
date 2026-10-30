using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Scanning;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>
/// Baut die Katalog-Sitzungen (ADR-0049) — umgesetzt von <c>App</c>: aktuellen DI-Container samt Fenstern, Hintergrund-
/// diensten, DB-Verbindungen und Logger vollständig schließen und einen neuen für den Zielkatalog starten.
/// </summary>
public interface ICatalogHostSwitcher
{
    /// <summary>Dienste der aktiven Sitzung (wechseln bei jedem Katalogwechsel).</summary>
    IServiceProvider CurrentServices { get; }

    /// <summary>
    /// Wechselt in den versteckten Katalog <paramref name="hiddenPaths"/>; <paramref name="prepare"/> läuft nach der
    /// Migration und vor dem Hauptfenster (Bibliothek anlegen). Scheitert der Start, ist danach wieder der normale
    /// Katalog aktiv und die Ausnahme wird weitergereicht.
    /// </summary>
    Task SwitchToHiddenAsync(AppDataPaths hiddenPaths, Func<IServiceProvider, Task>? prepare);

    /// <summary>Zurück zum normalen Katalog; die versteckte Sitzung ist danach vollständig geschlossen.</summary>
    Task SwitchToNormalAsync();

    /// <summary>true, solange ein Scan oder Datei-Vorgang der aktiven Sitzung läuft (dann wartet das automatische Zusperren).</summary>
    Task<bool> IsCatalogBusyAsync();

    /// <summary>Lightbox und Dialoge schließen, solange die Dienste noch leben; false, wenn ein Fenster ablehnt.</summary>
    Task<bool> CloseSecondaryWindowsAsync();

    /// <summary>
    /// Sicherheits-Check (ADR-0064), Option „Lumora nach dem Zusperren ganz beenden“: beendet Lumora, statt die normale
    /// Bibliothek aufzubauen. <c>App.OnExit</c> schließt dann die versteckte Sitzung wie beim normalen Beenden
    /// (Sitzung, Protokoll im Container, ggf. Aushängen).
    /// </summary>
    void ShutdownAfterLock()
    {
        // Ohne Anwendung (sollte nie vorkommen) lieber normal zusperren als die Sitzung offen stehen zu lassen
        var app = Application.Current ?? throw new InvalidOperationException("Keine laufende Anwendung zum Beenden.");
        app.Shutdown();
    }
}

/// <summary>Der Container ist unter einem anderen Laufwerksbuchstaben eingehängt als beim Anlegen der Bibliothek.</summary>
public sealed class HiddenLibraryMovedException(string previousDrive)
    : InvalidOperationException(
        $"Der Container war beim Anlegen als {previousDrive} eingehängt. Bitte wieder unter {previousDrive} einhängen (in VeraCrypt den Laufwerksbuchstaben wählen) und erneut öffnen.");

/// <summary>Ergebnis von Anlegen/Öffnen/Zusperren; <see cref="Message"/> für den Benutzer (Fehler bzw. Hinweis).</summary>
public sealed record HiddenSessionResult(bool Success, string? Message)
{
    public static HiddenSessionResult Ok(string? hint = null) => new(true, hint);

    public static HiddenSessionResult Fail(string message) => new(false, message);
}

/// <summary>Einstellungen des Prozesses für versteckte Bibliotheken.</summary>
/// <param name="Enabled">Startschalter <c>_hidden</c> (bzw. Rauchtest-Simulation).</param>
/// <param name="NormalAppDataRoot">App-Wurzel des normalen Katalogs (verbotener Ort, Modellordner).</param>
/// <param name="NormalDataFolder">Datenordner des normalen Katalogs (verbotener Ort).</param>
/// <param name="MaxFolderDepth">Ebenen unter der Laufwerkswurzel beim Anlegen (1; nur der Rauchtest mehr).</param>
/// <param name="UseUiHooks">Eingaben über den WPF-InputManager beobachten und per Timer prüfen (Tests: aus).</param>
public sealed record HiddenModeOptions(
    bool Enabled,
    string NormalAppDataRoot,
    string NormalDataFolder,
    int MaxFolderDepth = 1,
    bool UseUiHooks = true);

/// <summary>Sitzungen und Untätigkeit der versteckten Bibliotheken (ADR-0049); eine Instanz je Prozess, in jedem Container registriert.</summary>
public interface ICatalogSessionManager
{
    bool IsHiddenModeEnabled { get; }

    bool IsHiddenSessionActive { get; }

    /// <summary>Gerade geöffnete versteckte Bibliothek (nur im Speicher).</summary>
    HiddenLibraryInfo? ActiveLibrary { get; }

    HiddenLibraryManifest? ActiveManifest { get; }

    IContainerTools Containers { get; }

    IdleLockMonitor Idle { get; }

    /// <summary>Erster Scan nach Anlegen bzw. nach verlorenem Katalog (Rauchtest wartet darauf); sonst null.</summary>
    Task? InitialScan { get; }

    Task<IReadOnlyList<HiddenLibraryInfo>> FindAsync(CancellationToken ct = default);

    /// <summary>Laufwerke für „Versteckte Bibliothek anlegen…“ (eingehängte lokale Laufwerke außer dem Systemlaufwerk).</summary>
    IReadOnlyList<string> GetCandidateDrives();

    /// <summary>null = Ordner geeignet, sonst die Meldung.</summary>
    Task<string?> ValidateNewFolderAsync(string folder);

    Task<bool> VerifyPasswordAsync(HiddenLibraryInfo library, string password);

    Task<HiddenSessionResult> CreateAsync(string folder, string name, string password);

    Task<HiddenSessionResult> OpenAsync(HiddenLibraryInfo library, string password);

    /// <summary>Neues Kennwort nur mit Bestätigungsdatei im Container; öffnet danach.</summary>
    Task<HiddenSessionResult> ResetPasswordAndOpenAsync(HiddenLibraryInfo library, string newPassword);

    /// <summary>Zurück zur normalen Bibliothek, versteckter Katalog geschlossen, ggf. Container aushängen.</summary>
    Task<HiddenSessionResult> LockAsync(bool automatic = false);

    /// <summary>Sperrt, wenn die Untätigkeitszeit abgelaufen ist und nichts läuft; true = gesperrt.</summary>
    Task<bool> CheckIdleAsync();

    HiddenLibraryManifest? UpdateSettings(int autoLockMinutes, bool dismountOnLock);

    Task<ContainerCommandResult> MountContainerAsync(string containerFile, char driveLetter);

    /// <summary>BitLocker-VHDX einhängen und von Windows entsperren lassen (0.5.4) — ohne VeraCrypt.</summary>
    Task<ContainerCommandResult> MountVirtualDiskAsync(string imageFile);

    /// <summary>Beim Beenden (nach dem Schließen der Sitzung): Container aushängen, wenn eingestellt. Synchron mit Zeitgrenze.</summary>
    ContainerCommandResult? DismountAfterExit(TimeSpan timeout);

    /// <summary>Container-Datei auf ein anderes Laufwerk verschieben (Mockup 26); siehe <see cref="CatalogSessionManager"/>.</summary>
    Task<HiddenSessionResult> RelocateContainerAsync(
        Lumora.Services.HiddenLibraries.Relocation.ContainerRelocationPlan plan,
        Func<IContainerRelocationProgress> progressFactory,
        Lumora.Services.Operations.IRecycleBin? recycleBin = null)
        => Task.FromResult(HiddenSessionResult.Fail("Verschieben wird hier nicht unterstützt."));
}

/// <inheritdoc cref="ICatalogSessionManager" />
public sealed partial class CatalogSessionManager : ICatalogSessionManager
{
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(10);

    private readonly HiddenModeOptions _options;
    private readonly HiddenLibraryStore _store;
    private readonly HiddenLibraryLocator _locator;
    private readonly IDriveRootSource _drives;
    private readonly ILogger _logger;
    private ICatalogHostSwitcher? _switcher;
    private DispatcherTimer? _idleTimer;
    private bool _inputHooked;
    private int _switching;
    private bool _modeEnded;
    private HiddenLibraryInfo? _pendingExitDismount;

    public CatalogSessionManager(
        HiddenModeOptions options,
        HiddenLibraryStore store,
        IDriveRootSource drives,
        IContainerTools containers,
        TimeProvider time,
        ILogger logger)
    {
        _options = options;
        _store = store;
        _drives = drives;
        _locator = new HiddenLibraryLocator(drives);
        Containers = containers;
        _logger = logger;
        Idle = new IdleLockMonitor(time, TimeSpan.FromMinutes(HiddenLibraryManifest.DefaultAutoLockMinutes));
    }

    /// <summary>
    /// Startschalter gesetzt und seit dem Start noch nicht zugesperrt: Zusperren beendet den versteckten Modus für den
    /// Rest der Laufzeit — danach zeigt Lumora nichts mehr davon, wie ein normaler Start (0.4.14, Herbert). Wieder
    /// öffnen nur nach einem Neustart mit <c>_hidden</c>.
    /// </summary>
    public bool IsHiddenModeEnabled => _options.Enabled && !_modeEnded;

    /// <summary>Nur Rauchtest: simuliert nach dem Zusperren einen Neustart mit <c>_hidden</c> (0.4.14).</summary>
    internal void RestartHiddenModeForSmoke() => _modeEnded = false;

    public bool IsHiddenSessionActive => ActiveLibrary is not null;

    public HiddenLibraryInfo? ActiveLibrary { get; private set; }

    public HiddenLibraryManifest? ActiveManifest { get; private set; }

    public IContainerTools Containers { get; }

    public IdleLockMonitor Idle { get; }

    public Task? InitialScan { get; private set; }

    /// <summary>Wird einmal von <c>App</c> gesetzt, bevor eine Sitzung gewechselt werden kann.</summary>
    public void AttachSwitcher(ICatalogHostSwitcher switcher) => _switcher = switcher;

    private ICatalogHostSwitcher Switcher => _switcher ?? throw new InvalidOperationException("Kein Katalog-Wechsler angebunden.");

    public Task<IReadOnlyList<HiddenLibraryInfo>> FindAsync(CancellationToken ct = default)
        => IsHiddenModeEnabled ? _locator.FindAsync(ct) : Task.FromResult<IReadOnlyList<HiddenLibraryInfo>>([]);

    public IReadOnlyList<string> GetCandidateDrives()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory);
        return _drives.GetSearchRoots()
            .Where(r => !string.Equals(r, system, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<string?> ValidateNewFolderAsync(string folder)
    {
        var forbidden = new List<string> { _options.NormalAppDataRoot, _options.NormalDataFolder };
        forbidden.AddRange(HiddenLibraryFolderRules.OneDriveFolders());
        if (!IsHiddenSessionActive && _switcher?.CurrentServices.GetService<ILibraryService>() is { } libraries)
        {
            try
            {
                foreach (var library in await libraries.GetAllAsync().ConfigureAwait(true))
                {
                    forbidden.AddRange(library.Roots.Select(r => r.Path));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bibliotheksordner nicht lesbar");
            }
        }

        return HiddenLibraryFolderRules.Validate(folder, forbidden, _options.MaxFolderDepth);
    }

    public Task<bool> VerifyPasswordAsync(HiddenLibraryInfo library, string password)
        => Task.Run(() => HiddenLibraryStore.VerifyPassword(library.FolderPath, password));

    public async Task<HiddenSessionResult> CreateAsync(string folder, string name, string password)
    {
        if (!IsHiddenModeEnabled || IsHiddenSessionActive)
        {
            return HiddenSessionResult.Fail("Eine versteckte Bibliothek kann nur aus der normalen Bibliothek heraus angelegt werden.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return HiddenSessionResult.Fail("Bitte einen Namen eingeben.");
        }

        if (HiddenLibraryPassword.Validate(password, password) is { } passwordProblem)
        {
            return HiddenSessionResult.Fail(passwordProblem);
        }

        if (await ValidateNewFolderAsync(folder).ConfigureAwait(true) is { } problem)
        {
            return HiddenSessionResult.Fail(problem);
        }

        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HiddenSessionResult.Fail($"Der Ordner kann nicht angelegt werden: {ex.Message}");
        }

        var full = Path.GetFullPath(folder);
        HiddenLibraryManifest manifest;
        try
        {
            manifest = await Task.Run(() => _store.Create(full, name, password)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return HiddenSessionResult.Fail($"Die versteckte Bibliothek konnte nicht angelegt werden: {ex.Message}");
        }

        return await EnterAsync(new HiddenLibraryInfo(full, manifest.Name), manifest).ConfigureAwait(true);
    }

    public async Task<HiddenSessionResult> OpenAsync(HiddenLibraryInfo library, string password)
    {
        if (!IsHiddenModeEnabled || IsHiddenSessionActive)
        {
            return HiddenSessionResult.Fail("Bitte zuerst die geöffnete versteckte Bibliothek zusperren.");
        }

        if (!await VerifyPasswordAsync(library, password).ConfigureAwait(true))
        {
            return HiddenSessionResult.Fail("Das Kennwort ist falsch.");
        }

        var manifest = HiddenLibraryStore.TryLoad(library.FolderPath);
        return manifest is null
            ? HiddenSessionResult.Fail("Die versteckte Bibliothek ist nicht mehr erreichbar (Container ausgehängt?).")
            : await EnterAsync(library, manifest).ConfigureAwait(true);
    }

    public async Task<HiddenSessionResult> ResetPasswordAndOpenAsync(HiddenLibraryInfo library, string newPassword)
    {
        if (!IsHiddenModeEnabled || IsHiddenSessionActive)
        {
            return HiddenSessionResult.Fail("Bitte zuerst die geöffnete versteckte Bibliothek zusperren.");
        }

        if (HiddenLibraryPassword.Validate(newPassword, newPassword) is { } problem)
        {
            return HiddenSessionResult.Fail(problem);
        }

        HiddenLibraryManifest manifest;
        try
        {
            manifest = await Task.Run(() => _store.ResetPassword(library.FolderPath, newPassword)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return HiddenSessionResult.Fail(ex.Message);
        }

        return await EnterAsync(library, manifest).ConfigureAwait(true);
    }

    private async Task<HiddenSessionResult> EnterAsync(HiddenLibraryInfo library, HiddenLibraryManifest manifest)
    {
        if (Interlocked.Exchange(ref _switching, 1) != 0)
        {
            return HiddenSessionResult.Fail("Gerade läuft schon ein Wechsel.");
        }

        try
        {
            InitialScan = null;
            ActiveLibrary = library;
            ActiveManifest = manifest;
            HiddenSessionPrivacy.IsHiddenSessionActive = true;
            Idle.Timeout = TimeSpan.FromMinutes(manifest.AutoLockMinutes);
            Idle.NotifyActivity();
            await Switcher.SwitchToHiddenAsync(HiddenLibraryStore.GetCatalogPaths(library.FolderPath), sp => EnsureLibraryAsync(sp, library))
                .ConfigureAwait(true);
            StartIdleWatch();
            return HiddenSessionResult.Ok();
        }
        catch (Exception ex)
        {
            // Der Wechsler ist schon zurück im normalen Katalog; Details stehen im Protokoll im Container (falls schreibbar).
            ActiveLibrary = null;
            ActiveManifest = null;
            HiddenSessionPrivacy.IsHiddenSessionActive = false;
            return HiddenSessionResult.Fail(ex is HiddenLibraryMovedException
                ? ex.Message
                : $"Die versteckte Bibliothek konnte nicht geöffnet werden ({ex.GetType().Name}).");
        }
        finally
        {
            Volatile.Write(ref _switching, 0);
        }
    }

    /// <summary>
    /// Legt im versteckten Katalog die Bibliothek an, falls sie fehlt (neu angelegt oder Katalog verloren), und scannt.
    /// Der Katalog speichert absolute Pfade: ist der Container unter einem anderen Laufwerksbuchstaben eingehängt als beim
    /// Anlegen, bricht das Öffnen mit Hinweis ab (sonst erschiene die Bibliothek leer bzw. offline).
    /// </summary>
    private async Task EnsureLibraryAsync(IServiceProvider services, HiddenLibraryInfo library)
    {
        var libraries = services.GetRequiredService<ILibraryService>();
        var all = await libraries.GetAllAsync().ConfigureAwait(true);
        if (all.Count > 0)
        {
            if (!all.Any(l => l.Roots.Any(r => AppDataPaths.PathsEqual(r.Path, library.FolderPath))))
            {
                var previous = all[0].ArchiveRoot?.Path ?? all[0].RootPath;
                throw new HiddenLibraryMovedException((Path.GetPathRoot(previous) ?? previous).TrimEnd('\\'));
            }

            if (await libraries.GetActiveAsync().ConfigureAwait(true) is null)
            {
                await libraries.SetActiveAsync(all[0].Id).ConfigureAwait(true);
            }

            return;
        }

        var created = await libraries.CreateAsync(library.Name, library.FolderPath).ConfigureAwait(true);
        await libraries.SetActiveAsync(created.Id).ConfigureAwait(true);
        var scan = services.GetRequiredService<IScanService>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Lumora.HiddenLibraries");
        InitialScan = Task.Run(async () =>
        {
            try
            {
                await scan.ScanAsync(created.Id, ScanMode.Fresh, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Erster Scan der versteckten Bibliothek fehlgeschlagen");
            }
        });
    }

    public async Task<HiddenSessionResult> LockAsync(bool automatic = false)
    {
        if (!IsHiddenSessionActive)
        {
            return HiddenSessionResult.Ok();
        }

        if (Interlocked.Exchange(ref _switching, 1) != 0)
        {
            return HiddenSessionResult.Fail("Gerade läuft schon ein Wechsel.");
        }

        var library = ActiveLibrary!;
        var manifest = ActiveManifest;
        try
        {
            // Lightbox und Dialoge zuerst schließen, solange die Dienste noch leben; lehnt ein Fenster ab (laufendes
            // Entpacken wird dabei abgebrochen), wartet das automatische Zusperren auf die nächste Prüfung.
            if (!await Switcher.CloseSecondaryWindowsAsync().ConfigureAwait(true) && automatic)
            {
                Volatile.Write(ref _switching, 0);
                return HiddenSessionResult.Fail("Ein Fenster ist noch beschäftigt.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Fenster vor dem Zusperren nicht geschlossen ({Type})", ex.GetType().Name);
        }

        // Sicherheits-Check (ADR-0064): „Lumora nach dem Zusperren ganz beenden“ — von Hand wie nach Untätigkeit. Die
        // Sitzung bleibt bis App.OnExit offen (PrepareExit merkt sich das Aushängen); keine normale Oberfläche dazwischen.
        if (await ShouldShutdownAfterLockAsync().ConfigureAwait(true))
        {
            StopIdleWatch();
            _modeEnded = true;
            try
            {
                Switcher.ShutdownAfterLock();
                return HiddenSessionResult.Ok(); // _switching bleibt gesetzt: kein Wechsel mehr bis zum Ende
            }
            catch (Exception ex)
            {
                _logger.LogError("Beenden nach dem Zusperren fehlgeschlagen ({Type}) — zurück zur normalen Bibliothek", ex.GetType().Name);
            }
        }

        try
        {
            StopIdleWatch();
            // Vor dem Aufbau der normalen Oberfläche: Umschalter und Einstellungen der neuen Sitzung zeigen nichts mehr
            _modeEnded = true;
            await Switcher.SwitchToNormalAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Normaler Katalog ließ sich nicht starten — nur der Typ ins (normale) Protokoll
            _logger.LogError("Zurück zur normalen Bibliothek fehlgeschlagen ({Type})", ex.GetType().Name);
        }
        finally
        {
            ActiveLibrary = null;
            ActiveManifest = null;
            InitialScan = null;
            HiddenSessionPrivacy.IsHiddenSessionActive = false;
            Volatile.Write(ref _switching, 0);
        }

        if (manifest is not { DismountOnLock: true })
        {
            return HiddenSessionResult.Ok();
        }

        var dismount = await DismountAsync(library).ConfigureAwait(true);
        if (!dismount.Success)
        {
            await ShowHintAsync(automatic ? "Automatisch zugesperrt" : "Zugesperrt", dismount.Message).ConfigureAwait(true);
        }

        return HiddenSessionResult.Ok(dismount.Success ? null : dismount.Message);
    }

    /// <summary>Einstellung aus dem versteckten Katalog (Dienste der Sitzung leben noch); nicht lesbar = normal zusperren.</summary>
    private async Task<bool> ShouldShutdownAfterLockAsync()
    {
        try
        {
            var services = _switcher?.CurrentServices;
            // Zuerst der Stand im Speicher: ein eben umgelegter Schalter gilt, auch wenn das Speichern noch läuft
            if (services?.GetService<Lumora.Services.HiddenLibraries.SecurityCheck.ISecurityCheckService>() is { HasSettings: true } check)
            {
                return check.Settings.ExitAfterLock;
            }

            if (services?.GetService<Lumora.Services.HiddenLibraries.SecurityCheck.ISecurityCheckSettingsStore>() is not { } store)
            {
                return false;
            }

            var settings = await store.LoadAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            return settings.ExitAfterLock;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Einstellung „nach dem Zusperren beenden“ nicht lesbar ({Type})", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Läuft schon eine Prüfung (z. B. vom Zeitgeber), bekommt ein zweiter Aufruf deren Ergebnis — sonst bricht der
    /// zweite am gesetzten Wechsel-Schalter ab und meldet „nicht zugesperrt“, obwohl gerade zugesperrt wird (0.4.43).
    /// </summary>
    public Task<bool> CheckIdleAsync()
    {
        if (_idleCheck is { IsCompleted: false } running)
        {
            return running;
        }

        return _idleCheck = CheckIdleCoreAsync();
    }

    private Task<bool>? _idleCheck;

    private async Task<bool> CheckIdleCoreAsync()
    {
        if (!IsHiddenSessionActive || Volatile.Read(ref _switching) != 0 || !Idle.IsExpired)
        {
            return false;
        }

        if (await Switcher.IsCatalogBusyAsync().ConfigureAwait(true))
        {
            return false; // Scan/Datei-Vorgang zuerst fertig werden lassen (kein halber Batch); nächste Prüfung folgt
        }

        var result = await LockAsync(automatic: true).ConfigureAwait(true);
        return result.Success;
    }

    public HiddenLibraryManifest? UpdateSettings(int autoLockMinutes, bool dismountOnLock)
    {
        if (ActiveLibrary is not { } library)
        {
            return null;
        }

        ActiveManifest = HiddenLibraryStore.SaveSettings(library.FolderPath, autoLockMinutes, dismountOnLock);
        Idle.Timeout = TimeSpan.FromMinutes(ActiveManifest.AutoLockMinutes);
        return ActiveManifest;
    }

    public Task<ContainerCommandResult> MountContainerAsync(string containerFile, char driveLetter)
        => Containers.MountVeraCryptAsync(containerFile, driveLetter);

    public Task<ContainerCommandResult> MountVirtualDiskAsync(string imageFile)
        => Containers.MountVirtualDiskAsync(imageFile);

    /// <summary>Für <c>App.OnExit</c>: merkt sich die aktive Bibliothek vor dem Schließen der Sitzung.</summary>
    public void PrepareExit()
    {
        StopIdleWatch();
        _pendingExitDismount = ActiveManifest is { DismountOnLock: true } ? ActiveLibrary : null;
        ActiveLibrary = null;
        ActiveManifest = null;
        HiddenSessionPrivacy.IsHiddenSessionActive = false;
    }

    public ContainerCommandResult? DismountAfterExit(TimeSpan timeout)
    {
        if (_pendingExitDismount is not { } library)
        {
            return null;
        }

        _pendingExitDismount = null;
        try
        {
            var task = Task.Run(() => DismountAsync(library));
            return task.Wait(timeout) ? task.Result : new ContainerCommandResult(false, "Aushängen dauerte zu lange.");
        }
        catch (Exception ex)
        {
            return new ContainerCommandResult(false, ex.GetType().Name);
        }
    }

    private async Task<ContainerCommandResult> DismountAsync(HiddenLibraryInfo library)
    {
        // Nachzügler der geschlossenen Sitzung (Thumbnail-Worker) geben ihre Dateien kurz danach frei → ein zweiter Versuch
        var result = await Containers.DismountAsync(library.DriveRoot).ConfigureAwait(false);
        if (!result.Success && Containers.Detect(library.DriveRoot) == ContainerKind.VeraCrypt)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            result = await Containers.DismountAsync(library.DriveRoot).ConfigureAwait(false);
        }

        return result;
    }

    private async Task ShowHintAsync(string title, string message)
    {
        try
        {
            if (_switcher?.CurrentServices.GetService<IDialogService>() is { } dialogs)
            {
                await dialogs.ShowErrorAsync(title, message).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Hinweis konnte nicht angezeigt werden ({Type})", ex.GetType().Name);
        }
    }

    // ----- Untätigkeit: Maus/Tastatur in Lumora -----

    private void StartIdleWatch()
    {
        Idle.NotifyActivity();
        if (!_options.UseUiHooks || Application.Current is null)
        {
            return;
        }

        if (!_inputHooked)
        {
            InputManager.Current.PreProcessInput += OnPreProcessInput;
            _inputHooked = true;
        }

        _idleTimer ??= new DispatcherTimer(IdleCheckInterval, DispatcherPriority.Background, OnIdleTimerTick, Application.Current.Dispatcher);
        _idleTimer.Start();
    }

    private void StopIdleWatch()
    {
        _idleTimer?.Stop();
        if (_inputHooked)
        {
            InputManager.Current.PreProcessInput -= OnPreProcessInput;
            _inputHooked = false;
        }
    }

    private async void OnIdleTimerTick(object? sender, EventArgs e)
    {
        try
        {
            await CheckIdleAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Prüfung auf Untätigkeit fehlgeschlagen ({Type})", ex.GetType().Name);
        }
    }

    private POINT _lastCursor;

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        switch (e.StagingItem.Input)
        {
            case KeyboardEventArgs:
            case MouseButtonEventArgs:
            case MouseWheelEventArgs:
                Idle.NotifyActivity();
                break;
            case MouseEventArgs:
                // WPF erzeugt auch künstliche MouseMove-Ereignisse (Layout unter dem stehenden Zeiger) — nur echte Bewegung zählt
                if (GetCursorPos(out var point) && (point.X != _lastCursor.X || point.Y != _lastCursor.Y))
                {
                    _lastCursor = point;
                    Idle.NotifyActivity();
                }

                break;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier.
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
#pragma warning restore SYSLIB1054
}
