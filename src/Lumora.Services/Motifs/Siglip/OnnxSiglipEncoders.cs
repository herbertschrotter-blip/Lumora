using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Lumora.Services.Faces;
using Lumora.Services.Motifs.Models;

namespace Lumora.Services.Motifs.Siglip;

/// <summary>Feste Werte des SigLIP-2-Modells (ADR-0052).</summary>
public static class SiglipModel
{
    /// <summary>
    /// Modell + Vorverarbeitung. „v1“ = vision_model.onnx fp32, 224 × 224 PIL-bilinear, (x/255 − 0,5)/0,5; Laden mit
    /// längster Kante ≥ 448 bzw. aus dem Vorschaubild. Ändert sich etwas davon, hochzählen → alle Bilder neu.
    /// </summary>
    public const string Version = "siglip2-base-p16-224/v1";

    /// <summary>Länge der Bild- und Text-Vektoren.</summary>
    public const int Dimensions = 768;

    public const string OutputName = "pooler_output";

    /// <summary>Ausgabe wählen: „pooler_output“, sonst die erste mit zwei Dimensionen.</summary>
    internal static string ResolveOutput(InferenceSession session)
        => session.OutputNames.FirstOrDefault(n => n == OutputName)
           ?? session.OutputMetadata.First(m => m.Value.Dimensions.Length == 2).Key;

    internal static SessionOptions CreateOptions(bool gpu)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        if (gpu)
        {
            options.EnableMemoryPattern = false; // DirectML-Vorgabe (ONNX-Runtime-Doku)
            options.AppendExecutionProvider_DML(0);
            // Was auf dem Prozessor bleibt (Vor-/Nachbereitung im Graphen), nicht mit allen Kernen (ADR-0074 Nachtrag 0.5.15)
            options.IntraOpNumThreads = Background.BackgroundCpu.OnnxThreads;
            options.InterOpNumThreads = 1;
        }
        else
        {
            // Höchstens die Hälfte der Kerne: Oberfläche, Vorschaubilder und Gesichtserkennung behalten Luft.
            options.IntraOpNumThreads = Math.Min(Background.BackgroundCpu.MaxThreads, 8);
            options.InterOpNumThreads = 1;
        }

        return options;
    }
}

/// <summary>
/// SigLIP-2-Bildmodell über ONNX Runtime (ADR-0052): <c>pixel_values</c> [N, 3, 224, 224] → <c>pooler_output</c>
/// [N, 768], danach L2-normiert. Grafikkarte über DirectML mit Rückfall auf CPU (Probelauf beim Laden), eine Sitzung,
/// ein Lauf zur Zeit.
/// </summary>
public sealed class OnnxSiglipImageEncoder : IMotifImageEncoder
{
    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string _output;
    private readonly object _lock = new();
    private int _disposed;

    private OnnxSiglipImageEncoder(InferenceSession session, string provider)
    {
        _session = session;
        _input = session.InputNames[0];
        _output = SiglipModel.ResolveOutput(session);
        ExecutionProvider = provider;
    }

    public string ModelVersion => SiglipModel.Version;

    public string ExecutionProvider { get; }

    public static OnnxSiglipImageEncoder Load(string modelPath, bool useGpu, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Das Bildmodell der Motiv-Erkennung fehlt.", modelPath);
        }

        if (useGpu)
        {
            OnnxSiglipImageEncoder? gpu = null;
            try
            {
                gpu = new OnnxSiglipImageEncoder(new InferenceSession(modelPath, SiglipModel.CreateOptions(gpu: true)), "DirectML");
                gpu.EncodeImage(new FaceImage(new byte[32 * 32 * 3], 32, 32)); // Probelauf
                logger.LogInformation("Motive: Bildmodell geladen (DirectML)");
                return gpu;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                gpu?.Dispose();
                logger.LogWarning(ex, "Motive: DirectML nicht nutzbar — Rückfall auf CPU");
            }
        }

        var cpu = new OnnxSiglipImageEncoder(new InferenceSession(modelPath, SiglipModel.CreateOptions(gpu: false)), "CPU");
        logger.LogInformation("Motive: Bildmodell geladen (CPU)");
        return cpu;
    }

    public float[] EncodeImage(FaceImage image)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var tensor = new float[SiglipPreprocessor.TensorLength];
        SiglipPreprocessor.Fill(image, tensor);
        float[] output;
        lock (_lock)
        {
            using var input = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, SiglipPreprocessor.Size, SiglipPreprocessor.Size]);
            using var runOptions = new RunOptions();
            using var results = _session.Run(runOptions, [_input], [input], [_output]);
            output = results[0].GetTensorDataAsSpan<float>().ToArray();
        }

        if (output.Length != SiglipModel.Dimensions)
        {
            throw new InvalidDataException($"Bildmodell lieferte {output.Length} statt {SiglipModel.Dimensions} Werte.");
        }

        MotifVectors.NormalizeL2(output);
        return output;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_lock)
        {
            _session.Dispose();
        }
    }
}

/// <summary>
/// SigLIP-2-Sprachmodell (int8) über ONNX Runtime auf dem Prozessor (ADR-0052): <c>input_ids</c> [N, 64] (int64,
/// <see cref="GemmaTokenizer"/>) → <c>pooler_output</c> [N, 768], danach L2-normiert.
/// </summary>
public sealed class OnnxSiglipTextEncoder : IMotifTextEncoder
{
    private readonly InferenceSession _session;
    private readonly GemmaTokenizer _tokenizer;
    private readonly string _input;
    private readonly string _output;
    private readonly object _lock = new();
    private int _disposed;

    private OnnxSiglipTextEncoder(InferenceSession session, GemmaTokenizer tokenizer)
    {
        _session = session;
        _tokenizer = tokenizer;
        _input = session.InputNames.FirstOrDefault(n => n == "input_ids") ?? session.InputNames[0];
        _output = SiglipModel.ResolveOutput(session);
    }

    public string ModelVersion => SiglipModel.Version;

    public GemmaTokenizer Tokenizer => _tokenizer;

    public static OnnxSiglipTextEncoder Load(string modelPath, string tokenizerPath, ILogger? logger = null)
    {
        if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
        {
            throw new FileNotFoundException("Sprachmodell oder Tokenizer der Motiv-Erkennung fehlen.", File.Exists(modelPath) ? tokenizerPath : modelPath);
        }

        var tokenizer = GemmaTokenizer.Load(tokenizerPath);
        var session = new InferenceSession(modelPath, SiglipModel.CreateOptions(gpu: false));
        (logger ?? NullLogger.Instance).LogInformation("Motive: Sprachmodell geladen (CPU, {Vocab:N0} Token)", tokenizer.VocabularySize);
        return new OnnxSiglipTextEncoder(session, tokenizer);
    }

    public IReadOnlyList<float[]> EncodeTexts(IReadOnlyList<string> texts)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var result = new List<float[]>(texts.Count);
        const int batch = 16;
        for (var start = 0; start < texts.Count; start += batch)
        {
            var count = Math.Min(batch, texts.Count - start);
            var ids = new long[count * GemmaTokenizer.MaxLength];
            for (var i = 0; i < count; i++)
            {
                _tokenizer.EncodeForModel(texts[start + i]).CopyTo(ids, i * GemmaTokenizer.MaxLength);
            }

            float[] output;
            lock (_lock)
            {
                using var input = OrtValue.CreateTensorValueFromMemory(ids, [count, GemmaTokenizer.MaxLength]);
                using var runOptions = new RunOptions();
                using var results = _session.Run(runOptions, [_input], [input], [_output]);
                output = results[0].GetTensorDataAsSpan<float>().ToArray();
            }

            if (output.Length != count * SiglipModel.Dimensions)
            {
                throw new InvalidDataException($"Sprachmodell lieferte {output.Length} statt {count * SiglipModel.Dimensions} Werte.");
            }

            for (var i = 0; i < count; i++)
            {
                var vector = output.AsSpan(i * SiglipModel.Dimensions, SiglipModel.Dimensions).ToArray();
                MotifVectors.NormalizeL2(vector);
                result.Add(vector);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_lock)
        {
            _session.Dispose();
        }
    }
}

/// <summary>Erzeugt die SigLIP-Encoder aus den Dateien des <see cref="IMotifModelProvider"/>.</summary>
public sealed class OnnxSiglipEncoderFactory(IMotifModelProvider models, ILogger<OnnxSiglipEncoderFactory>? logger = null) : IMotifEncoderFactory
{
    public string ModelVersion => SiglipModel.Version;

    public bool IsAvailable => models.State == MotifModelState.Ready;

    public MotifScoring Scoring => MotifScoring.Siglip2Base;

    public IMotifImageEncoder CreateImageEncoder(bool useGpu)
        => OnnxSiglipImageEncoder.Load(models.PathOf(MotifModelDownloadInfo.VisionModel), useGpu, logger);

    public IMotifTextEncoder CreateTextEncoder()
        => OnnxSiglipTextEncoder.Load(models.PathOf(MotifModelDownloadInfo.TextModel), models.PathOf(MotifModelDownloadInfo.Tokenizer), logger);
}
