using Stopwatch = System.Diagnostics.Stopwatch;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Activity;
using Lumora.Services.Database;
using Lumora.Services.Faces;
using Lumora.Services.Infrastructure;
using Lumora.Services.ModelPackages;
using Lumora.Services.Operations;

namespace Lumora.Services.Enhance;

/// <summary>„Automatisch“ fand bei diesem Foto nichts zu tun.</summary>
public sealed class EnhanceNothingToDoException(string message) : Exception(message);

/// <summary>
/// „Foto verbessern“ (ADR-0071, Mockup 13): Vorschau berechnen, als neue Datei speichern oder verwerfen, mehrere Fotos im
/// Hintergrund. Das Original bleibt IMMER unverändert — Ergebnis ist eine neue Datei daneben, im Katalog mit dem Original
/// verknüpft und über einen CreateVariant-Batch rückgängig machbar.
/// </summary>
public interface IPhotoEnhanceService
{
    /// <summary>Zustand der Werkzeuge (Modell geladen / fehlt / Quelle folgt).</summary>
    Task<IReadOnlyList<EnhanceToolStatus>> GetToolStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Berechnet das Ergebnis für ein Foto als JPEG im Temp-Ordner des Katalogs. Verlangt ein gewähltes Werkzeug ein Modell, das
    /// nicht bereit ist: <see cref="EnhanceModelMissingException"/> — es wird nichts berechnet und nichts geschrieben.
    /// </summary>
    Task<EnhancePreview> RenderAsync(int fileId, EnhanceOptions options, IProgress<EnhanceProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Als neue Datei speichern (<c>&lt;Name&gt;_verbessert.jpg</c> nach den Einstellungen), verknüpft, mit Rückgängig.</summary>
    Task<EnhanceSaveResult> SaveAsync(EnhancePreview preview, CancellationToken ct = default);

    /// <summary>Verwerfen: Zwischenstand löschen.</summary>
    void Discard(EnhancePreview preview);

    /// <summary>Vorgeschlagener Dateiname der Fassung (ohne Nummerierung) — für die Leiste.</summary>
    Task<string?> SuggestFileNameAsync(int fileId, CancellationToken ct = default);

    /// <summary>Zeitschätzung für mehrere Fotos.</summary>
    Task<EnhanceEstimate> EstimateAsync(IReadOnlyList<int> fileIds, EnhanceOptions options, CancellationToken ct = default);

    /// <summary>
    /// Mehrere Fotos im Hintergrund (Fortschritt, Abbrechen und Rückgängig in der Aktivitäts-Anzeige). Fehlt ein Modell der
    /// gewählten Werkzeuge: <see cref="EnhanceModelMissingException"/> vor dem ersten Foto. Gespeichert wird in EINEM Batch;
    /// nach einem Abbruch die bis dahin fertigen.
    /// </summary>
    Task<EnhanceBatchResult> RunBatchAsync(IReadOnlyList<int> fileIds, EnhanceOptions options, CancellationToken ct = default);

    /// <summary>Geladene Modelle freigeben (Grafikspeicher), z. B. beim Schließen der Leiste.</summary>
    void ReleaseModels();
}

/// <inheritdoc cref="IPhotoEnhanceService" />
public sealed class PhotoEnhanceService : IPhotoEnhanceService, IDisposable
{
    /// <summary>Längste Kante beim Laden (größere Originale werden für die Berechnung verkleinert).</summary>
    public const int MaxLoadEdge = 10_000;

    /// <summary>Höchstens so viele Pixel im Ergebnis (64 MP); sonst wird vor dem Hochskalieren verkleinert.</summary>
    public const long MaxOutputPixels = 64_000_000;

    public const int JpegQuality = 92;

    private readonly ILibraryContextFactory _factory;
    private readonly ICatalogStoragePaths _storage;
    private readonly IEnhanceImageCodec _codec;
    private readonly IEnhanceEngineFactory _engines;
    private readonly IEnhanceModelAccess _models;
    private readonly IEnhanceFaceLocator? _faces;
    private readonly IEnhanceSettingsStore _settings;
    private readonly IFileOperationService _fileOps;
    private readonly IGpuGate _gpu;
    private readonly IActivityCenter? _activities;
    private readonly ILogger _logger;
    private readonly IMessenger _messenger;
    private readonly Dictionary<EnhanceTool, (IEnhanceEngine Engine, bool UseGpu)> _loaded = [];
    private readonly object _engineLock = new();
    private int _tempCleaned;

    /// <summary>Laufende Berechnungen (Leiste und Stapel teilen sich die Modelle): Freigeben erst, wenn keine mehr läuft.</summary>
    private int _activeRenders;
    private bool _releasePending;

    public PhotoEnhanceService(
        ILibraryContextFactory factory,
        ICatalogStoragePaths storage,
        IEnhanceImageCodec codec,
        IEnhanceEngineFactory engines,
        IEnhanceModelAccess models,
        IEnhanceSettingsStore settings,
        IFileOperationService fileOps,
        IGpuGate gpu,
        IEnhanceFaceLocator? faces = null,
        IActivityCenter? activities = null,
        ILogger<PhotoEnhanceService>? logger = null,
        IMessenger? messenger = null)
    {
        _factory = factory;
        _storage = storage;
        _codec = codec;
        _engines = engines;
        _models = models;
        _settings = settings;
        _fileOps = fileOps;
        _gpu = gpu;
        _faces = faces;
        _activities = activities;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        // Neu geladene oder entfernte Modelle: geladene Sitzungen verwerfen (beim nächsten Lauf neu)
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _messenger.Register<PhotoEnhanceService, ModelPackageChangedMessage>(this, static (r, m) =>
        {
            if (m.Id.StartsWith("enhance.", StringComparison.Ordinal))
            {
                r.ReleaseModels();
            }
        });
    }

    /// <summary>Ordner der Zwischenstände (im Temp-Ordner des Katalogs, in der versteckten Sitzung im Container).</summary>
    public string TempFolder => Path.Combine(_storage.TempRoot, "enhance");

    // ------------------------------------------------------------------ Zustand

    public async Task<IReadOnlyList<EnhanceToolStatus>> GetToolStatusAsync(CancellationToken ct = default)
    {
        var list = new List<EnhanceToolStatus>();
        var ready = new HashSet<EnhanceTool>();
        foreach (var tool in new[] { EnhanceTool.Upscale, EnhanceTool.UpscaleNatural, EnhanceTool.Faces, EnhanceTool.Colorize, EnhanceTool.Erase })
        {
            var state = await _models.GetStateAsync(tool, ct).ConfigureAwait(false);
            var name = EnhanceModelCatalog.DefinitionOf(tool)?.Name;
            var id = EnhanceModelCatalog.ModelIdOf(tool);
            var (ok, text) = state switch
            {
                EnhanceModelState.Ready => (true, "bereit"),
                EnhanceModelState.Broken => (false, "Modell defekt"),
                EnhanceModelState.NoSource => (false, "Quelle folgt"),
                _ => (false, "Modell fehlt"),
            };
            if (ok && tool == EnhanceTool.Faces && _faces?.IsAvailable != true)
            {
                (ok, text) = (false, "braucht Gesichtserkennung");
            }

            if (ok)
            {
                ready.Add(tool);
            }

            list.Add(new EnhanceToolStatus(tool, id, ok, state is EnhanceModelState.NotInstalled or EnhanceModelState.Broken, text, name));
        }

        // Die Karte „Hochskalieren“ ist bereit, wenn eines der beiden Modelle geladen ist
        var auto = ready.Count > 0 ? "bereit" : "Modelle fehlen";
        list.Insert(0, new EnhanceToolStatus(EnhanceTool.Auto, null, true, false, auto, null));
        list.Add(new EnhanceToolStatus(EnhanceTool.Adjust, null, true, false, "bereit", null));
        return list;
    }

    private async Task<HashSet<EnhanceTool>> ReadyToolsAsync(CancellationToken ct)
        => (await GetToolStatusAsync(ct).ConfigureAwait(false))
            .Where(s => s.Ready && s.ModelId is not null)
            .Select(s => s.Tool)
            .ToHashSet();

    /// <summary>Ohne Angabe gilt das Hochskalier-Modell aus Einstellungen › Verbessern (0.5.7).</summary>
    private static EnhanceOptions WithStoredModel(EnhanceOptions options, EnhanceSettings settings)
        => options.UpscaleModel is null ? options with { UpscaleModel = settings.UpscaleModel } : options;

    private static void RequireModels(EnhanceOptions options, IReadOnlyList<EnhanceToolStatus> status)
    {
        foreach (var tool in options.RequiredModelTools())
        {
            var s = status.First(x => x.Tool == tool);
            if (!s.Ready)
            {
                var hint = s.StateText switch
                {
                    "Quelle folgt" => "Für dieses Werkzeug gibt es noch keine geprüfte Modell-Quelle.",
                    "braucht Gesichtserkennung" => "Zum Finden der Gesichter zuerst die Gesichtserkennung laden (Einstellungen › Modelle & Daten).",
                    _ => "Das Modell zuerst laden (Einstellungen › Modelle & Daten oder „Laden…“ in der Leiste).",
                };
                throw new EnhanceModelMissingException(tool, $"{EnhanceTexts.Name(tool)}: {s.StateText}. {hint}");
            }
        }
    }

    // ------------------------------------------------------------------ Ein Foto

    public async Task<EnhancePreview> RenderAsync(int fileId, EnhanceOptions options, IProgress<EnhanceProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsEmpty)
        {
            throw new EnhanceException("Kein Werkzeug gewählt.");
        }

        var status = await GetToolStatusAsync(ct).ConfigureAwait(false);
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        options = WithStoredModel(options, settings);
        RequireModels(options, status);
        var info = await LoadInfoAsync(fileId, ct).ConfigureAwait(false);
        var ready = status.Where(s => s.Ready && s.ModelId is not null).Select(s => s.Tool).ToHashSet();
        return await Task.Run(() => RenderCoreAsync(info, options, settings, ready, progress, ct), ct).ConfigureAwait(false);
    }

    private async Task<EnhancePreview> RenderCoreAsync(
        FileInfoRow info,
        EnhanceOptions options,
        EnhanceSettings settings,
        IReadOnlySet<EnhanceTool> ready,
        IProgress<EnhanceProgress>? progress,
        CancellationToken ct)
    {
        lock (_engineLock)
        {
            _activeRenders++;
        }

        try
        {
            return await RenderCoreInnerAsync(info, options, settings, ready, progress, ct).ConfigureAwait(false);
        }
        catch (OutOfMemoryException ex)
        {
            // ein zu großes Foto bricht nur sich selbst ab (im Stapel zählt es als fehlgeschlagen)
            _logger.LogWarning(ex, "Foto verbessern: zu wenig Arbeitsspeicher für Datei {FileId}", info.Id);
            throw new EnhanceException("Zu wenig Arbeitsspeicher für dieses Foto — kleineren Faktor wählen oder andere Programme schließen.", ex);
        }
        finally
        {
            bool release;
            lock (_engineLock)
            {
                _activeRenders--;
                release = _activeRenders == 0 && _releasePending;
            }

            if (release)
            {
                ReleaseModels();
            }
        }
    }

    private async Task<EnhancePreview> RenderCoreInnerAsync(
        FileInfoRow info,
        EnhanceOptions options,
        EnhanceSettings settings,
        IReadOnlySet<EnhanceTool> ready,
        IProgress<EnhanceProgress>? progress,
        CancellationToken ct)
    {
        CleanTempOnce();
        var clock = Stopwatch.StartNew();
        var notes = new List<string>();
        var applied = new List<EnhanceTool>();
        progress?.Report(new EnhanceProgress("Foto wird geladen", 0.02));
        EnhanceImage image;
        try
        {
            image = _codec.Load(info.FullPath, MaxLoadEdge);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EnhanceException("Das Foto ließ sich nicht lesen.", ex);
        }

        ct.ThrowIfCancellationRequested();
        if (info.Width is int w0 && info.Height is int h0 && Math.Max(w0, h0) > image.Width + 1 && Math.Max(w0, h0) > image.Height + 1)
        {
            notes.Add($"Sehr großes Foto — für die Berechnung auf {MaxLoadEdge:N0} Pixel verkleinert.");
        }

        if (options.Auto)
        {
            progress?.Report(new EnhanceProgress("Foto wird untersucht", 0.08));
            IReadOnlyList<FacePoint[]>? faces = null;
            if (_faces?.IsAvailable == true && ready.Contains(EnhanceTool.Faces))
            {
                faces = await LocateFacesAsync(image, settings.UseGpu, ct).ConfigureAwait(false);
            }

            var plan = EnhanceAutoPlanner.Decide(options, image, ready, faces);
            notes.AddRange(plan.Notes);
            if (!plan.HasWork)
            {
                throw new EnhanceNothingToDoException(plan.Notes.LastOrDefault() ?? "Automatisch: nichts zu verbessern.");
            }

            options = plan.Options;
        }

        // 1) Entfernen (Pinsel in Koordinaten des angezeigten Originals, Kratzer automatisch)
        if (options.HasErase)
        {
            progress?.Report(new EnhanceProgress("Störendes wird entfernt", 0.15));
            var mask = EnhanceImageOps.RasterizeDots(image.Width, image.Height, options.BrushDots);
            if (options.Scratches)
            {
                var scratches = EnhanceImageOps.DetectScratches(image);
                for (var i = 0; i < mask.Length; i++)
                {
                    mask[i] |= scratches[i];
                }
            }

            if (EnhanceImageOps.Bounds(mask, image.Width, image.Height) is not null)
            {
                image = await RunEngineAsync(EnhanceTool.Erase, new EnhanceStepInput(image, Mask: mask), settings.UseGpu, ct).ConfigureAwait(false);
                applied.Add(EnhanceTool.Erase);
            }
            else
            {
                notes.Add("Keine Kratzer oder Flecken gefunden.");
            }
        }

        // 2) Drehen und Zuschneiden
        if (options.Rotation % 360 != 0)
        {
            image = EnhanceImageOps.Rotate(image, options.Rotation);
        }

        if (options.Crop is { IsFull: false } crop)
        {
            image = EnhanceImageOps.Crop(image, crop);
        }
        else if (options.CropAspect is double aspect and > 0)
        {
            image = EnhanceImageOps.Crop(image, EnhanceImageOps.CenteredCrop(image.Width, image.Height, aspect));
        }

        // 3) Einfärben
        if (options.Colorize)
        {
            progress?.Report(new EnhanceProgress("Foto wird eingefärbt", 0.35));
            image = await RunEngineAsync(EnhanceTool.Colorize, new EnhanceStepInput(image), settings.UseGpu, ct).ConfigureAwait(false);
            applied.Add(EnhanceTool.Colorize);
        }

        // 4) Gesichter restaurieren (Gesichter im aktuellen Bild suchen)
        if (options.Faces)
        {
            progress?.Report(new EnhanceProgress("Gesichter werden restauriert", 0.5));
            if (_faces?.IsAvailable != true)
            {
                throw new EnhanceModelMissingException(EnhanceTool.Faces, "Zum Finden der Gesichter zuerst die Gesichtserkennung laden (Einstellungen › Modelle & Daten).");
            }

            var found = await LocateFacesAsync(image, settings.UseGpu, ct).ConfigureAwait(false);
            if (found.Count == 0)
            {
                notes.Add("Keine Gesichter gefunden.");
            }
            else
            {
                image = await RunEngineAsync(EnhanceTool.Faces, new EnhanceStepInput(image, Strength: options.FaceStrength, Faces: found), settings.UseGpu, ct).ConfigureAwait(false);
                applied.Add(EnhanceTool.Faces);
            }
        }

        // 5) Licht
        if (Math.Abs(options.Brightness) > 0.001 || Math.Abs(options.Contrast) > 0.001)
        {
            image = EnhanceImageOps.AdjustLight(image, options.Brightness, options.Contrast);
        }

        if (options.HasAdjust)
        {
            applied.Add(EnhanceTool.Adjust);
        }

        // 6) Hochskalieren zuletzt (größtes Bild); Ergebnis höchstens 64 MP
        if (options.Upscale)
        {
            progress?.Report(new EnhanceProgress("Foto wird hochskaliert", 0.65));
            var factor = options.UpscaleFactor >= 4 ? 4 : 2;
            var (maxW, maxH) = EnhanceImageOps.FitPixels(image.Width * factor, image.Height * factor, MaxOutputPixels);
            if (maxW < image.Width * factor)
            {
                image = EnhanceImageOps.Resize(image, Math.Max(1, maxW / factor), Math.Max(1, maxH / factor));
                notes.Add("Sehr großes Foto — vor dem Hochskalieren verkleinert (höchstens 64 Megapixel).");
            }

            var beforeUpscale = image;
            // Gewähltes Modell (0.5.7); „Automatisch“ weicht auf das andere aus, wenn nur dieses geladen ist
            var engine = options.UpscaleEngine;
            if (!ready.Contains(engine))
            {
                var other = engine == EnhanceTool.UpscaleNatural ? EnhanceTool.Upscale : EnhanceTool.UpscaleNatural;
                if (ready.Contains(other))
                {
                    notes.Add($"{EnhanceTexts.Name(engine)}: Modell fehlt — {EnhanceTexts.Name(other)} verwendet.");
                    engine = other;
                }
            }

            image = await RunEngineAsync(engine, new EnhanceStepInput(image, Factor: factor), settings.UseGpu, ct).ConfigureAwait(false);
            applied.Add(EnhanceTool.Upscale);
            if (engine == EnhanceTool.UpscaleNatural)
            {
                notes.Add("Modell „Natürlich“ (Real-ESRGAN general x4v3).");
            }

            // Natürlichkeit (0.5.6): etwas Original zurück und feine Körnung gegen die glatte „Plastikhaut“ des Modells
            var naturalness = options.Naturalness ?? settings.Naturalness;
            var grain = options.Grain ?? settings.Grain;
            image = EnhanceImageOps.Naturalize(image, beforeUpscale, naturalness, grain, info.Id);
            if (naturalness > 0.001 || grain > 0.001)
            {
                notes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Natürlichkeit {Math.Round(naturalness * 100):0} %, Körnung {Math.Round(grain * 100):0} %."));
            }
        }

        if (applied.Count == 0)
        {
            throw new EnhanceNothingToDoException(notes.LastOrDefault() ?? "Nichts zu verbessern.");
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new EnhanceProgress("Ergebnis wird geschrieben", 0.95));
        Directory.CreateDirectory(TempFolder);
        var temp = Path.Combine(TempFolder, Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            _codec.SaveJpeg(image, temp, JpegQuality);
            if (settings.CopyMetadata && IsJpeg(info.Name))
            {
                try
                {
                    JpegExifCopier.Copy(info.FullPath, temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    notes.Add("Aufnahmedaten ließen sich nicht in die Datei übernehmen (im Katalog schon).");
                    _logger.LogWarning(ex, "Foto verbessern: EXIF von Datei {FileId} nicht übernommen", info.Id);
                }
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        progress?.Report(new EnhanceProgress("Fertig", 1));
        var key = string.Join('+', applied.Select(EnhanceTexts.Key));
        _logger.LogInformation("Foto verbessern: Datei {FileId} ({Tools}, {Width} × {Height}, {Ms} ms)", info.Id, key, image.Width, image.Height, clock.ElapsedMilliseconds);
        return new EnhancePreview(info.Id, temp, image.Width, image.Height, applied, key, clock.Elapsed, notes);
    }

    private async Task<IReadOnlyList<FacePoint[]>> LocateFacesAsync(EnhanceImage image, bool useGpu, CancellationToken ct)
    {
        if (_faces is FaceAnalyzerEnhanceLocator locator)
        {
            locator.UseGpu = useGpu;
        }

        using var lease = useGpu ? await _gpu.AcquireAsync(ct).ConfigureAwait(false) : null;
        return _faces!.Locate(image, ct);
    }

    private async Task<EnhanceImage> RunEngineAsync(EnhanceTool tool, EnhanceStepInput input, bool useGpu, CancellationToken ct)
    {
        var engine = GetEngine(tool, useGpu);
        using var lease = engine.ExecutionProvider == "DirectML" ? await _gpu.AcquireAsync(ct).ConfigureAwait(false) : null;
        try
        {
            return engine.Process(input, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not EnhanceException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Foto verbessern: {Tool} fehlgeschlagen", tool);
            throw new EnhanceException($"{EnhanceTexts.Name(tool)} ist fehlgeschlagen: {ex.Message}", ex);
        }
    }

    private IEnhanceEngine GetEngine(EnhanceTool tool, bool useGpu)
    {
        lock (_engineLock)
        {
            if (_loaded.TryGetValue(tool, out var loaded))
            {
                if (loaded.UseGpu == useGpu)
                {
                    return loaded.Engine;
                }

                loaded.Engine.Dispose();
                _loaded.Remove(tool);
            }

            var path = _models.GetModelPath(tool) ?? throw new EnhanceModelMissingException(tool, $"{EnhanceTexts.Name(tool)}: kein Modell.");
            IEnhanceEngine engine;
            try
            {
                engine = _engines.Create(tool, path, useGpu);
            }
            catch (Exception ex) when (ex is not EnhanceModelMissingException and not EnhanceException)
            {
                throw new EnhanceException($"Das Modell für „{EnhanceTexts.Name(tool)}“ ließ sich nicht laden: {ex.Message}", ex);
            }

            _loaded[tool] = (engine, useGpu);
            return engine;
        }
    }

    public void ReleaseModels()
    {
        List<IEnhanceEngine> engines;
        lock (_engineLock)
        {
            if (_activeRenders > 0)
            {
                _releasePending = true; // Stapel oder Vorschau rechnet noch: danach freigeben
                return;
            }

            _releasePending = false;
            engines = _loaded.Values.Select(v => v.Engine).ToList();
            _loaded.Clear();
        }

        foreach (var engine in engines)
        {
            engine.Dispose();
        }

        (_faces as FaceAnalyzerEnhanceLocator)?.Release();
    }

    // ------------------------------------------------------------------ Speichern / Verwerfen

    public async Task<EnhanceSaveResult> SaveAsync(EnhancePreview preview, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!File.Exists(preview.ResultPath))
        {
            return new EnhanceSaveResult(0, 0, string.Empty, "Das Ergebnis ist nicht mehr vorhanden — bitte neu berechnen.");
        }

        var info = await LoadInfoAsync(preview.OriginalFileId, ct).ConfigureAwait(false);
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var request = Request(info, preview, settings);
        var result = await _fileOps.CreateVariantsAsync([request], ct).ConfigureAwait(false);
        var item = result.Items.FirstOrDefault();
        if (item is { Success: true } && item.FileId > 0)
        {
            Usage.UsageReport.Used([info.Id], _messenger); // „Verbessern“ zählt als Verwendung des Originals (ADR-0072)
            return new EnhanceSaveResult(result.BatchId, item.FileId, item.DestPath ?? request.TargetPath, null);
        }

        return new EnhanceSaveResult(result.BatchId, 0, item?.DestPath ?? string.Empty, item?.Error ?? "Speichern fehlgeschlagen.");
    }

    private static VariantCreateRequest Request(FileInfoRow info, EnhancePreview preview, EnhanceSettings settings)
        => new(
            info.Id,
            preview.ResultPath,
            EnhanceNaming.TargetPath(info.FolderPath, info.Name, settings),
            preview.ToolsKey,
            settings.CopyMetadata,
            settings.CopyRatingAndTags,
            preview.Width,
            preview.Height);

    public void Discard(EnhancePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (IsUnder(preview.ResultPath, TempFolder))
        {
            TryDelete(preview.ResultPath);
        }
    }

    public async Task<string?> SuggestFileNameAsync(int fileId, CancellationToken ct = default)
    {
        var rows = await LoadInfosAsync([fileId], ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var target = EnhanceNaming.TargetPath(rows[0].FolderPath, rows[0].Name, settings);
        var name = Path.GetFileName(FileOperationService.UniqueVariantPath(target));
        return settings.Location == EnhanceSaveLocation.Subfolder ? Path.Combine(EnhanceNaming.CleanFolderName(settings.SubfolderName), name) : name;
    }

    // ------------------------------------------------------------------ Mehrere Fotos

    public async Task<EnhanceEstimate> EstimateAsync(IReadOnlyList<int> fileIds, EnhanceOptions options, CancellationToken ct = default)
    {
        var status = await GetToolStatusAsync(ct).ConfigureAwait(false);
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        options = WithStoredModel(options, settings);
        var missing = options.RequiredModelTools().Where(t => !status.First(s => s.Tool == t).Ready).ToList();
        var rows = (await LoadInfosAsync(fileIds, ct).ConfigureAwait(false)).Where(r => r.IsPhoto).ToList();
        var gpu = settings.UseGpu;
        double seconds = 0;
        foreach (var row in rows)
        {
            var mp = Math.Max(0.3, (row.Width ?? 3000) * (double)(row.Height ?? 2000) / 1_000_000);
            seconds += 0.4 * mp; // laden + schreiben
            seconds += SecondsFor(options, mp, gpu, row);
        }

        return new EnhanceEstimate(rows.Count, TimeSpan.FromSeconds(seconds), gpu, missing);
    }

    /// <summary>Grobe Kosten je Foto (Messwerte folgen; Grafikkarte ≈ 10–20× schneller als der Prozessor).</summary>
    private static double SecondsFor(EnhanceOptions options, double mp, bool gpu, FileInfoRow row)
    {
        var k = gpu ? 1.0 : 15.0;
        double s = 0;
        var upscale = options.Upscale || (options.Auto && Math.Max(row.Width ?? 4000, row.Height ?? 3000) < EnhanceAutoPlanner.SmallImageEdge);
        if (upscale)
        {
            var factor = options.Upscale ? Math.Max(2, options.UpscaleFactor) : 2;
            s += 1.2 * mp * (factor >= 4 ? 1.0 : 1.0) * k;
        }

        if (options.Faces || options.Auto)
        {
            s += 0.6 * k;
        }

        if (options.Colorize || options.Auto)
        {
            s += 0.4 * k;
        }

        if (options.HasErase || options.Auto)
        {
            s += 0.6 * k;
        }

        if (options.HasAdjust)
        {
            s += 0.05 * mp;
        }

        return s;
    }

    public async Task<EnhanceBatchResult> RunBatchAsync(IReadOnlyList<int> fileIds, EnhanceOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsEmpty)
        {
            throw new EnhanceException("Kein Werkzeug gewählt.");
        }

        var status = await GetToolStatusAsync(ct).ConfigureAwait(false);
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        options = WithStoredModel(options, settings);
        RequireModels(options, status); // Modell fehlt → kein Lauf, nichts geschrieben
        var ready = status.Where(s => s.Ready && s.ModelId is not null).Select(s => s.Tool).ToHashSet();
        var ids = fileIds.Distinct().ToList();
        var rows = (await LoadInfosAsync(ids, ct).ConfigureAwait(false)).ToDictionary(r => r.Id);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var handle = _activities?.Start(new ActivityStartInfo(
            ActivityKind.Enhance,
            "Foto verbessern",
            ids.Count == 1 ? "1 Foto" : $"{ids.Count:N0} Fotos",
            UserInitiated: true,
            Cancel: () => _ = linked.CancelAsync()));
        handle?.Report(0, ids.Count, "im Hintergrund · Grafikkarte geteilt mit Gesichtern und Motiven");

        var finished = new List<(FileInfoRow Info, EnhancePreview Preview)>();
        var problems = new List<string>();
        int skipped = 0, failed = 0;
        var canceled = false;
        try
        {
            for (var i = 0; i < ids.Count; i++)
            {
                if (linked.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                handle?.Report(i, ids.Count);
                if (!rows.TryGetValue(ids[i], out var info) || !info.IsPhoto || info.Missing || !File.Exists(info.FullPath))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var preview = await Task.Run(() => RenderCoreAsync(info, options, settings, ready, null, linked.Token), linked.Token).ConfigureAwait(false);
                    finished.Add((info, preview));
                }
                catch (EnhanceNothingToDoException)
                {
                    skipped++;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex) when (ex is EnhanceException or EnhanceModelMissingException or IOException or UnauthorizedAccessException)
                {
                    failed++;
                    problems.Add(ex.Message);
                    _logger.LogWarning(ex, "Foto verbessern: Datei {FileId} fehlgeschlagen", info.Id);
                }
            }

            handle?.Report(ids.Count, ids.Count, finished.Count > 0 ? "wird gespeichert" : null);
            FileOperationBatchResult? saved = null;
            if (finished.Count > 0)
            {
                var requests = finished.Select(f => Request(f.Info, f.Preview, settings)).ToList();
                // Im Vorgang „Foto verbessern“: der Datei-Batch legt keinen eigenen Eintrag an; Speichern läuft ohne Abbruch zu Ende
                using (handle is null ? null : ActivityScope.Enter(handle))
                {
                    saved = await _fileOps.CreateVariantsAsync(requests, CancellationToken.None).ConfigureAwait(false);
                }

                failed += saved.Failed;
                var byPath = finished.GroupBy(f => f.Info.FullPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Info.Id, StringComparer.OrdinalIgnoreCase);
                var usedOriginals = saved.Items.Where(x => x.Success && x.FileId > 0 && byPath.ContainsKey(x.SourcePath))
                    .Select(x => byPath[x.SourcePath]).Distinct().ToList();
                Usage.UsageReport.Used(usedOriginals, _messenger); // Verwendung der Originale (ADR-0072)
                problems.AddRange(saved.Items.Where(x => !x.Success && x.Error is not null).Select(x => x.Error!));
            }

            var count = saved?.Succeeded ?? 0;
            var batchId = saved?.BatchId ?? 0;
            Complete(handle, count, skipped, failed, canceled, batchId, problems);
            _logger.LogInformation("Foto verbessern: {Saved} gespeichert, {Skipped} übersprungen, {Failed} fehlgeschlagen{Canceled}",
                count, skipped, failed, canceled ? ", abgebrochen" : string.Empty);
            return new EnhanceBatchResult(batchId, count, skipped, failed, canceled, problems);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            handle?.Fail($"Fehlgeschlagen: {ex.Message}");
            throw;
        }
        finally
        {
            foreach (var (_, preview) in finished)
            {
                TryDelete(preview.ResultPath); // gespeicherte sind verschoben; übrig bleiben nur nicht gespeicherte
            }

            ReleaseModels();
        }
    }

    private void Complete(IActivityHandle? handle, int saved, int skipped, int failed, bool canceled, int batchId, List<string> problems)
    {
        if (handle is null)
        {
            return;
        }

        if (canceled && saved == 0)
        {
            handle.Canceled("Abgebrochen — nichts gespeichert");
            return;
        }

        var text = (saved == 1 ? "1 Foto verbessert" : $"{saved:N0} Fotos verbessert")
                   + (skipped > 0 ? $" · {skipped:N0} übersprungen" : string.Empty)
                   + (failed > 0 ? $" · {failed:N0} fehlgeschlagen" : string.Empty)
                   + (canceled ? " · abgebrochen" : string.Empty);
        var actions = new List<ActivityAction>();
        if (batchId > 0 && saved > 0)
        {
            actions.Add(ActivityAction.Undo(async t =>
            {
                var undone = await _fileOps.UndoBatchAsync(batchId, t).ConfigureAwait(false);
                return undone is null ? "Nichts rückgängig zu machen" : $"{undone.Succeeded:N0} verbesserte Fassungen in den Papierkorb";
            }));
        }

        if (problems.Count > 0)
        {
            actions.Add(ActivityAction.Details);
        }

        var state = saved == 0 && failed > 0 ? ActivityState.Failed : failed > 0 || canceled ? ActivityState.Warning : ActivityState.Succeeded;
        handle.Complete(new ActivityCompletion(text, state, actions, problems.Count > 0 ? string.Join(Environment.NewLine, problems.Distinct().Take(20)) : null));
    }

    // ------------------------------------------------------------------ Katalog

    private sealed record FileInfoRow(int Id, string FolderPath, string Name, int MediaType, bool Missing, int? Width, int? Height)
    {
        public string FullPath => Path.Combine(FolderPath, Name);

        public bool IsPhoto => MediaType == 0;
    }

    private async Task<FileInfoRow> LoadInfoAsync(int fileId, CancellationToken ct)
    {
        var rows = await LoadInfosAsync([fileId], ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new EnhanceException("Die Datei ist nicht (mehr) im Katalog.");
        }

        var info = rows[0];
        if (!info.IsPhoto)
        {
            throw new EnhanceException("Nur Fotos lassen sich verbessern.");
        }

        if (info.Missing || !File.Exists(info.FullPath))
        {
            throw new EnhanceException("Die Datei fehlt.");
        }

        return info;
    }

    private Task<List<FileInfoRow>> LoadInfosAsync(IReadOnlyList<int> ids, CancellationToken ct)
        => Task.Run(() =>
        {
            var list = new List<FileInfoRow>();
            if (ids.Count == 0)
            {
                return list;
            }

            using var connection = _factory.OpenSqliteConnection();
            foreach (var chunk in ids.Distinct().Chunk(500))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT f.Id, d.Path, f.Name, f.MediaType, f.Missing, f.Width, f.Height
                    FROM Files f JOIN Folders d ON d.Id = f.FolderId
                    WHERE f.Id IN ({string.Join(',', chunk)});
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new FileInfoRow(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetInt64(4) != 0,
                        reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6)));
                }
            }

            var order = ids.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
            return list.OrderBy(r => order.GetValueOrDefault(r.Id)).ToList();
        }, ct);

    // ------------------------------------------------------------------ Hilfen

    private static bool IsJpeg(string name)
        => Path.GetExtension(name) is { } ext && (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));

    private static bool IsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Zwischenstände früherer Sitzungen (älter als einen Tag) wegräumen — einmal je Dienst.</summary>
    private void CleanTempOnce()
    {
        if (Interlocked.Exchange(ref _tempCleaned, 1) != 0 || !Directory.Exists(TempFolder))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(TempFolder, "*.jpg"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Foto verbessern: alte Zwischenstände nicht aufgeräumt");
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
            // bleibt im Temp-Ordner des Katalogs und wird beim nächsten Start weggeräumt
        }
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        ReleaseModels();
    }
}
