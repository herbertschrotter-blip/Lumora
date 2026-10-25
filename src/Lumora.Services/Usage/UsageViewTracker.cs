namespace Lumora.Services.Usage;

/// <summary>
/// Misst die aktive Ansehdauer eines Fensters (Lightbox, Diashow) für die Nutzungs-Signale (ADR-0072), WPF-frei:
/// <see cref="Show"/> beim Wechsel auf eine Datei, <see cref="SetActive"/> bei Aktivieren/Deaktivieren/Minimieren des
/// Fensters, <see cref="NoteZoom"/> beim Zoomen, <see cref="Stop"/> beim Schließen. Gezählt wird nur Zeit, in der das
/// Fenster vorne ist; die beendete Ansicht geht an <see cref="IUsageSignals.RecordView"/> (dort gelten 1 s bis 2 min).
/// Läuft auf einem Thread (UI); nicht threadsicher.
/// </summary>
public sealed class UsageViewTracker
{
    private readonly IUsageSignals _signals;
    private TimeProvider _time;
    private int? _fileId;
    private TimeSpan _accumulated;
    private DateTimeOffset? _activeSince;
    private bool _active;
    private bool _zoomed;

    public UsageViewTracker(IUsageSignals signals, UsageViewContext context, TimeProvider? time = null)
    {
        _signals = signals;
        Context = context;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Woher die Ansichten kommen (Lightbox aus Story: gedämpft; „Doppelte“: zählt nicht).</summary>
    public UsageViewContext Context { get; set; }

    /// <summary>Aktuell gemessene Datei oder null.</summary>
    public int? FileId => _fileId;

    public bool IsActive => _active;

    /// <summary>Bisher gezählte aktive Zeit der laufenden Ansicht (Rauchtest, Tests).</summary>
    public TimeSpan Elapsed => _accumulated + (_active && _activeSince is { } since ? _time.GetUtcNow() - since : TimeSpan.Zero);

    /// <summary>Andere Uhr (Rauchtest mit simulierter Zeit); beendet vorher die laufende Ansicht.</summary>
    public void UseTime(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        Stop();
        _time = time;
    }

    /// <summary>Neue Datei im Fenster: beendet die vorige Ansicht und beginnt eine neue (null = nichts zu messen, z. B. Archiv).</summary>
    public void Show(int? fileId)
    {
        if (fileId == _fileId)
        {
            return;
        }

        Complete();
        _fileId = fileId;
        if (_active && fileId is not null)
        {
            _activeSince = _time.GetUtcNow();
        }
    }

    /// <summary>Fenster vorne (true) bzw. im Hintergrund oder minimiert (false).</summary>
    public void SetActive(bool active)
    {
        if (active == _active)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (!active && _activeSince is { } since)
        {
            _accumulated += now - since;
            _activeSince = null;
        }

        _active = active;
        if (active && _fileId is not null)
        {
            _activeSince = now;
        }
    }

    /// <summary>Der Benutzer hat in die laufende Ansicht gezoomt.</summary>
    public void NoteZoom()
    {
        if (_fileId is not null)
        {
            _zoomed = true;
        }
    }

    /// <summary>Laufende Ansicht beenden (Fenster schließt, Kontext wechselt).</summary>
    public void Stop() => Complete();

    private void Complete()
    {
        if (_fileId is { } fileId)
        {
            var elapsed = Elapsed;
            if (Context != UsageViewContext.None && elapsed >= UsageScoring.MinView)
            {
                _signals.RecordView(fileId, elapsed, _zoomed, Context);
            }
        }

        _fileId = null;
        _accumulated = TimeSpan.Zero;
        _activeSince = null;
        _zoomed = false;
    }
}
