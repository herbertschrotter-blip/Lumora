using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Lightbox.Imaging;

/// <summary>
/// Kleiner LRU-Cache dekodierter Lightbox-Bilder (aktuelles Bild + Prefetch ±1, ADR-0007 „Critical“).
/// Schlüssel ist die FileId; ein geänderter Pfad (Move/Rename) invalidiert den Eintrag.
/// Laufende Decodes werden beim Verdrängen abgebrochen. Thread-sicher. Seit 0.5.15 dekodiert jedes Bild auf einem eigenen
/// Thread (nicht im gemeinsamen Thread-Pool, wo es hinter Hintergrund-Arbeit warten müsste) und meldet währenddessen
/// Vorrang an (<see cref="Lumora.Services.Background.ForegroundHold"/>, ADR-0074 Nachtrag 0.5.15).
/// </summary>
internal sealed class LightboxImageCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly LinkedList<int> _lru = new();
    private readonly int _capacity;
    private readonly Lumora.Services.Background.ForegroundHold? _hold;
    private bool _disposed;

    public LightboxImageCache(int capacity = 4, Lumora.Services.Background.ForegroundHold? hold = null)
    {
        _capacity = Math.Max(2, capacity);
        _hold = hold;
    }

    private sealed class Entry
    {
        public required string Path { get; init; }
        public required Task<DecodedImage> Task { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    /// <summary>Liefert den laufenden/fertigen Decode oder startet ihn im Hintergrund.</summary>
    public Task<DecodedImage> GetOrStartAsync(int fileId, string path, int maxDecodePixelWidth)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_entries.TryGetValue(fileId, out var existing))
            {
                var reusable = string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)
                    && !existing.Task.IsCanceled
                    && !existing.Task.IsFaulted;
                if (reusable)
                {
                    Touch(fileId);
                    return existing.Task;
                }

                RemoveLocked(fileId);
            }

            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var hold = _hold;
            var task = Task.Factory.StartNew(
                () =>
                {
                    using (hold?.Enter())
                    {
                        return LightboxImageDecoder.Decode(path, maxDecodePixelWidth, token);
                    }
                },
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _entries[fileId] = new Entry { Path = path, Task = task, Cts = cts };
            _lru.AddFirst(fileId);
            EvictLocked();
            return task;
        }
    }

    public bool TryGetCompleted(int fileId, out DecodedImage? image)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(fileId, out var entry) && entry.Task.IsCompletedSuccessfully)
            {
                image = entry.Task.Result;
                return true;
            }

            image = null;
            return false;
        }
    }

    public void Invalidate(int fileId)
    {
        lock (_gate)
        {
            RemoveLocked(fileId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var id in new List<int>(_entries.Keys))
            {
                RemoveLocked(id);
            }
        }
    }

    private void Touch(int fileId)
    {
        _lru.Remove(fileId);
        _lru.AddFirst(fileId);
    }

    private void EvictLocked()
    {
        while (_entries.Count > _capacity && _lru.Last is not null)
        {
            RemoveLocked(_lru.Last.Value);
        }
    }

    private void RemoveLocked(int fileId)
    {
        if (_entries.Remove(fileId, out var entry))
        {
            _lru.Remove(fileId);
            if (entry.Task.IsCompleted)
            {
                entry.Cts.Dispose();
            }
            else
            {
                // Laufenden Decode abbrechen; CTS nicht disposen, solange der Token noch benutzt wird.
                entry.Cts.Cancel();
            }

            // Fehlerhafte/abgebrochene Tasks nicht als unbeobachtet melden.
            entry.Task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var id in new List<int>(_entries.Keys))
            {
                RemoveLocked(id);
            }
        }
    }
}
