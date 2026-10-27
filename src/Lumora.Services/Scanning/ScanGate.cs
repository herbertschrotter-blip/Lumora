using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Messaging;

namespace Lumora.Services.Scanning;

/// <summary>
/// Hält Scans und Datei-Operationen derselben Bibliothek auseinander: <see cref="IScanService"/> belegt die Bibliothek
/// für die Dauer eines Scans (jeder Modus, jeder Aufrufer — Bibliotheksseite, Entpacken, Ordnerüberwachung), der
/// <c>FileOperationService</c> für die Dauer eines Batches (seit 0.2.17). Wer später kommt, wartet; verschiedene
/// Bibliotheken laufen unabhängig. Die Sperre ist nicht wiedereintrittsfähig: wer sie hält, darf weder scannen noch
/// eine Datei-Operation derselben Bibliothek starten (Entpacken, Sortierer und „Ins Archiv“ rufen den Scan erst nach
/// dem Ende des Batches auf). Wer warten muss, meldet das über den Messenger (<see cref="ScanGateWaitStartedMessage"/>,
/// <see cref="ScanGateWaitEndedMessage"/>, 0.2.22); Statusleiste und Bibliotheksseite zeigen es an.
/// </summary>
public interface IScanGate
{
    /// <summary>
    /// Wird ausgelöst, sobald ein Scan die Bibliothek belegt hat und bevor er das Dateisystem liest (synchron auf dem
    /// Scan-Thread; Handler müssen kurz sein und dürfen nicht werfen). Alle Dateiänderungen, die vorher geschehen sind,
    /// sieht dieser Scan — die Ordnerüberwachung verwirft deshalb hier ihre bis dahin gesammelten Ereignisse.
    /// </summary>
    event Action<int>? Acquired;

    /// <summary>Wartet, bis kein anderer Scan der Bibliothek läuft, und belegt sie; Dispose gibt sie frei.</summary>
    Task<IDisposable> AcquireAsync(int libraryId, CancellationToken ct);

    /// <summary>
    /// Wie <see cref="AcquireAsync(int, CancellationToken)"/>; <paramref name="callerId"/> landet in
    /// <see cref="ScanGateWait.CallerId"/> der Wartenachrichten, damit der Aufrufer sein eigenes Warten von dem anderer
    /// Scans derselben Bibliothek (z. B. der Ordnerüberwachung) unterscheiden kann (0.2.29).
    /// </summary>
    Task<IDisposable> AcquireAsync(int libraryId, Guid? callerId, CancellationToken ct);

    /// <summary>
    /// Wie <see cref="AcquireAsync"/>, aber für einen Datei-Batch: löst <see cref="Acquired"/> nicht aus — ein Batch liest
    /// das Dateisystem nicht neu ein und deckt keine gesammelten Ereignisse der Ordnerüberwachung ab.
    /// </summary>
    Task<IDisposable> AcquireForFileOperationAsync(int libraryId, CancellationToken ct);

    /// <summary>true, solange ein Scan oder ein Datei-Batch die Bibliothek belegt (Momentaufnahme).</summary>
    bool IsBusy(int libraryId);

    /// <summary>true, solange irgendeine Bibliothek belegt ist — der Hintergrund-Planer hält dann alles an (ADR-0074).</summary>
    bool IsAnyBusy { get; }

    /// <summary>Alle Aufrufer, die gerade auf eine Sperre warten (Momentaufnahme, nach WaitId aufsteigend).</summary>
    IReadOnlyList<ScanGateWait> GetWaits();

    /// <summary>
    /// Bricht das Warten mit dieser Id ab (Wartende ohne eigenes Abbruch-Token, z. B. Datei-Operation aus der Galerie):
    /// der Aufrufer bekommt eine <see cref="ScanGateWaitCanceledException"/>. false, wenn das Warten schon vorbei ist.
    /// Die Abbruch-Fortsetzungen laufen nicht auf dem aufrufenden Thread (UI-Thread bleibt frei).
    /// </summary>
    bool CancelWait(long waitId);
}

/// <summary>
/// Ein <see cref="SemaphoreSlim"/>(1, 1) je Bibliothek (wenige Bibliotheken → nie entfernt). Wartezustände (0.2.22):
/// ist die Bibliothek belegt, meldet der Wartende <see cref="ScanGateWaitStartedMessage"/> und am Ende
/// <see cref="ScanGateWaitEndedMessage"/> über den Messenger; wer die Sperre sofort bekommt, meldet nichts.
/// </summary>
public sealed class ScanGate : IScanGate
{
    private readonly ConcurrentDictionary<int, Slot> _gates = new();
    private readonly ConcurrentDictionary<long, PendingWait> _waits = new();
    private readonly IMessenger _messenger;
    private long _nextWaitId;

    /// <param name="messenger">Optional (Tests: eigene Instanz); produktiv WeakReferenceMessenger.Default.</param>
    public ScanGate(IMessenger? messenger = null)
    {
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public event Action<int>? Acquired;

    public Task<IDisposable> AcquireAsync(int libraryId, CancellationToken ct) => AcquireCoreAsync(libraryId, ScanGateUse.Scan, null, ct);

    public Task<IDisposable> AcquireAsync(int libraryId, Guid? callerId, CancellationToken ct) => AcquireCoreAsync(libraryId, ScanGateUse.Scan, callerId, ct);

    public Task<IDisposable> AcquireForFileOperationAsync(int libraryId, CancellationToken ct) => AcquireCoreAsync(libraryId, ScanGateUse.FileOperation, null, ct);

    private async Task<IDisposable> AcquireCoreAsync(int libraryId, ScanGateUse use, Guid? callerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var slot = _gates.GetOrAdd(libraryId, static _ => new Slot());
        if (!slot.Semaphore.Wait(0))
        {
            await WaitAsync(slot, libraryId, use, callerId, ct).ConfigureAwait(false);
        }

        slot.Holder = use;
        if (use == ScanGateUse.Scan)
        {
            try
            {
                Acquired?.Invoke(libraryId);
            }
            catch
            {
                // Beobachter-Fehler dürfen den Scan nicht verhindern (die Ordnerüberwachung protokolliert selbst).
            }
        }

        return new Lease(slot.Semaphore);
    }

    /// <summary>Wartet auf die belegte Bibliothek und meldet Beginn und Ende des Wartens.</summary>
    private async Task WaitAsync(Slot slot, int libraryId, ScanGateUse use, Guid? callerId, CancellationToken ct)
    {
        var wait = new ScanGateWait(Interlocked.Increment(ref _nextWaitId), libraryId, use, slot.Holder, callerId);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new PendingWait(wait, cts);
        _waits[wait.WaitId] = pending;
        var acquired = false;
        Send(new ScanGateWaitStartedMessage(wait));
        try
        {
            await slot.Semaphore.WaitAsync(cts.Token).ConfigureAwait(false);
            acquired = true;
        }
        catch (OperationCanceledException) when (pending.CanceledByUser && !ct.IsCancellationRequested)
        {
            throw new ScanGateWaitCanceledException(wait);
        }
        finally
        {
            _waits.TryRemove(wait.WaitId, out _);
            pending.Completed = true;
            Send(new ScanGateWaitEndedMessage(wait, acquired, !acquired && pending.CanceledByUser && !ct.IsCancellationRequested));
        }
    }

    public bool IsBusy(int libraryId) => _gates.TryGetValue(libraryId, out var slot) && slot.Semaphore.CurrentCount == 0;

    public bool IsAnyBusy => _gates.Values.Any(slot => slot.Semaphore.CurrentCount == 0);

    public IReadOnlyList<ScanGateWait> GetWaits() => _waits.Values.Select(w => w.Wait).OrderBy(w => w.WaitId).ToList();

    public bool CancelWait(long waitId)
    {
        if (!_waits.TryGetValue(waitId, out var pending) || pending.Completed)
        {
            return false;
        }

        pending.CanceledByUser = true;
        try
        {
            // Asynchron: die Fortsetzungen des Wartenden (Ende-Nachricht, Ausnahme bis zum Aufrufer) laufen nicht
            // auf dem Thread des Abbrechenden — meist der UI-Thread, der gerade ein Command ausführt.
            _ = pending.Cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return false; // das Warten endete gerade
        }

        return true;
    }

    private void Send<TMessage>(TMessage message)
        where TMessage : class
    {
        try
        {
            _messenger.Send(message);
        }
        catch
        {
            // Anzeige-Fehler dürfen Scan und Datei-Operation nicht verhindern.
        }
    }

    private sealed class Slot
    {
        private volatile int _holder;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Zuletzt erteilte Belegung (für die Wartenachricht; bei freier Sperre ohne Bedeutung).</summary>
        public ScanGateUse Holder
        {
            get => (ScanGateUse)_holder;
            set => _holder = (int)value;
        }
    }

    private sealed class PendingWait(ScanGateWait wait, CancellationTokenSource cts)
    {
        private volatile bool _canceledByUser;
        private volatile bool _completed;

        public ScanGateWait Wait { get; } = wait;

        public CancellationTokenSource Cts { get; } = cts;

        public bool CanceledByUser
        {
            get => _canceledByUser;
            set => _canceledByUser = value;
        }

        public bool Completed
        {
            get => _completed;
            set => _completed = value;
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
