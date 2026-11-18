using OpenCvSharp;

namespace Lumora.Imaging.Tests.Synthetic;

/// <summary>
/// Synthetische Szene für die Tests (ADR-0078 Punkt 18): eine prozedurale Rundum-Textur (equirektangular, 360° × 180°) aus
/// zufälligen Formen und Rauschen mit festem Startwert, daraus Lochkamera-Ansichten mit bekannter Gier/Neigung, Brennweite und
/// Verstärkung. Keine externen Bilder.
/// </summary>
internal sealed class SyntheticScene : IDisposable
{
    public SyntheticScene(int seed = 7, int width = 4096)
    {
        Width = width;
        Height = width / 2;
        World = new Mat(Height, Width, MatType.CV_8UC3);
        var rnd = new Random(seed);
        // Grund: weiche Verläufe (Himmel oben, Boden unten), damit Belichtung und Farbe eine Rolle spielen
        for (var y = 0; y < Height; y++)
        {
            var t = y / (double)Height;
            var row = new Scalar(90 + 120 * (1 - t), 120 + 60 * Math.Sin(t * Math.PI), 70 + 140 * t);
            Cv2.Line(World, new Point(0, y), new Point(Width - 1, y), row);
        }

        for (var i = 0; i < 6000; i++)
        {
            var color = new Scalar(rnd.Next(256), rnd.Next(256), rnd.Next(256));
            var x = rnd.Next(Width);
            var y = rnd.Next(Height / 8, Height * 7 / 8);
            var size = (int)(4 + Math.Pow(rnd.NextDouble(), 2.5) * 90);
            switch (rnd.Next(4))
            {
                case 0:
                    Cv2.Circle(World, new Point(x, y), size / 2 + 1, color, -1, LineTypes.AntiAlias);
                    break;
                case 1:
                    Cv2.Rectangle(World, new Rect(x, y, size, (int)(size * (0.3 + rnd.NextDouble()))), color, -1);
                    break;
                case 2:
                    Cv2.Line(World, new Point(x, y), new Point(x + rnd.Next(-size * 2, size * 2), y + rnd.Next(-size, size)), color, 1 + rnd.Next(4), LineTypes.AntiAlias);
                    break;
                default:
                    Cv2.PutText(World, ((char)('A' + rnd.Next(26))).ToString(), new Point(x, y), HersheyFonts.HersheySimplex, 0.5 + rnd.NextDouble() * 2, color, 1 + rnd.Next(3), LineTypes.AntiAlias);
                    break;
            }
        }

        using var noise = new Mat(Height, Width, MatType.CV_8UC3);
        Cv2.SetTheRNG((ulong)seed);
        Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(6, 6, 6));
        Cv2.Add(World, noise, World);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Equirektangulare Textur (BGR).</summary>
    public Mat World { get; }

    /// <summary>
    /// Ansicht einer Lochkamera: Gier (Grad, rechts positiv), Neigung (Grad, oben positiv), Brennweite in Pixeln, Bildgröße,
    /// Helligkeit/Farbe als Faktor je Kanal (B, G, R).
    /// </summary>
    public Mat Render(double yawDeg, double pitchDeg, double f, int w, int h, double gain = 1.0, double rollDeg = 0, (double B, double G, double R)? channel = null)
    {
        double yaw = yawDeg * Math.PI / 180, pitch = pitchDeg * Math.PI / 180, roll = rollDeg * Math.PI / 180;
        using var mapX = new Mat(h, w, MatType.CV_32FC1);
        using var mapY = new Mat(h, w, MatType.CV_32FC1);
        // Kamera → Welt: erst Rollen um z, dann Neigen um x (oben positiv), dann Gier um y (Welt: x rechts, y unten, z vorne)
        double cr = Math.Cos(roll), sr = Math.Sin(roll), cp = Math.Cos(pitch), sp = Math.Sin(pitch), cy = Math.Cos(yaw), sy = Math.Sin(yaw);
        for (var v = 0; v < h; v++)
        {
            for (var u = 0; u < w; u++)
            {
                double x = (u - (w - 1) / 2.0) / f, y = (v - (h - 1) / 2.0) / f, z = 1;
                double x1 = cr * x - sr * y, y1 = sr * x + cr * y, z1 = z;
                double x2 = x1, y2 = cp * y1 - sp * z1, z2 = sp * y1 + cp * z1;
                double x3 = cy * x2 + sy * z2, y3 = y2, z3 = -sy * x2 + cy * z2;
                var n = Math.Sqrt(x3 * x3 + y3 * y3 + z3 * z3);
                var lon = Math.Atan2(x3, z3);
                var lat = Math.Asin(y3 / n);
                mapX.Set(v, u, (float)((lon + Math.PI) / (2 * Math.PI) * Width - 0.5));
                mapY.Set(v, u, (float)((lat + Math.PI / 2) / Math.PI * Height - 0.5));
            }
        }

        var view = new Mat();
        Cv2.Remap(World, view, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Wrap);
        if (Math.Abs(gain - 1) > 1e-9 || channel is not null)
        {
            var (b, g, r) = channel ?? (1, 1, 1);
            Cv2.Multiply(view, new Scalar(gain * b, gain * g, gain * r), view);
        }

        return view;
    }

    /// <summary>Schreibt eine Ansicht als JPEG (ohne EXIF).</summary>
    public static string Save(Mat image, string path, int quality = 92)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Cv2.ImEncode(".jpg", image, out var buffer, [(int)ImwriteFlags.JpegQuality, quality]);
        File.WriteAllBytes(path, buffer);
        return path;
    }

    public void Dispose() => World.Dispose();
}
