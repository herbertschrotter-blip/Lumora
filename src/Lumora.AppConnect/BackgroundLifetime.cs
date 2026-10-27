using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.AppConnect;

/// <summary>
/// Lebensdauer ohne Fenster (Abschnitt 9): Lumora läuft im Hintergrund weiter, solange es jemand braucht, und meldet
/// <see cref="ExitRequested"/>, wenn (a) keine gültige Halte-Zusage mehr besteht und 10 Minuten keine Anfrage kam, oder (b) der
/// Prozess aus <c>--started-by</c> beendet ist und kein anderer Abfrager eine Zusage hält. Mit Fenster gilt nichts davon.
/// </summary>
public sealed class BackgroundLifetime : IDisposable
{
    /// <summary>Ohne Zusage höchstens so lange ohne Anfrage im Hintergrund (Abschnitt 9).</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);

    /// <summary>Wie oft geprüft wird.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly LeaseRegistry _leases;
    private readonly Func<DateTimeOffset?> _lastRequest;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly ITimer _timer;
    private bool _background;
    private DateTimeOffset _since;
    private BackgroundLaunch? _startedBy;
    private bool _exitRaised;

    /// <param name="lastRequest">Letzte Anfrage an die Schnittstelle (<see cref="AppConnectServer.LastRequestUtc"/>).</param>
    /// <param name="isProcessAlive">Prüft die PID aus <c>--started-by</c> (Tests geben eine eigene).</param>
    public BackgroundLifetime(LeaseRegistry leases, Func<DateTimeOffset?> lastRequest, TimeProvider? time = null,
        Func<int, bool>? isProcessAlive = null, ILogger? logger = null)
    {
        _leases = leases;
        _lastRequest = lastRequest;
        _time = time ?? TimeProvider.System;
        _isProcessAlive = isProcessAlive ?? IsAlive;
        _logger = logger ?? NullLogger.Instance;
        _timer = _time.CreateTimer(_ => Check(), null, CheckInterval, CheckInterval);
    }

    /// <summary>Lumora soll sich beenden (einmal je Hintergrund-Abschnitt; vom Takt-Thread).</summary>
    public event Action<string>? ExitRequested;

    public bool IsBackground
    {
        get
        {
            lock (_lock)
            {
                return _background;
            }
        }
    }

    /// <summary>Seit wann Lumora im Hintergrund läuft (für „seit 14:02“).</summary>
    public DateTimeOffset Since
    {
        get
        {
            lock (_lock)
            {
                return _since;
            }
        }
    }

    /// <summary>Die App, die Lumora im Hintergrund gestartet hat; null nach dem ersten Fenster oder ohne Hintergrundstart.</summary>
    public BackgroundLaunch? StartedBy
    {
        get
        {
            lock (_lock)
            {
                return _startedBy;
            }
        }
    }

    /// <summary>Für wen Lumora gerade im Hintergrund läuft: Zusagen-Halter und die startende App.</summary>
    public IReadOnlyList<string> Requesters
    {
        get
        {
            var holders = _leases.Holders.ToList();
            if (StartedBy is { } launch && !holders.Contains(launch.StartedByAppId, StringComparer.Ordinal))
            {
                holders.Insert(0, launch.StartedByAppId);
            }

            return holders;
        }
    }

    /// <summary>Ohne Fenster weiterlaufen (Hintergrundstart oder Fenster geschlossen bei gültiger Zusage).</summary>
    public void EnterBackground(BackgroundLaunch? startedBy = null)
    {
        lock (_lock)
        {
            _background = true;
            _since = _time.GetUtcNow();
            _exitRaised = false;
            if (startedBy is not null)
            {
                _startedBy = startedBy;
            }
        }
    }

    /// <summary>Fenster ist offen: Lumora läuft für den Benutzer, die Hintergrund-Regeln gelten nicht mehr.</summary>
    public void EnterWindow()
    {
        lock (_lock)
        {
            _background = false;
            _startedBy = null; // ab jetzt gehört Lumora dem Benutzer — Regel (b) gilt nicht mehr
        }
    }

    /// <summary>Grund zum Beenden; null = weiterlaufen.</summary>
    public string? ExitReason()
    {
        BackgroundLaunch? startedBy;
        DateTimeOffset since;
        lock (_lock)
        {
            if (!_background)
            {
                return null;
            }

            startedBy = _startedBy;
            since = _since;
        }

        var holders = _leases.Holders;
        if (startedBy is not null && !_isProcessAlive(startedBy.StartedByPid)
            && !holders.Any(h => !string.Equals(h, startedBy.StartedByAppId, StringComparison.Ordinal)))
        {
            return $"{startedBy.StartedByAppId} ist beendet";
        }

        if (holders.Count == 0)
        {
            var last = _lastRequest() is { } request && request > since ? request : since;
            if (_time.GetUtcNow() - last >= IdleLimit)
            {
                return "keine Halte-Zusage und 10 Minuten keine Anfrage";
            }
        }

        return null;
    }

    /// <summary>Einmal prüfen (Takt; öffentlich für Tests).</summary>
    public void Check()
    {
        if (ExitReason() is not { } reason)
        {
            return;
        }

        lock (_lock)
        {
            if (_exitRaised)
            {
                return;
            }

            _exitRaised = true;
        }

        _logger.LogInformation("App-Verbindung: Hintergrund endet — {Reason}", reason);
        ExitRequested?.Invoke(reason);
    }

    public void Dispose() => _timer.Dispose();

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
