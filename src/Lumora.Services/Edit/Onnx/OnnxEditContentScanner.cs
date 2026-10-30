using Lumora.Services.Faces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lumora.Services.Edit.Onnx;

/// <summary>Prüft ein Bild mit den Prüfmodellen (ADR-0076 Punkt 8.3).</summary>
public interface IEditContentScanner : IDisposable
{
    EditContentScan Scan(FaceImage image);
}

/// <summary>
/// NudeNet 640m (YOLOv8m, 18 Klassen) und ViT-NSFW (sfw/nsfw) über ONNX Runtime auf dem Prozessor — die Grafikkarte gehört
/// während eines Auftrags ComfyUI. Nur die höchste Sicherheit je Klasse zählt (kein NMS nötig). Eingaben in float32
/// oder float16, je nach Modell (zur Laufzeit gelesen).
/// </summary>
public sealed class OnnxEditContentScanner : IEditContentScanner
{
    /// <summary>Klassen von NudeNet v3 in der Reihenfolge der Modellausgabe.</summary>
    public static readonly string[] NudeNetClasses =
    [
        "FEMALE_GENITALIA_COVERED", "FACE_FEMALE", "BUTTOCKS_EXPOSED", "FEMALE_BREAST_EXPOSED", "FEMALE_GENITALIA_EXPOSED",
        "MALE_BREAST_EXPOSED", "ANUS_EXPOSED", "FEET_EXPOSED", "BELLY_COVERED", "FEET_COVERED", "ARMPITS_COVERED",
        "ARMPITS_EXPOSED", "FACE_MALE", "BELLY_EXPOSED", "MALE_GENITALIA_EXPOSED", "ANUS_COVERED", "FEMALE_BREAST_COVERED",
        "BUTTOCKS_COVERED",
    ];

    public const int NudeNetSize = 640;
    public const int VitSize = 384;

    /// <summary>ViT-Ausgabe: Index der Klasse „nsfw“ (config.json: 0 = sfw, 1 = nsfw).</summary>
    public const int VitNsfwIndex = 1;

    private readonly InferenceSession _nudeNet;
    private readonly InferenceSession _vit;
    private readonly object _gate = new();

    public OnnxEditContentScanner(string nudeNetModel, string vitModel)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Min(Background.BackgroundCpu.MaxThreads, 8),
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        _nudeNet = new InferenceSession(nudeNetModel, options);
        // Das float16-Modell scheitert an einer Layer-Norm-Verschmelzung der vollen Optimierung — nur Grundoptimierung
        using var vitOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
            IntraOpNumThreads = options.IntraOpNumThreads,
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        _vit = new InferenceSession(vitModel, vitOptions);
    }

    public EditContentScan Scan(FaceImage image)
    {
        lock (_gate)
        {
            return new EditContentScan(ScanParts(image), ScanNsfw(image));
        }
    }

    private Dictionary<string, float> ScanParts(FaceImage image)
    {
        // Letterbox wie NudeNet: Seitenverhältnis halten, Rest schwarz, RGB 0..1
        var input = new float[3 * NudeNetSize * NudeNetSize];
        var scale = (float)NudeNetSize / Math.Max(image.Width, image.Height);
        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        Resize(image, w, h, (x, y, r, g, b) =>
        {
            var i = (y * NudeNetSize) + x;
            input[i] = r / 255f;
            input[(NudeNetSize * NudeNetSize) + i] = g / 255f;
            input[(2 * NudeNetSize * NudeNetSize) + i] = b / 255f;
        });

        var output = Run(_nudeNet, input, [1, 3, NudeNetSize, NudeNetSize]);
        // [1, 4 + 18, Anker]: je Klasse die höchste Sicherheit über alle Anker
        var anchors = output.Length / (4 + NudeNetClasses.Length);
        var result = new Dictionary<string, float>(NudeNetClasses.Length);
        for (var c = 0; c < NudeNetClasses.Length; c++)
        {
            var best = 0f;
            var offset = (4 + c) * anchors;
            for (var a = 0; a < anchors; a++)
            {
                best = Math.Max(best, output[offset + a]);
            }

            result[NudeNetClasses[c]] = best;
        }

        return result;
    }

    private float ScanNsfw(FaceImage image)
    {
        var input = new float[3 * VitSize * VitSize];
        Resize(image, VitSize, VitSize, (x, y, r, g, b) =>
        {
            var i = (y * VitSize) + x;
            input[i] = ((r / 255f) - 0.5f) / 0.5f;
            input[(VitSize * VitSize) + i] = ((g / 255f) - 0.5f) / 0.5f;
            input[(2 * VitSize * VitSize) + i] = ((b / 255f) - 0.5f) / 0.5f;
        });

        var logits = Run(_vit, input, [1, 3, VitSize, VitSize]);
        var max = logits.Max();
        var exp = logits.Select(l => Math.Exp(l - max)).ToArray();
        return (float)(exp[VitNsfwIndex] / exp.Sum());
    }

    /// <summary>Eine Eingabe, eine Ausgabe; float16-Modelle bekommen float16 und liefern es zurück.</summary>
    internal static float[] Run(InferenceSession session, float[] input, int[] shape)
    {
        var name = session.InputMetadata.Keys.First();
        var isHalf = session.InputMetadata[name].ElementType == typeof(Float16);
        NamedOnnxValue value = isHalf
            ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<Float16>(input.Select(v => (Float16)v).ToArray(), shape))
            : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(input, shape));
        using var results = session.Run([value]);
        var first = results.First();
        return first.Value switch
        {
            DenseTensor<float> f => f.ToArray(),
            DenseTensor<Float16> h => h.ToArray().Select(v => (float)v).ToArray(),
            _ => first.AsEnumerable<float>().ToArray(),
        };
    }

    /// <summary>Bilineares Verkleinern bzw. Vergrößern des RGB-Bilds auf <paramref name="width"/> × <paramref name="height"/>.</summary>
    internal static void Resize(FaceImage image, int width, int height, Action<int, int, float, float, float> write)
    {
        var px = image.Pixels;
        var sx = (float)image.Width / width;
        var sy = (float)image.Height / height;
        for (var y = 0; y < height; y++)
        {
            var fy = Math.Clamp(((y + 0.5f) * sy) - 0.5f, 0, image.Height - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, image.Height - 1);
            var dy = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp(((x + 0.5f) * sx) - 0.5f, 0, image.Width - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, image.Width - 1);
                var dx = fx - x0;
                var r = Sample(px, image.Width, x0, x1, y0, y1, dx, dy, 0);
                var g = Sample(px, image.Width, x0, x1, y0, y1, dx, dy, 1);
                var b = Sample(px, image.Width, x0, x1, y0, y1, dx, dy, 2);
                write(x, y, r, g, b);
            }
        }
    }

    private static float Sample(byte[] px, int stride, int x0, int x1, int y0, int y1, float dx, float dy, int c)
    {
        var a = px[(((y0 * stride) + x0) * 3) + c];
        var b = px[(((y0 * stride) + x1) * 3) + c];
        var d = px[(((y1 * stride) + x0) * 3) + c];
        var e = px[(((y1 * stride) + x1) * 3) + c];
        var top = a + ((b - a) * dx);
        var bottom = d + ((e - d) * dx);
        return top + ((bottom - top) * dy);
    }

    public void Dispose()
    {
        _nudeNet.Dispose();
        _vit.Dispose();
    }
}
