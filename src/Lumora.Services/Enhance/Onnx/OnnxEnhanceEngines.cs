using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Lumora.Services.Faces;
using Lumora.Services.Faces.Alignment;

namespace Lumora.Services.Enhance.Onnx;

/// <summary>
/// Erzeugt die ONNX-Modelle der Werkzeuge (ADR-0071) wie Gesichter/Motive: DirectML (Grafikkarte 0) mit Rückfall auf die
/// CPU, wenn die Sitzung damit nicht entsteht; CPU mit höchstens der Hälfte der Kerne. Ein Lauf je Sitzung zur Zeit.
/// <para>
/// Die Ein- und Ausgabenamen werden aus dem Modell gelesen (nicht fest verdrahtet); feste Eingabemaße (z. B. 512 × 512)
/// ebenso. Wertebereiche der Ausgaben, die je ONNX-Fassung verschieden sein können (0…1 oder 0…255), erkennt der Code am
/// größten Wert.
/// </para>
/// </summary>
public sealed class OnnxEnhanceEngineFactory(ILogger<OnnxEnhanceEngineFactory>? logger = null) : IEnhanceEngineFactory
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public IEnhanceEngine Create(EnhanceTool tool, string modelPath, bool useGpu)
    {
        if (!File.Exists(modelPath))
        {
            throw new EnhanceModelMissingException(tool, "Das Modell ist nicht geladen.");
        }

        var (session, provider) = OnnxSessions.Open(modelPath, useGpu, _logger, EnhanceTexts.Name(tool));
        try
        {
            return tool switch
            {
                EnhanceTool.Upscale or EnhanceTool.UpscaleNatural => new OnnxUpscaler(session, provider), // beide x4, Werte 0…1
                EnhanceTool.Faces => new OnnxFaceRestorer(session, provider),
                EnhanceTool.Colorize => new OnnxColorizer(session, provider),
                EnhanceTool.Erase => new OnnxInpainter(session, provider),
                _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Werkzeug ohne Modell."),
            };
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}

/// <summary>Sitzungs-Optionen wie bei Gesichtern/Motiven.</summary>
internal static class OnnxSessions
{
    public static (InferenceSession Session, string Provider) Open(string path, bool useGpu, ILogger logger, string name)
    {
        if (useGpu)
        {
            try
            {
                var gpu = new InferenceSession(path, Options(gpu: true));
                logger.LogInformation("Foto verbessern: Modell {Name} geladen (DirectML)", name);
                return (gpu, "DirectML");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Foto verbessern: DirectML für {Name} nicht nutzbar — Rückfall auf CPU", name);
            }
        }

        var cpu = new InferenceSession(path, Options(gpu: false));
        logger.LogInformation("Foto verbessern: Modell {Name} geladen (CPU)", name);
        return (cpu, "CPU");
    }

    private static SessionOptions Options(bool gpu)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        if (gpu)
        {
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(0);
        }
        else
        {
            options.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            options.InterOpNumThreads = 1;
        }

        return options;
    }
}

/// <summary>Gemeinsamer Rahmen: Sitzung, Namen, feste Maße, ein Lauf zur Zeit.</summary>
public abstract class OnnxEnhanceEngine : IEnhanceEngine
{
    private readonly object _lock = new();
    private int _disposed;

    protected OnnxEnhanceEngine(InferenceSession session, string provider, EnhanceTool tool)
    {
        Session = session;
        ExecutionProvider = provider;
        Tool = tool;
        if (session.InputMetadata.Values.Any(m => m.ElementDataType != TensorElementType.Float)
            || session.OutputMetadata.Values.First().ElementDataType != TensorElementType.Float)
        {
            throw new EnhanceException($"Das Modell für „{EnhanceTexts.Name(tool)}“ erwartet keine 32-Bit-Gleitkommazahlen und wird nicht unterstützt.");
        }

        OutputName = session.OutputNames[0];
    }

    public EnhanceTool Tool { get; }

    public string ExecutionProvider { get; }

    protected InferenceSession Session { get; }

    protected string OutputName { get; }

    /// <summary>Feste Kantenlänge (H, W) einer Eingabe aus dem Modell oder <paramref name="fallback"/> bei dynamischen Maßen.</summary>
    protected (int Height, int Width) FixedSize(string input, int fallback)
    {
        var dims = Session.InputMetadata[input].Dimensions;
        var h = dims.Length >= 4 && dims[2] > 0 ? dims[2] : fallback;
        var w = dims.Length >= 4 && dims[3] > 0 ? dims[3] : fallback;
        return (h, w);
    }

    /// <summary>Ein Lauf mit den Eingaben (Name → Daten + Form); liefert die erste Ausgabe samt Form.</summary>
    protected (float[] Data, int[] Shape) Run(IReadOnlyList<(string Name, float[] Data, long[] Shape)> inputs)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_lock)
        {
            var values = inputs.Select(i => OrtValue.CreateTensorValueFromMemory(i.Data, i.Shape)).ToList();
            try
            {
                using var runOptions = new RunOptions();
                using var results = Session.Run(runOptions, inputs.Select(i => i.Name).ToList(), values, [OutputName]);
                var shape = results[0].GetTensorTypeAndShape().Shape.Select(d => (int)d).ToArray();
                return (results[0].GetTensorDataAsSpan<float>().ToArray(), shape);
            }
            finally
            {
                foreach (var value in values)
                {
                    value.Dispose();
                }
            }
        }
    }

    public abstract EnhanceImage Process(EnhanceStepInput input, IProgress<double>? progress, CancellationToken ct);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_lock)
            {
                Session.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Real-ESRGAN x4plus (Eingabe RGB 0…1 NCHW, Ausgabe 0…1, Faktor aus den Maßen): in Kacheln zu 256 Pixeln mit 16 Pixel
/// Überlappung (wie Real-ESRGAN <c>tile</c>/<c>tile_pad</c>), 2× = 4× und danach mit Flächenmittel halbiert.
/// </summary>
public sealed class OnnxUpscaler(InferenceSession session, string provider) : OnnxEnhanceEngine(session, provider, EnhanceTool.Upscale)
{
    private const int Tile = 256;
    private const int Pad = 16;

    public override EnhanceImage Process(EnhanceStepInput input, IProgress<double>? progress, CancellationToken ct)
    {
        var src = input.Image;
        var name = Session.InputNames[0];
        var (fixedH, fixedW) = FixedSize(name, 0);
        var tile = fixedW > 0 ? Math.Max(32, fixedW - 2 * Pad) : Tile;
        int scale = 0;
        var wanted = 1;
        EnhanceImage? output = null;
        var tilesX = (src.Width + tile - 1) / tile;
        var tilesY = (src.Height + tile - 1) / tile;
        var done = 0;
        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                ct.ThrowIfCancellationRequested();
                var x0 = tx * tile;
                var y0 = ty * tile;
                var x1 = Math.Min(src.Width, x0 + tile);
                var y1 = Math.Min(src.Height, y0 + tile);
                var px0 = Math.Max(0, x0 - Pad);
                var py0 = Math.Max(0, y0 - Pad);
                var px1 = Math.Min(src.Width, x1 + Pad);
                var py1 = Math.Min(src.Height, y1 + Pad);
                var region = EnhanceImageOps.Region(src, px0, py0, px1 - px0, py1 - py0);
                if (fixedW > 0 && fixedH > 0 && (region.Width != fixedW || region.Height != fixedH))
                {
                    region = PadTo(region, fixedW, fixedH);
                }

                var (data, shape) = Run([(name, EnhanceImageOps.ToChw(region), [1, 3, region.Height, region.Width])]);
                if (shape.Length != 4 || shape[1] != 3)
                {
                    throw new EnhanceException("Das Modell lieferte ein unerwartetes Ergebnis.");
                }

                var outH = shape[2];
                var outW = shape[3];
                if (scale == 0)
                {
                    scale = Math.Max(1, (int)Math.Round((double)outW / region.Width));
                    wanted = Math.Clamp(input.Factor, 1, scale);
                    output = EnhanceImage.Blank(src.Width * wanted, src.Height * wanted); // 2× mit dem 4×-Modell: nie den 4×-Puffer anlegen
                }

                var multiplier = EnhanceImageOps.MaxOf(data) > 2f ? 1f : 255f;
                var up = EnhanceImageOps.FromChw(data, outW, outH, multiplier);
                // nur der innere Teil (ohne Überlappung), je Kachel auf den gewünschten Faktor verkleinert (ganzzahlig, keine Nähte)
                var inner = EnhanceImageOps.Region(up, (x0 - px0) * scale, (y0 - py0) * scale, (x1 - x0) * scale, (y1 - y0) * scale);
                if (wanted != scale)
                {
                    inner = EnhanceImageOps.Resize(inner, (x1 - x0) * wanted, (y1 - y0) * wanted);
                }

                for (var row = 0; row < inner.Height; row++)
                {
                    Buffer.BlockCopy(inner.Pixels, row * inner.Width * 3, output!.Pixels, ((y0 * wanted + row) * output.Width + x0 * wanted) * 3, inner.Width * 3);
                }

                progress?.Report(++done / (double)(tilesX * tilesY));
            }
        }

        return output ?? src;
    }

    private static EnhanceImage PadTo(EnhanceImage src, int width, int height)
    {
        var dst = EnhanceImage.Blank(width, height);
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(y, src.Height - 1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(x, src.Width - 1);
                Buffer.BlockCopy(src.Pixels, (sy * src.Width + sx) * 3, dst.Pixels, (y * width + x) * 3, 3);
            }
        }

        return dst;
    }
}

/// <summary>
/// GFPGAN v1.4 (Eingabe 512 × 512 RGB in −1…1, Ausgabe −1…1): je Gesicht Ausrichtung der 5 Landmarken auf die FFHQ-Vorlage,
/// Restaurieren, mit weichem Rand und der gewählten Stärke zurück ins Bild.
/// </summary>
public sealed class OnnxFaceRestorer(InferenceSession session, string provider) : OnnxEnhanceEngine(session, provider, EnhanceTool.Faces)
{
    /// <summary>FFHQ-Ausrichtung (wie GFPGAN/facefusion „ffhq_512“), relativ.</summary>
    private static readonly (float X, float Y)[] Template =
    [
        (0.37691676f, 0.46864664f),
        (0.62285697f, 0.46912813f),
        (0.50123859f, 0.61331904f),
        (0.39308822f, 0.72541100f),
        (0.61150205f, 0.72490465f),
    ];

    public override EnhanceImage Process(EnhanceStepInput input, IProgress<double>? progress, CancellationToken ct)
    {
        var faces = input.Faces ?? [];
        var result = input.Image.Clone();
        if (faces.Count == 0)
        {
            return result;
        }

        var name = Session.InputNames[0];
        var (size, _) = FixedSize(name, 512);
        var reference = Template.Select(t => new FacePoint(t.X * size, t.Y * size)).ToArray();
        var source = input.Image.AsFaceImage();
        for (var i = 0; i < faces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var toCrop = FaceAligner.Estimate(faces[i], reference);
            var crop = new EnhanceImage(FaceAligner.Warp(source, toCrop, size), size, size);
            var feeds = new List<(string, float[], long[])> { (name, EnhanceImageOps.ToChw(crop, 1f / 255f, 0.5f, 0.5f), [1, 3, size, size]) };
            foreach (var extra in Session.InputNames.Skip(1))
            {
                // CodeFormer-artige Fassungen haben eine zweite Eingabe „weight“ (Treue); GFPGAN nicht.
                feeds.Add((extra, [0.5f], [1]));
            }

            var (data, _) = Run(feeds);
            var restored = EnhanceImageOps.FromChw(data, size, size, 255f, 0.5f, 0.5f);
            EnhanceImageOps.PasteFace(result, restored, toCrop, input.Strength);
            progress?.Report((i + 1) / (double)faces.Count);
        }

        return result;
    }
}

/// <summary>
/// DeOldify artistic (Eingabe Graustufen als RGB 0…255, Ausgabe RGB): Modell rechnet auf einer festen Kantenlänge
/// (512, sofern das Modell keine feste nennt); übernommen wird nur die Farbe (a, b in Lab) — die Helligkeit bleibt aus dem
/// Original in voller Auflösung.
/// </summary>
public sealed class OnnxColorizer(InferenceSession session, string provider) : OnnxEnhanceEngine(session, provider, EnhanceTool.Colorize)
{
    public override EnhanceImage Process(EnhanceStepInput input, IProgress<double>? progress, CancellationToken ct)
    {
        var name = Session.InputNames[0];
        var (h, w) = FixedSize(name, 512);
        var gray = EnhanceImageOps.ToGrayRgb(input.Image);
        var small = EnhanceImageOps.Resize(gray, w, h);
        ct.ThrowIfCancellationRequested();
        var (data, shape) = Run([(name, EnhanceImageOps.ToChw(small, 1f), [1, 3, h, w])]);
        if (shape.Length != 4 || shape[1] != 3)
        {
            throw new EnhanceException("Das Modell lieferte ein unerwartetes Ergebnis.");
        }

        var multiplier = EnhanceImageOps.MaxOf(data) > 2f ? 1f : 255f;
        var colored = EnhanceImageOps.FromChw(data, shape[3], shape[2], multiplier);
        progress?.Report(0.7);
        var full = EnhanceImageOps.Resize(colored, input.Image.Width, input.Image.Height);
        var merged = EnhanceImageOps.MergeLabColor(input.Image, full);
        progress?.Report(1);
        return merged;
    }
}

/// <summary>
/// LaMa big-lama (Eingaben „image“ RGB 0…1 und „mask“ 0/1, je 512 × 512): der Bereich um die Maske (mit Rand, quadratisch)
/// wird auf 512 skaliert gefüllt und nur an den maskierten Stellen weich ins Original eingeblendet.
/// </summary>
public sealed class OnnxInpainter(InferenceSession session, string provider) : OnnxEnhanceEngine(session, provider, EnhanceTool.Erase)
{
    public override EnhanceImage Process(EnhanceStepInput input, IProgress<double>? progress, CancellationToken ct)
    {
        var image = input.Image;
        var mask = input.Mask ?? throw new ArgumentException("Entfernen braucht eine Maske.", nameof(input));
        var result = image.Clone();
        if (EnhanceImageOps.Bounds(mask, image.Width, image.Height) is not { } box)
        {
            return result;
        }

        var maskName = Session.InputNames.FirstOrDefault(n => n.Contains("mask", StringComparison.OrdinalIgnoreCase)) ?? Session.InputNames[^1];
        var imageName = Session.InputNames.First(n => n != maskName);
        var (size, _) = FixedSize(imageName, 512);

        // quadratischer Ausschnitt um die Maske mit Rand (mindestens 128 Pixel bzw. halbe Maskengröße)
        var margin = Math.Max(128, Math.Max(box.Width, box.Height) / 2);
        var side = Math.Min(Math.Max(box.Width, box.Height) + 2 * margin, Math.Max(image.Width, image.Height));
        var cx = box.X + box.Width / 2;
        var cy = box.Y + box.Height / 2;
        var rw = Math.Min(side, image.Width);
        var rh = Math.Min(side, image.Height);
        var rx = Math.Clamp(cx - rw / 2, 0, image.Width - rw);
        var ry = Math.Clamp(cy - rh / 2, 0, image.Height - rh);
        var region = EnhanceImageOps.Region(image, rx, ry, rw, rh);
        var regionMask = EnhanceImageOps.RegionMask(mask, image.Width, rx, ry, rw, rh);
        var small = EnhanceImageOps.Resize(region, size, size);
        var smallMask = EnhanceImageOps.Dilate(EnhanceImageOps.ResizeMask(regionMask, rw, rh, size, size), size, size, 2);
        var maskData = smallMask.Select(m => m != 0 ? 1f : 0f).ToArray();
        ct.ThrowIfCancellationRequested();
        var (data, shape) = Run([(imageName, EnhanceImageOps.ToChw(small), [1, 3, size, size]), (maskName, maskData, [1, 1, size, size])]);
        if (shape.Length != 4 || shape[1] != 3)
        {
            throw new EnhanceException("Das Modell lieferte ein unerwartetes Ergebnis.");
        }

        var multiplier = EnhanceImageOps.MaxOf(data) > 2f ? 1f : 255f;
        var filled = EnhanceImageOps.Resize(EnhanceImageOps.FromChw(data, shape[3], shape[2], multiplier), rw, rh);
        EnhanceImageOps.CompositeMasked(result, filled, regionMask, rx, ry);
        progress?.Report(1);
        return result;
    }
}
