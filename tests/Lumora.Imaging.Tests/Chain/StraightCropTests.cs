using OpenCvSharp;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Tests.Synthetic;
using Lumora.Imaging.Worker;
using Lumora.Imaging.Worker.Panorama;

namespace Lumora.Imaging.Tests.Chain;

/// <summary>
/// Zuschnitt „Gerade Kanten“ (ADR-0078 Punkt 6, Nachtrag 0.5.47): kein einziges gefülltes Pixel im Ausschnitt. Bis 0.5.46
/// dehnte der Zuschnitt die Deckungsmaske um 3 % und füllte die Ränder per Inpaint — beim welligen Handy-Schwenk entstanden
/// helle Flecken links und rechts an der unteren Kante. Szene: freihändiger Schwenk mit Höhen- und Rollfehlern, damit die
/// Ecken ungedeckt bleiben.
/// </summary>
public sealed class StraightCropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Lumora.Imaging.Tests", Guid.NewGuid().ToString("N"));

    public StraightCropTests() => WorkerThreads.Set(Math.Max(2, Environment.ProcessorCount / 2));

    private static readonly Action<WorkerStep, int, int> NoProgress = (_, _, _) => { };

    private static ComposeOutcome Compose(List<Lumora.Imaging.Worker.Decoding.SourceImage> sources, AlignmentOutcome a, WireCrop crop, WireProjection projection = WireProjection.Cylindrical)
        => PanoComposer.Compose(sources, a, new ComposeSpec { Projection = projection, Crop = crop }, fullQuality: true, ownsSources: false, 0, WorkerLimits.MaxJpegEdge, NoProgress, CancellationToken.None);

    [Fact]
    public void Gerade_Kanten_enthalten_kein_gefuelltes_Pixel_beim_welligen_Schwenk()
    {
        using var scene = new SyntheticScene(seed: 21);
        // freihändig: Neigung schwankt um ± 4°, dazu leichtes Rollen — die Ecken des Bands bleiben ungedeckt
        var poses = new (double Yaw, double Pitch)[] { (-64, 4), (-40, -3), (-16, 4), (8, -4), (32, 3), (56, -4) };
        var sources = new List<Lumora.Imaging.Worker.Decoding.SourceImage>();
        for (var i = 0; i < poses.Length; i++)
        {
            using var view = scene.Render(poses[i].Yaw, poses[i].Pitch, 700, 800, 600, rollDeg: i % 2 == 0 ? 2 : -2);
            var path = SyntheticScene.Save(view, Path.Combine(_dir, $"w{i:00}.jpg"));
            sources.Add(Lumora.Imaging.Worker.Decoding.WicSourceDecoder.Read(i, path, 0));
        }

        try
        {
            var a = PanoAligner.Align(sources, 2.0, 11, NoProgress, CancellationToken.None);
            a.Cameras.Should().HaveCount(poses.Length);

            // Gegenprobe: dieselbe Szene hat ungedeckte Ränder — „Lücken füllen“ füllt sie (das Maß greift)
            using var filled = Compose(sources, a, WireCrop.Fill);
            filled.UncoveredInCrop.Should().BeGreaterThan(1000, "der wellige Schwenk lässt die Ecken offen");

            using var straight = Compose(sources, a, WireCrop.Straight);
            straight.Check.Passed.Should().BeTrue($"{string.Join(",", straight.Check.Problems)} Höhe {straight.Check.CropHeightFraction} Fläche {straight.Check.CropAreaFraction}");
            straight.UncoveredInCrop.Should().Be(0, "„Gerade Kanten“ füllt nichts");
            straight.Check.UncoveredPixels.Should().Be(0);
            straight.Image.Width.Should().BeGreaterThan(1500);

            // Kanten selbst prüfen: die äußersten Zeilen und Spalten sind Bildinhalt, nicht schwarz und nicht flach gefüllt
            foreach (var edge in new[]
                     {
                         new Rect(0, 0, straight.Image.Width, 1),
                         new Rect(0, straight.Image.Height - 1, straight.Image.Width, 1),
                         new Rect(0, 0, 1, straight.Image.Height),
                         new Rect(straight.Image.Width - 1, 0, 1, straight.Image.Height),
                     })
            {
                using var line = new Mat(straight.Image, edge);
                using var gray = new Mat();
                Cv2.CvtColor(line, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.MinMaxLoc(gray, out double min, out _);
                min.Should().BeGreaterThan(0, $"kein schwarzes Pixel an der Kante {edge}");
            }

            // Bühne des Fensters (Vorschau-Sitzung): ungeschnittenes Panorama mit schraffierten Lücken, Zuschnitt als Rahmen darin
            using var preview = PanoComposer.Compose(sources, a, new ComposeSpec { Projection = WireProjection.Cylindrical, Crop = WireCrop.Straight }, fullQuality: false, ownsSources: false, 0,
                WorkerLimits.MaxJpegEdge, NoProgress, CancellationToken.None, keepUntoned: true);
            preview.Frame.Should().NotBeNull();
            var frame = preview.Frame!;
            var crop = preview.FrameCrop;
            (crop.X >= 0 && crop.Y >= 0 && crop.Right <= frame.Width && crop.Bottom <= frame.Height).Should().BeTrue($"Zuschnitt {crop} im Rahmen {frame.Width}×{frame.Height}");
            (crop.Width, crop.Height).Should().Be((preview.Image.Width, preview.Image.Height));
            (frame.Width * frame.Height).Should().BeGreaterThan(crop.Width * crop.Height, "der Rahmen zeigt, was der Zuschnitt abschneidet");
            using (var gray = new Mat())
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                using var black = new Mat();
                Cv2.Threshold(gray, black, 4, 255, ThresholdTypes.BinaryInv);
                Cv2.CountNonZero(black).Should().BeLessThan(frame.Width * frame.Height / 200, "Lücken sind schraffiert statt schwarz");
            }
        }
        finally
        {
            sources.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void Kanten_nachziehen_entfernt_jedes_ungedeckte_Pixel()
    {
        using var mask = new Mat(200, 300, MatType.CV_8UC1, Scalar.All(255));
        // ungedeckte Ecke unten links, Kerbe oben rechts und ein Loch im Inneren
        Cv2.Rectangle(mask, new Rect(0, 170, 40, 30), Scalar.All(0), -1);
        Cv2.Rectangle(mask, new Rect(280, 0, 20, 6), Scalar.All(0), -1);
        mask.Set<byte>(100, 150, 0);
        var r = PanoComposer.TrimToCovered(mask, new Rect(0, 0, 300, 200));
        r.Width.Should().BeGreaterThan(0);
        using var view = new Mat(mask, r);
        Cv2.CountNonZero(view).Should().Be(r.Width * r.Height);
        (r.Width * r.Height).Should().BeGreaterThan(200 * 300 / 3, "das Loch kostet höchstens die kleinere Seite");

        // zwei verstreute Einzel-Löcher: jedes wird für sich abgeschnitten, nicht ihr gemeinsames Rechteck
        using var two = new Mat(400, 600, MatType.CV_8UC1, Scalar.All(255));
        two.Set<byte>(20, 30, 0);
        two.Set<byte>(380, 570, 0);
        var kept = PanoComposer.TrimToCovered(two, new Rect(0, 0, 600, 400));
        using var keptView = new Mat(two, kept);
        Cv2.CountNonZero(keptView).Should().Be(kept.Width * kept.Height);
        (kept.Width * kept.Height).Should().BeGreaterThan(600 * 400 * 8 / 10, "zwei Pixel kosten nur schmale Streifen");
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
