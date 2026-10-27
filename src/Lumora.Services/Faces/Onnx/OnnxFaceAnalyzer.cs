using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Lumora.Services.Faces.Alignment;
using Lumora.Services.Faces.Detection;
using Lumora.Services.Faces.Models;
using Indexing = Lumora.Services.Faces.Indexing;

namespace Lumora.Services.Faces.Onnx;

/// <summary>
/// Gesichtserkennung mit InsightFace buffalo_l über ONNX Runtime (ADR-0035): SCRFD det_10g (Eingabe <c>input.1</c>
/// [1, 3, H, W], hier fest 640 × 640; neun Ausgaben Score/Box/Landmarken je Stride 8/16/32) und ArcFace w600k_r50
/// (<c>input.1</c> [N, 3, 112, 112] → [N, 512]). Beide Sitzungen werden einmal geladen und wiederverwendet; ein
/// Lauf je Sitzung zur Zeit (DirectML erlaubt keine parallelen <c>Run</c>-Aufrufe derselben Sitzung).
/// <para>
/// Ausführung: <c>useGpu</c> = DirectML (Grafikkarte 0) mit automatischem Rückfall auf CPU, wenn die Sitzung damit
/// nicht entsteht oder der erste Probelauf scheitert. CPU: höchstens die Hälfte der Kerne (UI und Thumbnails behalten Luft).
/// </para>
/// </summary>
public sealed class OnnxFaceAnalyzer : IFaceAnalyzer
{
    /// <summary>
    /// Modell + Vorverarbeitung. Teil „v1“ = Laden mit längster Kante <see cref="Indexing.FaceIndexDefaults.LoadMaxEdge"/>, Letterbox 640,
    /// Schwellen 0.5/0.4, Ausrichtung arcface_dst. Ändert sich etwas davon, Teil hochzählen → Neuindizierung.
    /// </summary>
    public const string Version = "buffalo_l/det_10g+w600k_r50/v1";

    /// <summary>Höchstens so viele Gesichter je Bild (nach Score) bekommen einen Fingerabdruck.</summary>
    public const int MaxFacesPerImage = 64;

    private const int ArcFaceBatch = 16;
    private const int InputSize = ScrfdPreprocessor.InputSize;

    private readonly InferenceSession _detector;
    private readonly InferenceSession _recognizer;
    private readonly string _detectorInput;
    private readonly string _recognizerInput;
    private readonly string[] _detectorOutputs;
    private readonly string _recognizerOutput;
    private readonly object _detectorLock = new();
    private readonly object _recognizerLock = new();
    private readonly ILogger _logger;
    private ScrfdOutputLayout? _layout;
    private bool _batchSupported = true;
    private int _disposed;

    private OnnxFaceAnalyzer(InferenceSession detector, InferenceSession recognizer, string provider, ILogger logger)
    {
        _detector = detector;
        _recognizer = recognizer;
        _logger = logger;
        ExecutionProvider = provider;
        // Stapel (mehrere Gesichter je Lauf) nur auf der CPU: DirectML scheitert bei w600k_r50 mit Stapel > 1 an einer
        // BatchNormalization („Falscher Parameter“, gemessen mit 1.24.4 / GTX 1080 Ti); einzeln braucht ein Gesicht dort ≈ 11 ms.
        _batchSupported = !string.Equals(provider, "DirectML", StringComparison.Ordinal);
        _detectorInput = detector.InputNames[0];
        _detectorOutputs = [.. detector.OutputNames];
        _recognizerInput = recognizer.InputNames[0];
        _recognizerOutput = recognizer.OutputNames[0];
    }

    public string ModelVersion => Version;

    public string ExecutionProvider { get; }

    public float ScoreThreshold { get; init; } = ScrfdDecoder.DefaultScoreThreshold;

    public float NmsThreshold { get; init; } = ScrfdDecoder.DefaultNmsThreshold;

    /// <summary>Lädt beide Modelle; mit <paramref name="useGpu"/> zuerst DirectML, bei Fehler CPU.</summary>
    public static OnnxFaceAnalyzer Load(string detectorPath, string recognizerPath, bool useGpu, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (!File.Exists(detectorPath) || !File.Exists(recognizerPath))
        {
            throw new FileNotFoundException("Die Modelle der Gesichtserkennung fehlen.", File.Exists(detectorPath) ? recognizerPath : detectorPath);
        }

        if (useGpu)
        {
            OnnxFaceAnalyzer? gpu = null;
            try
            {
                gpu = Create(detectorPath, recognizerPath, gpu: true, logger);
                gpu.Probe();
                logger.LogInformation("Gesichtserkennung: Modelle geladen (DirectML)");
                return gpu;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                gpu?.Dispose(); // Sitzungen des gescheiterten Probelaufs nicht liegen lassen
                logger.LogWarning(ex, "Gesichtserkennung: DirectML nicht nutzbar — Rückfall auf CPU");
            }
        }

        var cpu = Create(detectorPath, recognizerPath, gpu: false, logger);
        logger.LogInformation("Gesichtserkennung: Modelle geladen (CPU)");
        return cpu;
    }

    private static OnnxFaceAnalyzer Create(string detectorPath, string recognizerPath, bool gpu, ILogger logger)
    {
        InferenceSession? detector = null;
        try
        {
            detector = new InferenceSession(detectorPath, CreateOptions(gpu));
            var recognizer = new InferenceSession(recognizerPath, CreateOptions(gpu));
            return new OnnxFaceAnalyzer(detector, recognizer, gpu ? "DirectML" : "CPU", logger);
        }
        catch
        {
            detector?.Dispose();
            throw;
        }
    }

    private static SessionOptions CreateOptions(bool gpu)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            // Nur Fehler protokollieren: w600k_r50 deklariert die Ausgabe als [1, 512] und warnt sonst bei jedem Stapel.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        if (gpu)
        {
            // DirectML-Vorgaben: kein Memory-Pattern, sequentielle Ausführung (ONNX-Runtime-Doku).
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(0);
            // Was auf dem Prozessor bleibt (Vor-/Nachbereitung im Graphen), nicht mit allen Kernen (ADR-0074 Nachtrag 0.5.15)
            options.IntraOpNumThreads = Background.BackgroundCpu.OnnxThreads;
            options.InterOpNumThreads = 1;
        }
        else
        {
            options.IntraOpNumThreads = Math.Min(Background.BackgroundCpu.MaxThreads, 8);
            options.InterOpNumThreads = 1;
        }

        return options;
    }

    /// <summary>Probelauf mit einem leeren Bild — deckt DirectML-Fehler auf, bevor der Index darauf baut.</summary>
    private void Probe()
    {
        var blank = new FaceImage(new byte[64 * 64 * 3], 64, 64);
        Analyze(blank);
        var crop = new byte[FaceAligner.CropSize * FaceAligner.CropSize * 3];
        Embed([crop]);
    }

    public FaceAnalysis Analyze(FaceImage image, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(image);

        var clock = Stopwatch.StartNew();
        var detections = Detect(image);
        var detectTime = clock.Elapsed;
        ct.ThrowIfCancellationRequested();

        clock.Restart();
        var selected = detections.OrderByDescending(d => d.Score).Take(MaxFacesPerImage).ToList();
        var crops = new List<byte[]>(selected.Count);
        var sharpness = new float[selected.Count];
        for (var i = 0; i < selected.Count; i++)
        {
            var crop = FaceAligner.AlignCrop(image, selected[i].Landmarks);
            crops.Add(crop);
            sharpness[i] = FaceQuality.LaplacianVariance(crop, FaceAligner.CropSize, FaceAligner.CropSize);
        }

        var embeddings = crops.Count == 0 ? [] : Embed(crops);
        var faces = new List<AnalyzedFace>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            var d = selected[i];
            faces.Add(new AnalyzedFace(d, embeddings[i], FaceQuality.Score(d.Score, FaceQuality.FaceSize(d), sharpness[i])));
        }

        return new FaceAnalysis(image.Width, image.Height, faces) { DetectTime = detectTime, EmbedTime = clock.Elapsed };
    }

    /// <summary>SCRFD: Letterbox → Lauf → Dekodierung in Bildpixeln.</summary>
    public IReadOnlyList<FaceDetection> Detect(FaceImage image)
    {
        var tensor = new float[3 * InputSize * InputSize];
        var letterbox = ScrfdPreprocessor.Fill(image, tensor);
        var outputs = new float[_detectorOutputs.Length][];
        var shapes = new int[_detectorOutputs.Length][];
        lock (_detectorLock)
        {
            using var input = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, InputSize, InputSize]);
            using var runOptions = new RunOptions();
            using var results = _detector.Run(runOptions, [_detectorInput], [input], _detectorOutputs);
            for (var i = 0; i < results.Count; i++)
            {
                outputs[i] = results[i].GetTensorDataAsSpan<float>().ToArray();
                shapes[i] = [.. results[i].GetTensorTypeAndShape().Shape.Select(d => (int)d)];
            }
        }

        _layout ??= ScrfdOutputLayout.Resolve(shapes);
        return ScrfdDecoder.Decode(outputs, _layout, letterbox, ScoreThreshold, NmsThreshold);
    }

    /// <summary>ArcFace für ausgerichtete 112er-Ausschnitte (RGB) → L2-normierte Fingerabdrücke.</summary>
    public float[][] Embed(IReadOnlyList<byte[]> crops)
    {
        var result = new float[crops.Count][];
        var start = 0;
        while (start < crops.Count)
        {
            var count = _batchSupported ? Math.Min(ArcFaceBatch, crops.Count - start) : 1;
            float[] output;
            try
            {
                output = RunRecognizer(crops, start, count);
            }
            catch (OnnxRuntimeException ex) when (count > 1)
            {
                _logger.LogInformation(ex, "ArcFace nimmt keine Stapel — einzeln weiter");
                _batchSupported = false;
                continue;
            }

            if (output.Length != count * FaceEmbeddingCodec.Dimensions)
            {
                if (count > 1)
                {
                    _batchSupported = false;
                    continue;
                }

                throw new InvalidDataException($"ArcFace lieferte {output.Length} statt {FaceEmbeddingCodec.Dimensions} Werte.");
            }

            for (var i = 0; i < count; i++)
            {
                var embedding = output.AsSpan(i * FaceEmbeddingCodec.Dimensions, FaceEmbeddingCodec.Dimensions).ToArray();
                FaceEmbeddingCodec.NormalizeL2(embedding);
                result[start + i] = embedding;
            }

            start += count;
        }

        return result;
    }

    private float[] RunRecognizer(IReadOnlyList<byte[]> crops, int start, int count)
    {
        const int plane = FaceAligner.CropSize * FaceAligner.CropSize;
        var tensor = new float[count * 3 * plane];
        for (var n = 0; n < count; n++)
        {
            FillArcFaceTensor(crops[start + n], tensor.AsSpan(n * 3 * plane, 3 * plane));
        }

        lock (_recognizerLock)
        {
            using var input = OrtValue.CreateTensorValueFromMemory(tensor, [count, 3, FaceAligner.CropSize, FaceAligner.CropSize]);
            using var runOptions = new RunOptions();
            using var results = _recognizer.Run(runOptions, [_recognizerInput], [input], [_recognizerOutput]);
            return results[0].GetTensorDataAsSpan<float>().ToArray();
        }
    }

    /// <summary>ArcFace-Normierung wie InsightFace: RGB, (x − 127.5) / 127.5, NCHW.</summary>
    public static void FillArcFaceTensor(byte[] rgbCrop, Span<float> tensor)
    {
        const int plane = FaceAligner.CropSize * FaceAligner.CropSize;
        if (rgbCrop.Length != plane * 3 || tensor.Length != plane * 3)
        {
            throw new ArgumentException("ArcFace erwartet einen 112 × 112-RGB-Ausschnitt.");
        }

        for (var i = 0; i < plane; i++)
        {
            tensor[i] = (rgbCrop[i * 3] - 127.5f) / 127.5f;
            tensor[plane + i] = (rgbCrop[i * 3 + 1] - 127.5f) / 127.5f;
            tensor[2 * plane + i] = (rgbCrop[i * 3 + 2] - 127.5f) / 127.5f;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_detectorLock)
        {
            _detector.Dispose();
        }

        lock (_recognizerLock)
        {
            _recognizer.Dispose();
        }
    }
}

/// <summary>Erzeugt <see cref="OnnxFaceAnalyzer"/> aus den Dateien des <see cref="IFaceModelProvider"/>.</summary>
public sealed class OnnxFaceAnalyzerFactory(IFaceModelProvider models, ILogger<OnnxFaceAnalyzer>? logger = null) : IFaceAnalyzerFactory
{
    public string ModelVersion => OnnxFaceAnalyzer.Version;

    public bool IsAvailable => models.State == FaceModelState.Ready;

    public IFaceAnalyzer Create(bool useGpu)
        => OnnxFaceAnalyzer.Load(models.DetectorPath, models.RecognizerPath, useGpu, logger);
}
