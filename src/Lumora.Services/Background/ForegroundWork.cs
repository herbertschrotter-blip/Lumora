namespace Lumora.Services.Background;

/// <summary>Was eine Arbeit, auf die der Benutzer wartet, gerade braucht.</summary>
[Flags]
public enum ForegroundNeed
{
    None = 0,

    /// <summary>Grafikkarte: Gesichter und Motive pausieren.</summary>
    Gpu = 1,

    /// <summary>Prozessor: alle Hintergrund-Aufgaben pausieren.</summary>
    Cpu = 2,
}

/// <summary>
/// Vordergrund-Vorrang (ADR-0074 Nachtrag 0.5.12): Arbeit, auf die der Benutzer gerade wartet (Foto verbessern, Assistent),
/// meldet sich hier an. Solange sie läuft, lässt der Hintergrund-Planer die betroffenen Hintergrund-Aufgaben nach dem
/// laufenden Foto bzw. Stapel warten; danach laufen sie von selbst weiter.
/// </summary>
public interface IForegroundWork
{
    /// <summary>Anmelden; Dispose meldet ab. Mehrere Anmeldungen gleichzeitig sind erlaubt.</summary>
    IDisposable Begin(ForegroundNeed need, string what);

    /// <summary>Alles, was angemeldete Arbeiten gerade brauchen (None = keine).</summary>
    ForegroundNeed Need { get; }

    /// <summary>Name der jüngsten angemeldeten Arbeit (für „wartet — Vorrang für …“); null = keine.</summary>
    string? What { get; }

    /// <summary>Eine Arbeit hat sich an- oder abgemeldet.</summary>
    event Action? Changed;
}

/// <inheritdoc />
public sealed class ForegroundWork : IForegroundWork
{
    private readonly Lock _lock = new();
    private readonly List<Claim> _claims = [];

    public event Action? Changed;

    public ForegroundNeed Need
    {
        get
        {
            lock (_lock)
            {
                return _claims.Aggregate(ForegroundNeed.None, (all, c) => all | c.Need);
            }
        }
    }

    public string? What
    {
        get
        {
            lock (_lock)
            {
                return _claims.Count == 0 ? null : _claims[^1].What;
            }
        }
    }

    public IDisposable Begin(ForegroundNeed need, string what)
    {
        var claim = new Claim(this, need, what);
        lock (_lock)
        {
            _claims.Add(claim);
        }

        Changed?.Invoke();
        return claim;
    }

    private void End(Claim claim)
    {
        bool removed;
        lock (_lock)
        {
            removed = _claims.Remove(claim);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    private sealed class Claim(ForegroundWork owner, ForegroundNeed need, string what) : IDisposable
    {
        public ForegroundNeed Need { get; } = need;

        public string What { get; } = what;

        public void Dispose() => owner.End(this);
    }
}
