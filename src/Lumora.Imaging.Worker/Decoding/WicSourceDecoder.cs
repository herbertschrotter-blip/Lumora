using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace Lumora.Imaging.Worker.Decoding;

/// <summary>Eine Quelle ließ sich nicht lesen (fehlt, gesperrt, Format ohne WIC-Codec).</summary>
internal sealed class SourceDecodeException(int index, string message, Exception? inner = null) : Exception(message, inner)
{
    public int SourceIndex { get; } = index;
}

/// <summary>
/// Liest Quellen über WIC wie der Gesichts-Lader von Lumora (ADR-0035): JPEG, HEIC (mit HEIF-Erweiterung), PNG, TIFF — geteilt
/// geöffnet, beim Dekodieren verkleinert (JPEG schnell), EXIF-Ausrichtung angewandt, als BGR 8 Bit (16-bit-Quellen werden
/// gewandelt). Dazu aus dem Dateikopf: Brennweite KB, Hersteller/Modell und die Gimbal-Winkel aus dem DJI-XMP. Thread-sicher
/// (keine gemeinsamen Objekte). Kein Pfad gelangt in Meldungen.
/// </summary>
internal static class WicSourceDecoder
{
    private static readonly Regex DjiAttribute = new("drone-dji:(GimbalYawDegree|GimbalPitchDegree|GimbalRollDegree)=\"([-+0-9.eE]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Liest die Quelle mit der längeren Kante höchstens <paramref name="maxEdge"/> (0 = volle Größe).</summary>
    public static SourceImage Read(int index, string path, int maxEdge, SourceMeta? hints = null)
    {
        if (!File.Exists(path))
        {
            throw new SourceDecodeException(index, "Quelle fehlt.");
        }

        try
        {
            return Decode(index, path, maxEdge, hints);
        }
        catch (SourceDecodeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new SourceDecodeException(index, "Quelle nicht lesbar: " + ex.GetType().Name, ex);
        }
    }

    private static SourceImage Decode(int index, string path, int maxEdge, SourceMeta? hints)
    {
        int storedWidth, storedHeight, orientation;
        double? focal35;
        bool hasCamera;
        using (var header = OpenShared(path))
        {
            var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var first = decoder.Frames[0];
            storedWidth = first.PixelWidth;
            storedHeight = first.PixelHeight;
            var metadata = first.Metadata as BitmapMetadata;
            orientation = ReadOrientation(metadata);
            focal35 = ReadNumber(metadata, "System.Photo.FocalLengthInFilm", "/app1/ifd/exif/{ushort=41989}", "/ifd/exif/{ushort=41989}");
            hasCamera = ReadText(metadata, "System.Photo.CameraModel", "/app1/ifd/{ushort=272}", "/ifd/{ushort=272}") is { Length: > 0 }
                        || ReadText(metadata, "System.Photo.CameraManufacturer", "/app1/ifd/{ushort=271}", "/ifd/{ushort=271}") is { Length: > 0 };
        }

        if (storedWidth <= 0 || storedHeight <= 0)
        {
            throw new SourceDecodeException(index, "Quelle ohne Bildgröße.");
        }

        var gimbal = ReadDjiGimbal(path);
        var bitmap = new BitmapImage();
        using (var stream = OpenShared(path))
        {
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (maxEdge > 0)
            {
                if (storedWidth >= storedHeight)
                {
                    if (storedWidth > maxEdge)
                    {
                        bitmap.DecodePixelWidth = maxEdge;
                    }
                }
                else if (storedHeight > maxEdge)
                {
                    bitmap.DecodePixelHeight = maxEdge;
                }
            }

            bitmap.EndInit();
        }

        bitmap.Freeze();
        var oriented = ApplyOrientation(bitmap, orientation);
        BitmapSource bgr = oriented.Format == PixelFormats.Bgr24 ? oriented : new FormatConvertedBitmap(oriented, PixelFormats.Bgr24, null, 0);
        if (!bgr.IsFrozen)
        {
            bgr.Freeze();
        }

        int w = bgr.PixelWidth, h = bgr.PixelHeight;
        var mat = new Mat(h, w, MatType.CV_8UC3);
        try
        {
            bgr.CopyPixels(System.Windows.Int32Rect.Empty, mat.Data, (int)(mat.Step() * h), (int)mat.Step());
        }
        catch
        {
            mat.Dispose();
            throw;
        }

        var swap = orientation is >= 5 and <= 8;
        var meta = new SourceMeta
        {
            Focal35 = hints?.Focal35 ?? (focal35 is > 1 and < 2000 ? focal35 : null),
            GimbalYaw = hints?.GimbalYaw ?? gimbal?.Yaw,
            GimbalPitch = hints?.GimbalPitch ?? gimbal?.Pitch,
            GimbalRoll = hints?.GimbalRoll ?? gimbal?.Roll,
            HasCamera = hasCamera || hints?.HasCamera == true,
        };
        return new SourceImage
        {
            Index = index,
            Image = mat,
            FullSize = swap ? new OpenCvSharp.Size(storedHeight, storedWidth) : new OpenCvSharp.Size(storedWidth, storedHeight),
            Meta = meta,
        };
    }

    /// <summary>Gimbal-Winkel aus dem DJI-XMP im Dateikopf (Klartext in den ersten 600 KB), sonst null.</summary>
    internal static (double Yaw, double Pitch, double Roll)? ReadDjiGimbal(string path)
    {
        try
        {
            using var fs = OpenShared(path);
            var buffer = new byte[(int)Math.Min(fs.Length, 600_000)];
            var read = fs.Read(buffer, 0, buffer.Length);
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            if (!text.Contains("drone-dji:", StringComparison.Ordinal))
            {
                return null;
            }

            double? yaw = null, pitch = null, roll = null;
            foreach (Match m in DjiAttribute.Matches(text))
            {
                if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    continue;
                }

                switch (m.Groups[1].Value)
                {
                    case "GimbalYawDegree":
                        yaw ??= v;
                        break;
                    case "GimbalPitchDegree":
                        pitch ??= v;
                        break;
                    default:
                        roll ??= v;
                        break;
                }
            }

            return yaw is { } y && pitch is { } p ? (y, p, roll ?? 0) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    /// <summary>EXIF-Orientierung 1–8 (1 = unverändert), wie WicImageThumbnailRenderer.ReadOrientation in Lumora.</summary>
    private static int ReadOrientation(BitmapMetadata? metadata)
    {
        var value = ReadNumber(metadata, "System.Photo.Orientation", "/app1/ifd/{ushort=274}");
        return value is >= 1 and <= 8 ? (int)value.Value : 1;
    }

    private static double? ReadNumber(BitmapMetadata? metadata, params string[] queries)
    {
        if (metadata is null)
        {
            return null;
        }

        foreach (var query in queries)
        {
            try
            {
                if (!metadata.ContainsQuery(query))
                {
                    continue;
                }

                var value = metadata.GetQuery(query) switch
                {
                    ushort us => us,
                    short s => s,
                    int i => i,
                    uint ui => ui,
                    byte b => b,
                    long l => l,
                    ulong ul => ul,
                    double d => d,
                    float f => f,
                    _ => (double?)null,
                };
                if (value is { } v && double.IsFinite(v))
                {
                    return v;
                }
            }
            catch
            {
                // Metadaten-Policy für diesen Container nicht verfügbar (z. B. PNG) → nächste Abfrage
            }
        }

        return null;
    }

    private static string? ReadText(BitmapMetadata? metadata, params string[] queries)
    {
        if (metadata is null)
        {
            return null;
        }

        foreach (var query in queries)
        {
            try
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is string s && !string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim();
                }
            }
            catch
            {
                // nächste Abfrage
            }
        }

        return null;
    }

    /// <summary>EXIF-Orientierung als Drehung (90°-Schritte, im Uhrzeigersinn) und Spiegelung — gleiche Zuordnung wie Lumora.</summary>
    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        var (angle, flipHorizontal, flipVertical) = orientation switch
        {
            2 => (0.0, true, false),
            3 => (180.0, false, false),
            4 => (0.0, false, true),
            5 => (90.0, true, false),
            6 => (90.0, false, false),
            7 => (270.0, true, false),
            8 => (270.0, false, false),
            _ => (0.0, false, false),
        };
        if (angle == 0 && !flipHorizontal && !flipVertical)
        {
            return source;
        }

        var group = new TransformGroup();
        if (angle != 0)
        {
            group.Children.Add(new RotateTransform(angle));
        }

        if (flipHorizontal || flipVertical)
        {
            group.Children.Add(new ScaleTransform(flipHorizontal ? -1 : 1, flipVertical ? -1 : 1));
        }

        var transformed = new TransformedBitmap(source, group);
        transformed.Freeze();
        return transformed;
    }
}
