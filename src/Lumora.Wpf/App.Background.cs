using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Lumora.AppConnect;
using Lumora.Services.Background;
using Lumora.Shell;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumora;

/// <summary>
/// App-Verbindung Stufe 6 (ADR-0073, Vertrag Abschnitt 9): Hintergrundbetrieb. Lumora startet mit
/// <c>--background --started-by &lt;app&gt;:&lt;pid&gt;</c> ohne Fenster und ohne Hintergrunddienste (kein Scan, keine Gesichter, keine
/// Motive, keine Grafikkarte) — nur Katalog und Schnittstelle. Schließt der Benutzer das Fenster, während eine App eine
/// Halte-Zusage hat, läuft Lumora ohne Fenster weiter und pausiert alle Hintergrund-Aufgaben. Ohne Fenster beendet es sich
/// selbst nach den Regeln aus <see cref="BackgroundLifetime"/>. „Lumora öffnen“ (zweiter Start, Infobereich) holt das Fenster im
/// selben Prozess und startet dabei die Hintergrunddienste, falls noch nicht geschehen.
/// </summary>
public partial class App
{
    private AppConnectSettingsStore? _appConnectSettings;
    private LeaseRegistry? _leases;
    private BackgroundLifetime? _lifetime;
    private AppConnectServer? _appConnectServer;
    private TourSync? _tourSync;
    private BackgroundTrayIcon? _tray;
    private DispatcherTimer? _trayTimer;
    private bool _windowServicesStarted;
    private bool _pausedForBackground;
    private bool _exiting;

    /// <summary>Start mit <c>--background</c> (aus <see cref="Program"/>); false = normaler Start.</summary>
    internal static bool BackgroundRequested { get; set; }

    /// <summary>Abfrager aus <c>--started-by</c>; null, wenn es fehlt oder ungültig ist.</summary>
    internal static BackgroundLaunch? BackgroundStart { get; set; }

    /// <summary>Kopplungen, Schalter und Halte-Zusagen einmal je Start anlegen (auch vor dem Katalog, für den Hintergrundstart).</summary>
    private void PrepareAppConnect()
    {
        if (AppConnectStart is not { Participation: not AppConnectParticipation.Off } || AppConnections is not null)
        {
            return;
        }

        var folder = Path.Combine(_paths.AppDataRoot, "appconnect");
        // Kopplungen (Stufe 2): Geheimnis nur DPAPI-verschlüsselt unter <App-Wurzel>\appconnect
        AppConnections = new ConnectionStore(Path.Combine(folder, "connections.json"), new DpapiSecretProtector());
        _appConnectSettings = new AppConnectSettingsStore(Path.Combine(folder, "settings.json"));
        _appConnectSettings.Changed += (_, _) => Dispatcher.BeginInvoke(OnAppConnectSettingsChangedAsync);
        _leases = new LeaseRegistry();
        _lifetime = new BackgroundLifetime(_leases, () => _appConnectServer?.LastRequestUtc,
            logger: new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger).CreateLogger("Lumora.AppConnect"));
        _lifetime.ExitRequested += _ => Dispatcher.BeginInvoke(ExitFromBackground);
    }

    /// <summary>Warum ein Hintergrundstart nicht erlaubt ist; null = erlaubt.</summary>
    private string? BackgroundRefusal()
    {
        PrepareAppConnect();
        return BackgroundStart is not { } launch ? "--started-by fehlt oder ist ungültig"
            : _appConnectSettings is null || AppConnections is null ? "App-Verbindung ist in diesem Start aus"
            : launch.Refusal(_appConnectSettings.Current, AppConnections);
    }

    /// <summary>Schließen des Hauptfensters: mit gültiger Halte-Zusage im Hintergrund weiterlaufen (Abschnitt 9).</summary>
    private void AttachMainWindow(Window window) => window.Closing += OnMainWindowClosing;

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _appConnect is null || _leases?.HasActive != true || sender is not Window window)
        {
            return;
        }

        e.Cancel = true;
        _ = MoveToBackgroundAsync(window);
    }

    private async Task MoveToBackgroundAsync(Window window)
    {
        try
        {
            await CloseSecondaryWindowsAsync().ConfigureAwait(true);
            window.Hide();
            EnterBackground(null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "App-Verbindung: Wechsel in den Hintergrund fehlgeschlagen");
        }
    }

    /// <summary>Ohne Fenster weiterlaufen: Modus „background“, Hintergrund-Aufgaben pausieren, ggf. Symbol im Infobereich.</summary>
    private void EnterBackground(BackgroundLaunch? launch)
    {
        if (_lifetime is null || _appConnect is null)
        {
            return;
        }

        _lifetime.EnterBackground(launch);
        _appConnect.Mode = AppConnectHost.BackgroundMode;
        if (_windowServicesStarted && _host.Services.GetService<IBackgroundPlanner>() is { AllPaused: false } planner)
        {
            planner.SetAllPaused(true); // schlank im Hintergrund: kein Datum, keine Orte, keine Gesichter, keine Motive
            _pausedForBackground = true;
        }

        Log.Information("App-Verbindung: Lumora läuft ohne Fenster weiter ({Count} Abfrager)", _lifetime.Requesters.Count);
        UpdateTray();
    }

    /// <summary>Fenster ist wieder da: Modus „window“, Hintergrund-Aufgaben weiter, Symbol weg.</summary>
    private void LeaveBackground()
    {
        if (_lifetime is not { IsBackground: true })
        {
            return;
        }

        _lifetime.EnterWindow();
        if (_appConnect is not null)
        {
            _appConnect.Mode = AppConnectHost.WindowMode;
        }

        if (_pausedForBackground)
        {
            _pausedForBackground = false;
            _host.Services.GetService<IBackgroundPlanner>()?.SetAllPaused(false);
        }

        UpdateTray();
    }

    /// <summary>„Lumora öffnen“ (zweiter Start, Infobereich): Fenster im selben Prozess, beim ersten Mal mit allen Diensten.</summary>
    private async void ShowMainWindow(bool connectedApps = false)
    {
        try
        {
            if (MainWindow is not Lumora.MainWindow window)
            {
                // Nach einem Hintergrundstart gibt es noch kein Hauptfenster (ein Kopplungsdialog kann Application.MainWindow sein)
                await StartWindowServicesAsync().ConfigureAwait(true);
                window = _host.Services.GetRequiredService<MainWindow>();
                MainWindow = window;
                AttachMainWindow(window);
                ShutdownMode = ShutdownMode.OnLastWindowClose;
                window.Show();
                Log.Information("Hauptfenster aus dem Hintergrund geöffnet");
            }
            else
            {
                BringMainWindowToFront();
            }

            LeaveBackground();
            _ = RequestToursAfterStartAsync();
            if (connectedApps)
            {
                _host.Services.GetRequiredService<INavigationService>().NavigateTo(NavigationTags.SettingsConnectedApps);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hauptfenster konnte nicht geöffnet werden");
        }
    }

    /// <summary>Hintergrunddienste einmal je Sitzung (Scan, Gesichter, Motive …) — nach einem Hintergrundstart erst mit dem Fenster.</summary>
    private async Task StartWindowServicesAsync()
    {
        if (_windowServicesStarted)
        {
            return;
        }

        _windowServicesStarted = true;
        await StartBackgroundServicesAsync().ConfigureAwait(true);
    }

    /// <summary>Schalter geändert: Schnittstelle an/aus, Symbol im Infobereich.</summary>
    private async void OnAppConnectSettingsChangedAsync()
    {
        try
        {
            var settings = _appConnectSettings?.Current;
            if (settings is { Enabled: true } && _appConnect is null)
            {
                await StartAppConnectAsync().ConfigureAwait(true);
            }
            else if (settings is { Enabled: false } && _appConnect is { } host)
            {
                _appConnect = null;
                _appConnectServer = null;
                _host.Services.GetService<Lumora.Services.Places.Tracks.ExternalTrackSyncHub>()?.Attach(null); // fragt keine App mehr
                await host.DisposeAsync().ConfigureAwait(true);
                Log.Information("App-Verbindung: aus (Schalter) — Schnittstelle geschlossen");
                if (_lifetime?.IsBackground == true)
                {
                    ExitFromBackground(); // ohne Schnittstelle braucht Lumora im Hintergrund niemand
                }
            }

            UpdateTray();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "App-Verbindung: Schalter konnte nicht angewendet werden");
        }
    }

    /// <summary>Symbol im Infobereich: nur im Hintergrund und nur, wenn der Benutzer es eingeschaltet hat (Standard aus).</summary>
    private void UpdateTray()
    {
        var show = _lifetime is { IsBackground: true } && _appConnectSettings?.Current is { Enabled: true, TrayIcon: true } && !_exiting;
        if (!show)
        {
            _tray?.Hide();
            _trayTimer?.Stop();
            return;
        }

        _tray ??= new BackgroundTrayIcon(() => ShowMainWindow(), () => ShowMainWindow(connectedApps: true), ExitNow);
        var names = _lifetime!.Requesters.Select(id => AppConnections?.Find(id)?.DisplayName ?? id).ToList();
        _tray.Show(BackgroundTrayIcon.TextFor(names, _lifetime.Since));
        if (_trayTimer is null)
        {
            // Abfrager kommen und gehen (Halte-Zusagen) — Text gelegentlich nachziehen
            _trayTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = BackgroundLifetime.CheckInterval };
            _trayTimer.Tick += (_, _) => UpdateTray();
        }

        _trayTimer.Start();
    }

    /// <summary>Hintergrund-Regeln sagen „niemand braucht Lumora mehr“ (Abschnitt 9).</summary>
    private void ExitFromBackground()
    {
        if (_lifetime?.IsBackground == true)
        {
            ExitNow();
        }
    }

    /// <summary>Beenden ohne Rückfrage (Infobereich „Beenden“ bzw. Hintergrund-Ende); das Fenster wird nicht mehr festgehalten.</summary>
    private void ExitNow()
    {
        _exiting = true;
        UpdateTray();
        Shutdown(0);
    }

    /// <summary>
    /// Touren verbundener Apps kurz nach dem Start nachfragen (Stufe 7) — nur bei Apps, die schon laufen; im Hintergrundbetrieb
    /// nicht (schlank), dort erst mit dem Fenster.
    /// </summary>
    private async Task RequestToursAfterStartAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            _host.Services.GetService<Lumora.Services.Places.Tracks.ExternalTrackSyncHub>()?.Request(mayStartApps: false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "App-Verbindung: Touren nach dem Start nicht nachgefragt");
        }
    }

    /// <summary>Beim Beenden: Symbol und Takt weg.</summary>
    private void DisposeBackground()
    {
        _trayTimer?.Stop();
        _tray?.Dispose();
        _tray = null;
        _lifetime?.Dispose();
    }
}
