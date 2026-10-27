using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Data;
using Lumora.Services.Infrastructure;
using Lumora.Services.Scanning;

namespace Lumora.Services.Thumbnails;

/// <summary>
/// Produktiver Thumbnail-Motor: zwei Hintergrund-Worker-Pools (Bild via WIC,
/// Video via FFmpeg) lesen aus getrennten Channels, dedupliziert über einen
/// In-Memory-State. DB-Status (Pending → Generated/Failed) wird serialisiert
/// geschrieben (Single-Writer via SemaphoreSlim, gegen SQLITE_BUSY). Atomic
/// .tmp+Move liegt in den Renderern. Siehe ADR-0007.
/// Bilder, die WIC nicht decodieren kann (ErrorCode <c>wic_decode_failed</c>), bekommen einen
/// zweiten Versuch über den optionalen <see cref="IFallbackImageThumbnailRenderer"/> (FFmpeg).
/// In-Memory-State: je FileId Ergebnis + <c>SourceModifiedAtUtc</c> der gerenderten Version. Ein
/// Request mit anderem Zeitstempel (Datei geändert, FileId nach Papierkorb/Rescan wiederverwendet)
/// verwirft den Eintrag und prüft neu; ein Done-Eintrag ohne Cache-Datei ebenso. Fehlschläge
/// bleiben für die Session gesperrt (kein Hämmern), außer bei „äußeren“ Ursachen
/// (<see cref="ThumbnailErrorCodes.SourceMissing"/>, <see cref="ThumbnailErrorCodes.FfmpegNotFound"/>):
/// die verbrauchen keinen <c>AttemptCount</c> und werden nach <see cref="RetryCooldown"/> erneut versucht.
/// <para>
/// Größe (ADR-0043): kurze Kante <see cref="Size"/> px (<see cref="ThumbnailSizing"/>). Vorschaubilder älterer Größen
/// (<see cref="ThumbnailSizing.LegacySizes"/>) werden gezeigt, bis das neue fertig ist, und danach samt Zeile gelöscht;
/// die Pflege im Hintergrund (<c>ThumbnailService.Maintenance.cs</c>) ersetzt die übrigen und trägt fehlende Bildmaße
/// nach. Beim Erzeugen schreibt der Dienst die Maße des Originals nach <c>Files.Width/Height</c> — in derselben
/// Transaktion wie den Status „Generated“.
/// </para>
/// </summary>
public sealed partial class ThumbnailService : IThumbnailService, IDisposable
{
    /// <summary>Kurze Kante der Vorschaubilder in px (<c>Thumbnails.Size</c>, Dateiname <c>{fileId}_400_still.jpg</c>).</summary>
    public const int Size = ThumbnailSizing.ShortEdge;

    /// <summary>Nach so vielen gescheiterten Versuchen wird eine Datei nicht mehr eingereiht.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Nach dieser Zeit wird ein Fehlschlag mit äußerer Ursache (Quelle fehlt, FFmpeg fehlt) erneut versucht.</summary>
    public static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(5);

    /// <summary>.tmp-Leichen, die älter sind, gehören keinem laufenden Job mehr und werden beim Start entfernt.</summary>
    public static readonly TimeSpan TempFileMaxAge = TimeSpan.FromMinutes(2);

    private const int ErrorMessageMaxLen = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly IImageThumbnailRenderer _imageRenderer;
    private readonly IVideoThumbnailRenderer _videoRenderer;
    private readonly IFallbackImageThumbnailRenderer? _fallbackRenderer;
    private readonly IScanGate? _scanGate;
    private readonly ILogger<ThumbnailService> _logger;

    private readonly Channel<ThumbnailRequest> _imageChannel =
        Channel.CreateUnbounded<ThumbnailRequest>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly Channel<ThumbnailRequest> _videoChannel =
        Channel.CreateUnbounded<ThumbnailRequest>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly ConcurrentDictionary<int, JobState> _states = new();
    private readonly SemaphoreSlim _dbWriteLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();
    private int _started;
    private int _pending;

    /// <summary>Vorrang, solange Vorschaubilder fehlen, auf die jemand wartet (Galerie, Lightbox-Leiste; ADR-0074 Nachtrag 0.5.15).</summary>
    private readonly Background.ForegroundHold? _hold;
    private readonly Lock _foregroundLock = new();
    private int _foregroundJobs;
    private IDisposable? _foregroundEntry;
    private int _disposed;
    private int _ffmpegMissingLogged;
    private readonly object _pauseLock = new();
    private TaskCompletionSource? _resumed; // gesetzt, solange pausiert (ADR-0026)
    private readonly Dictionary<int, ThumbnailRequest> _deferred = new(); // während der Pause angefordert; unter _pauseLock
    private int _activeJobs;

    private enum JobOutcome { Queued, Done, Failed }

    /// <summary>
    /// Zustand je FileId; <c>Retryable</c>-Fehlschläge dürfen nach <see cref="RetryCooldown"/> erneut laufen.
    /// <c>FallbackPath</c> = Vorschaubild einer älteren Größe, das gezeigt wird, solange das neue fehlt (ADR-0043);
    /// <c>Background</c> = Auftrag der Pflege im Hintergrund (nach Erfolg wird der Eintrag verworfen, damit 500 000
    /// ersetzte Dateien nicht im Speicher bleiben).
    /// </summary>
    private readonly record struct JobState(
        JobOutcome Outcome,
        DateTime SourceModifiedAtUtc,
        DateTime AtUtc,
        bool Retryable,
        string? FallbackPath = null,
        bool Background = false);

    public ThumbnailService(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        IImageThumbnailRenderer imageRenderer,
        IVideoThumbnailRenderer videoRenderer,
        ILogger<ThumbnailService> logger,
        IFallbackImageThumbnailRenderer? fallbackRenderer = null,
        IScanGate? scanGate = null,
        Background.IForegroundWork? foreground = null)
    {
        _hold = foreground is null ? null : new Background.ForegroundHold(foreground, Background.ForegroundNeed.Cpu, "Vorschaubilder");
        _factory = factory;
        _paths = paths;
        _imageRenderer = imageRenderer;
        _videoRenderer = videoRenderer;
        _fallbackRenderer = fallbackRenderer;
        _scanGate = scanGate;
        _logger = logger;
    }

    public int ThumbnailSize => Size;

    /// <summary>Eingereihte/laufende Aufträge plus die noch zu ersetzenden Vorschaubilder älterer Größe (Statusleiste).</summary>
    public int PendingCount => Volatile.Read(ref _pending) + Math.Max(0, Volatile.Read(ref _upgradeRemaining));

    public event Action<ThumbnailReadyEventArgs>? ThumbnailReady;

    public void Invalidate(IReadOnlyList<int> fileIds)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        foreach (var id in fileIds)
        {
            // Laufende Jobs behalten ihren Eintrag (Dedupe); der nächste Request validiert ihn ohnehin.
            if (_states.TryGetValue(id, out var state) && state.Outcome != JobOutcome.Queued)
            {
                _states.TryRemove(new KeyValuePair<int, JobState>(id, state));
            }
        }
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        lock (_pauseLock)
        {
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        while (Volatile.Read(ref _activeJobs) > 0)
        {
            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("ThumbnailService pausiert ({Pending} Aufträge warten)", PendingCount);
    }

    public void Resume()
    {
        TaskCompletionSource? resumed;
        List<ThumbnailRequest> deferred;
        lock (_pauseLock)
        {
            resumed = _resumed;
            _resumed = null;
            deferred = [.. _deferred.Values];
            _deferred.Clear();
        }

        if (resumed is not null)
        {
            resumed.TrySetResult();
            _logger.LogInformation("ThumbnailService fortgesetzt ({Deferred} zurückgestellte Anforderungen)", deferred.Count);
        }

        if (deferred.Count > 0)
        {
            // Nach der Rücknahme eines Umzugs sofort nachholen — nicht erst mit dem nächsten Seitenwechsel der Galerie.
            _ = Task.Run(() => ResubmitDeferredAsync(deferred));
        }
    }

    private async Task ResubmitDeferredAsync(IReadOnlyList<ThumbnailRequest> deferred)
    {
        try
        {
            await RequestThumbnailsAsync(deferred).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Zurückgestellte Thumbnail-Anforderungen konnten nicht nachgeholt werden");
        }
    }

    /// <summary>
    /// Pausiert: Anforderungen je FileId (die neueste gewinnt) zurückstellen statt sie zu verwerfen; <see cref="Resume"/>
    /// reicht sie nach. Unter derselben Sperre wie <see cref="Resume"/>, damit keine zwischen Prüfen und Merken verloren geht.
    /// </summary>
    private bool TryDefer(IReadOnlyList<ThumbnailRequest> requests)
    {
        lock (_pauseLock)
        {
            if (_resumed is null)
            {
                return false;
            }

            foreach (var req in requests)
            {
                if (req.MediaType != MediaType.Archive)
                {
                    _deferred[req.FileId] = req;
                }
            }

            return true;
        }
    }

    /// <summary>Belegt einen Job-Platz; ist der Dienst pausiert, gibt er ihn wieder frei und wartet auf <see cref="Resume"/>.</summary>
    private async Task EnterJobAsync(CancellationToken token)
    {
        while (true)
        {
            Interlocked.Increment(ref _activeJobs);
            Task? resumed;
            lock (_pauseLock)
            {
                resumed = _resumed?.Task;
            }

            if (resumed is null)
            {
                return;
            }

            Interlocked.Decrement(ref _activeJobs);
            await resumed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public void InvalidateAll()
    {
        // ConcurrentDictionary erlaubt Entfernen während der Enumeration; laufende Jobs bleiben (Dedupe).
        foreach (var (id, state) in _states)
        {
            if (state.Outcome != JobOutcome.Queued)
            {
                _states.TryRemove(new KeyValuePair<int, JobState>(id, state));
            }
        }
    }

    public Task RequestThumbnailsAsync(
        IReadOnlyList<ThumbnailRequest> requests,
        CancellationToken cancellationToken = default)
        => RequestCoreAsync(requests, background: false, cancellationToken);

    /// <param name="background">
    /// Auftrag der Pflege (<c>Maintenance</c>): meldet keine Treffer und keine Vorschaubilder älterer Größe (niemand
    /// wartet darauf), nur neu erzeugte; der Zustand wird nach Erfolg verworfen.
    /// </param>
    /// <returns>false, wenn ein Pflege-Auftrag wegen Pause nicht angenommen wurde (die Pflege versucht ihn später).</returns>
    private async Task<bool> RequestCoreAsync(
        IReadOnlyList<ThumbnailRequest> requests,
        bool background,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return true;
        }

        if (background && IsPaused)
        {
            return false; // nicht zurückstellen: Resume reichte sie sonst als Galerie-Anforderungen nach
        }

        if (TryDefer(requests))
        {
            // Katalog-DB zieht um (ADR-0026): sie ist gesperrt. Nach einer Rücknahme reicht Resume die Anforderungen
            // nach; nach Erfolg startet die App neu und die Galerie fordert ohnehin neu an.
            _logger.LogDebug("ThumbnailService pausiert — {Count} Anforderungen zurückgestellt", requests.Count);
            return true;
        }

        EnsureStarted();

        // 1) Schon bekannte Files direkt behandeln, Rest sammeln.
        var toCheck = new List<ThumbnailRequest>(requests.Count);
        foreach (var req in requests)
        {
            // Archive (ADR-0022) haben kein Vorschaubild: weder DB-Zeile noch Renderer noch Event — die Kachel zeigt ein Icon.
            if (req.MediaType == MediaType.Archive)
            {
                continue;
            }

            if (TryHandleKnown(req, announce: !background))
            {
                continue;
            }

            toCheck.Add(req);
        }

        if (toCheck.Count == 0)
        {
            return true;
        }

        // 2) Batch-DB-Check: existierende Thumbnail-Einträge für diese FileIds (ein Query) — aktuelle Größe und ältere
        //    Größen (ADR-0043: das alte wird gezeigt, bis das neue fertig ist).
        var ids = toCheck.Select(r => r.FileId).Distinct().ToList();
        var legacySizes = ThumbnailSizing.LegacySizes;
        Dictionary<int, Thumbnail> existing;
        Dictionary<int, Thumbnail> legacy;
        try
        {
            await using var ctx = _factory.Create();
            var rows = await ctx.Thumbnails
                .AsNoTracking()
                .Where(t => t.Kind == ThumbnailKind.Still && ids.Contains(t.FileId) && (t.Size == Size || legacySizes.Contains(t.Size)))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            existing = rows.Where(t => t.Size == Size).ToDictionary(t => t.FileId);
            legacy = rows.Where(t => t.Size != Size && t.Status == ThumbnailStatus.Generated)
                .GroupBy(t => t.FileId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Size).First());
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail-Batch-Check fehlgeschlagen");
            existing = new Dictionary<int, Thumbnail>();
            legacy = new Dictionary<int, Thumbnail>();
        }

        // 3) Pro Request entscheiden: cache-hit, skip (zu oft gescheitert / kein FFmpeg) oder enqueue.
        foreach (var req in toCheck)
        {
            existing.TryGetValue(req.FileId, out var row);

            var cacheHit = row is { Status: ThumbnailStatus.Generated }
                && row.SourceModifiedAtUtc == req.SourceModifiedAtUtc
                && File.Exists(row.CachePath);
            if (cacheHit)
            {
                if (background)
                {
                    continue; // schon in der neuen Größe — nichts zu tun, nichts zu merken
                }

                SetState(req, JobOutcome.Done);
                RaiseReady(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, row!.CachePath));
                continue;
            }

            // Älteres Vorschaubild derselben Dateiversion: sofort zeigen, das neue entsteht im Hintergrund.
            string? fallback = null;
            if (legacy.TryGetValue(req.FileId, out var old)
                && old.SourceModifiedAtUtc == req.SourceModifiedAtUtc
                && File.Exists(old.CachePath))
            {
                fallback = old.CachePath;
                if (!background)
                {
                    RaiseReady(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, fallback));
                }
            }

            if (row is { Status: ThumbnailStatus.Failed } && row.AttemptCount >= MaxAttempts)
            {
                if (!background)
                {
                    SetState(req, JobOutcome.Failed, fallbackPath: fallback); // Pflege merkt sich nichts (Speicher)
                }

                continue;
            }

            if (req.MediaType == MediaType.Video && !_videoRenderer.IsAvailable)
            {
                if (Interlocked.Exchange(ref _ffmpegMissingLogged, 1) == 0)
                {
                    _logger.LogWarning("FFmpeg nicht gefunden — Video-Thumbnails werden in dieser Sitzung übersprungen");
                }

                if (!background)
                {
                    SetState(req, JobOutcome.Failed, fallbackPath: fallback);
                }

                continue;
            }

            if (_states.TryAdd(req.FileId, new JobState(JobOutcome.Queued, req.SourceModifiedAtUtc, DateTime.UtcNow, false, fallback, background)))
            {
                var channel = req.MediaType == MediaType.Video ? _videoChannel : _imageChannel;
                if (channel.Writer.TryWrite(req))
                {
                    Interlocked.Increment(ref _pending);
                    if (!background)
                    {
                        ForegroundJobQueued();
                    }
                }
                else
                {
                    _states.TryRemove(req.FileId, out _);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Behandelt einen Request über den In-Memory-State. True = erledigt (Event gefeuert, läuft schon
    /// oder bewusst übersprungen); false = Eintrag fehlt/ungültig → DB-Prüfung nötig.
    /// </summary>
    /// <param name="announce">false für Aufträge der Pflege: nichts melden.</param>
    private bool TryHandleKnown(ThumbnailRequest req, bool announce)
    {
        if (!_states.TryGetValue(req.FileId, out var state))
        {
            return false;
        }

        if (state.Outcome == JobOutcome.Queued)
        {
            // in Arbeit — auch für eine andere Version: der nächste Request validiert neu. Bis dahin das ältere zeigen.
            if (announce && state.SourceModifiedAtUtc == req.SourceModifiedAtUtc)
            {
                AnnounceFallback(req, state);
            }

            return true;
        }

        if (state.SourceModifiedAtUtc != req.SourceModifiedAtUtc)
        {
            // Datei geändert oder FileId wiederverwendet (Papierkorb/Rescan) → Zustand verwerfen.
            _states.TryRemove(new KeyValuePair<int, JobState>(req.FileId, state));
            return false;
        }

        if (state.Outcome == JobOutcome.Done)
        {
            var path = BuildCachePath(req.LibraryId, req.FileId);
            if (File.Exists(path))
            {
                if (announce)
                {
                    RaiseReady(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, path));
                }

                return true;
            }

            // Cache-Datei fehlt (gelöscht, Cache geleert) → neu rendern.
            _states.TryRemove(new KeyValuePair<int, JobState>(req.FileId, state));
            return false;
        }

        // Failed: für die Session gesperrt — außer äußere Ursache und Cooldown abgelaufen.
        if (state.Retryable && DateTime.UtcNow - state.AtUtc >= RetryCooldown)
        {
            _states.TryRemove(new KeyValuePair<int, JobState>(req.FileId, state));
            return false;
        }

        if (announce)
        {
            AnnounceFallback(req, state); // neues gescheitert: das ältere bleibt sichtbar
        }

        return true;
    }

    private void AnnounceFallback(ThumbnailRequest req, JobState state)
    {
        if (state.FallbackPath is { } fallback && File.Exists(fallback))
        {
            RaiseReady(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, fallback));
        }
    }

    /// <summary>Setzt den Zustand; <paramref name="fallbackPath"/> null übernimmt den Pfad des bisherigen Eintrags.</summary>
    private void SetState(ThumbnailRequest req, JobOutcome outcome, bool retryable = false, string? fallbackPath = null)
    {
        _states.TryGetValue(req.FileId, out var previous);
        _states[req.FileId] = new JobState(
            outcome, req.SourceModifiedAtUtc, DateTime.UtcNow, retryable, fallbackPath ?? previous.FallbackPath, previous.Background);
    }

    private void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        // UI-Reaktion vor Durchsatz: höchstens die Hälfte der logischen Kerne, maximal 6 (PLAN Kap. 11 nennt
        // CPU-2 für SSD-Profile; damit war der UI-Thread beim Erstaufbau von 500 großen JPEGs ausgehungert).
        var imageWorkers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        for (var i = 0; i < imageWorkers; i++)
        {
            _workers.Add(Task.Run(() => WorkerLoopAsync(_imageChannel.Reader)));
        }

        // Video: max. 1 Worker (FFmpeg ist teuer, ADR-0007).
        _workers.Add(Task.Run(() => WorkerLoopAsync(_videoChannel.Reader)));

        // Pflege: ältere Größen ersetzen, fehlende Bildmaße nachtragen — verzögert und nur bei leerer Warteschlange.
        StartMaintenance();

        // .tmp-Leichen aus abgestürzten Sitzungen entfernen (nur alte, damit laufende Jobs unberührt bleiben).
        _ = Task.Run(CleanupTempFiles);

        _logger.LogInformation(
            "ThumbnailService gestartet: {ImageWorkers} Bild-Worker + 1 Video-Worker (FFmpeg verfügbar: {Ffmpeg})",
            imageWorkers, _videoRenderer.IsAvailable);
    }

    /// <summary>Worker-Schleife: Ausnahmen werden geloggt, der Worker läuft weiter (Pool schrumpft nicht); Ende nur bei Shutdown.</summary>
    private async Task WorkerLoopAsync(ChannelReader<ThumbnailRequest> reader)
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            ThumbnailRequest? current = null;
            try
            {
                if (!await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    return; // Channel abgeschlossen
                }

                while (reader.TryRead(out var req))
                {
                    current = req;
                    await EnterJobAsync(token).ConfigureAwait(false);
                    try
                    {
                        await ProcessJobAsync(req).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeJobs);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return; // Shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Thumbnail-Worker: unerwartete Ausnahme (File={FileId}) — Worker läuft weiter", current?.FileId);
            }
        }
    }

    private void ForegroundJobQueued()
    {
        if (_hold is null)
        {
            return;
        }

        lock (_foregroundLock)
        {
            if (_foregroundJobs++ == 0)
            {
                _foregroundEntry = _hold.Enter();
            }
        }
    }

    private void ForegroundJobDone()
    {
        IDisposable? entry = null;
        lock (_foregroundLock)
        {
            if (_foregroundJobs > 0 && --_foregroundJobs == 0)
            {
                (entry, _foregroundEntry) = (_foregroundEntry, null);
            }
        }

        entry?.Dispose();
    }

    private async Task ProcessJobAsync(ThumbnailRequest req)
    {
        // Auftrag der Galerie (nicht der Pflege): zählt für den Vorrang, bis er fertig ist
        var foreground = _hold is not null && _states.TryGetValue(req.FileId, out var queued) && !queued.Background;
        var cachePath = BuildCachePath(req.LibraryId, req.FileId);
        try
        {
            if (!File.Exists(req.SourcePath))
            {
                // Quelle fehlt (Offline-Root, gelöscht): kein Renderer, kein Versuch verbraucht.
                var missing = ThumbnailRenderResult.Fail(ThumbnailErrorCodes.SourceMissing, $"Quelldatei fehlt: {req.SourcePath}");
                await UpsertStatusAsync(req, ThumbnailStatus.Failed, cachePath, missing, attemptDelta: 0).ConfigureAwait(false);
                SetState(req, JobOutcome.Failed, retryable: true);
                return;
            }

            await UpsertStatusAsync(req, ThumbnailStatus.Pending, cachePath, null, attemptDelta: +1).ConfigureAwait(false);

            ThumbnailRenderResult result = req.MediaType == MediaType.Video
                ? await _videoRenderer.RenderAsync(req.SourcePath, cachePath, Size, _cts.Token).ConfigureAwait(false)
                : await RenderImageWithFallbackAsync(req, cachePath).ConfigureAwait(false);

            if (result.Success)
            {
                // Das Thumbnail existiert — ein Fehler beim Status-Schreiben darf es nicht verstecken
                // (die Zeile bleibt Pending → beim nächsten Start Stale → erneut gerendert).
                try
                {
                    await UpsertStatusAsync(req, ThumbnailStatus.Generated, cachePath, result, attemptDelta: 0).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Thumb erzeugt, aber DB-Status konnte nicht geschrieben werden: File={FileId}", req.FileId);
                }

                if (_states.TryGetValue(req.FileId, out var finished) && finished.Background)
                {
                    _states.TryRemove(req.FileId, out _); // Pflege: nicht 500 000 Einträge im Speicher halten
                }
                else
                {
                    SetState(req, JobOutcome.Done);
                }

                RaiseReady(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, cachePath));
            }
            else
            {
                var retryable = IsRetryable(result.ErrorCode);
                await UpsertStatusAsync(req, ThumbnailStatus.Failed, cachePath, result, attemptDelta: retryable ? -1 : 0).ConfigureAwait(false);
                SetState(req, JobOutcome.Failed, retryable);
                _logger.LogDebug(
                    "Thumb fehlgeschlagen: File={FileId} {Code}: {Msg}",
                    req.FileId, result.ErrorCode, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            _states.TryRemove(req.FileId, out _);
        }
        catch (Exception ex)
        {
            SetState(req, JobOutcome.Failed);
            _logger.LogWarning(ex, "Thumb-Job-Ausnahme: File={FileId}", req.FileId);
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
            if (foreground)
            {
                ForegroundJobDone();
            }
        }
    }

    /// <summary>Fehlschläge mit äußerer Ursache: die Datei selbst ist nicht „kaputt“, ein späterer Versuch kann gelingen.</summary>
    private static bool IsRetryable(string? errorCode)
        => errorCode is ThumbnailErrorCodes.SourceMissing or ThumbnailErrorCodes.FfmpegNotFound;

    /// <summary>
    /// Bild via WIC; scheitert WIC am Decoding (<see cref="ThumbnailErrorCodes.WicDecodeFailed"/>)
    /// und ist der Fallback-Renderer verfügbar, zweiter Versuch via FFmpeg. Erst danach Failed —
    /// mit dem ErrorCode des Fallbacks und beiden Fehlertexten.
    /// </summary>
    private async Task<ThumbnailRenderResult> RenderImageWithFallbackAsync(ThumbnailRequest req, string cachePath)
    {
        var result = RenderImageLowPriority(req.SourcePath, cachePath);
        if (result.Success
            || result.ErrorCode != ThumbnailErrorCodes.WicDecodeFailed
            || _fallbackRenderer is not { IsAvailable: true })
        {
            return result;
        }

        _logger.LogDebug("WIC konnte {Path} nicht decodieren — FFmpeg-Fallback", req.SourcePath);
        var fallback = await _fallbackRenderer.RenderAsync(req.SourcePath, cachePath, Size, _cts.Token).ConfigureAwait(false);
        if (fallback.Success)
        {
            return fallback;
        }

        return ThumbnailRenderResult.Fail(
            fallback.ErrorCode ?? ThumbnailErrorCodes.FfmpegFailed,
            $"WIC: {result.ErrorMessage} | FFmpeg: {fallback.ErrorMessage}");
    }

    /// <summary>Abonnenten laufen auf dem Worker-Thread; ihre Ausnahmen dürfen weder Job-Zustand noch Worker beschädigen.</summary>
    private void RaiseReady(ThumbnailReadyEventArgs args)
    {
        try
        {
            ThumbnailReady?.Invoke(args);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThumbnailReady-Abonnent warf eine Ausnahme (File={FileId})", args.FileId);
        }
    }

    /// <param name="attemptDelta">+1 beim Start eines Versuchs (Pending), −1 wenn ein Fehlschlag mit äußerer Ursache den Versuch nicht zählen soll.</param>
    private async Task UpsertStatusAsync(
        ThumbnailRequest req,
        ThumbnailStatus status,
        string cachePath,
        ThumbnailRenderResult? result,
        int attemptDelta)
    {
        List<string> legacyFiles = [];
        await _dbWriteLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();

            // „Generated“ ändert drei Dinge auf einmal (ADR-0043): Status, ältere Größen weg, Bildmaße in Files.
            await using var tx = status == ThumbnailStatus.Generated
                ? await ctx.Database.BeginTransactionAsync(_cts.Token).ConfigureAwait(false)
                : null;
            var entity = await ctx.Thumbnails
                .FindAsync(new object[] { req.FileId, Size, ThumbnailKind.Still }, _cts.Token)
                .ConfigureAwait(false);

            if (entity is null)
            {
                entity = new Thumbnail
                {
                    FileId = req.FileId,
                    Size = Size,
                    Kind = ThumbnailKind.Still,
                    Format = "jpg",
                    CachePath = cachePath,
                    SourceModifiedAtUtc = req.SourceModifiedAtUtc,
                    Status = status,
                };
                ctx.Thumbnails.Add(entity);
            }
            else
            {
                entity.CachePath = cachePath;
                entity.SourceModifiedAtUtc = req.SourceModifiedAtUtc;
                entity.Status = status;
            }

            entity.AttemptCount = Math.Max(0, entity.AttemptCount + attemptDelta);

            switch (status)
            {
                case ThumbnailStatus.Pending:
                    entity.LastAttemptAtUtc = DateTime.UtcNow;
                    break;
                case ThumbnailStatus.Generated when result is not null:
                    entity.GeneratedAtUtc = DateTime.UtcNow;
                    entity.ByteSize = result.ByteSize;
                    entity.ErrorCode = null;
                    entity.ErrorMessage = null;
                    break;
                case ThumbnailStatus.Failed when result is not null:
                    entity.LastAttemptAtUtc ??= DateTime.UtcNow;
                    entity.ErrorCode = result.ErrorCode;
                    entity.ErrorMessage = Truncate(result.ErrorMessage, ErrorMessageMaxLen);
                    break;
            }

            if (status == ThumbnailStatus.Generated)
            {
                var fileId = req.FileId;
                var oldRows = await ctx.Thumbnails
                    .Where(t => t.FileId == fileId && t.Kind == ThumbnailKind.Still && t.Size != Size)
                    .ToListAsync(_cts.Token)
                    .ConfigureAwait(false);
                legacyFiles = oldRows.Select(t => t.CachePath).ToList();
                ctx.Thumbnails.RemoveRange(oldRows);
            }

            await ctx.SaveChangesAsync(_cts.Token).ConfigureAwait(false);

            if (status == ThumbnailStatus.Generated && result is { SourceWidth: > 0, SourceHeight: > 0 })
            {
                await WriteDimensionsAsync(ctx, req.FileId, req.SourceModifiedAtUtc, result.SourceWidth, result.SourceHeight, _cts.Token)
                    .ConfigureAwait(false);
            }

            if (tx is not null)
            {
                await tx.CommitAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _dbWriteLock.Release();
        }

        // Erst nach dem Commit: eine Galerie, die das alte noch lädt, liest mit FileShare.Delete.
        foreach (var path in legacyFiles)
        {
            TryDeleteFile(path);
        }
    }

    /// <summary>
    /// Schreibt die Maße des Originals (nach EXIF-Drehung) nach Files — nur für die Dateiversion, die gelesen wurde
    /// (<paramref name="sourceModifiedAtUtc"/>; eine inzwischen ersetzte Datei bekommt keine alten Maße), und nur, wenn
    /// sie sich ändern. 0 × 0 = „nicht lesbar“ (die Pflege versucht es dann nicht in jeder Sitzung erneut).
    /// </summary>
    private static Task<int> WriteDimensionsAsync(
        LibraryContext ctx, int fileId, DateTime sourceModifiedAtUtc, int width, int height, CancellationToken ct)
        => ctx.Files
            .Where(f => f.Id == fileId && f.ModifiedAtUtc == sourceModifiedAtUtc
                        && (f.Width == null || f.Height == null || f.Width != width || f.Height != height))
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.Width, width).SetProperty(f => f.Height, height), ct);

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Altes Vorschaubild konnte nicht gelöscht werden: {Path}", path);
        }
    }

    /// <summary>Entfernt alte <c>.tmp</c>-Dateien unter <c>thumbs\{libraryId}\</c> — Reste abgestürzter Renderer-Läufe.</summary>
    private void CleanupTempFiles()
    {
        try
        {
            var root = _paths.ThumbnailsRoot;
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - TempFileMaxAge;
            var removed = 0;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                foreach (var tmp in Directory.EnumerateFiles(dir, "*.tmp"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(tmp) <= cutoff)
                        {
                            File.Delete(tmp);
                            removed++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Thumbnail-.tmp konnte nicht entfernt werden: {Path}", tmp);
                    }
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation("Thumbnail-Cache: {Count} verwaiste .tmp-Dateien entfernt", removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thumbnail-.tmp-Bereinigung übersprungen");
        }
    }

    /// <summary>WIC-Decode ist CPU-lastig: während des Renderns läuft der Thread mit niedriger Priorität, damit UI und Eingabe Vorrang haben.</summary>
    private ThumbnailRenderResult RenderImageLowPriority(string sourcePath, string cachePath)
    {
        var thread = Thread.CurrentThread;
        var previous = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.BelowNormal;
            return _imageRenderer.Render(sourcePath, cachePath, Size);
        }
        finally
        {
            thread.Priority = previous;
        }
    }

    private string BuildCachePath(int libraryId, int fileId)
        => Path.Combine(_paths.GetLibraryThumbnailsPath(libraryId), $"{fileId}_{Size}_still.jpg");

    private static string? Truncate(string? value, int maxLen)
        => value is not null && value.Length > maxLen ? value[..maxLen] : value;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _hold?.Dispose();
        _imageChannel.Writer.TryComplete();
        _videoChannel.Writer.TryComplete();
        try
        {
            Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Worker wurden gecancelt — AggregateException erwartet
        }

        _cts.Dispose();
        _dbWriteLock.Dispose();
    }
}
