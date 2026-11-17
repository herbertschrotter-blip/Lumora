using System.Collections.Concurrent;

namespace Lumora.Services.Cloud;

/// <summary>
/// Feste Anzahl STA-Threads für Shell-COM (ADR-0077, Spike 19: die Shell liefert Vorschaubilder und Eigenschaften nur auf
/// STA-Threads). Die Zahl der Threads ist zugleich die Obergrenze gleichzeitiger Aufrufe (Vorschaubilder: 8). Threads
/// entstehen beim ersten Auftrag und laufen als Hintergrund-Threads bis zum Beenden; ein Auftrag, dessen Token schon
/// abgebrochen ist, wird übersprungen.
/// </summary>
public sealed class StaWorkerPool : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly int _threads;
    private readonly string _name;
    private int _started;

    private sealed record WorkItem(Action Run, Action Cancel, CancellationToken Token);

    public StaWorkerPool(int threads, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        _threads = threads;
        _name = name;
    }

    /// <summary>Höchstzahl gleichzeitiger Aufträge.</summary>
    public int Concurrency => _threads;

    /// <summary>Führt die Arbeit auf einem STA-Thread aus; Ausnahmen kommen über die Task zurück.</summary>
    public Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
            return tcs.Task;
        }

        EnsureThreads();
        var item = new WorkItem(
            () =>
            {
                try
                {
                    tcs.TrySetResult(work(ct));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            () => tcs.TrySetCanceled(ct),
            ct);
        try
        {
            _queue.Add(item);
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(StaWorkerPool)));
        }

        return tcs.Task;
    }

    private void EnsureThreads()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        for (var i = 0; i < _threads; i++)
        {
            var thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = $"{_name} {i + 1}",
            };
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.STA);
            }

            thread.Start();
        }
    }

    private void Loop()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (item.Token.IsCancellationRequested)
                {
                    item.Cancel();
                    continue;
                }

                item.Run();
            }
        }
        catch (ObjectDisposedException)
        {
            // beendet
        }
    }

    /// <summary>Nimmt keine Aufträge mehr an; laufende enden von selbst, wartende werden noch abgearbeitet bzw. übersprungen.</summary>
    public void Dispose() => _queue.CompleteAdding();
}
