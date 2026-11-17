using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Services.Cloud;

namespace Lumora.Services.Thumbnails;

/// <summary>
/// OneDrive-Platzhalter (ADR-0077): Ein Auftrag, dessen Original nur online liegt, wird nie an WIC oder FFmpeg gegeben (das
/// Lesen löste einen Download aus). Der Bild- bzw. Video-Worker erkennt ihn an den Attributen (<see cref="CloudFiles"/>) und
/// reicht ihn an die eigene Cloud-Warteschlange weiter; deren <see cref="CloudWorkerCount"/> Worker holen das Vorschaubild
/// über <see cref="ICloudThumbnailRenderer"/> (Shell, wie der Explorer) — in dieselbe Cache-Datei mit demselben DB-Status.
/// <para>
/// Ein Abruf dauert 0,35–2,8 s (Spike 19). Deshalb gilt in der Warteschlange: zuletzt angeforderte Kacheln zuerst (Rang je
/// Anforderung, innerhalb einer Anforderung deren Reihenfolge — die Galerie schickt sichtbare zuerst), höchstens
/// <see cref="MaxCloudBacklog"/> warten; ältere verfallen und werden beim nächsten Sichtbarwerden neu angefordert. Die
/// jüngste Anforderung in der Warteschlange verfällt erst über <see cref="MaxCloudQueue"/>, auch wenn sie allein mehr als
/// <see cref="MaxCloudBacklog"/> Platzhalter hat (Nachbesserung 0.5.41: sonst blieben sichtbare Kacheln leer, bis der
/// Benutzer scrollt). Ohne Shell-Weg (Tests, versteckte Sitzung) scheitert
/// der Auftrag mit <see cref="ThumbnailErrorCodes.CloudUnavailable"/>, ohne einen Versuch zu verbrauchen.
/// </para>
/// </summary>
public sealed partial class ThumbnailService
{
    /// <summary>Gleichzeitige Shell-Abrufe (Spike 19: 8 gleichzeitig ≈ 4 Bilder/s).</summary>
    public const int CloudWorkerCount = 8;

    /// <summary>
    /// Höchstens so viele Platzhalter warten auf ihr Vorschaubild; die am längsten nicht mehr angeforderten verfallen — die
    /// der jüngsten Anforderung erst über <see cref="MaxCloudQueue"/> (<see cref="CloudJobQueue"/>).
    /// </summary>
    public const int MaxCloudBacklog = 96;

    /// <summary>
    /// Harte Obergrenze der Warteschlange, auch für die jüngste Anforderung (die Galerie fordert sichtbar ± 12 an; Alben,
    /// Orte, Storys oder die App-Verbindung können mehr schicken) — darüber verfallen deren hintere.
    /// </summary>
    public const int MaxCloudQueue = 512;

    /// <summary>Abstand der Ränge zweier Anforderungen (größer als jede Anforderung lang ist).</summary>
    private const long RankStride = 1_000_000;

    private readonly ICloudThumbnailRenderer? _cloudRenderer;
    private readonly CloudJobQueue _cloudQueue;
    private readonly SemaphoreSlim _cloudSignal = new(0);
    private long _rankCounter;

    /// <summary>Wartende Platzhalter (Tests, Diagnose).</summary>
    internal int CloudQueueLength => _cloudQueue.Count;

    /// <summary>Rang-Basis einer neuen Anforderung: jede spätere liegt vor allen früheren.</summary>
    private long NextRankBase() => Interlocked.Add(ref _rankCounter, RankStride);

    private long RankOf(int fileId) => _states.TryGetValue(fileId, out var state) ? state.Rank : 0;

    /// <summary>
    /// Im Worker vor dem Rendern: liegt das Original nur online, kommt der Auftrag in die Cloud-Warteschlange (bleibt
    /// „eingereiht“, zählt weiter in <see cref="PendingCount"/>). Nur Attribute, kein Dateiinhalt.
    /// </summary>
    private bool TryRouteToCloud(ThumbnailRequest req)
    {
        if (!CloudFiles.IsOnlineOnly(req.SourcePath))
        {
            return false;
        }

        // Vorrang abgeben: ein Platzhalter wartet aufs Netz, nicht auf den Prozessor — Hintergrund-Analysen dürfen weiter
        while (_states.TryGetValue(req.FileId, out var state) && !state.Cloud)
        {
            if (_states.TryUpdate(req.FileId, state with { Cloud = true }, state))
            {
                if (_hold is not null && !state.Background)
                {
                    ForegroundJobDone();
                }

                break;
            }
        }

        foreach (var dropped in _cloudQueue.Add(req))
        {
            AbandonQueued(dropped);
        }

        _cloudSignal.Release();
        return true;
    }

    /// <summary>
    /// Ein verfallener Auftrag: Zustand verwerfen (die nächste Anforderung reiht neu ein), Zähler zurück. Den Vordergrund-
    /// Vorrang hat er schon beim Einreihen abgegeben (<see cref="TryRouteToCloud"/>).
    /// </summary>
    private void AbandonQueued(ThumbnailRequest req)
    {
        _states.TryRemove(req.FileId, out _);
        Interlocked.Decrement(ref _pending);
    }

    /// <summary>Aus <see cref="EnsureStarted"/>: die Cloud-Worker warten ohne eigenen Thread auf Aufträge.</summary>
    private void StartCloudWorkers()
    {
        for (var i = 0; i < CloudWorkerCount; i++)
        {
            _workers.Add(Task.Run(CloudWorkerLoopAsync));
        }

        _logger.LogDebug(
            "ThumbnailService: {Workers} Worker für OneDrive-Platzhalter bereit (Shell-Vorschaubild verfügbar: {Available})",
            CloudWorkerCount, _cloudRenderer?.IsAvailable == true);
    }

    private async Task CloudWorkerLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            ThumbnailRequest? current = null;
            try
            {
                await _cloudSignal.WaitAsync(token).ConfigureAwait(false);
                if (!_cloudQueue.TryTake(out var req))
                {
                    continue; // verfallen, bevor ein Worker ihn nahm
                }

                current = req;
                await EnterJobAsync(token).ConfigureAwait(false);
                try
                {
                    // Inzwischen lokal (geöffnet, „Immer behalten“)? Dann der normale Renderer.
                    await ProcessJobAsync(req, queued: true, cloud: CloudFiles.IsOnlineOnly(req.SourcePath)).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeJobs);
                }
            }
            catch (OperationCanceledException)
            {
                return; // Shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cloud-Worker: unerwartete Ausnahme (File={FileId}) — Worker läuft weiter", current?.FileId);
            }
        }
    }

    /// <summary>Shell-Vorschaubild; ohne Shell-Weg scheitert es als äußere Ursache (kein Versuch verbraucht, kein Download).</summary>
    private async Task<ThumbnailRenderResult> RenderCloudAsync(ThumbnailRequest req, string cachePath)
    {
        if (_cloudRenderer is not { IsAvailable: true } renderer)
        {
            return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.CloudUnavailable, "Platzhalter: kein Shell-Vorschaubild verfügbar");
        }

        try
        {
            return await renderer.RenderAsync(req.SourcePath, cachePath, Size, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.CloudUnavailable, ex.Message);
        }
    }
}

/// <summary>
/// Warteschlange der Platzhalter (ADR-0077): <see cref="TryTake"/> liefert den Auftrag mit dem höchsten Rang (zuletzt
/// angefordert, innerhalb einer Anforderung der vordere); über <see cref="MaxBacklog"/> verfallen die mit dem niedrigsten —
/// aber nie Aufträge der jüngsten Anforderung, die in der Warteschlange liegt (eine Anforderung = ein Rang-Block der Breite
/// <c>rankStride</c>). Eine große Anforderung (viele sichtbare Kacheln) wird so bis <see cref="HardLimit"/> nicht gekürzt;
/// erst die nächste Anforderung lässt die übrigen auf <see cref="MaxBacklog"/> schrumpfen. Der Rang kommt aus dem Zustand
/// des Dienstes und kann sich ändern (erneute Anforderung) — deshalb wird beim Nehmen gesucht. Thread-sicher.
/// </summary>
internal sealed class CloudJobQueue
{
    private readonly Lock _lock = new();
    private readonly List<ThumbnailRequest> _items = new();
    private readonly Func<int, long> _rank;
    private readonly long _rankStride;

    /// <param name="rankStride">Breite eines Rang-Blocks je Anforderung (1 = jeder Rang eine eigene Anforderung).</param>
    /// <param name="hardLimit">Obergrenze auch für die jüngste Anforderung (Standard: <paramref name="maxBacklog"/>).</param>
    public CloudJobQueue(int maxBacklog, Func<int, long> rank, long rankStride = 1, int? hardLimit = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBacklog, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rankStride, 1);
        MaxBacklog = maxBacklog;
        HardLimit = Math.Max(maxBacklog, hardLimit ?? maxBacklog);
        _rank = rank;
        _rankStride = rankStride;
    }

    public int MaxBacklog { get; }

    public int HardLimit { get; }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Reiht ein; liefert die Aufträge, die dafür verfallen (niedrigster Rang, aus der jüngsten Anforderung erst über <see cref="HardLimit"/>).</summary>
    public IReadOnlyList<ThumbnailRequest> Add(ThumbnailRequest request)
    {
        lock (_lock)
        {
            _items.Add(request);
            if (_items.Count <= MaxBacklog)
            {
                return [];
            }

            var newest = NewestRequest();
            var dropped = new List<ThumbnailRequest>();
            while (_items.Count > MaxBacklog)
            {
                var index = IndexOf(lowest: true);
                if (RequestOf(_rank(_items[index].FileId)) >= newest && _items.Count <= HardLimit)
                {
                    break; // nur noch Aufträge der jüngsten Anforderung — die verfallen nicht (Nachbesserung 0.5.41)
                }

                dropped.Add(_items[index]);
                _items.RemoveAt(index);
            }

            return dropped;
        }
    }

    /// <summary>Nummer der Anforderung eines Rangs (Ränge einer Anforderung liegen in (k − 1)·Breite … k·Breite); 0 = unbekannt.</summary>
    private long RequestOf(long rank) => rank <= 0 ? 0 : ((rank - 1) / _rankStride) + 1;

    private long NewestRequest()
    {
        var newest = 0L;
        foreach (var item in _items)
        {
            newest = Math.Max(newest, RequestOf(_rank(item.FileId)));
        }

        return newest;
    }

    public bool TryTake(out ThumbnailRequest request)
    {
        lock (_lock)
        {
            if (_items.Count == 0)
            {
                request = null!;
                return false;
            }

            var index = IndexOf(lowest: false);
            request = _items[index];
            _items.RemoveAt(index);
            return true;
        }
    }

    private int IndexOf(bool lowest)
    {
        var best = 0;
        var bestRank = _rank(_items[0].FileId);
        for (var i = 1; i < _items.Count; i++)
        {
            var rank = _rank(_items[i].FileId);
            if (lowest ? rank < bestRank : rank > bestRank)
            {
                best = i;
                bestRank = rank;
            }
        }

        return best;
    }
}
