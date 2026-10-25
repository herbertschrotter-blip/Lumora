using Lumora.Services.Faces;
using Lumora.Services.Faces.Alignment;

namespace Lumora.Services.Enhance;

/// <summary>
/// Bildoperationen ohne Modell für „Foto verbessern“ (ADR-0071): Drehen, Zuschneiden, Licht, Skalieren, Graustufen-Erkennung,
/// Lab-Farbraum, Masken (Pinsel, Kratzer) und Einblenden. Reines C# auf RGB24-Puffern — testbar ohne WPF und ohne Modell.
/// </summary>
public static class EnhanceImageOps
{
    // ------------------------------------------------------------------ Geometrie

    /// <summary>Dreht im Uhrzeigersinn um 0/90/180/270 Grad (andere Werte werden auf Vielfache von 90 gerundet).</summary>
    public static EnhanceImage Rotate(EnhanceImage src, int degrees)
    {
        var turns = ((int)Math.Round(degrees / 90.0) % 4 + 4) % 4;
        if (turns == 0)
        {
            return src;
        }

        var w = src.Width;
        var h = src.Height;
        var (dw, dh) = turns % 2 == 1 ? (h, w) : (w, h);
        var dst = new byte[src.Pixels.Length];
        var s = src.Pixels;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (nx, ny) = turns switch
                {
                    1 => (h - 1 - y, x),
                    2 => (w - 1 - x, h - 1 - y),
                    _ => (y, w - 1 - x),
                };
                var si = (y * w + x) * 3;
                var di = (ny * dw + nx) * 3;
                dst[di] = s[si];
                dst[di + 1] = s[si + 1];
                dst[di + 2] = s[si + 2];
            }
        }

        return new EnhanceImage(dst, dw, dh);
    }

    /// <summary>Schneidet den relativen Ausschnitt aus (mindestens 1 × 1 Pixel, auf das Bild begrenzt).</summary>
    public static EnhanceImage Crop(EnhanceImage src, EnhanceCrop crop)
    {
        if (crop.IsFull)
        {
            return src;
        }

        var x0 = Math.Clamp((int)Math.Round(crop.Left * src.Width), 0, src.Width - 1);
        var y0 = Math.Clamp((int)Math.Round(crop.Top * src.Height), 0, src.Height - 1);
        var x1 = Math.Clamp((int)Math.Round((crop.Left + crop.Width) * src.Width), x0 + 1, src.Width);
        var y1 = Math.Clamp((int)Math.Round((crop.Top + crop.Height) * src.Height), y0 + 1, src.Height);
        var w = x1 - x0;
        var h = y1 - y0;
        var dst = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(src.Pixels, ((y0 + y) * src.Width + x0) * 3, dst, y * w * 3, w * 3);
        }

        return new EnhanceImage(dst, w, h);
    }

    /// <summary>Mittiger Zuschnitt auf das Seitenverhältnis <paramref name="aspect"/> (Breite/Höhe).</summary>
    public static EnhanceCrop CenteredCrop(int width, int height, double aspect)
    {
        var current = (double)width / height;
        if (aspect <= 0 || Math.Abs(current - aspect) < 0.005)
        {
            return new EnhanceCrop(0, 0, 1, 1);
        }

        if (current > aspect)
        {
            var w = aspect / current;
            return new EnhanceCrop((1 - w) / 2, 0, w, 1);
        }

        var h = current / aspect;
        return new EnhanceCrop(0, (1 - h) / 2, 1, h);
    }

    /// <summary>
    /// Helligkeit und Kontrast (je −1…1) über eine Tabelle: Kontrast um die Mitte 128 (bis 2,5-fach bzw. bis flach),
    /// Helligkeit als Versatz bis ±96. Liefert ein neues Bild.
    /// </summary>
    public static EnhanceImage AdjustLight(EnhanceImage src, double brightness, double contrast)
    {
        brightness = Math.Clamp(brightness, -1, 1);
        contrast = Math.Clamp(contrast, -1, 1);
        var factor = contrast >= 0 ? 1 + contrast * 1.5 : 1 + contrast * 0.9;
        var offset = brightness * 96;
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            lut[i] = ClampByte(((i - 128) * factor) + 128 + offset);
        }

        var s = src.Pixels;
        var d = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            d[i] = lut[s[i]];
        }

        return new EnhanceImage(d, src.Width, src.Height);
    }

    /// <summary>
    /// Skaliert auf <paramref name="width"/> × <paramref name="height"/>: verkleinern mit Flächenmittel (kein Flimmern),
    /// vergrößern bilinear.
    /// </summary>
    public static EnhanceImage Resize(EnhanceImage src, int width, int height)
    {
        if (width == src.Width && height == src.Height)
        {
            return src;
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return width <= src.Width && height <= src.Height ? AreaDownscale(src, width, height) : Bilinear(src, width, height);
    }

    private static EnhanceImage Bilinear(EnhanceImage src, int width, int height)
    {
        var dst = new byte[width * height * 3];
        var sx = (double)src.Width / width;
        var sy = (double)src.Height / height;
        Parallel.For(0, height, y =>
        {
            var fy = Math.Clamp(((y + 0.5) * sy) - 0.5, 0, src.Height - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, src.Height - 1);
            var wy = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp(((x + 0.5) * sx) - 0.5, 0, src.Width - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, src.Width - 1);
                var wx = fx - x0;
                var o = (y * width + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var a = src.Pixels[(y0 * src.Width + x0) * 3 + c];
                    var b = src.Pixels[(y0 * src.Width + x1) * 3 + c];
                    var cc = src.Pixels[(y1 * src.Width + x0) * 3 + c];
                    var dd = src.Pixels[(y1 * src.Width + x1) * 3 + c];
                    var top = a + (b - a) * wx;
                    var bottom = cc + (dd - cc) * wx;
                    dst[o + c] = ClampByte(top + (bottom - top) * wy);
                }
            }
        });
        return new EnhanceImage(dst, width, height);
    }

    private static EnhanceImage AreaDownscale(EnhanceImage src, int width, int height)
    {
        var dst = new byte[width * height * 3];
        var sx = (double)src.Width / width;
        var sy = (double)src.Height / height;
        Parallel.For(0, height, y =>
        {
            var ys = y * sy;
            var ye = Math.Min(src.Height, (y + 1) * sy);
            for (var x = 0; x < width; x++)
            {
                var xs = x * sx;
                var xe = Math.Min(src.Width, (x + 1) * sx);
                double r = 0, g = 0, b = 0, total = 0;
                for (var yy = (int)ys; yy < Math.Ceiling(ye); yy++)
                {
                    var wy = Math.Min(yy + 1, ye) - Math.Max(yy, ys);
                    if (wy <= 0)
                    {
                        continue;
                    }

                    for (var xx = (int)xs; xx < Math.Ceiling(xe); xx++)
                    {
                        var wx = Math.Min(xx + 1, xe) - Math.Max(xx, xs);
                        if (wx <= 0)
                        {
                            continue;
                        }

                        var weight = wx * wy;
                        var i = (yy * src.Width + xx) * 3;
                        r += src.Pixels[i] * weight;
                        g += src.Pixels[i + 1] * weight;
                        b += src.Pixels[i + 2] * weight;
                        total += weight;
                    }
                }

                var o = (y * width + x) * 3;
                dst[o] = ClampByte(r / total);
                dst[o + 1] = ClampByte(g / total);
                dst[o + 2] = ClampByte(b / total);
            }
        });
        return new EnhanceImage(dst, width, height);
    }

    /// <summary>Maße, die höchstens <paramref name="maxPixels"/> haben (Seitenverhältnis bleibt).</summary>
    public static (int Width, int Height) FitPixels(int width, int height, long maxPixels)
    {
        var count = (long)width * height;
        if (count <= maxPixels)
        {
            return (width, height);
        }

        var scale = Math.Sqrt((double)maxPixels / count);
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

    // ------------------------------------------------------------------ Farbe

    /// <summary>
    /// true, wenn das Foto (fast) keine Farbe hat: bei mindestens 97 % der Stichproben liegen R, G, B höchstens 28 auseinander
    /// (Schwarz-Weiß, leicht getönte Scans, Sepia schwach).
    /// </summary>
    public static bool IsGrayscale(EnhanceImage image)
    {
        var step = Math.Max(1, (int)(image.PixelCount / 40_000));
        long samples = 0, colored = 0;
        var p = image.Pixels;
        for (long i = 0; i < image.PixelCount; i += step)
        {
            var o = (int)(i * 3);
            var max = Math.Max(p[o], Math.Max(p[o + 1], p[o + 2]));
            var min = Math.Min(p[o], Math.Min(p[o + 1], p[o + 2]));
            samples++;
            if (max - min > 28)
            {
                colored++;
            }
        }

        return samples > 0 && colored <= samples * 0.03;
    }

    /// <summary>Helligkeit je Pixel (BT.601), 0…255.</summary>
    public static byte[] Luminance(EnhanceImage image)
    {
        var p = image.Pixels;
        var l = new byte[image.PixelCount];
        for (var i = 0; i < l.Length; i++)
        {
            var o = i * 3;
            l[i] = (byte)((p[o] * 299 + p[o + 1] * 587 + p[o + 2] * 114 + 500) / 1000);
        }

        return l;
    }

    /// <summary>Grau als RGB (alle drei Kanäle = Helligkeit).</summary>
    public static EnhanceImage ToGrayRgb(EnhanceImage image)
    {
        var l = Luminance(image);
        var d = new byte[l.Length * 3];
        for (var i = 0; i < l.Length; i++)
        {
            d[i * 3] = d[i * 3 + 1] = d[i * 3 + 2] = l[i];
        }

        return new EnhanceImage(d, image.Width, image.Height);
    }

    /// <summary>
    /// Farbe übertragen: Helligkeit (L in Lab) aus <paramref name="luminanceSource"/>, Farbe (a, b) aus
    /// <paramref name="colorSource"/> (gleiche Maße) — so bleibt die volle Schärfe des Originals.
    /// </summary>
    public static EnhanceImage MergeLabColor(EnhanceImage luminanceSource, EnhanceImage colorSource)
    {
        if (luminanceSource.Width != colorSource.Width || luminanceSource.Height != colorSource.Height)
        {
            throw new ArgumentException("Bilder unterschiedlich groß.");
        }

        var dst = new byte[luminanceSource.Pixels.Length];
        var lp = luminanceSource.Pixels;
        var cp = colorSource.Pixels;
        Parallel.For(0, luminanceSource.Height, y =>
        {
            for (var x = 0; x < luminanceSource.Width; x++)
            {
                var o = (y * luminanceSource.Width + x) * 3;
                var (l, _, _) = RgbToLab(lp[o], lp[o + 1], lp[o + 2]);
                var (_, a, b) = RgbToLab(cp[o], cp[o + 1], cp[o + 2]);
                var (r, g, bb) = LabToRgb(l, a, b);
                dst[o] = r;
                dst[o + 1] = g;
                dst[o + 2] = bb;
            }
        });
        return new EnhanceImage(dst, luminanceSource.Width, luminanceSource.Height);
    }

    private static readonly double[] SrgbToLinear = BuildLinearTable();

    private static double[] BuildLinearTable()
    {
        var t = new double[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255.0;
            t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return t;
    }

    /// <summary>sRGB (D65) → CIE-Lab.</summary>
    public static (double L, double A, double B) RgbToLab(byte r, byte g, byte b)
    {
        var rl = SrgbToLinear[r];
        var gl = SrgbToLinear[g];
        var bl = SrgbToLinear[b];
        var x = (rl * 0.4124564 + gl * 0.3575761 + bl * 0.1804375) / 0.95047;
        var y = rl * 0.2126729 + gl * 0.7151522 + bl * 0.0721750;
        var z = (rl * 0.0193339 + gl * 0.1191920 + bl * 0.9503041) / 1.08883;
        var fx = LabF(x);
        var fy = LabF(y);
        var fz = LabF(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIE-Lab → sRGB (D65), auf 0…255 begrenzt.</summary>
    public static (byte R, byte G, byte B) LabToRgb(double l, double a, double b)
    {
        var fy = (l + 16) / 116;
        var fx = fy + a / 500;
        var fz = fy - b / 200;
        var x = LabFInv(fx) * 0.95047;
        var y = LabFInv(fy);
        var z = LabFInv(fz) * 1.08883;
        var rl = x * 3.2404542 - y * 1.5371385 - z * 0.4985314;
        var gl = -x * 0.9692660 + y * 1.8760108 + z * 0.0415560;
        var bl = x * 0.0556434 - y * 0.2040259 + z * 1.0572252;
        return (ToSrgb(rl), ToSrgb(gl), ToSrgb(bl));
    }

    private static double LabF(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116);

    private static double LabFInv(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116) / 7.787;

    private static byte ToSrgb(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        var c = linear <= 0.0031308 ? 12.92 * linear : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
        return ClampByte(c * 255);
    }

    // ------------------------------------------------------------------ Masken

    /// <summary>Pinselstriche (relativ) als Maske in Bildpixeln (255 = füllen).</summary>
    public static byte[] RasterizeDots(int width, int height, IReadOnlyList<EnhanceBrushDot> dots)
    {
        var mask = new byte[(long)width * height];
        foreach (var dot in dots)
        {
            var cx = dot.X * width;
            var cy = dot.Y * height;
            var r = Math.Max(1, dot.Radius * width);
            var x0 = Math.Max(0, (int)(cx - r));
            var x1 = Math.Min(width - 1, (int)Math.Ceiling(cx + r));
            var y0 = Math.Max(0, (int)(cy - r));
            var y1 = Math.Min(height - 1, (int)Math.Ceiling(cy + r));
            var r2 = r * r;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x + 0.5 - cx;
                    var dy = y + 0.5 - cy;
                    if (dx * dx + dy * dy <= r2)
                    {
                        mask[(long)y * width + x] = 255;
                    }
                }
            }
        }

        return mask;
    }

    /// <summary>
    /// Kratzer und Flecken eines Scans finden: die Nachbarn im Abstand 2 und 3 sind waagrecht UND senkrecht je unter sich ruhig
    /// (Spanne &lt; 24), und das Pixel weicht in einer Richtung um mehr als 36 von deren Mittel ab — so fallen dünne Linien
    /// und kleine Flecken auf, Kanten, Struktur und Rauschen nicht. Danach um 1 Pixel erweitert. Deckt die Maske mehr als 6 % ab, ist das
    /// eher Struktur als Kratzer — dann leer (lieber nichts als das Foto zu verwischen).
    /// </summary>
    public static byte[] DetectScratches(EnhanceImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var l = Luminance(image);
        var raw = new byte[(long)w * h];
        long hits = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = l[y * w + x];
                if (x < 3 || y < 3 || x + 3 >= w || y + 3 >= h)
                {
                    continue;
                }

                var dh = Deviation(v, l[y * w + x - 3], l[y * w + x - 2], l[y * w + x + 2], l[y * w + x + 3]);
                var dv = Deviation(v, l[(y - 3) * w + x], l[(y - 2) * w + x], l[(y + 2) * w + x], l[(y + 3) * w + x]);
                // beide Richtungen ruhig (sonst Kante, Struktur oder Rauschen), dann zählt die größere Abweichung
                if (dh >= 0 && dv >= 0 && Math.Max(dh, dv) > 36)
                {
                    raw[(long)y * w + x] = 255;
                    hits++;
                }
            }
        }

        if (hits == 0 || hits > image.PixelCount * 0.06)
        {
            return new byte[(long)w * h];
        }

        return Dilate(raw, w, h, 1);

        static int Deviation(byte v, byte a, byte b, byte c, byte d)
        {
            var max = Math.Max(Math.Max(a, b), Math.Max(c, d));
            var min = Math.Min(Math.Min(a, b), Math.Min(c, d));
            return max - min >= 24 ? -1 : Math.Abs(v - ((a + b + c + d) / 4));
        }
    }

    /// <summary>Maske um <paramref name="radius"/> Pixel erweitern (Quadrat).</summary>
    public static byte[] Dilate(byte[] mask, int width, int height, int radius)
    {
        var dst = new byte[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (mask[(long)y * width + x] == 0)
                {
                    continue;
                }

                for (var yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
                {
                    for (var xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
                    {
                        dst[(long)yy * width + xx] = 255;
                    }
                }
            }
        }

        return dst;
    }

    /// <summary>Umschließendes Rechteck der Maske (x, y, Breite, Höhe) oder null bei leerer Maske.</summary>
    public static (int X, int Y, int Width, int Height)? Bounds(byte[] mask, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (mask[(long)y * width + x] != 0)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Ausschnitt (Pixelrechteck) eines Bildes.</summary>
    public static EnhanceImage Region(EnhanceImage src, int x, int y, int width, int height)
    {
        var dst = new byte[width * height * 3];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(src.Pixels, ((y + row) * src.Width + x) * 3, dst, row * width * 3, width * 3);
        }

        return new EnhanceImage(dst, width, height);
    }

    /// <summary>Ausschnitt einer Maske.</summary>
    public static byte[] RegionMask(byte[] mask, int maskWidth, int x, int y, int width, int height)
    {
        var dst = new byte[width * height];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(mask, (y + row) * maskWidth + x, dst, row * width, width);
        }

        return dst;
    }

    /// <summary>Maske skalieren (nächster Nachbar).</summary>
    public static byte[] ResizeMask(byte[] mask, int width, int height, int newWidth, int newHeight)
    {
        var dst = new byte[newWidth * newHeight];
        for (var y = 0; y < newHeight; y++)
        {
            var sy = Math.Min(height - 1, (int)((y + 0.5) * height / newHeight));
            for (var x = 0; x < newWidth; x++)
            {
                var sx = Math.Min(width - 1, (int)((x + 0.5) * width / newWidth));
                dst[y * newWidth + x] = mask[sy * width + sx];
            }
        }

        return dst;
    }

    /// <summary>
    /// <paramref name="patch"/> (gleich groß wie das Rechteck) bei (<paramref name="x"/>, <paramref name="y"/>) in
    /// <paramref name="target"/> einblenden — nur wo die Maske gesetzt ist, mit weichem Rand (2 Pixel).
    /// </summary>
    public static void CompositeMasked(EnhanceImage target, EnhanceImage patch, byte[] patchMask, int x, int y)
    {
        var soft = BoxBlur(patchMask, patch.Width, patch.Height, 2);
        for (var row = 0; row < patch.Height; row++)
        {
            for (var col = 0; col < patch.Width; col++)
            {
                var weight = Math.Max(soft[row * patch.Width + col], patchMask[row * patch.Width + col]) / 255.0;
                if (weight <= 0)
                {
                    continue;
                }

                var ti = ((y + row) * target.Width + x + col) * 3;
                var pi = (row * patch.Width + col) * 3;
                for (var c = 0; c < 3; c++)
                {
                    target.Pixels[ti + c] = ClampByte(target.Pixels[ti + c] + (patch.Pixels[pi + c] - target.Pixels[ti + c]) * weight);
                }
            }
        }
    }

    /// <summary>Kastenweichzeichner für eine Maske.</summary>
    public static byte[] BoxBlur(byte[] mask, int width, int height, int radius)
    {
        var tmp = new byte[mask.Length];
        var dst = new byte[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int s = 0, n = 0;
                for (var xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
                {
                    s += mask[y * width + xx];
                    n++;
                }

                tmp[y * width + x] = (byte)(s / n);
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int s = 0, n = 0;
                for (var yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
                {
                    s += tmp[yy * width + x];
                    n++;
                }

                dst[y * width + x] = (byte)(s / n);
            }
        }

        return dst;
    }

    // ------------------------------------------------------------------ Gesichter

    /// <summary>
    /// Gesichtsausschnitt zurück ins Bild: <paramref name="toCrop"/> bildet Bildpixel auf Ausschnittpixel ab. Gewicht = weicher
    /// Rand (innen 1, zu den Kanten des Ausschnitts auf 0 über 12 % der Breite) × <paramref name="strength"/>.
    /// </summary>
    public static void PasteFace(EnhanceImage target, EnhanceImage crop, SimilarityTransform toCrop, double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        if (strength <= 0)
        {
            return;
        }

        var fromCrop = toCrop.Inverse();
        var size = crop.Width;
        var corners = new[] { new FacePoint(0, 0), new FacePoint(size, 0), new FacePoint(0, size), new FacePoint(size, size) }
            .Select(fromCrop.Apply).ToList();
        var x0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.X)));
        var x1 = Math.Min(target.Width - 1, (int)Math.Ceiling(corners.Max(c => c.X)));
        var y0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y)));
        var y1 = Math.Min(target.Height - 1, (int)Math.Ceiling(corners.Max(c => c.Y)));
        var margin = size * 0.12;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var p = toCrop.Apply(new FacePoint(x + 0.5f, y + 0.5f));
                if (p.X < 0 || p.Y < 0 || p.X >= size - 1 || p.Y >= size - 1)
                {
                    continue;
                }

                var edge = Math.Min(Math.Min(p.X, p.Y), Math.Min(size - 1 - p.X, size - 1 - p.Y));
                var weight = Math.Clamp(edge / margin, 0, 1) * strength;
                if (weight <= 0)
                {
                    continue;
                }

                var cx = (int)p.X;
                var cy = (int)p.Y;
                var fx = p.X - cx;
                var fy = p.Y - cy;
                var ti = (y * target.Width + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var a = crop.Pixels[(cy * size + cx) * 3 + c];
                    var b = crop.Pixels[(cy * size + cx + 1) * 3 + c];
                    var cc = crop.Pixels[((cy + 1) * size + cx) * 3 + c];
                    var d = crop.Pixels[((cy + 1) * size + cx + 1) * 3 + c];
                    var top = a + (b - a) * fx;
                    var bottom = cc + (d - cc) * fx;
                    var v = top + (bottom - top) * fy;
                    target.Pixels[ti + c] = ClampByte(target.Pixels[ti + c] + (v - target.Pixels[ti + c]) * weight);
                }
            }
        }
    }

    // ------------------------------------------------------------------ Tensoren

    /// <summary>RGB24 → NCHW-Float (<c>(v / 255 − <paramref name="mean"/>) / <paramref name="std"/></c>; bzw. Rohwert × <paramref name="scale"/>).</summary>
    public static float[] ToChw(EnhanceImage image, float scale = 1f / 255f, float mean = 0f, float std = 1f)
    {
        var plane = image.Width * image.Height;
        var data = new float[plane * 3];
        var p = image.Pixels;
        for (var i = 0; i < plane; i++)
        {
            data[i] = ((p[i * 3] * scale) - mean) / std;
            data[plane + i] = ((p[i * 3 + 1] * scale) - mean) / std;
            data[2 * plane + i] = ((p[i * 3 + 2] * scale) - mean) / std;
        }

        return data;
    }

    /// <summary>NCHW-Float → RGB24: <c>(v · <paramref name="std"/> + <paramref name="mean"/>) · <paramref name="multiplier"/></c>, begrenzt.</summary>
    public static EnhanceImage FromChw(ReadOnlySpan<float> data, int width, int height, float multiplier = 255f, float mean = 0f, float std = 1f)
    {
        var plane = width * height;
        if (data.Length < plane * 3)
        {
            throw new EnhanceException("Das Modell lieferte ein unerwartetes Ergebnis.");
        }

        var dst = new byte[plane * 3];
        for (var i = 0; i < plane; i++)
        {
            dst[i * 3] = ClampByte(((data[i] * std) + mean) * multiplier);
            dst[i * 3 + 1] = ClampByte(((data[plane + i] * std) + mean) * multiplier);
            dst[i * 3 + 2] = ClampByte(((data[2 * plane + i] * std) + mean) * multiplier);
        }

        return new EnhanceImage(dst, width, height);
    }

    /// <summary>Größter Wert (für die Erkennung, ob ein Modell 0…1 oder 0…255 liefert).</summary>
    public static float MaxOf(ReadOnlySpan<float> data)
    {
        var max = float.MinValue;
        foreach (var v in data)
        {
            if (v > max)
            {
                max = v;
            }
        }

        return max;
    }

    public static byte ClampByte(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
}
