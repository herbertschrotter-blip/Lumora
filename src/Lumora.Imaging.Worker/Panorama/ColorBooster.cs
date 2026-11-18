using System.Globalization;
using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>Stellgrößen der Farbverstärkung (fest kalibriert, siehe <see cref="ColorBooster"/>); nur Tests und Messungen ändern sie.</summary>
internal sealed record ColorBoostParams
{
    public static readonly ColorBoostParams Default = new();

    /// <summary>Gleichmäßige Sättigung bei Stärke 100 % (Anteil, 0,4 = +40 %; im linearen Licht, vor dem weichen Knie).</summary>
    public double Global { get; init; } = 0.40;

    /// <summary>Zusatz für blasse Farben bei Stärke 100 % (Dynamik); fällt mit der Buntheit ab.</summary>
    public double Vibrance { get; init; } = 0.65;

    /// <summary>Buntheit C*, bei der der Dynamik-Zusatz auf die Hälfte gefallen ist.</summary>
    public double PaleChroma { get; init; } = 55;

    /// <summary>Unterhalb dieser Buntheit bleibt eine Farbe, wie sie ist (Grau bleibt grau, kein Farbstich aus Rauschen) …</summary>
    public double NeutralBelow { get; init; } = 1.5;

    /// <summary>… ab dieser wirkt die Verstärkung voll (dazwischen weicher Übergang).</summary>
    public double NeutralAbove { get; init; } = 7;

    /// <summary>Hauttöne (Farbwinkel um <see cref="SkinHue"/>) bekommen nur diesen Anteil der Verstärkung.</summary>
    public double SkinKeep { get; init; } = 0.45;

    public double SkinHue { get; init; } = 50;

    public double SkinHueWidth { get; init; } = 28;

    /// <summary>
    /// Abstand des weichen Knies zur Lichter-Grenze in sRGB-Stufen: ein Kanal nähert sich nur 255 − Abstand. Ohne ihn häufen
    /// sich verstärkte Farben knapp unter der Grenze, und erst das JPEG (Quantisierung, Farb-Unterabtastung) schneidet sie ab.
    /// </summary>
    public double EdgeMargin { get; init; } = 4;

    /// <summary>Dasselbe zur Tiefen-Grenze (0 + Abstand): in gesättigten dunklen Farben geht ein Kanal sonst nach dem JPEG auf 0.</summary>
    public double EdgeMarginLow { get; init; } = 16;
}

/// <summary>
/// Farbverstärkung nach dem Tonwert-Angleich (Herbert 2026-11-18 nach den Vergleichsbildern: „Farben wie in Photoshop, nicht so
/// blass, aber abschaltbar“). Dynamik in LCh: blasse Farben werden stärker angehoben als schon kräftige, dazu eine leichte
/// gleichmäßige Sättigung; Hauttöne gedämpft, Grautöne unverändert (unterhalb einer kleinen Buntheit wirkt nichts), sehr dunkle
/// und sehr helle Töne sanft ausgenommen. Angewandt wird die gewünschte Verstärkung im linearen Licht um die Leuchtdichte Y des
/// Pixels — L* (Helligkeit und Kontrast aus dem Tonwert-Angleich) bleibt dadurch unverändert — mit weichem Knie an die
/// Farbraumgrenze: ein Kanal nähert sich 16 bzw. 251 (sRGB) nur an und wird nie abgeschnitten (der Abstand fängt die Unschärfe
/// des JPEG ab), schon abgeschnittene Pixel bleiben, wie sie sind. Stärke in Prozent: 0 = aus (Bild bleibt Byte für Byte gleich),
/// 100 = Standard, bis 150.
/// <para>
/// Kalibrierung (Bau Stufe 1, ein Parametersatz für alle Bilder, nie je Bild): an zwei Testserien gegen Herberts
/// Photoshop-Fassungen — Buntheit C* bei 95 % bzw. 101 % von Photoshop, mittlere Farbabweichung kleiner als ohne Verstärkung
/// und als Spike Runde 2, L*-Verteilung unverändert; das Bild selbst bekommt kein abgeschnittenes Pixel dazu (erst das JPEG
/// mit Farb-Unterabtastung schneidet ein wenig in den Tiefen ab) — ADR-0078, Nachtrag 0.5.46.
/// </para>
/// </summary>
internal static class ColorBooster
{
    public const int MaxPercent = 150;

    /// <summary>Wendet die Verstärkung an (in place); liefert einen Hinweis-Code oder null, wenn nichts zu tun war.</summary>
    public static string? Apply(Mat bgr, int percent, ColorBoostParams? p = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        var strength = Math.Clamp(percent, 0, MaxPercent) / 100.0;
        if (strength <= 0 || bgr.Empty())
        {
            return null;
        }

        if (bgr.Type() != MatType.CV_8UC3)
        {
            throw new ArgumentException("Erwartet 8-bit BGR.", nameof(bgr));
        }

        p ??= ColorBoostParams.Default;
        var abGain = AbGain(strength, p);
        var lWeight = LightnessWeight();
        var high = Math.Clamp(p.EdgeMargin, 0.75, 32);
        var low = Math.Clamp(p.EdgeMarginLow, 0.75, 32);
        var bounds = ((float)SrgbToLinear(low / 255), (float)SrgbToLinear((255 - high) / 255));
        const int strip = 1024;
        int rows = bgr.Rows, cols = bgr.Cols;
        for (var y0 = 0; y0 < rows; y0 += strip)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(strip, rows - y0);
            using var view = new Mat(bgr, new Rect(0, y0, cols, n));
            using var lab = new Mat();
            Cv2.CvtColor(view, lab, ColorConversionCodes.BGR2Lab);
            ApplyStrip(view, lab, n, cols, abGain, lWeight, bounds);
        }

        return string.Create(CultureInfo.InvariantCulture, $"color:{Math.Round(strength * 100):0}");
    }

    private static unsafe void ApplyStrip(Mat view, Mat lab, int rows, int cols, float[] abGain, float[] lWeight, (float Low, float High) bounds)
    {
        var vp = (byte*)view.Data;
        var lp = (byte*)lab.Data;
        long vs = view.Step(), ls = lab.Step();
        Parallel.For(0, rows, new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count }, y =>
        {
            var row = vp + y * vs;
            var labRow = lp + y * ls;
            for (var x = 0; x < cols; x++)
            {
                var q = labRow + x * 3;
                var extra = abGain[(q[1] << 8) | q[2]] * lWeight[q[0]];
                if (extra <= 0)
                {
                    continue;
                }

                // im linearen Licht um die Leuchtdichte Y: L* bleibt, nur die Buntheit wächst
                var px = row + x * 3;
                float b = ToLinear[px[0]], g = ToLinear[px[1]], r = ToLinear[px[2]];
                var luminance = 0.0722f * b + 0.7152f * g + 0.2126f * r;
                float db = b - luminance, dg = g - luminance, dr = r - luminance;

                // größte Verstärkung, bei der kein Kanal die Grenze (mit Abstand) erreicht
                var limit = float.MaxValue;
                limit = Math.Min(limit, Headroom(luminance, db, bounds));
                limit = Math.Min(limit, Headroom(luminance, dg, bounds));
                limit = Math.Min(limit, Headroom(luminance, dr, bounds));
                var room = limit - 1f;
                if (room <= 1e-4f)
                {
                    continue; // schon an der Grenze (oder abgeschnitten): unverändert
                }

                // weiches Knie: 1 + room · (1 − e^(−extra/room)) — klein wie gewünscht, groß nie über die Grenze
                var gain = 1f + room * (1f - MathF.Exp(-extra / room));
                px[0] = ToSrgb(luminance + gain * db);
                px[1] = ToSrgb(luminance + gain * dg);
                px[2] = ToSrgb(luminance + gain * dr);
            }
        });
    }

    /// <summary>sRGB-Byte → lineares Licht 0…1.</summary>
    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(v => (float)SrgbToLinear(v / 255.0)).ToArray();

    /// <summary>Lineares Licht (65 536 Stufen) → sRGB-Byte, gerundet.</summary>
    private static readonly byte[] FromLinear = Enumerable.Range(0, 65536).Select(i => (byte)Math.Clamp(Math.Round(255 * LinearToSrgb(i / 65535.0)), 0, 255)).ToArray();


    private static byte ToSrgb(float linear) => FromLinear[(int)(Math.Clamp(linear, 0f, 1f) * 65535f + 0.5f)];

    private static double SrgbToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    private static float Headroom(float luminance, float d, (float Low, float High) bounds)
    {
        if (d > 1e-7f)
        {
            return (bounds.High - luminance) / d;
        }

        if (d < -1e-7f)
        {
            return (luminance - bounds.Low) / -d;
        }

        return float.MaxValue;
    }

    /// <summary>Gewünschte Zusatz-Verstärkung je (a, b) des 8-bit-Lab (a* = a − 128, b* = b − 128): Dynamik, Grau- und Hautschutz.</summary>
    internal static float[] AbGain(double strength, ColorBoostParams p)
    {
        var lut = new float[256 * 256];
        for (var a = 0; a < 256; a++)
        {
            for (var b = 0; b < 256; b++)
            {
                double da = a - 128, db = b - 128;
                var c = Math.Sqrt(da * da + db * db);
                var pale = 1 / (1 + (c / p.PaleChroma) * (c / p.PaleChroma));
                var neutral = SmoothStep(p.NeutralBelow, p.NeutralAbove, c);
                var hue = Math.Atan2(db, da) * 180 / Math.PI;
                var dh = Math.Abs(((hue - p.SkinHue + 540) % 360) - 180);
                var skin = dh < p.SkinHueWidth ? 0.5 * (1 + Math.Cos(Math.PI * dh / p.SkinHueWidth)) : 0; // weich um den Hautton
                skin *= SmoothStep(4, 10, c) * (1 - SmoothStep(45, 60, c)); // nur bei hauttypischer Buntheit
                var keep = 1 - (1 - p.SkinKeep) * skin;
                lut[(a << 8) | b] = (float)(strength * (p.Global + p.Vibrance * pale) * neutral * keep);
            }
        }

        return lut;
    }

    /// <summary>Gewicht je L (8-bit, L* = L / 2,55): Tiefen und Lichter sanft ausnehmen.</summary>
    private static float[] LightnessWeight()
    {
        var w = new float[256];
        for (var l = 0; l < 256; l++)
        {
            var lStar = l / 2.55;
            w[l] = (float)(SmoothStep(3, 12, lStar) * (1 - SmoothStep(93, 99, lStar)));
        }

        return w;
    }

    private static double SmoothStep(double e0, double e1, double x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
