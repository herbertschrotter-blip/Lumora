using OpenCvSharp;
using Lumora.Imaging.Worker.Panorama;

namespace Lumora.Imaging.Tests.Chain;

/// <summary>Farbverstärkung (Herbert 2026-11-18: „Farben wie Photoshop, abschaltbar“): aus = unverändert, Grau bleibt grau, kein Abschneiden, Dynamik.</summary>
public sealed class ColorBoosterTests
{
    /// <summary>Ein Bild aus lauter gleichen Pixeln (B, G, R).</summary>
    private static Mat Solid(byte b, byte g, byte r, int size = 8) => new(size, size, MatType.CV_8UC3, new Scalar(b, g, r));

    private static (double L, double C) LabOf(Mat bgr)
    {
        using var f = new Mat();
        bgr.ConvertTo(f, MatType.CV_32FC3, 1 / 255.0);
        using var lab = new Mat();
        Cv2.CvtColor(f, lab, ColorConversionCodes.BGR2Lab);
        var v = lab.Get<Vec3f>(0, 0);
        return (v.Item0, Math.Sqrt(v.Item1 * v.Item1 + v.Item2 * v.Item2));
    }

    /// <summary>Zufällige Farben über den ganzen RGB-Würfel, auch an seinen Rändern.</summary>
    private static Mat RandomColors(int seed)
    {
        var rng = new Random(seed);
        byte Channel()
        {
            var roll = rng.NextDouble();
            return roll < 0.08 ? (byte)0 : roll < 0.16 ? (byte)255 : (byte)rng.Next(256);
        }

        var pixels = new Vec3b[64 * 64];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Vec3b(Channel(), Channel(), Channel());
        }

        var img = new Mat(64, 64, MatType.CV_8UC3);
        img.SetArray(pixels);
        return img;
    }

    [Fact]
    public void Staerke_0_laesst_das_Bild_Byte_fuer_Byte_gleich()
    {
        using var img = RandomColors(1);
        using var before = img.Clone();

        ColorBooster.Apply(img, 0).Should().BeNull("aus = nichts zu tun");

        Cv2.Norm(img, before, NormTypes.L1).Should().Be(0);
    }

    [Fact]
    public void Grau_bleibt_neutral_auch_fast_graue_Toene_bekommen_keinen_Farbstich()
    {
        using var ramp = new Mat(1, 256, MatType.CV_8UC3);
        for (var v = 0; v < 256; v++)
        {
            ramp.Set(0, v, new Vec3b((byte)v, (byte)v, (byte)v));
        }

        using var rampBefore = ramp.Clone();
        ColorBooster.Apply(ramp, 150);
        Cv2.Norm(ramp, rampBefore, NormTypes.L1).Should().Be(0, "reines Grau hat keine Farbe zum Verstärken");

        using var almostGray = Solid(128, 128, 129); // C* ≈ 0,5
        using var almostBefore = almostGray.Clone();
        ColorBooster.Apply(almostGray, 150);
        Cv2.Norm(almostGray, almostBefore, NormTypes.L1).Should().Be(0, "unterhalb der Schutzgrenze bleibt ein Ton, wie er ist");
    }

    [Fact]
    public void Kein_hartes_Abschneiden_kein_Kanal_landet_neu_auf_0_oder_255()
    {
        using var img = RandomColors(7);
        using var before = img.Clone();

        ColorBooster.Apply(img, 150);

        img.GetArray(out Vec3b[] after);
        before.GetArray(out Vec3b[] orig);
        var newlyClipped = 0;
        var clippedChanged = 0;
        for (var i = 0; i < after.Length; i++)
        {
            if (!IsClipped(orig[i]) && IsClipped(after[i]))
            {
                newlyClipped++;
            }
            else if (IsClipped(orig[i]) && !after[i].Equals(orig[i]))
            {
                clippedChanged++;
            }
        }

        newlyClipped.Should().Be(0, "weiches Knie an der Farbraumgrenze statt Abschneiden");
        clippedChanged.Should().Be(0, "schon abgeschnittene Pixel bleiben, wie sie sind");
        Cv2.Norm(img, before, NormTypes.L1).Should().BeGreaterThan(0, "die übrigen Farben wurden kräftiger");
    }

    private static bool IsClipped(Vec3b p) => p.Item0 is 0 or 255 || p.Item1 is 0 or 255 || p.Item2 is 0 or 255;

    [Fact]
    public void Dynamik_hebt_blasse_Farben_staerker_als_satte()
    {
        // gleicher Farbton (Grünblau), einmal blass, einmal kräftig — beide mit Luft zur Farbraumgrenze
        using var pale = Solid(130, 140, 110);
        using var vivid = Solid(150, 170, 40);
        var (paleL, paleBefore) = LabOf(pale);
        var (vividL, vividBefore) = LabOf(vivid);

        ColorBooster.Apply(pale, 100);
        ColorBooster.Apply(vivid, 100);

        LabOf(pale).L.Should().BeApproximately(paleL, 0.8, "im linearen Licht um die Leuchtdichte: L* bleibt");
        LabOf(vivid).L.Should().BeApproximately(vividL, 0.8);
        var paleGain = LabOf(pale).C / paleBefore;
        var vividGain = LabOf(vivid).C / vividBefore;
        paleGain.Should().BeGreaterThan(1.2, "Standardstärke hebt blasse Farben deutlich");
        paleGain.Should().BeGreaterThan(vividGain + 0.1, "blasse Farben stärker als schon kräftige");
        vividGain.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void Hauttoene_werden_gedaempft_angehoben()
    {
        // ähnliche Buntheit und Helligkeit: Hautton (Orange-Rosa) gegen Himmelblau
        using var skin = Solid(150, 170, 215);
        using var sky = Solid(215, 175, 140);
        var (_, skinBefore) = LabOf(skin);
        var (_, skyBefore) = LabOf(sky);

        ColorBooster.Apply(skin, 100);
        ColorBooster.Apply(sky, 100);

        (LabOf(skin).C / skinBefore).Should().BeLessThan(LabOf(sky).C / skyBefore - 0.05, "Haut wirkt sonst schnell unnatürlich");
        (LabOf(skin).C / skinBefore).Should().BeGreaterThan(1.0, "gedämpft, nicht ausgenommen");
    }
}
