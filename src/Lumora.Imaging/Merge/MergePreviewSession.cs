using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge.Panorama;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Worker;
using Lumora.Services.Activity;
using Lumora.Services.Background;
using Lumora.Services.Merge;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Services.Operations;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Usage;

namespace Lumora.Imaging.Merge;

/// <summary>
/// Vorschau-Sitzung und Speichern (ADR-0078 Punkte 10–14, Bau Stufe 1 Teil A). Ein Hilfsprozess je Sitzung hält Quellen und
/// Ausrichtung; Speichern rechnet in einem frischen Hilfsprozess aus dem Rezept. Vorrang nur, solange gerechnet wird
/// (Vorschau: <see cref="ForegroundHold"/>; Speichern: <see cref="IForegroundWork"/> bis zum Eintrag im Katalog). Ein schwerer
/// Lauf zur Zeit (<see cref="IImagingWorkerHost.EnterHeavyAsync"/>). Zwischenstände nur im Temp-Ordner des Katalogs.
/// </summary>
internal sealed class MergePreviewSession : IMergePreviewSession
{
    /// <summary>Wiederholungen bei einer Prüfung, die erst beim Zusammensetzen scheitert (Zuschnitthöhe, Nähte).</summary>
    private const int ComposeRetries = 2;

    private const int BaseSeed = 20_781;

    private readonly MergeService _owner;
    private readonly string _workDir;
    private readonly ForegroundHold _hold;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Lock _ctsLock = new();
    private CancellationTokenSource? _current;
    private IPanoramaStitchSession? _stitch;
    private EstimateResult? _estimate;
    private List<MergeSourceInfo> _sources = [];
    private List<SourceSpec> _specs = [];
    private int _seed = BaseSeed;
    private int _previewCounter;
    private IDisposable? _saveClaim;
    private Action<int>? _setSaveThreads;
    private int _disposed;
    private int _saving;

    public MergePreviewSession(MergeService owner, MergeRequest request)
    {
        _owner = owner;
        Request = request;
        Options = request.Options ?? new MergeOptions();
        _workDir = Path.Combine(owner.Host.WorkRoot, Guid.NewGuid().ToString("N"));
        _hold = new ForegroundHold(owner.Foreground, ForegroundNeed.Cpu, "Zusammenfügen");
    }

    public MergeRequest Request { get; }

    public MergeAnalysis? Analysis { get; private set; }

    public MergePreview? Current { get; private set; }

    public MergeOptions Options { get; private set; }

    public bool IsBusy => _lock.CurrentCount == 0;

    public event Action<MergeProgress>? ProgressChanged;

    // ------------------------------------------------------------------ Vorschau

    public async Task<MergePreview> StartAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var linked = Begin(ct); // „Abbrechen“ und Schließen wirken schon während der Prüfung der Quellen
            ct = linked.Token;
            var analysis = await _owner.AnalyzeAsync(Request.FileIds, ct).ConfigureAwait(false);
            Analysis = analysis;
            if (analysis.MixedLibraries)
            {
                throw new MergeException("Die Fotos stammen aus verschiedenen Bibliotheken — Zusammenfügen geht nur innerhalb einer Bibliothek.");
            }

            _sources = analysis.Sources.Where(s => s.IsUsable || (Request.AllowOnlineOnlyDownload && s.State == MergeSourceState.OnlineOnly)).ToList();
            if (_sources.Count < MergeLimits.MinSources)
            {
                throw new MergeException("Mindestens zwei lesbare Fotos anhaken.");
            }

            if (_sources.Count > MergeLimits.MaxPanoramaSources)
            {
                throw new MergeException($"Panorama: höchstens {MergeLimits.MaxPanoramaSources} Fotos.");
            }

            var paths = await PathsAsync(_sources.Select(s => s.FileId).ToList(), ct).ConfigureAwait(false);
            _specs = _sources.Select((s, i) => new SourceSpec { Path = paths[s.FileId], Index = i }).ToList();
            Options = await ResolveAsync(Options, ct).ConfigureAwait(false);
            return await RunPreviewAsync(Options, estimate: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MergePreview> UpdateAsync(MergeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var linked = Begin(ct);
            ct = linked.Token;
            if (_specs.Count == 0)
            {
                throw new MergeException("Erst die Vorschau starten.");
            }

            var resolved = await ResolveAsync(options, ct).ConfigureAwait(false);
            // nur Tonwert-Angleich bzw. Farbverstärkung geändert: der Hilfsprozess tont nur neu — nie neu ausrichten, auch nicht
            // als Wiederholung nach einer nicht bestandenen Prüfung (die hängt nicht am Look)
            var lookOnly = Current is not null && resolved.SameGeometry(Options);
            Options = resolved;
            // abgestürzter Hilfsprozess: neu öffnen und mit demselben Startwert neu ausrichten
            var estimate = _stitch is null || !_stitch.IsAlive || _estimate is null;
            return await RunPreviewAsync(Options, estimate, ct, allowRetry: !lookOnly).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<MergeOptions> ResolveAsync(MergeOptions options, CancellationToken ct)
    {
        if (options.SphereForm is not null && options.ColorBoost is not null)
        {
            return options with { ColorBoost = Math.Clamp(options.ColorBoost.Value, MergeLimits.ColorBoostOff, MergeLimits.ColorBoostMax) };
        }

        var settings = await _owner.SettingsStore.LoadAsync(ct).ConfigureAwait(false);
        return options with
        {
            SphereForm = options.SphereForm ?? settings.SphereForm,
            ColorBoost = Math.Clamp(options.ColorBoost ?? settings.ColorBoost, MergeLimits.ColorBoostOff, MergeLimits.ColorBoostMax),
        };
    }

    private async Task<MergePreview> RunPreviewAsync(MergeOptions options, bool estimate, CancellationToken token, bool allowRetry = true)
    {
        var activity = _owner.Activities?.Start(new ActivityStartInfo(
            Lumora.Services.Activity.ActivityKind.Merge,
            "Zusammenfügen – Vorschau",
            string.Create(CultureInfo.GetCultureInfo("de-DE"), $"{_specs.Count:N0} Fotos"),
            UserInitiated: true,
            KeepInRecent: false,
            Cancel: Cancel));
        var clock = Stopwatch.StartNew();
        try
        {
            using var heavy = await EnterHeavyAsync(activity, token).ConfigureAwait(false);
            using var priority = _hold.Enter();
            Directory.CreateDirectory(_workDir);
            ComposeResult composed;
            var attempt = 0;
            while (true)
            {
                var stitch = _stitch;
                if (estimate || _estimate is null)
                {
                    if (stitch is null || !stitch.IsAlive)
                    {
                        await DisposeStitchAsync().ConfigureAwait(false);
                        stitch = await _owner.Stitcher.OpenSessionAsync(Environment.ProcessorCount, token).ConfigureAwait(false);
                        _stitch = stitch;
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            await DisposeStitchAsync().ConfigureAwait(false); // Sitzung inzwischen geschlossen: kein verwaister Prozess
                            throw new OperationCanceledException(token);
                        }
                    }

                    _estimate = await stitch.EstimateAsync(_specs, MergeLimits.PreviewDecodeEdge, _seed, p => Report(p, saving: false, estimating: true, activity), token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                if (stitch is null)
                {
                    throw new OperationCanceledException(token); // Sitzung geschlossen
                }

                var number = Interlocked.Increment(ref _previewCounter);
                var path = Path.Combine(_workDir, $"preview-{number:000}.jpg");
                var framePath = options.Crop == MergeCrop.Straight ? Path.Combine(_workDir, $"frame-{number:000}.jpg") : null;
                composed = await stitch.ComposeAsync(Spec(options, preview: true), path, 85, p => Report(p, saving: false, estimating: false, activity), token, framePath).ConfigureAwait(false);
                if (composed.Check.Passed || !allowRetry || attempt >= ComposeRetries || !composed.Check.Problems.Any(p => p is "crop-height" or "crop-size" or "tilted" or "residual" or "seams"))
                {
                    break;
                }

                // gekippter Lauf: Wiederholung mit anderem festen Startwert (der Hilfsprozess probierte schon _seed … _seed+2)
                attempt++;
                _seed += PanoramaRetryStride;
                estimate = true;
                TryDelete(path);
            }

            DeleteOlderPreviews(composed.OutputPath, composed.Frame?.Path);
            var preview = ToPreview(composed, options, clock.Elapsed);
            Current = preview;
            activity?.Dismiss();
            return preview;
        }
        catch (OperationCanceledException)
        {
            activity?.Dismiss();
            throw;
        }
        catch (ImagingWorkerException ex)
        {
            activity?.Dismiss();
            if (ex.Failure is ImagingFailure.Crashed or ImagingFailure.TimedOut)
            {
                _estimate = null; // beim nächsten Mal neu öffnen und ausrichten
            }

            throw new MergeException(ex.Failure == ImagingFailure.Error ? MergeTexts.Error(ex.Code, null) : ex.Message, ex);
        }
        catch
        {
            activity?.Dismiss();
            throw;
        }
    }

    /// <summary>Abstand der Startwerte zwischen zwei Wiederholungen (der Hilfsprozess nutzt je Schätzung drei aufeinanderfolgende).</summary>
    private const int PanoramaRetryStride = 3;

    private MergePreview ToPreview(ComposeResult r, MergeOptions options, TimeSpan elapsed)
    {
        var dropped = _estimate?.Alignment.Dropped.Where(i => i >= 0 && i < _sources.Count).Select(i => _sources[i].FileId).ToList() ?? [];
        var notes = (_estimate?.Notes ?? []).Concat(r.Notes).Distinct()
            .Select(code => MergeTexts.Note(code, r.FullWidth, r.FullHeight, r.Scale, r.FullPeakBytes)).Where(n => n is not null).Select(n => n!).ToList();
        return new MergePreview(
            r.OutputPath,
            r.Width,
            r.Height,
            r.FullWidth,
            r.FullHeight,
            (PanoramaProjection)(int)r.Projection,
            (PanoramaProjection)(int)(_estimate?.SuggestedProjection ?? WireProjection.Auto),
            options.SphereForm ?? MergeSphereForm.Band,
            Quality(r.Check),
            notes,
            dropped,
            elapsed,
            r.Frame is { } f ? new MergePreviewFrame(f.Path, f.Width, f.Height, f.CropX, f.CropY, f.CropWidth, f.CropHeight) : null);
    }

    /// <summary>„Lumora 0.5.47“: Version der App (Lumora.exe trägt die CHANGELOG-Version), sonst die dieser Bibliothek.</summary>
    internal static string AppName()
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName();
        var version = entry is { Name: "Lumora" } ? entry.Version : typeof(MergePreviewSession).Assembly.GetName().Version;
        return "Lumora " + (version?.ToString(3) ?? "?");
    }

    private static MergeQuality Quality(QualityCheck c)
        => new(c.Passed, c.Problems.Select(MergeTexts.Problem).ToList(), c.ResidualMedianPx, c.SeamNcc, c.CropHeightFraction, c.UsedImages, c.TotalImages);

    private static ComposeSpec Spec(MergeOptions options, bool preview) => new()
    {
        Projection = (WireProjection)(int)options.Projection,
        SphereForm = (WireSphereForm)(int)(options.SphereForm ?? MergeSphereForm.Band),
        Crop = (WireCrop)(int)options.Crop,
        Exposure = options.Exposure,
        Seams = true,
        Deghost = !preview,
        ToneMatch = Math.Clamp(options.ToneMatch, 0, 1),
        ColorBoost = Math.Clamp(options.ColorBoost ?? MergeLimits.ColorBoostDefault, MergeLimits.ColorBoostOff, MergeLimits.ColorBoostMax),
        Scale = preview ? 0 : 1,
        MaxOutputPixels = preview ? MergeLimits.PreviewMaxPixels : 0,
        SeamMegapixels = preview ? 0.12 : 0.5,
    };

    public MergeEstimate Estimate()
    {
        var current = Current;
        var (_, available) = MergeMemoryBudget.Query();
        var budget = MergeMemoryBudget.Budget();
        if (current is null)
        {
            return new MergeEstimate(0, 0, 1, null, 0, available, TimeSpan.Zero, 0);
        }

        // Größte passende Stufe wie der Hilfsprozess (Speicher, JPEG-Kante)
        long peakFull = Math.Max(1, PeakFromPreview(current));
        var scale = 1.0;
        MergeNote? reduction = null;
        if (Math.Max(current.FullWidth, current.FullHeight) > WorkerLimits.MaxJpegEdge)
        {
            scale = StepBelow(WorkerLimits.MaxJpegEdge / (double)Math.Max(current.FullWidth, current.FullHeight));
            reduction = MergeTexts.Note("size-reduced:jpeg-edge", current.FullWidth, current.FullHeight, scale);
        }

        if (budget > 0 && peakFull * scale * scale > budget)
        {
            while (scale > 0.1 && peakFull * scale * scale > budget)
            {
                scale = StepBelow(scale - 1e-9);
            }

            reduction = MergeTexts.Note("size-reduced:memory", current.FullWidth, current.FullHeight, scale, peakFull);
        }

        var w = (int)Math.Round(current.FullWidth * scale);
        var h = (int)Math.Round(current.FullHeight * scale);
        var sourceMp = _sources.Sum(s => (s.Width ?? 4000) * (double)(s.Height ?? 3000)) / 1e6;
        var outMp = w * (double)h / 1e6;
        var seconds = 8 + 0.15 * sourceMp * scale * scale + 0.2 * outMp;
        return new MergeEstimate(w, h, scale, reduction, (long)(peakFull * scale * scale), available, TimeSpan.FromSeconds(seconds), (long)(w * (double)h * 0.25));
    }

    private long PeakFromPreview(MergePreview preview)
        => _lastFullPeak > 0 ? _lastFullPeak : (long)(1.25 * (_sources.Sum(s => (s.Width ?? 4000) * (double)(s.Height ?? 3000) * 3) + preview.FullWidth * (double)preview.FullHeight * 13.4) + 300e6);

    private long _lastFullPeak;

    private static double StepBelow(double s)
    {
        foreach (var step in new[] { 1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.42, 0.35, 0.3, 0.25, 0.2, 0.15, 0.1 })
        {
            if (step <= s + 1e-9)
            {
                return step;
            }
        }

        return 0.1;
    }

    // ------------------------------------------------------------------ Speichern

    public async Task<MergeSaveResult> SaveAsync(MergeSaveOptions? options = null, CancellationToken ct = default)
    {
        options ??= new MergeSaveOptions();
        if (options.Options is { } changed && changed != Options)
        {
            await UpdateAsync(changed, ct).ConfigureAwait(false);
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var preview = Current;
            if (preview is null || _estimate is null)
            {
                return MergeSaveResult.Failed("Erst die Vorschau rechnen.");
            }

            if (!preview.CanSave)
            {
                return MergeSaveResult.Failed("Nicht gespeichert: " + string.Join(" ", preview.Quality.Problems.Select(p => p.Text)));
            }

            Volatile.Write(ref _saving, 1);
            try
            {
                return await RenderAndSaveAsync(preview, options, ct).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _saving, 0);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<MergeSaveResult> RenderAndSaveAsync(MergePreview preview, MergeSaveOptions options, CancellationToken ct)
    {
        using var linked = Begin(ct);
        var token = linked.Token;
        var methodName = MergeTexts.Method(Request.Method);
        var activity = _owner.Activities?.Start(new ActivityStartInfo(
            Lumora.Services.Activity.ActivityKind.Merge,
            methodName + " zusammenfügen",
            string.Create(CultureInfo.GetCultureInfo("de-DE"), $"{_specs.Count:N0} Fotos"),
            UserInitiated: true,
            Cancel: Cancel));
        var clock = Stopwatch.StartNew();
        var resultPath = Path.Combine(_workDir, $"result-{Guid.NewGuid():N}.jpg");
        lock (_ctsLock)
        {
            _saveClaim = _owner.Foreground?.Begin(ForegroundNeed.Cpu, "Zusammenfügen");
        }

        try
        {
            var settings = await _owner.SettingsStore.LoadAsync(token).ConfigureAwait(false);
            using var heavy = await EnterHeavyAsync(activity, token).ConfigureAwait(false);
            Directory.CreateDirectory(_workDir);
            var spec = Spec(Options, preview: false);
            var request = new RenderRequestData(_specs, _estimate!.Alignment, spec with { Projection = (WireProjection)(int)preview.Projection }, resultPath, settings.JpegQuality, MergeMemoryBudget.Budget());
            var rendered = await _owner.Stitcher.RenderAsync(
                request,
                p => Report(p, saving: true, estimating: false, activity),
                _saveClaim is null ? BackgroundCpu.MaxThreads : Environment.ProcessorCount,
                worker => _setSaveThreads = worker.SetThreads,
                token).ConfigureAwait(false);
            _lastFullPeak = rendered.FullPeakBytes;
            if (!rendered.Check.Passed)
            {
                TryDelete(resultPath);
                var problems = rendered.Check.Problems.Select(MergeTexts.Problem).ToList();
                activity?.Fail("Nicht gespeichert: Prüfung nicht bestanden", string.Join(Environment.NewLine, problems.Select(p => p.Text)));
                return MergeSaveResult.Failed("Nicht gespeichert: " + string.Join(" ", problems.Select(p => p.Text)), problems);
            }

            activity?.Report(980, 1000, "Aufnahmedaten schreiben");
            var notes = rendered.Notes.Select(code => MergeTexts.Note(code, rendered.FullWidth, rendered.FullHeight, rendered.Scale, rendered.FullPeakBytes)).Where(n => n is not null).Select(n => n!).ToList();
            var saved = await StoreAsync(preview, options, settings, rendered, resultPath, activity, token).ConfigureAwait(false);
            if (!saved.Success)
            {
                activity?.Fail("Nicht gespeichert", saved.Error);
                return saved with { Notes = notes };
            }

            _owner.Logger.LogInformation("Zusammenfügen: {Method} aus {Count} Fotos gespeichert (Datei {FileId}, {Width} × {Height}, Maßstab {Scale:0.00}, {Seconds:0} s, Spitze {PeakMb} MB)",
                Request.Method, _specs.Count, saved.ResultFileId, rendered.Width, rendered.Height, rendered.Scale, clock.Elapsed.TotalSeconds, rendered.PeakBytes / 1_000_000);
            var batch = saved.BatchId;
            var actions = new List<ActivityAction> { ActivityAction.ShowResult([saved.ResultFileId]) };
            if (batch > 0)
            {
                actions.Add(ActivityAction.Undo(async t =>
                {
                    var undone = await _owner.FileOps.UndoBatchAsync(batch, t).ConfigureAwait(false);
                    return undone is null ? "Nichts rückgängig zu machen" : $"{methodName} in den Papierkorb";
                }));
            }

            var headline = notes.FirstOrDefault(n => n.Code.StartsWith("size-reduced", StringComparison.Ordinal));
            activity?.Complete(new ActivityCompletion(
                headline is not null ? $"{methodName} gespeichert · {headline.Text}" : $"{methodName} gespeichert",
                ActivityState.Succeeded,
                actions));
            return saved with { Notes = notes };
        }
        catch (OperationCanceledException)
        {
            TryDelete(resultPath);
            activity?.Canceled("Abgebrochen — nichts gespeichert");
            throw;
        }
        catch (ImagingWorkerException ex)
        {
            TryDelete(resultPath);
            var text = ex.Failure == ImagingFailure.Error ? MergeTexts.Error(ex.Code, null) : ex.Message;
            activity?.Fail("Nicht gespeichert: " + text);
            return MergeSaveResult.Failed(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MergeException)
        {
            TryDelete(resultPath);
            activity?.Fail("Nicht gespeichert: " + ex.Message);
            return MergeSaveResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // z. B. Katalog nicht lesbar: Aktivität nie offen stehen lassen
            TryDelete(resultPath);
            activity?.Fail("Nicht gespeichert: " + ex.GetType().Name);
            throw;
        }
        finally
        {
            lock (_ctsLock)
            {
                _saveClaim?.Dispose();
                _saveClaim = null;
                _setSaveThreads = null;
            }

            End(linked);
        }
    }

    /// <summary>Aufnahmedaten in die Datei, dann als neue Datei in den Katalog (CreateMerged-Batch), Album, Nutzung, Meldung.</summary>
    private async Task<MergeSaveResult> StoreAsync(MergePreview preview, MergeSaveOptions options, MergeSettings settings, ComposeResult rendered, string resultPath,
        IActivityHandle? activity, CancellationToken ct)
    {
        // Nur die im Ergebnis verwendeten Quellen zählen (Katalog-Verknüpfung, Zeit/Ort/Kamera, gemeinsame Tags, Anzahl);
        // ausgelassene stehen nur im Rezept
        var dropped = _estimate!.Alignment.Dropped.ToHashSet();
        var ids = _sources.Where((_, i) => !dropped.Contains(i)).Select(s => s.FileId).ToList();
        List<MediaFile> rows;
        List<(int Id, string Path)> paths;
        await using (var ctx = _owner.Factory.Create())
        {
            var loaded = await ctx.Files.AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(ct).ConfigureAwait(false);
            rows = ids.Select(id => loaded.FirstOrDefault(f => f.Id == id)).Where(f => f is not null).Select(f => f!).ToList();
            var folderIds = rows.Select(r => r.FolderId).Distinct().ToList();
            var folders = await ctx.Folders.AsNoTracking().Where(d => folderIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Path, ct).ConfigureAwait(false);
            paths = rows.Select(r => (r.Id, Path.Combine(folders[r.FolderId], r.Name))).ToList();
        }

        if (rows.Count < MergeLimits.MinSources)
        {
            throw new MergeException("Die Fotos sind nicht mehr im Katalog.");
        }

        var pathOf = paths.ToDictionary(p => p.Id, p => p.Path);
        var fromFiles = ReadMissingMetadata(rows, pathOf);
        var choice = MergeMetadataPicker.Pick(rows, fromFiles);
        var time = choice.TimeSourceId is { } tid ? rows.First(r => r.Id == tid) : null;
        var location = choice.LocationSourceId is { } lid ? rows.First(r => r.Id == lid) : null;
        var camera = choice.CameraSourceId is { } cid ? rows.First(r => r.Id == cid) : null;
        DateTime? wall = null;
        int? offset = null;
        if (time is not null && MergeMetadataPicker.EffectiveTime(time, fromFiles) is ({ } utc, var timeOffset))
        {
            offset = timeOffset;
            wall = offset is int o ? DateTime.SpecifyKind(utc.AddMinutes(o), DateTimeKind.Unspecified) : utc.ToLocalTime();
        }

        var equirect = preview.Projection == PanoramaProjection.Spherical360 && (Options.SphereForm ?? MergeSphereForm.Band) == MergeSphereForm.Full
                       && !rendered.Notes.Contains("sphere-not-closed");
        var fallback = location is { Latitude: { } lat, Longitude: { } lon } ? new GeoCoordinate(lat, lon) : (GeoCoordinate?)null;
        try
        {
            MergeExifWriter.Write(resultPath, new MergeExifInput(
                time is not null ? pathOf[time.Id] : pathOf[rows[0].Id],
                location is not null ? pathOf[location.Id] : null,
                fallback,
                wall,
                offset,
                camera?.CameraModel,
                rendered.Width,
                rendered.Height,
                MergeTexts.Method(Request.Method),
                rows.Count,
                equirect));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _owner.Logger.LogWarning("Zusammenfügen: Aufnahmedaten nicht in die Datei geschrieben ({Error})", ex.GetType().Name);
        }

        var first = rows[0];
        var target = MergeNaming.TargetPath(Path.GetDirectoryName(pathOf[first.Id])!, first.Name, settings);
        var recipe = new MergeRecipe(
            MergeRecipe.CurrentVersion,
            Request.Method,
            Options,
            preview.Projection,
            Options.SphereForm ?? MergeSphereForm.Band,
            _sources.Select((s, i) => new MergeRecipeSource(s.FileId, _specs[i].Index)).ToList(),
            _estimate!.Alignment,
            rendered.Scale,
            AppName(),
            Options.ColorBoost ?? MergeLimits.ColorBoostDefault);
        var create = new MergeCreateRequest(
            rows.Select(r => r.Id).ToList(),
            resultPath,
            target,
            Request.Method,
            Request.Method == MergeMethod.Panorama ? preview.Projection : null,
            "merge:" + Request.Method.ToString().ToLowerInvariant(),
            recipe.ToJson(),
            Request.SeriesId,
            rendered.Width,
            rendered.Height,
            options.CopyCommonTags ?? settings.CopyCommonTags,
            options.CopyRating ?? settings.CopyRating,
            fromFiles.Count > 0 ? fromFiles : null);
        FileOperationBatchResult result;
        using (activity is null ? null : ActivityScope.Enter(activity))
        {
            result = await _owner.FileOps.CreateMergedAsync([create], CancellationToken.None).ConfigureAwait(false);
        }

        var item = result.Items.FirstOrDefault(i => i.Success && i.FileId > 0);
        if (item is null)
        {
            TryDelete(resultPath);
            return MergeSaveResult.Failed(result.Items.FirstOrDefault(i => !i.Success)?.Error ?? "Speichern fehlgeschlagen.");
        }

        if (options.AlbumId is { } albumId && _owner.Albums is { } albums)
        {
            try
            {
                await albums.AddFilesAsync(albumId, [item.FileId], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DbUpdateException)
            {
                _owner.Logger.LogWarning("Zusammenfügen: Ergebnis {FileId} nicht ins Album {AlbumId} ({Error})", item.FileId, albumId, ex.GetType().Name);
            }
        }

        UsageReport.Used(rows.Select(r => r.Id).ToList(), _owner.Messenger); // Verwendung der Quellen (ADR-0072)
        _owner.Messenger.Send(new MergeSavedMessage(first.LibraryId, item.FileId, rows.Select(r => r.Id).ToList(), Request.Method, result.BatchId));
        if (preview.Projection == PanoramaProjection.Spherical360 && Options.SphereForm is { } form)
        {
            await _owner.SettingsStore.RememberSphereFormAsync(form, CancellationToken.None).ConfigureAwait(false);
        }

        if (Options.ColorBoost is { } boost)
        {
            await _owner.SettingsStore.RememberColorBoostAsync(boost, CancellationToken.None).ConfigureAwait(false); // Vorauswahl beim nächsten Mal
        }

        return new MergeSaveResult(true, item.FileId, result.BatchId, item.DestPath, rendered.Width, rendered.Height, rendered.Scale, [], null);
    }

    /// <summary>
    /// Zeit und Ort frisch aus den Dateien für Quellen, denen sie im Katalog noch fehlen (bis eben nur in OneDrive, ADR-0077:
    /// GPS erst, wenn die Datei lokal ist). Nur lesen, nie schreiben.
    /// </summary>
    private static Dictionary<int, MergeSourceMetadata> ReadMissingMetadata(List<MediaFile> rows, Dictionary<int, string> pathOf)
    {
        var result = new Dictionary<int, MergeSourceMetadata>();
        foreach (var row in rows)
        {
            var needTime = row.CapturedAt is null || row.CapturedAtSource == CaptureDateSource.Cloud;
            var needGeo = row.Latitude is null && row.GeoSource is null;
            if (!needTime && !needGeo)
            {
                continue;
            }

            var path = pathOf[row.Id];
            if (!Lumora.Services.Cloud.CloudFiles.IsLocallyAvailable(path))
            {
                continue;
            }

            var date = needTime ? MediaDateReader.Read(path) : null;
            var gps = needGeo ? MediaGpsReader.Read(path) : null;
            GeoAccuracy? accuracy = null;
            GeoAccuracyReason? reason = null;
            DateTime? gpsTime = null;
            if (gps is not null && ExifGpsDetailsReader.Read(path) is { } details)
            {
                (accuracy, reason) = GeoAccuracyRules.Classify(details);
                gpsTime = details.GpsTimeUtc;
            }

            if (date is null && gps is null)
            {
                continue;
            }

            result[row.Id] = new MergeSourceMetadata(
                date?.Utc,
                date?.Source,
                date?.Precision ?? CaptureDatePrecision.Exact,
                date?.OffsetMinutes,
                gps?.Coordinate.Latitude,
                gps?.Coordinate.Longitude,
                accuracy,
                reason,
                gpsTime);
        }

        return result;
    }

    public void ContinueInBackground()
    {
        lock (_ctsLock)
        {
            _saveClaim?.Dispose();
            _saveClaim = null;
            _setSaveThreads?.Invoke(BackgroundCpu.MaxThreads);
        }
    }

    // ------------------------------------------------------------------ Rahmen

    private async Task<IDisposable> EnterHeavyAsync(IActivityHandle? activity, CancellationToken ct)
    {
        if (_owner.Host.IsBusy)
        {
            activity?.Wait("wartet — eine andere Berechnung „Zusammenfügen“ läuft", Cancel);
        }

        var heavy = await _owner.Host.EnterHeavyAsync(ct).ConfigureAwait(false);
        activity?.Resume();
        return heavy;
    }

    private CancellationTokenSource Begin(CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_ctsLock)
        {
            _current = linked;
            if (Volatile.Read(ref _disposed) != 0)
            {
                linked.Cancel(); // nach dem Schließen beginnt nichts mehr
            }
        }

        return linked;
    }

    private void End(CancellationTokenSource linked)
    {
        lock (_ctsLock)
        {
            if (ReferenceEquals(_current, linked))
            {
                _current = null;
            }
        }
    }

    public void Cancel()
    {
        lock (_ctsLock)
        {
            try
            {
                _current?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static readonly WorkerStep[] Order =
        [WorkerStep.Load, WorkerStep.Features, WorkerStep.Match, WorkerStep.Bundle, WorkerStep.Exposure, WorkerStep.Seams, WorkerStep.Deghost, WorkerStep.Warp, WorkerStep.Blend, WorkerStep.Finish, WorkerStep.Write];

    private static readonly double[] EstimateShare = [0.15, 0.15, 0.25, 0.1, 0.05, 0.1, 0.05, 0.1, 0.02, 0.02, 0.01];

    private static readonly double[] RenderShare = [0.12, 0, 0, 0, 0.06, 0.22, 0.14, 0.38, 0.03, 0.03, 0.02];

    private void Report(ProgressMessage p, bool saving, bool estimating, IActivityHandle? activity)
    {
        var shares = saving ? RenderShare : EstimateShare;
        var index = Array.IndexOf(Order, p.Step);
        double start = 0, total = 0;
        var first = saving || estimating ? 0 : Array.IndexOf(Order, WorkerStep.Exposure);
        for (var i = first; i < shares.Length; i++)
        {
            total += shares[i];
            if (i < index)
            {
                start += shares[i];
            }
        }

        var fraction = total <= 0 || index < 0 ? 0 : Math.Clamp((start + shares[index] * p.Fraction) / total, 0, 1);
        if (!saving && estimating)
        {
            fraction *= 0.9; // Zusammensetzen folgt noch
        }

        var text = p.Count > 1 && p.Step is WorkerStep.Load or WorkerStep.Features or WorkerStep.Warp
            ? string.Create(CultureInfo.GetCultureInfo("de-DE"), $"{MergeTexts.Step(p.Step)} {p.Index:N0}/{p.Count:N0}")
            : MergeTexts.Step(p.Step);
        var progress = new MergeProgress(saving, p.Step, text, p.Index, p.Count, fraction);
        try
        {
            ProgressChanged?.Invoke(progress);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _owner.Logger.LogDebug(ex, "Zusammenfügen: Fortschritt nicht zugestellt");
        }

        bool priority;
        lock (_ctsLock)
        {
            priority = _saveClaim is not null;
        }

        activity?.Report((int)Math.Round(fraction * 1000), 1000, saving && priority ? text + " · Vorrang" : text);
    }

    private async Task<Dictionary<int, string>> PathsAsync(List<int> ids, CancellationToken ct)
    {
        await using var ctx = _owner.Factory.Create();
        var rows = await ctx.Files.AsNoTracking().Where(f => ids.Contains(f.Id))
            .Join(ctx.Folders, f => f.FolderId, d => d.Id, (f, d) => new { f.Id, d.Path, f.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => Path.Combine(r.Path, r.Name));
    }

    private void DeleteOlderPreviews(string keep, string? keepFrame)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_workDir, "preview-*.jpg").Concat(Directory.EnumerateFiles(_workDir, "frame-*.jpg")))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase) && !string.Equals(file, keepFrame, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // bleibt bis zum Schließen der Sitzung
        }
    }

    private static void TryDelete(string path)
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
            // Temp-Ordner des Katalogs, wird beim Schließen bzw. beim nächsten Start geräumt
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Eine laufende Vorschau endet als Abbruch (ihr Prozess wird sofort beendet); ein laufendes Speichern rechnet in
        // eigenem Prozess zu Ende — auch nach „Im Hintergrund weiterrechnen“ — und erst danach wird aufgeräumt.
        if (Volatile.Read(ref _saving) == 0)
        {
            Cancel();
        }

        await DisposeStitchAsync().ConfigureAwait(false);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeStitchAsync().ConfigureAwait(false); // falls eine Vorschau in der Lücke noch einen geöffnet hatte
            _hold.Dispose();
            lock (_ctsLock)
            {
                _saveClaim?.Dispose();
                _saveClaim = null;
            }

            try
            {
                if (Directory.Exists(_workDir))
                {
                    Directory.Delete(_workDir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _owner.Logger.LogDebug(ex, "Zusammenfügen: Zwischenstände nicht gelöscht");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DisposeStitchAsync()
    {
        var stitch = Interlocked.Exchange(ref _stitch, null);
        if (stitch is not null)
        {
            await stitch.DisposeAsync().ConfigureAwait(false);
        }
    }
}
