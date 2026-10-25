using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Faces;
using Lumora.Services.Enhance;
using Lumora.Services.Faces;

namespace Lumora.Enhance;

/// <summary>
/// WIC-Codec für „Foto verbessern“ (ADR-0071, Implementierung von <see cref="IEnhanceImageCodec"/>): Lesen wie die
/// Gesichtserkennung (geteilt geöffnet, verkleinert dekodiert, EXIF-Ausrichtung angewandt, RGB24), Schreiben als JPEG über eine
/// Temp-Datei im Zielordner (der Zielordner ist der Temp-Ordner des Katalogs, in der versteckten Sitzung im Container).
/// Thread-sicher.
/// </summary>
public sealed class WicEnhanceImageCodec : IEnhanceImageCodec
{
    private readonly WicFaceImageLoader _loader = new();

    public EnhanceImage Load(string path, int maxEdge)
    {
        try
        {
            var image = _loader.Load(path, maxEdge);
            return new EnhanceImage(image.Pixels, image.Width, image.Height);
        }
        catch (FaceImageDecodeException ex)
        {
            throw new EnhanceException("Das Foto ließ sich nicht lesen.", ex);
        }
    }

    public void SaveJpeg(EnhanceImage image, string path, int quality)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Rgb24, null, image.Pixels, image.Width * 3);
        source.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(source));
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
