using System.Reflection;

namespace Lumora.Tests.Activity;

/// <summary>Zeitquelle mit manuell vorgerückter Uhr und Timern (Drosselung, Vorschaubild-Abfrage) — kein echtes Warten.</summary>
internal sealed class ManualTimerTime(DateTimeOffset start) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = start;

    public ManualTimerTime()
        : this(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Uhr vorrücken und fällige Timer (auch periodische) in Zeitreihenfolge auslösen.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_sync)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_sync)
            {
                next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Due = next.Period is { } p && p > TimeSpan.Zero ? _now + p : null;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimerTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._sync)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Schnell-Fake für große Schnittstellen: merkt sich aufgerufene Methoden, Eigenschaften liefern Standardwerte bzw. <see cref="Values"/>.</summary>
public class RecordingProxy<T> : DispatchProxy
    where T : class
{
    public List<string> Calls { get; } = new();

    public Dictionary<string, object?> Values { get; } = new();

    public static (T Fake, RecordingProxy<T> Recorder) Create()
    {
        var fake = DispatchProxy.Create<T, RecordingProxy<T>>();
        return (fake, (RecordingProxy<T>)(object)fake);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name.StartsWith("get_", StringComparison.Ordinal) ? targetMethod.Name[4..] : targetMethod.Name;
        if (Values.TryGetValue(name, out var value))
        {
            return value;
        }

        if (!targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
        {
            lock (Calls)
            {
                Calls.Add(name);
            }
        }

        var type = targetMethod.ReturnType;
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
