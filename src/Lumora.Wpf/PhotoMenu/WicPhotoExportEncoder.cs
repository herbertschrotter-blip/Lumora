using System;
using System.IO;
using System.Windows.Media.Imaging;
using Lumora.Services.Export;
using Lumora.Thumbnails;

namespace Lumora.PhotoMenu;

/// <summary>
/// WIC-Kodierer für „Exportieren (verkleinert)“ (ADR-0068): liest das Bild geteilt (Cloud-Sync-tolerant wie der
/// Vorschaubild-Renderer), dekodiert gleich auf die Zielgröße (längste Kante, nie hochskaliert), wendet die EXIF-Ausrichtung an,
/// legt Transparenz auf den dunklen Hintergrund und schreibt atomar ein JPEG (<c>.tmp</c> + Umbenennen). Metadaten werden nicht
/// mitkopiert (verkleinerte Weitergabe-Kopie).
/// </summary>
public sealed class WicPhotoExportEncoder : IPhotoExportEncoder
{
    public void EncodeJpeg(string sourcePath, string targetPath, int maxLongEdge, int jpegQuality)
    {
        int width, height, orientation;
        using (var header = Open(sourcePath))
        {
            var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var first = decoder.Frames[0];
            width = first.PixelWidth;
            height = first.PixelHeight;
            orientation = WicImageThumbnailRenderer.ReadOrientation(first.Metadata as BitmapMetadata);
        }

        var bitmap = new BitmapImage();
        using (var stream = Open(sourcePath))
        {
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (Math.Max(width, height) > maxLongEdge)
            {
                // Nur eine Seite vorgeben — die andere folgt proportional (gespeicherte Pixel, vor der Drehung)
                if (width >= height)
                {
                    bitmap.DecodePixelWidth = maxLongEdge;
                }
                else
                {
                    bitmap.DecodePixelHeight = maxLongEdge;
                }
            }

            bitmap.EndInit();
        }

        bitmap.Freeze();
        BitmapSource result = WicImageThumbnailRenderer.ApplyOrientation(bitmap, orientation);
        if (!WicImageThumbnailRenderer.IsOpaque(result.Format))
        {
            result = WicImageThumbnailRenderer.FlattenAlpha(result, WicImageThumbnailRenderer.AlphaBackground);
        }

        var temp = targetPath + ".tmp";
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }

            File.Move(temp, targetPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp); // eigene Zwischendatei des Exports
            }
        }
    }

    private static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
}
