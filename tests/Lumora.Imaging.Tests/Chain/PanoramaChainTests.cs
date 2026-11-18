using OpenCvSharp;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Tests.Synthetic;
using Lumora.Imaging.Worker;
using Lumora.Imaging.Worker.Decoding;
using Lumora.Imaging.Worker.Panorama;

namespace Lumora.Imaging.Tests.Chain;

/// <summary>
/// Rechenkette im Prozess (ADR-0078 Punkt 18): synthetische Szene → überlappende Ansichten mit bekannter Gier → Ausrichtung
/// und Zusammensetzen. Prüft die Geometrie (Winkel, Größe, Horizont), den Belichtungsausgleich und die Prüfung vor dem Speichern.
/// </summary>
public sealed class PanoramaChainTests : IDisposable
{
    private const double F = 700;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Lumora.Imaging.Tests", Guid.NewGuid().ToString("N"));

    public PanoramaChainTests() => WorkerThreads.Set(Math.Max(2, Environment.ProcessorCount / 2));

    internal static List<SourceImage> Views(SyntheticScene scene, string dir, IReadOnlyList<(double Yaw, double Pitch)> poses, double[]? gains = null, int w = 800, int h = 600)
    {
        var list = new List<SourceImage>();
        for (var i = 0; i < poses.Count; i++)
        {
            using var view = scene.Render(poses[i].Yaw, poses[i].Pitch, F, w, h, gains?[i] ?? 1.0);
            var path = SyntheticScene.Save(view, Path.Combine(dir, $"v{i:00}.jpg"));
            list.Add(WicSourceDecoder.Read(i, path, 0));
        }

        return list;
    }

    private static double YawDeg(PanoCamera c)
    {
        var z = c.R.Col(2);
        return Math.Atan2(z.X, z.Z) * 180 / Math.PI;
    }

    private static double PitchDeg(PanoCamera c)
    {
        var z = c.R.Col(2);
        return -Math.Asin(Math.Clamp(z.Y, -1, 1)) * 180 / Math.PI;
    }

    [Fact]
    public void Schwenk_aus_fuenf_Ansichten_trifft_die_Winkel_und_ergibt_ein_geprueftes_Panorama()
    {
        using var scene = new SyntheticScene();
        var sources = Views(scene, _dir, [(-60, 0), (-30, 0), (0, 0), (30, 0), (60, 0)]);
        try
        {
            var a = PanoAligner.Align(sources, 2.0, 11, (_, _, _) => { }, CancellationToken.None);
            a.Cameras.Should().HaveCount(5);
            a.Dropped.Should().BeEmpty();
            a.Problems.Should().BeEmpty();
            var cams = a.Cameras.OrderBy(c => c.Source).ToList();
            for (var i = 1; i < cams.Count; i++)
            {
                var step = YawDeg(cams[i]) - YawDeg(cams[i - 1]);
                step.Should().BeApproximately(30, 0.6, $"Gier-Schritt {i}");
            }

            // Brennweite aus den Homographien (kein EXIF), Horizont waagrecht (Wellenkorrektur)
            cams[2].F.Should().BeApproximately(F, F * 0.03);
            cams.Select(PitchDeg).Should().OnlyContain(p => Math.Abs(p) < 1.0);
            a.ResidualMedianPx.Should().BeLessThan(1.5);

            using var outcome = PanoComposer.Compose(sources, a, new ComposeSpec { Projection = WireProjection.Cylindrical }, fullQuality: true, ownsSources: false, 0, WorkerLimits.MaxJpegEdge, (_, _, _) => { }, CancellationToken.None);
            outcome.Check.Passed.Should().BeTrue(string.Join(",", outcome.Check.Problems));
            // 120° Schwenk + ein Bildwinkel (≈ 59°) bei f = 700 px → ≈ 2 190 px breit; Höhe nahe der Bildhöhe
            outcome.Image.Width.Should().BeInRange(1900, 2300);
            outcome.Image.Height.Should().BeInRange(480, 620);
            outcome.Check.SeamNcc.Should().BeGreaterThan(0.5);
            outcome.Check.CropHeightFraction.Should().BeGreaterThan(0.8);
        }
        finally
        {
            sources.ForEach(s => s.Dispose());
        }
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
