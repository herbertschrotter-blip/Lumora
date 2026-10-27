using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.Services.Places.Tracks;

/// <summary>
/// Spuren aus verbundenen Apps nachfragen (App-Verbindung Stufe 7, ADR-0073), ohne dass die Services die App-Verbindung
/// kennen: die Seite „Orte“ (Ungenaue Orte) und der Start melden „jetzt wäre ein Abgleich gut“; die App-Verbindung hängt den
/// eigentlichen Abgleich ein (<see cref="ExternalTrackSyncHub.Attach"/>). Ohne App-Verbindung (Start mit <c>_hidden</c>,
/// „Verbindungen erlauben“ aus) passiert nichts.
/// </summary>
public interface IExternalTrackSync
{
    /// <summary>
    /// Abgleich anstoßen, höchstens alle <see cref="ExternalTrackSyncHub.MinInterval"/>; kehrt sofort zurück.
    /// <paramref name="mayStartApps"/> = die App darf dafür im Hintergrund gestartet werden.
    /// </summary>
    void Request(bool mayStartApps);
}

/// <inheritdoc cref="IExternalTrackSync"/>
public sealed class ExternalTrackSyncHub(ILogger<ExternalTrackSyncHub>? logger = null, TimeProvider? time = null) : IExternalTrackSync
{
    /// <summary>Automatische Abgleiche höchstens so oft (der Knopf im Reiter gleicht immer ab).</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private Func<bool, CancellationToken, Task>? _run;
    private DateTimeOffset? _lastUtc;
    private DateTimeOffset? _lastWithStartUtc;
    private bool _running;

    /// <summary>Die App-Verbindung hängt ihren Abgleich ein (null = wieder aus).</summary>
    public void Attach(Func<bool, CancellationToken, Task>? run)
    {
        lock (_lock)
        {
            _run = run;
        }
    }

    public void Request(bool mayStartApps)
    {
        Func<bool, CancellationToken, Task>? run;
        lock (_lock)
        {
            // getrennt gezählt: ein Abgleich ohne Starten (z. B. nach dem Programmstart) hält „Ungenaue Orte“ nicht davon ab,
            // die App gleich danach im Hintergrund zu starten
            var now = _time.GetUtcNow();
            var last = mayStartApps ? _lastWithStartUtc : _lastUtc;
            if (_run is null || _running || (last is { } at && now - at < MinInterval))
            {
                return;
            }

            run = _run;
            _running = true;
            _lastUtc = now;
            if (mayStartApps)
            {
                _lastWithStartUtc = now;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await run(mayStartApps, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Spuren aus verbundenen Apps: Abgleich fehlgeschlagen");
            }
            finally
            {
                lock (_lock)
                {
                    _running = false;
                }
            }
        });
    }
}
