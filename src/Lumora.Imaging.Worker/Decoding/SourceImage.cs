using OpenCvSharp;

namespace Lumora.Imaging.Worker.Decoding;

/// <summary>Angaben aus dem Dateikopf einer Quelle (für Startwerte der Ausrichtung).</summary>
internal sealed record SourceMeta
{
    /// <summary>Brennweite KB (35 mm) oder null.</summary>
    public double? Focal35 { get; init; }

    public double? GimbalYaw { get; init; }

    public double? GimbalPitch { get; init; }

    public double? GimbalRoll { get; init; }

    /// <summary>EXIF-Hersteller/-Modell vorhanden (ohne: Scan oder bereinigte Datei).</summary>
    public bool HasCamera { get; init; }

    public bool HasGimbal => GimbalYaw is not null && GimbalPitch is not null;
}

/// <summary>
/// Eine gelesene Quelle: Bild (BGR 8 Bit, EXIF-Ausrichtung angewandt), Maße bei voller Auflösung und Maßstab der gelesenen
/// Fassung (gelesen / voll). Der Besitzer gibt <see cref="Image"/> frei.
/// </summary>
internal sealed class SourceImage : IDisposable
{
    /// <summary>Position in der Auswahl (SourceSpec.Index).</summary>
    public required int Index { get; init; }

    public required Mat Image { get; set; }

    /// <summary>Volle Auflösung nach Anwenden der Ausrichtung.</summary>
    public required Size FullSize { get; init; }

    public required SourceMeta Meta { get; init; }

    /// <summary>Breite der gelesenen Fassung / volle Breite (≤ 1).</summary>
    public double Scale => Image.Empty() ? 0 : Image.Width / (double)FullSize.Width;

    public void Dispose() => Image.Dispose();
}
