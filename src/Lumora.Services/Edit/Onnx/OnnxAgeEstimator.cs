using Lumora.Services.Faces;
using Microsoft.ML.OnnxRuntime;

namespace Lumora.Services.Edit.Onnx;

/// <summary>Schätzt das Alter einer erkannten Person; null = nicht möglich (gilt dann wie „unter 21“).</summary>
public interface IAgeEstimator : IDisposable
{
    double? EstimateAge(FaceImage image, FaceDetection face);
}

/// <summary>Kein Altersmodell im Container: jedes Alter ist unbekannt — die strengen Regeln gelten.</summary>
public sealed class UnknownAgeEstimator : IAgeEstimator
{
    public double? EstimateAge(FaceImage image, FaceDetection face) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// genderage aus dem buffalo_l-Paket (insightface): Ausschnitt um das Gesicht (1,5 × die längere Kante der Box, ohne
/// Drehung) auf 96 × 96, RGB 0..255; Ausgabe [Frau, Mann, Alter/100]. Eine Schätzung — die Policy rechnet mit
/// Sicherheitsabstand (21 statt 18).
/// </summary>
public sealed class OnnxAgeEstimator : IAgeEstimator
{
    public const int Size = 96;
    private readonly InferenceSession _session;
    private readonly object _gate = new();

    public OnnxAgeEstimator(string model)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        _session = new InferenceSession(model, options);
    }

    public double? EstimateAge(FaceImage image, FaceDetection face)
    {
        var side = Math.Max(face.Width, face.Height) * 1.5f;
        if (!(side > 1))
        {
            return null;
        }

        var cx = (face.X1 + face.X2) / 2;
        var cy = (face.Y1 + face.Y2) / 2;
        var input = new float[3 * Size * Size];
        var step = side / Size;
        for (var y = 0; y < Size; y++)
        {
            var sy = (int)Math.Clamp(cy - (side / 2) + ((y + 0.5f) * step), 0, image.Height - 1);
            for (var x = 0; x < Size; x++)
            {
                var sx = (int)Math.Clamp(cx - (side / 2) + ((x + 0.5f) * step), 0, image.Width - 1);
                var p = ((sy * image.Width) + sx) * 3;
                var i = (y * Size) + x;
                input[i] = image.Pixels[p];
                input[(Size * Size) + i] = image.Pixels[p + 1];
                input[(2 * Size * Size) + i] = image.Pixels[p + 2];
            }
        }

        float[] output;
        lock (_gate)
        {
            output = OnnxEditContentScanner.Run(_session, input, [1, 3, Size, Size]);
        }

        if (output.Length < 3 || !float.IsFinite(output[2]))
        {
            return null;
        }

        var age = output[2] * 100;
        return age is > 0 and < 120 ? age : null;
    }

    public void Dispose() => _session.Dispose();
}
