namespace Lumora.Services.Background;

/// <summary>
/// Vorrang für kurze, oft wiederholte Vordergrund-Arbeit (Lightbox-Bilder, sichtbare Vorschaubilder; ADR-0074 Nachtrag
/// 0.5.15): zählt laufende Arbeiten und hält EINE Anmeldung bei <see cref="IForegroundWork"/>, solange eine läuft und
/// noch <see cref="Linger"/> danach — beim Blättern flackert der Vorrang so nicht bei jedem Bild an und aus.
/// </summary>
public sealed class ForegroundHold : IDisposable
{
    /// <summary>So lange bleibt der Vorrang nach der letzten Arbeit stehen.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(2);

    private readonly IForegroundWork? _work;
    private readonly ForegroundNeed _need;
    private readonly string _what;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();
    private IDisposable? _claim;
    private int _running;
    private bool _disposed;

    public ForegroundHold(IForegroundWork? work, ForegroundNeed need, string what, TimeProvider? time = null)
    {
        _work = work;
        _need = need;
        _what = what;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => ReleaseIfIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>true, solange der Vorrang angemeldet ist (Tests).</summary>
    public bool IsHeld
    {
        get
        {
            lock (_lock)
            {
                return _claim is not null;
            }
        }
    }

    /// <summary>Eine Arbeit beginnt; Dispose des Ergebnisses beendet sie.</summary>
    public IDisposable Enter()
    {
        if (_work is null)
        {
            return Nothing.Instance;
        }

        var begin = false;
        lock (_lock)
        {
            if (_disposed)
            {
                return Nothing.Instance;
            }

            _running++;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_claim is null)
            {
                _claim = Placeholder.Instance; // reserviert, damit parallele Enter nicht doppelt anmelden
                begin = true;
            }
        }

        if (begin)
        {
            var claim = _work.Begin(_need, _what); // außerhalb des Locks: Begin löst Changed aus
            lock (_lock)
            {
                if (_disposed)
                {
                    claim.Dispose();
                }
                else
                {
                    _claim = claim;
                    if (_running == 0)
                    {
                        _timer.Change(Linger, Timeout.InfiniteTimeSpan); // schon fertig, bevor die Anmeldung stand
                    }
                }
            }
        }

        return new Exit(this);
    }

    private void Leave()
    {
        lock (_lock)
        {
            if (--_running == 0 && !_disposed)
            {
                _timer.Change(Linger, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void ReleaseIfIdle()
    {
        IDisposable? claim = null;
        lock (_lock)
        {
            if (_running == 0 && _claim is not null and not Placeholder)
            {
                claim = _claim;
                _claim = null;
            }
        }

        claim?.Dispose();
    }

    public void Dispose()
    {
        IDisposable? claim;
        lock (_lock)
        {
            _disposed = true;
            claim = _claim;
            _claim = null;
        }

        _timer.Dispose();
        claim?.Dispose();
    }

    private sealed class Exit(ForegroundHold owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner.Leave();
            }
        }
    }

    private sealed class Placeholder : IDisposable
    {
        public static Placeholder Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static Nothing Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
