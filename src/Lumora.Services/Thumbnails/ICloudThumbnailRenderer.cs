using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Services.Thumbnails;

/// <summary>
/// Vorschaubild eines OneDrive-Platzhalters über die Windows-Shell, ohne Download (ADR-0077, Spike 19): wie der Explorer
/// über <c>IShellItemImageFactory</c> mit <c>SIIGBF_THUMBNAILONLY</c>, auf STA-Threads, höchstens 8 gleichzeitig. Das
/// Ergebnis landet in Lumoras normalem Vorschaubild-Cache (gleiche Datei <c>{fileId}_400_still.jpg</c>, kurze Kante wie
/// <see cref="ThumbnailSizing"/>, nie hochskaliert, atomar geschrieben). Die Implementierung braucht WPF-Typen (Bitmap,
/// JPEG-Encoder) und lebt deshalb im Wpf-Projekt.
/// </summary>
public interface ICloudThumbnailRenderer
{
    /// <summary>False in der versteckten Sitzung und außerhalb von Windows.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Holt das Shell-Vorschaubild und schreibt es nach <paramref name="targetThumbPath"/>. Scheitert es (keine Verbindung,
    /// Zeitgrenze), ist das Ergebnis <see cref="ThumbnailErrorCodes.CloudUnavailable"/>. Liest nie das Original.
    /// </summary>
    Task<ThumbnailRenderResult> RenderAsync(string sourcePath, string targetThumbPath, int targetShortEdge, CancellationToken ct);
}
