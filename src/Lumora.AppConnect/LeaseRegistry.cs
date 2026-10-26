namespace Lumora.AppConnect;

/// <summary>
/// Halte-Zusagen (Abschnitt 7): „ich brauche dich noch“, 60…900 s, je Abfrager. Bestimmt in Stufe 6 die Lebensdauer im
/// Hintergrund; bis dahin nur verwaltet.
/// </summary>
public sealed class LeaseRegistry(TimeProvider? time = null)
{
    public const int MinSeconds = 60;
    public const int MaxSeconds = 900;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    public static bool IsValidDuration(int seconds) => seconds is >= MinSeconds and <= MaxSeconds;

    public LeaseResponse Create(string appId, int seconds)
    {
        lock (_lock)
        {
            Cleanup();
            var lease = new Lease(Guid.NewGuid().ToString("N"), appId, seconds, _time.GetUtcNow().AddSeconds(seconds));
            _leases[lease.Id] = lease;
            return lease.ToResponse();
        }
    }

    /// <summary>Verlängert um <paramref name="seconds"/> bzw. die bisherige Dauer; null = abgelaufen oder fremd (404).</summary>
    public LeaseResponse? Extend(string appId, string leaseId, int? seconds)
    {
        lock (_lock)
        {
            Cleanup();
            if (!_leases.TryGetValue(leaseId, out var lease) || lease.AppId != appId)
            {
                return null;
            }

            var duration = seconds ?? lease.Seconds;
            var renewed = lease with { Seconds = duration, ExpiresUtc = _time.GetUtcNow().AddSeconds(duration) };
            _leases[leaseId] = renewed;
            return renewed.ToResponse();
        }
    }

    public bool Release(string appId, string leaseId)
    {
        lock (_lock)
        {
            Cleanup();
            return _leases.TryGetValue(leaseId, out var lease) && lease.AppId == appId && _leases.Remove(leaseId);
        }
    }

    /// <summary>Alle Zusagen einer App weg (beim Trennen).</summary>
    public void ReleaseAll(string appId)
    {
        lock (_lock)
        {
            foreach (var id in _leases.Values.Where(l => l.AppId == appId).Select(l => l.Id).ToList())
            {
                _leases.Remove(id);
            }
        }
    }

    /// <summary>Gibt es noch eine gültige Zusage (Stufe 6: im Hintergrund weiterlaufen)?</summary>
    public bool HasActive
    {
        get
        {
            lock (_lock)
            {
                Cleanup();
                return _leases.Count > 0;
            }
        }
    }

    private void Cleanup()
    {
        var now = _time.GetUtcNow();
        foreach (var id in _leases.Values.Where(l => l.ExpiresUtc <= now).Select(l => l.Id).ToList())
        {
            _leases.Remove(id);
        }
    }

    private sealed record Lease(string Id, string AppId, int Seconds, DateTimeOffset ExpiresUtc)
    {
        public LeaseResponse ToResponse() => new(Id, ExpiresUtc.UtcDateTime);
    }
}
