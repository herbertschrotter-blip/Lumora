using OpenCvSharp;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Tests.Synthetic;
using Lumora.Imaging.Worker;
using Lumora.Imaging.Worker.Decoding;
using Lumora.Imaging.Worker.Panorama;

namespace Lumora.Imaging.Tests.Chain;

/// <summary>
/// Weitere Geometrie- und Prüfungs-Tests der Kette (ADR-0078 Punkt 6/18): Kugel 360° mit Gimbal-Startwerten (Horizontstreifen
/// und 2 : 1), senkrechter Schwenk, „Nur verschieben“ für Scans, Belichtungsausgleich, feste Startwerte, Prüfung erkennt eine
/// gekippte Ausrichtung, Quellen ohne Überlappung, Tonwert-Angleich.
/// </summary>
public sealed class PanoramaChainMoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Lumora.Imaging.Tests", Guid.NewGuid().ToString("N"));

    public PanoramaChainMoreTests() => WorkerThreads.Set(Math.Max(2, Environment.ProcessorCount / 2));

    private static readonly Action<WorkerStep, int, int> NoProgress = (_, _, _) => { };

    private static ComposeOutcome Compose(List<SourceImage> sources, AlignmentOutcome a, ComposeSpec spec, bool full = true)
        => PanoComposer.Compose(sources, a, spec, full, ownsSources: false, 0, WorkerLimits.MaxJpegEdge, NoProgress, CancellationToken.None);

    [Fact]
    public void Kugel_aus_Gimbal_Reihen_ergibt_Horizontstreifen_und_2_zu_1_mit_Nordrichtung()
    {
        using var scene = new SyntheticScene(seed: 3);
        const double f = 520; // ≈ 75° waagrecht bei 800 px
        var list = new List<SourceImage>();
        var i = 0;
        foreach (var pitch in new[] { 13.0, -17.0, -47.0 })
        {
            for (var k = 0; k < 8; k++)
            {
                var yaw = k * 45.0;
                using var view = scene.Render(yaw, pitch, f, 800, 600);
                var path = SyntheticScene.Save(view, Path.Combine(_dir, $"d{i:00}.jpg"));
                list.Add(WicSourceDecoder.Read(i++, path, 0, new SourceMeta { GimbalYaw = yaw, GimbalPitch = pitch, Focal35 = f / 1000 * 43.2666 }));
            }
        }

        foreach (var yaw in new[] { 0.0, 180.0 })
        {
            using var view = scene.Render(yaw, -88, f, 800, 600);
            var path = SyntheticScene.Save(view, Path.Combine(_dir, $"d{i:00}.jpg"));
            list.Add(WicSourceDecoder.Read(i++, path, 0, new SourceMeta { GimbalYaw = yaw, GimbalPitch = -88, Focal35 = f / 1000 * 43.2666 }));
        }

        try
        {
            var a = PanoAligner.Align(list, 2.0, 5, NoProgress, CancellationToken.None);
            a.UsedGimbal.Should().BeTrue();
            a.Is360.Should().BeTrue();
            a.Cameras.Should().HaveCount(26);
            PanoComposer.Suggest(list, a).Should().Be(WireProjection.Spherical360);
            // Nordrichtung: Kamera mit Gier 0 blickt nach z (Länge 0)
            var north = a.Cameras.Single(c => c.Source == 0).R.Col(2);
            (Math.Atan2(north.X, north.Z) * 180 / Math.PI).Should().BeApproximately(0, 1.0);

            using var band = Compose(list, a, new ComposeSpec { Projection = WireProjection.Spherical360, SphereForm = WireSphereForm.Band, Scale = 0.5 }, full: false);
            band.Check.Passed.Should().BeTrue(string.Join(",", band.Check.Problems));
            ((double)band.Image.Width / band.Image.Height).Should().BeGreaterThan(2.2, "Horizontstreifen ist breiter als 2 : 1");

            using var full = Compose(list, a, new ComposeSpec { Projection = WireProjection.Spherical360, SphereForm = WireSphereForm.Full, Scale = 0.5 }, full: false);
            full.Image.Width.Should().Be(full.Image.Height * 2, "2 : 1-Rundum-Bild");
            // Zenit gefüllt (keine schwarzen Zeilen oben)
            using var topRow = new Mat(full.Image, new Rect(0, 0, full.Image.Width, 4));
            Cv2.Mean(topRow).Val1.Should().BeGreaterThan(10);
        }
        finally
        {
            list.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void Senkrechter_Schwenk_ergibt_ein_hohes_Panorama()
    {
        using var scene = new SyntheticScene(seed: 4);
        var sources = PanoramaChainTests.Views(scene, _dir, [(0, -40), (0, -20), (0, 0), (0, 20), (0, 40)]);
        try
        {
            var a = PanoAligner.Align(sources, 2.0, 1, NoProgress, CancellationToken.None);
            PanoComposer.Suggest(sources, a).Should().Be(WireProjection.Vertical);
            using var outcome = Compose(sources, a, new ComposeSpec { Projection = WireProjection.Vertical });
            outcome.Check.Passed.Should().BeTrue(string.Join(",", outcome.Check.Problems));
            outcome.Image.Height.Should().BeGreaterThan((int)(outcome.Image.Width * 1.5));
        }
        finally
        {
            sources.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void Scans_ohne_Kamera_werden_nur_verschoben_und_treffen_die_Vorlage()
    {
        using var scene = new SyntheticScene(seed: 5);
        using var flat = new Mat(scene.World, new Rect(1000, 600, 1400, 700)).Clone();
        // 4 × 2 Ausschnitte mit 25–35 % Überlappung, wie ein in Teilen gescanntes großes Blatt
        var offsets = new[] { (0, 0), (330, 0), (660, 0), (1000, 0), (0, 300), (330, 300), (660, 300), (1000, 300) };
        var list = new List<SourceImage>();
        for (var k = 0; k < offsets.Length; k++)
        {
            using var part = new Mat(flat, new Rect(offsets[k].Item1, offsets[k].Item2, 400, 400)).Clone();
            var path = SyntheticScene.Save(part, Path.Combine(_dir, $"s{k}.jpg"), 95);
            list.Add(WicSourceDecoder.Read(k, path, 0));
        }

        try
        {
            var a = PanoAligner.Align(list, 2.0, 2, NoProgress, CancellationToken.None);
            PanoComposer.Suggest(list, a).Should().Be(WireProjection.ShiftOnly);
            a.Cameras.Should().HaveCount(offsets.Length);
            a.SimilarityResidualPx.Should().BeLessThan(1.5);
            using var outcome = Compose(list, a, new ComposeSpec { Projection = WireProjection.ShiftOnly, ToneMatch = 0, ColorBoost = 0 });
            outcome.Image.Width.Should().BeInRange(1390, 1410);
            outcome.Image.Height.Should().BeInRange(690, 710);
            // Mitte der Vorlage im Ergebnis suchen (Ränder sind gefüllt) und vergleichen
            using var truth = new Mat(flat, new Rect(200, 150, 1000, 400));
            using var score = new Mat();
            Cv2.MatchTemplate(outcome.Image, truth, score, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(score, out _, out var best, out _, out var at);
            best.Should().BeGreaterThan(0.95);
            using var found = new Mat(outcome.Image, new Rect(at.X, at.Y, 1000, 400));
            Cv2.PSNR(truth, found).Should().BeGreaterThan(28);
        }
        finally
        {
            list.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void Belichtungsausgleich_glaettet_Spruenge_zwischen_hellen_und_dunklen_Aufnahmen()
    {
        using var scene = new SyntheticScene(seed: 6);
        var poses = new (double, double)[] { (-60, 0), (-30, 0), (0, 0), (30, 0), (60, 0) };
        var gained = PanoramaChainTests.Views(scene, Path.Combine(_dir, "g"), poses, [1.0, 1.35, 0.7, 1.25, 0.8]);
        var plain = PanoramaChainTests.Views(scene, Path.Combine(_dir, "p"), poses);
        try
        {
            // gleiche Ausrichtung (mit photometrischem Modell aus den unterschiedlich belichteten Bildern) für alle drei
            // Fassungen; Vergleich gegen die Fassung ohne Belichtungsunterschiede
            var a = PanoAligner.Align(gained, 2.0, 3, NoProgress, CancellationToken.None);
            a.Photometric!.Applicable.Should().BeTrue("reine Verstärkungen erklärt das Modell");
            using var truth = Compose(plain, a, new ComposeSpec { Projection = WireProjection.Cylindrical, Exposure = false, ToneMatch = 0, ColorBoost = 0 });
            using var on = Compose(gained, a, new ComposeSpec { Projection = WireProjection.Cylindrical, ToneMatch = 0, ColorBoost = 0 });
            using var off = Compose(gained, a, new ComposeSpec { Projection = WireProjection.Cylindrical, Exposure = false, ToneMatch = 0, ColorBoost = 0 });
            var spreadOn = RatioSpread(on.Image, truth.Image);
            var spreadOff = RatioSpread(off.Image, truth.Image);
            spreadOff.Should().BeGreaterThan(0.1, "ohne Ausgleich springt die Helligkeit von Bild zu Bild");
            spreadOn.Should().BeLessThan(spreadOff * 0.5);
        }
        finally
        {
            gained.ForEach(s => s.Dispose());
            plain.ForEach(s => s.Dispose());
        }
    }

    /// <summary>Streuung (Std.-Abw.) des log-Verhältnisses der Helligkeit je 40-px-Spaltenblock gegenüber der Vorlage.</summary>
    private static double RatioSpread(Mat img, Mat truth)
    {
        var w = Math.Min(img.Width, truth.Width);
        var h = Math.Min(img.Height, truth.Height);
        using var g1 = new Mat();
        using var g2 = new Mat();
        using (var v1 = new Mat(img, new Rect(0, 0, w, h)))
        using (var v2 = new Mat(truth, new Rect(0, 0, w, h)))
        {
            Cv2.CvtColor(v1, g1, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(v2, g2, ColorConversionCodes.BGR2GRAY);
        }

        var logs = new List<double>();
        for (var x = 0; x + 40 <= w; x += 40)
        {
            using var b1 = new Mat(g1, new Rect(x, 0, 40, h));
            using var b2 = new Mat(g2, new Rect(x, 0, 40, h));
            logs.Add(Math.Log((Cv2.Mean(b1).Val0 + 1) / (Cv2.Mean(b2).Val0 + 1)));
        }

        var mean = logs.Average();
        return Math.Sqrt(logs.Average(v => (v - mean) * (v - mean)));
    }

    [Fact]
    public void Feste_Startwerte_ergeben_dieselbe_Ausrichtung()
    {
        using var scene = new SyntheticScene(seed: 8);
        var sources = PanoramaChainTests.Views(scene, _dir, [(-30, 5), (0, 0), (30, -5), (60, 0)]);
        try
        {
            var first = PanoAligner.Align(sources, 2.0, 77, NoProgress, CancellationToken.None);
            var second = PanoAligner.Align(sources, 2.0, 77, NoProgress, CancellationToken.None);
            first.Cameras.Select(c => c.R.ToArray()).SelectMany(x => x).Should().Equal(second.Cameras.Select(c => c.R.ToArray()).SelectMany(x => x));
            first.Cameras.Select(c => c.F).Should().Equal(second.Cameras.Select(c => c.F));
        }
        finally
        {
            sources.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void Pruefung_erkennt_eine_gekippte_Ausrichtung()
    {
        using var scene = new SyntheticScene(seed: 9);
        var sources = PanoramaChainTests.Views(scene, _dir, [(-60, 0), (-30, 0), (0, 0), (30, 0), (60, 0)]);
        try
        {
            var good = PanoAligner.Align(sources, 2.0, 4, NoProgress, CancellationToken.None);
            // das letzte Bild um 35° nach oben gekippt (wie das Ende einer falsch geschlossenen Kette)
            var cams = good.Cameras.Select(c => c.Clone()).ToList();
            var last = cams.OrderBy(c => c.Source).Last();
            var tilt = M3.Rodrigues(last.R.Col(0) * (35 * Math.PI / 180));
            last.R = tilt * last.R;
            var broken = new AlignmentOutcome { Cameras = cams, Dropped = [], ResidualMedianPx = good.ResidualMedianPx, ResidualP90Px = good.ResidualP90Px };
            using var outcome = Compose(sources, broken, new ComposeSpec { Projection = WireProjection.Cylindrical }, full: false);
            outcome.Check.Passed.Should().BeFalse($"Höhe {outcome.Check.CropHeightFraction}, Fläche {outcome.Check.CropAreaFraction}");
            outcome.Check.Problems.Should().IntersectWith(["crop-height", "crop-size"]);
            // dieselbe Kette ungekippt besteht
            using var fine = Compose(sources, good, new ComposeSpec { Projection = WireProjection.Cylindrical }, full: false);
            fine.Check.Passed.Should().BeTrue($"Höhe {fine.Check.CropHeightFraction}, Fläche {fine.Check.CropAreaFraction}");
        }
        finally
        {
            sources.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void Quellen_ohne_Ueberlappung_scheitern_mit_verstaendlichem_Code()
    {
        using var a = new SyntheticScene(seed: 10);
        using var b = new SyntheticScene(seed: 11);
        using var va = a.Render(0, 0, 700, 800, 600);
        using var vb = b.Render(180, 30, 700, 800, 600);
        var list = new List<SourceImage>
        {
            WicSourceDecoder.Read(0, SyntheticScene.Save(va, Path.Combine(_dir, "x0.jpg")), 0),
            WicSourceDecoder.Read(1, SyntheticScene.Save(vb, Path.Combine(_dir, "x1.jpg")), 0),
        };
        try
        {
            var act = () => PanoAligner.Align(list, 2.0, 1, NoProgress, CancellationToken.None);
            act.Should().Throw<WorkerException>().Which.Code.Should().Be(WorkerErrorCode.NotEnoughOverlap);
        }
        finally
        {
            list.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void Tonwert_Angleich_holt_verlorenen_Kontrast_und_Buntheit_der_Quellen_zurueck()
    {
        using var scene = new SyntheticScene(seed: 12);
        using var source = scene.Render(0, 0, 700, 800, 600);
        // „flaches“ Panorama: Kontrast und Sättigung halbiert
        using var flat = new Mat();
        source.ConvertTo(flat, MatType.CV_8UC3, 0.55, 128 * 0.45);
        var before = LabStats(flat);
        var reference = ToneMatcher.Reference([source]);

        ToneMatcher.Match(flat, reference, 1.0).Should().NotBeNull();

        var after = LabStats(flat);
        var truth = LabStats(source);
        after.LStd.Should().BeGreaterThan(before.LStd * 1.3);
        Math.Abs(after.LStd - truth.LStd).Should().BeLessThan(Math.Abs(before.LStd - truth.LStd) * 0.5);
        after.Chroma.Should().BeGreaterThan(before.Chroma);
    }

    private static (double LStd, double Chroma) LabStats(Mat bgr)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        var ch = lab.Split();
        Cv2.MeanStdDev(ch[0], out _, out var sd);
        using var a = new Mat();
        using var b = new Mat();
        ch[1].ConvertTo(a, MatType.CV_32F, 1, -128);
        ch[2].ConvertTo(b, MatType.CV_32F, 1, -128);
        using var mag = new Mat();
        Cv2.Magnitude(a, b, mag);
        var chroma = Cv2.Mean(mag).Val0;
        foreach (var c in ch)
        {
            c.Dispose();
        }

        return (sd.Val0, chroma);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
