using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.Services.Activity;

/// <inheritdoc cref="IActivityCenter" />
/// <remarks>
/// Ordnung „Läuft“: vom Benutzer gestartet vor Hintergrund, laufend vor angehalten, dann nach <see cref="ActivityKind"/>
/// (Datei-Vorgänge zuerst), bei Gleichstand der jüngste zuerst. „Wartet“: vom Benutzer gestartet zuerst, dann der älteste.
/// „Zuletzt fertig“: höchstens <see cref="RecentLimit"/>, der neueste zuerst. Drosselung: die erste Änderung nach einer Ruhe
/// von <see cref="NotifyInterval"/> meldet sofort, weitere innerhalb des Intervalls werden zu einer nachlaufenden Meldung
/// gebündelt — die Oberfläche (UI-Automation) wird so höchstens alle 150 ms neu befüllt. Protokolliert werden nur Art und
/// Id (keine Titel: sie können Ordnernamen enthalten, ADR-0049).
/// </remarks>
public sealed class ActivityCenter : IActivityCenter, IDisposable
{
    public const int RecentLimit = 10;

    public static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Restzeit erst schätzen, wenn so lange Fortschritt gemeldet wurde.</summary>
    public static readonly TimeSpan EstimateAfter = TimeSpan.FromSeconds(3);

    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly List<Entry> _active = new();
    private readonly List<Entry> _recent = new();
    private readonly ITimer _notifyTimer;
    private long _nextId;
    private DateTimeOffset _lastNotify = DateTimeOffset.MinValue;
    private bool _notifyPending;
    private DateTime? _lastFinishedUtc;
    private bool _disposed;

    public ActivityCenter(TimeProvider? time = null, ILogger<ActivityCenter>? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _notifyTimer = _time.CreateTimer(_ => OnNotifyTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action? Changed;

    public event Action<ActivitySnapshot>? Finished;

    public IActivityHandle Start(ActivityStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Entry entry;
        lock (_sync)
        {
            if (info.Key is not null && _active.FirstOrDefault(e => e.Info.Key == info.Key) is { } existing)
            {
                return existing;
            }

            entry = new Entry(this, ++_nextId, info, UtcNow);
            _active.Add(entry);
        }

        _logger.LogDebug("Aktivität {Id} gestartet ({Kind})", entry.Id, info.Kind);
        RequestNotify();
        return entry;
    }

    public IActivityHandle? Find(string key)
    {
        lock (_sync)
        {
            return _active.FirstOrDefault(e => e.Info.Key == key);
        }
    }

    public ActivityOverview GetOverview()
    {
        lock (_sync)
        {
            var now = UtcNow;
            var running = _active.Where(e => e.State is ActivityState.Running or ActivityState.Paused)
                .OrderByDescending(e => e.Info.UserInitiated)
                .ThenBy(e => e.State == ActivityState.Paused)
                .ThenBy(e => (int)e.Info.Kind)
                .ThenByDescending(e => e.Id)
                .Select(e => e.Snapshot(now))
                .ToList();
            var waiting = _active.Where(e => e.State == ActivityState.Waiting)
                .OrderByDescending(e => e.Info.UserInitiated)
                .ThenBy(e => e.Id)
                .Select(e => e.Snapshot(now))
                .ToList();
            var recent = _recent.Select(e => e.Snapshot(now)).ToList();
            return new ActivityOverview(running, waiting, recent, _lastFinishedUtc);
        }
    }

    public bool Cancel(long id)
    {
        Action? cancel;
        lock (_sync)
        {
            var entry = _active.FirstOrDefault(e => e.Id == id);
            cancel = entry?.CancelAction;
            if (cancel is null)
            {
                return false;
            }

            entry!.CancelRequested = true;
        }

        _logger.LogInformation("Aktivität {Id}: Abbrechen angefordert", id);
        try
        {
            cancel();
        }
        catch (ObjectDisposedException)
        {
            // Vorgang war in diesem Moment schon fertig (CancellationTokenSource entsorgt) — nichts mehr abzubrechen.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aktivität {Id}: Abbrechen fehlgeschlagen", id);
        }

        RequestNotify();
        return true;
    }

    public bool SetPaused(long id, bool paused)
    {
        Action? action;
        Entry? entry;
        lock (_sync)
        {
            entry = _active.FirstOrDefault(e => e.Id == id);
            action = paused ? entry?.Info.Pause : entry?.Info.Resume;
            if (entry is null || action is null)
            {
                return false;
            }
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aktivität {Id}: Pause/Fortsetzen fehlgeschlagen", id);
            return false;
        }

        entry.SetPaused(paused); // sofort zeigen; die Quelle bestätigt mit ihrem nächsten Zustand
        return true;
    }

    public async Task<string?> UndoAsync(long id, CancellationToken ct = default)
    {
        Func<CancellationToken, Task<string>>? run;
        Entry? entry;
        lock (_sync)
        {
            entry = _recent.FirstOrDefault(e => e.Id == id);
            run = entry?.Actions.FirstOrDefault(a => a.Kind == ActivityActionKind.Undo)?.Run;
            if (entry is null || run is null || entry.UndoRunning)
            {
                return null;
            }

            entry.UndoRunning = true;
        }

        RequestNotify();
        string text;
        var ok = false;
        try
        {
            text = await run(ct).ConfigureAwait(false);
            ok = true;
        }
        catch (Exception ex)
        {
            // „Rückgängig“ bleibt stehen (z. B. Warten auf die Scan-Sperre abgebrochen, Datei kurz gesperrt) — erneut versuchbar
            _logger.LogWarning(ex, "Aktivität {Id}: Rückgängig fehlgeschlagen", id);
            text = $"Rückgängig fehlgeschlagen: {ex.Message}";
        }

        lock (_sync)
        {
            entry.UndoRunning = false;
        }

        if (ok)
        {
            MarkUndone(id, "rückgängig gemacht");
        }
        else
        {
            RequestNotify();
        }

        return text;
    }

    public void MarkUndone(long id, string note)
    {
        lock (_sync)
        {
            var entry = _recent.FirstOrDefault(e => e.Id == id);
            if (entry is null || !entry.Actions.Any(a => a.Kind == ActivityActionKind.Undo))
            {
                return;
            }

            entry.Actions = entry.Actions.Where(a => a.Kind != ActivityActionKind.Undo).ToList();
            entry.Result = string.IsNullOrEmpty(entry.Result) ? note : $"{entry.Result} · {note}";
        }

        RequestNotify();
    }

    public void ClearRecent()
    {
        lock (_sync)
        {
            _recent.Clear();
            _lastFinishedUtc = null;
        }

        RequestNotify();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _active.Clear();
            _recent.Clear();
            _lastFinishedUtc = null;
        }

        _notifyTimer.Dispose();
        Changed = null;
        Finished = null;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Gedrosselte Benachrichtigung: sofort nach einer Ruhe, sonst gebündelt am Ende des Intervalls.</summary>
    private void RequestNotify()
    {
        bool fire;
        lock (_sync)
        {
            if (_disposed || _notifyPending)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var due = _lastNotify + NotifyInterval;
            fire = now >= due;
            if (fire)
            {
                _lastNotify = now;
            }
            else
            {
                _notifyPending = true;
                _notifyTimer.Change(due - now, Timeout.InfiniteTimeSpan);
            }
        }

        if (fire)
        {
            RaiseChanged();
        }
    }

    private void OnNotifyTimer()
    {
        lock (_sync)
        {
            if (_disposed || !_notifyPending)
            {
                return;
            }

            _notifyPending = false;
            _lastNotify = _time.GetUtcNow();
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aktivitäten: Empfänger von Changed hat geworfen");
        }
    }

    /// <summary>Beendet einen Vorgang: aus „aktiv“ nehmen, ggf. unter „Zuletzt fertig“ (begrenzt), Finished melden.</summary>
    private void Finish(Entry entry, ActivityState state, string? result, IReadOnlyList<ActivityAction>? actions, string? details, bool silent)
    {
        ActivitySnapshot snapshot;
        lock (_sync)
        {
            if (!entry.IsActiveUnsafe || !_active.Remove(entry))
            {
                return;
            }

            var now = UtcNow;
            entry.State = state;
            entry.FinishedUtc = now;
            entry.Result = result;
            entry.Details = details;
            entry.Actions = actions ?? [];
            if (silent)
            {
                snapshot = entry.Snapshot(now);
            }
            else
            {
                _lastFinishedUtc = now;
                var keep = entry.Info.KeepInRecent || state is ActivityState.Failed or ActivityState.Warning or ActivityState.Canceled;
                if (keep)
                {
                    _recent.Insert(0, entry);
                    if (_recent.Count > RecentLimit)
                    {
                        _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);
                    }
                }

                snapshot = entry.Snapshot(now);
            }
        }

        _logger.LogDebug("Aktivität {Id} beendet ({Kind}, {State})", entry.Id, entry.Info.Kind, state);
        RequestNotify();
        if (!silent)
        {
            try
            {
                Finished?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Aktivitäten: Empfänger von Finished hat geworfen");
            }
        }
    }

    /// <summary>Ein Vorgang; alle Felder nur unter <see cref="_sync"/> der Zentrale.</summary>
    private sealed class Entry(ActivityCenter owner, long id, ActivityStartInfo info, DateTime startedUtc) : IActivityHandle
    {
        private int _done;
        private int _total;
        private string? _detail;
        private string? _waitReason;
        private Action? _waitCancel;
        private DateTime? _progressSinceUtc;
        private int _progressSinceDone;

        public long Id { get; } = id;

        public ActivityStartInfo Info { get; } = info;

        public string Title { get; private set; } = info.Title;

        public ActivityState State { get; set; } = ActivityState.Running;

        public DateTime? FinishedUtc { get; set; }

        public string? Result { get; set; }

        public string? Details { get; set; }

        public IReadOnlyList<ActivityAction> Actions { get; set; } = [];

        public bool CancelRequested { get; set; }

        public bool UndoRunning { get; set; }

        public bool IsActiveUnsafe => State is ActivityState.Running or ActivityState.Paused or ActivityState.Waiting;

        public bool IsActive
        {
            get
            {
                lock (owner._sync)
                {
                    return IsActiveUnsafe;
                }
            }
        }

        /// <summary>Abbruch-Weg: beim Warten der des Wartens (falls angegeben), sonst der der Quelle.</summary>
        public Action? CancelAction => CancelRequested ? null : State == ActivityState.Waiting && _waitCancel is not null ? _waitCancel : Info.Cancel;

        public void Report(int done, int total, string? detail = null)
        {
            lock (owner._sync)
            {
                if (!IsActiveUnsafe)
                {
                    return;
                }

                var now = owner.UtcNow;
                if (total != _total || done < _done || _progressSinceUtc is null)
                {
                    _progressSinceUtc = now; // neue Menge bzw. Neustart: Schätzung neu beginnen
                    _progressSinceDone = done;
                }

                _done = Math.Max(0, done);
                _total = Math.Max(0, total);
                if (detail is not null)
                {
                    _detail = detail;
                }

                if (State == ActivityState.Waiting)
                {
                    State = ActivityState.Running;
                    _waitReason = null;
                    _waitCancel = null;
                }
            }

            owner.RequestNotify();
        }

        public void SetTitle(string title)
        {
            lock (owner._sync)
            {
                Title = title;
            }

            owner.RequestNotify();
        }

        public void Wait(string reason, Action? cancel = null)
        {
            lock (owner._sync)
            {
                if (!IsActiveUnsafe)
                {
                    return;
                }

                State = ActivityState.Waiting;
                _waitReason = reason;
                _waitCancel = cancel;
            }

            owner.RequestNotify();
        }

        public void Resume()
        {
            lock (owner._sync)
            {
                if (State != ActivityState.Waiting)
                {
                    return;
                }

                State = ActivityState.Running;
                _waitReason = null;
                _waitCancel = null;
                CancelRequested = false; // abgebrochenes Warten: der Vorgang selbst bleibt abbrechbar
            }

            owner.RequestNotify();
        }

        public void SetPaused(bool paused)
        {
            lock (owner._sync)
            {
                if (!IsActiveUnsafe)
                {
                    return;
                }

                State = paused ? ActivityState.Paused : ActivityState.Running;
                if (paused)
                {
                    _waitReason = null;
                    _waitCancel = null;
                    _progressSinceUtc = null; // nach der Pause neu schätzen
                }
            }

            owner.RequestNotify();
        }

        public void Complete(ActivityCompletion completion)
        {
            ArgumentNullException.ThrowIfNull(completion);
            var state = completion.State is ActivityState.Running or ActivityState.Paused or ActivityState.Waiting ? ActivityState.Succeeded : completion.State;
            owner.Finish(this, state, completion.Result, completion.Actions, completion.Details, silent: false);
        }

        public void Fail(string message, string? details = null)
            => owner.Finish(this, ActivityState.Failed, message, details is null ? [] : [ActivityAction.Details], details, silent: false);

        public void Canceled(string? message = null)
            => owner.Finish(this, ActivityState.Canceled, message ?? "Abgebrochen", null, null, silent: false);

        public void Dismiss() => owner.Finish(this, ActivityState.Succeeded, Result, Actions, Details, silent: true);

        public ActivitySnapshot Snapshot(DateTime now)
        {
            TimeSpan? remaining = null;
            if (State == ActivityState.Running && _total > 0 && _done < _total && _progressSinceUtc is { } since && _done > _progressSinceDone)
            {
                var elapsed = now - since;
                if (elapsed >= EstimateAfter)
                {
                    remaining = TimeSpan.FromTicks((long)(elapsed.Ticks / (double)(_done - _progressSinceDone) * (_total - _done)));
                }
            }

            var canCancel = IsActiveUnsafe && CancelAction is not null;
            var canPause = IsActiveUnsafe && Info.Pause is not null && Info.Resume is not null && State != ActivityState.Waiting;
            return new ActivitySnapshot(
                Id,
                Info.Kind,
                Title,
                Info.Target,
                _detail,
                State,
                _done,
                _total,
                remaining,
                Info.UserInitiated,
                canCancel,
                canPause,
                State == ActivityState.Waiting ? _waitReason : null,
                startedUtc,
                FinishedUtc,
                Result,
                Details,
                Actions);
        }
    }
}
