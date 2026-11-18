using System.Globalization;
using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>Tonwert- und Farbstatistik der Quellen (Helligkeits-Histogramm in L*, mittlere Buntheit C*).</summary>
internal sealed class ToneReference
{
    public required double[] Cdf { get; init; }

    public double Chroma { get; init; }

    public double LStd { get; init; }
}

/// <summary>
/// Tonwert-Angleich an die Quellen (ADR-0078, Abnahme 2026-11-18 Nr. 1, Bau Stufe 1): Herbert fand die Spike-Ergebnisse
/// geometrisch gut, aber flacher als die Quellen bzw. seine Photoshop-Fassung. Belichtungsausgleich in Blöcken und
/// Überblenden gleichen die Automatik-Belichtung der Einzelbilder an — dabei wandert das Panorama zur Mitte und verliert
/// Tonwertumfang und Buntheit. Der Angleich misst Helligkeitsverteilung (L*) und Buntheit (C*) aller Quellen gemeinsam und
/// zieht das Panorama mit einer glatten, monotonen Tonkurve und einer Buntheits-Anhebung dorthin zurück (Stärke 0…1). Die
/// Kurve ist steigungsbegrenzt (keine Abrisse im Himmel); die Buntheit folgt der Helligkeit (Sättigung bleibt, wo die Kurve
/// aufhellt oder abdunkelt) und wird global nur angehoben, nie gesenkt. Gemessen an Spike-Serien: ohne Angleich ist das
/// Panorama so flach wie Runde 2, mit Angleich liegt der Kontrast nahe an Quellen und Photoshop.
/// </summary>
internal static class ToneMatcher
{
    private const double MaxSlope = 1.8, MinSlope = 0.55;
    private const double MaxShift = 36; // L-Stufen (0…255)
    private const double MaxChromaGain = 1.25;

    /// <summary>Statistik der Quellen (jede verkleinert auf ≈ 0,25 MP, gleich gewichtet).</summary>
    public static ToneReference Reference(IReadOnlyList<Mat> images)
    {
        var hist = new double[256];
        double chromaSum = 0, chromaN = 0;
        foreach (var img in images)
        {
            using var small = Downscale(img, 250_000);
            var (h, c, n) = Stats(small, excludeBlack: false);
            var total = h.Sum();
            if (total <= 0)
            {
                continue;
            }

            for (var v = 0; v < 256; v++)
            {
                hist[v] += h[v] / total;
            }

            chromaSum += c * n;
            chromaN += n;
        }

        return new ToneReference { Cdf = Cdf(hist), Chroma = chromaN > 0 ? chromaSum / chromaN : 0, LStd = StdOf(hist) };
    }

    /// <summary>Wendet den Angleich an (in place); liefert einen Hinweis-Code oder null, wenn nichts zu tun war.</summary>
    public static unsafe string? Match(Mat pano, ToneReference reference, double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        if (strength <= 0 || pano.Empty())
        {
            return null;
        }

        double[] hist;
        double chroma;
        using (var small = Downscale(pano, 1_000_000))
        {
            (hist, chroma, _) = Stats(small, excludeBlack: true);
        }

        if (hist.Sum() <= 0)
        {
            return null;
        }

        var cdf = Cdf(hist);
        var lut = new double[256];
        for (var v = 0; v < 256; v++)
        {
            lut[v] = Inverse(reference.Cdf, cdf[v]) - v;
        }

        // glätten (Kasten 15 Stufen, zweimal), Stärke, begrenzen, monoton mit begrenzter Steigung
        for (var pass = 0; pass < 2; pass++)
        {
            var copy = (double[])lut.Clone();
            for (var v = 0; v < 256; v++)
            {
                double s = 0;
                var n = 0;
                for (var d = -7; d <= 7; d++)
                {
                    var i = v + d;
                    if (i is >= 0 and < 256)
                    {
                        s += copy[i];
                        n++;
                    }
                }

                lut[v] = s / n;
            }
        }

        var curve = new double[256];
        for (var v = 0; v < 256; v++)
        {
            curve[v] = v + Math.Clamp(lut[v] * strength, -MaxShift, MaxShift);
        }

        curve[0] = Math.Max(0, Math.Min(curve[0], 8));
        for (var v = 1; v < 256; v++)
        {
            curve[v] = Math.Clamp(curve[v], curve[v - 1] + MinSlope, curve[v - 1] + MaxSlope);
        }

        // Spitze zurück an 255 binden, ohne die Kurve umzukehren
        if (curve[255] > 255)
        {
            var k = (255 - curve[0]) / Math.Max(1e-6, curve[255] - curve[0]);
            for (var v = 0; v < 256; v++)
            {
                curve[v] = curve[0] + (curve[v] - curve[0]) * k;
            }
        }

        var lLut = new byte[256];
        var maxDelta = 0.0;
        for (var v = 0; v < 256; v++)
        {
            lLut[v] = (byte)Math.Clamp(Math.Round(curve[v]), 0, 255);
            maxDelta = Math.Max(maxDelta, Math.Abs(curve[v] - v));
        }

        var gain = chroma > 1 ? Math.Clamp(reference.Chroma / chroma, 1.0, MaxChromaGain) : 1.0;
        gain = 1 + strength * (gain - 1);
        if (maxDelta < 1.5 && gain < 1.01)
        {
            return null;
        }

        // Buntheit je Helligkeitsstufe: globaler Faktor × Sättigungserhalt — wird ein Ton heller, wächst C* mit (sonst wirkt
        // z. B. ein aufgehellter Himmel blass), wird er dunkler, sinkt es mit; begrenzt auf 0,85…1,3
        var chromaFactor = new float[256];
        for (var v = 0; v < 256; v++)
        {
            var keepSaturation = Math.Clamp((curve[v] + 20) / (v + 20), 0.85, 1.3);
            chromaFactor[v] = (float)(gain * (1 + strength * (keepSaturation - 1)));
        }

        const int strip = 1024;
        int panoRows = pano.Rows, panoCols = pano.Cols;
        for (var y0 = 0; y0 < panoRows; y0 += strip)
        {
            var rows = Math.Min(strip, panoRows - y0);
            using var view = new Mat(pano, new Rect(0, y0, panoCols, rows));
            using var lab = new Mat();
            Cv2.CvtColor(view, lab, ColorConversionCodes.BGR2Lab);
            var lp = (byte*)lab.Data;
            var ls = lab.Step();
            Parallel.For(0, rows, new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count }, y =>
            {
                var row = lp + y * ls;
                for (var x = 0; x < panoCols; x++)
                {
                    var p = row + x * 3;
                    var l = p[0];
                    var k = chromaFactor[l];
                    p[0] = lLut[l];
                    p[1] = (byte)Math.Clamp((int)MathF.Round(128 + (p[1] - 128) * k), 0, 255);
                    p[2] = (byte)Math.Clamp((int)MathF.Round(128 + (p[2] - 128) * k), 0, 255);
                }
            });
            using var back = new Mat();
            Cv2.CvtColor(lab, back, ColorConversionCodes.Lab2BGR);
            // reine Schwarz-Lücken („Ungeschnitten“) schwarz lassen
            using var gray = new Mat();
            Cv2.CvtColor(view, gray, ColorConversionCodes.BGR2GRAY);
            using var keep = new Mat();
            Cv2.Threshold(gray, keep, 0, 255, ThresholdTypes.Binary);
            back.CopyTo(view, keep);
        }

        return string.Create(CultureInfo.InvariantCulture, $"tone:L{maxDelta:0},C{gain:0.00}");
    }

    private static Mat Downscale(Mat img, double maxPixels)
    {
        var area = (double)img.Width * img.Height;
        var small = new Mat();
        if (area <= maxPixels)
        {
            img.CopyTo(small);
            return small;
        }

        var f = Math.Sqrt(maxPixels / area);
        Cv2.Resize(img, small, new Size(Math.Max(1, (int)(img.Width * f)), Math.Max(1, (int)(img.Height * f))), 0, 0, InterpolationFlags.Area);
        return small;
    }

    /// <summary>L-Histogramm (8-bit-Lab) und mittlere Buntheit; <paramref name="excludeBlack"/>: Lücken (0,0,0) auslassen.</summary>
    private static unsafe (double[] Hist, double Chroma, double Count) Stats(Mat bgr, bool excludeBlack)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        var hist = new double[256];
        double chroma = 0, n = 0;
        var lp = (byte*)lab.Data;
        var bp = (byte*)bgr.Data;
        long ls = lab.Step(), bs = bgr.Step();
        int rows = lab.Rows, cols = lab.Cols;
        for (var y = 0; y < rows; y++)
        {
            var row = lp + y * ls;
            var brow = bp + y * bs;
            for (var x = 0; x < cols; x++)
            {
                if (excludeBlack && brow[x * 3] == 0 && brow[x * 3 + 1] == 0 && brow[x * 3 + 2] == 0)
                {
                    continue;
                }

                hist[row[x * 3]]++;
                double a = row[x * 3 + 1] - 128, b = row[x * 3 + 2] - 128;
                chroma += Math.Sqrt(a * a + b * b);
                n++;
            }
        }

        return (hist, n > 0 ? chroma / n : 0, n);
    }

    private static double[] Cdf(double[] hist)
    {
        var total = hist.Sum();
        var cdf = new double[256];
        double acc = 0;
        for (var v = 0; v < 256; v++)
        {
            acc += hist[v];
            cdf[v] = total > 0 ? acc / total : (v + 1) / 256.0;
        }

        return cdf;
    }

    /// <summary>Kleinste Stufe t mit cdf(t) ≥ p, linear zwischen den Stufen.</summary>
    private static double Inverse(double[] cdf, double p)
    {
        if (p <= cdf[0])
        {
            return 0;
        }

        for (var t = 1; t < 256; t++)
        {
            if (cdf[t] >= p)
            {
                var span = cdf[t] - cdf[t - 1];
                return span <= 1e-12 ? t : t - 1 + (p - cdf[t - 1]) / span;
            }
        }

        return 255;
    }

    private static double StdOf(double[] hist)
    {
        var total = hist.Sum();
        if (total <= 0)
        {
            return 0;
        }

        double mean = 0;
        for (var v = 0; v < 256; v++)
        {
            mean += v * hist[v];
        }

        mean /= total;
        double var = 0;
        for (var v = 0; v < 256; v++)
        {
            var += (v - mean) * (v - mean) * hist[v];
        }

        return Math.Sqrt(var / total);
    }
}
