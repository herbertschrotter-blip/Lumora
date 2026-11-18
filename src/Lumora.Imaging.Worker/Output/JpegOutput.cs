using OpenCvSharp;
using Lumora.Imaging.Protocol;

namespace Lumora.Imaging.Worker.Output;

/// <summary>
/// Schreibt das Ergebnis als JPEG (libjpeg in OpenCV) über eine Temp-Datei daneben. Kodiert in den Speicher und schreibt mit
/// .NET, weil <c>imwrite</c> unter Windows keine Unicode-Pfade kann (Spike 20). Ziel ist der Temp-Ordner des Katalogs.
/// </summary>
internal static class JpegOutput
{
    public static void Write(Mat image, string path, int quality)
    {
        if (image.Width > WorkerLimits.MaxJpegEdge || image.Height > WorkerLimits.MaxJpegEdge)
        {
            throw new WorkerException(WorkerErrorCode.WriteFailed, "Kante über der JPEG-Grenze.");
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!Cv2.ImEncode(".jpg", image, out var buffer, [(int)ImwriteFlags.JpegQuality, Math.Clamp(quality, 1, 100)]))
        {
            throw new WorkerException(WorkerErrorCode.WriteFailed, "JPEG-Kodierung fehlgeschlagen.");
        }

        var temp = path + ".part";
        try
        {
            File.WriteAllBytes(temp, buffer);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new WorkerException(WorkerErrorCode.WriteFailed, "Ergebnis nicht schreibbar: " + ex.GetType().Name, ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // bleibt im Temp-Ordner des Katalogs, Lumora räumt ihn auf
        }
    }
}
