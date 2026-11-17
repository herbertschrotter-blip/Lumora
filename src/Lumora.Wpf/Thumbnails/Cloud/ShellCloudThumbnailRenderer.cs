using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Services.Thumbnails;

namespace Lumora.Thumbnails.Cloud;

/// <summary>
/// <see cref="ICloudThumbnailRenderer"/> über die Shell (ADR-0077): holt das Vorschaubild eines OneDrive-Platzhalters in
/// <see cref="RequestBox"/> px (dieselbe Größe wie die Lightbox-Vorschau — der zweite Abruf kommt dann aus dem Cache von
/// Windows), verkleinert es auf Lumoras Vorschaubild-Größe (kurze Kante <see cref="ThumbnailSizing.ShortEdge"/>, lange
/// höchstens das Dreifache, nie hochskaliert) und schreibt es wie der WIC-Renderer atomar (<c>.tmp</c> + Move) mit
/// <see cref="ThumbnailSizing.JpegQuality"/>. Das Original wird nie gelesen. Bildmaße des Originals kennt die Shell hier
/// nicht — sie bleiben leer, bis die Datei lokal ist (die Galerie nimmt so lange das Seitenverhältnis des Vorschaubilds).
/// </summary>
public sealed class ShellCloudThumbnailRenderer : ICloudThumbnailRenderer
{
    /// <summary>Angefragte Kantenlänge (Spike 19: 1024 px kommen vollständig an der langen Kante).</summary>
    public const int RequestBox = 1024;

    private readonly ICloudPreviewSource _source;
    private readonly ShellThumbnailExtractor? _extractor;

    public ShellCloudThumbnailRenderer(ICloudPreviewSource source)
    {
        _source = source;
        _extractor = source as ShellThumbnailExtractor;
    }

    public bool IsAvailable => _extractor?.IsAvailable ?? true;

    public async Task<ThumbnailRenderResult> RenderAsync(string sourcePath, string targetThumbPath, int targetShortEdge, CancellationToken ct)
    {
        BitmapSource? preview;
        try
        {
            preview = await _source.GetPreviewAsync(sourcePath, RequestBox, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.CloudUnavailable, ex.Message);
        }

        if (preview is null)
        {
            return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.CloudUnavailable, "Die Shell lieferte kein Vorschaubild (keine Verbindung oder Zeitgrenze)");
        }

        return await Task.Run(() => Write(preview, targetThumbPath, targetShortEdge), ct).ConfigureAwait(false);
    }

    /// <summary>Verkleinern und atomar als JPEG schreiben (öffentlich für Tests).</summary>
    internal static ThumbnailRenderResult Write(BitmapSource preview, string targetThumbPath, int targetShortEdge)
    {
        try
        {
            var (width, height) = ThumbnailSizing.Fit(preview.PixelWidth, preview.PixelHeight, targetShortEdge);
            BitmapSource frame = preview;
            if (width > 0 && width < preview.PixelWidth)
            {
                var scaled = new TransformedBitmap(preview, new ScaleTransform((double)width / preview.PixelWidth, (double)height / preview.PixelHeight));
                scaled.Freeze();
                frame = scaled;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetThumbPath)!);
            var tempPath = targetThumbPath + ".tmp";
            var encoder = new JpegBitmapEncoder { QualityLevel = ThumbnailSizing.JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (var stream = File.Create(tempPath))
            {
                encoder.Save(stream);
            }

            File.Move(tempPath, targetThumbPath, overwrite: true);
            return ThumbnailRenderResult.Ok(frame.PixelWidth, frame.PixelHeight, new FileInfo(targetThumbPath).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return ThumbnailRenderResult.Fail(WicImageThumbnailRenderer.EncodeFailedErrorCode, ex.Message);
        }
    }
}
