using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.AppConnect;

/// <summary>Wie Lumora an der App-Verbindung teilnimmt (ADR-0073).</summary>
public enum AppConnectParticipation
{
    /// <summary>Gar nicht: Start mit <c>_hidden</c> — kein Mutex, keine Anmeldedatei, keine Schnittstelle (Herbert, Frage 1a).</summary>
    Off,

    /// <summary>Testlauf mit umgelenkter Wurzel (<c>LUMORA_APPDATA</c>): Schnittstelle und Anmeldedatei unter der Testwurzel, kein Mutex.</summary>
    Isolated,

    /// <summary>Normaler Start (Standard oder portabel): eine Instanz je Benutzer, Anmeldedatei unter <c>%LocalAppData%</c>.</summary>
    Normal,
}

/// <summary>
/// Schnittstelle und Anmeldedatei zusammen (Abschnitte 4 und 9): erst lauschen, dann die Anmeldedatei mit <c>port</c>
/// schreiben (Bereitschaft); beim Beenden <c>running: false</c> und <c>port: null</c>.
/// </summary>
public sealed class AppConnectHost : IAsyncDisposable
{
    private readonly AppConnectServer _server;
    private readonly RegistrationFiles _files;
    private readonly string _appVersion;
    private readonly string _exePath;
    private readonly ILogger _logger;
    private DateTime? _startedAtUtc;

    public AppConnectHost(AppConnectServer server, RegistrationFiles files, string appVersion, string exePath, ILogger? logger = null)
    {
        _server = server;
        _files = files;
        _appVersion = appVersion;
        _exePath = exePath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Teilnahme aus Startschalter und App-Wurzel.</summary>
    public static AppConnectParticipation Decide(bool hiddenStart, bool rootRedirected)
        => hiddenStart ? AppConnectParticipation.Off
            : rootRedirected ? AppConnectParticipation.Isolated
            : AppConnectParticipation.Normal;

    /// <summary><c>window</c> bzw. <c>background</c> (Stufe 6); Stufe 1 läuft immer mit Fenster.</summary>
    public string Mode { get; } = "window";

    public int Port => _server.Port;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _server.StartAsync(ct).ConfigureAwait(false);
        _startedAtUtc = DateTime.UtcNow;
        WriteRegistration(running: true);
    }

    /// <summary>Anmeldedatei auf „beendet“ setzen und die Schnittstelle schließen.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_startedAtUtc is null)
        {
            return;
        }

        _startedAtUtc = null;
        WriteRegistration(running: false);
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private void WriteRegistration(bool running)
    {
        try
        {
            _files.Write(new AppRegistration
            {
                AppId = AppConnectContract.AppId,
                DisplayName = AppConnectContract.DisplayName,
                AppVersion = _appVersion,
                ContractVersion = AppConnectContract.Version,
                ExePath = _exePath,
                BackgroundArg = AppConnectContract.BackgroundArg,
                Running = running,
                Pid = running ? Environment.ProcessId : null,
                Port = running ? _server.Port : null,
                Mode = running ? Mode : null,
                StartedAtUtc = running ? _startedAtUtc : null,
                Provides = AppConnectContract.Provides,
                Wants = AppConnectContract.Wants,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "App-Verbindung: Anmeldedatei nicht geschrieben");
        }
    }
}
