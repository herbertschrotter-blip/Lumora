using Lumora.Services.Edit;
using Lumora.Services.Edit.Onnx;
using Lumora.Services.Faces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.Tests.Edit;

/// <summary>Ergebnisprüfung (ADR-0076 Punkt 8.3) mit Attrappen: im Zweifel verwerfen, Personen aus Original und Ergebnis.</summary>
public sealed class EditResultCheckerTests
{
    private static EditResultChecker Checker(int faces, EditContentScan? scan, double? age = 30, bool analyzerAvailable = true, bool loaderThrows = false)
        => new(new EditPolicy(), new FakeLoader(loaderThrows), new FakeFaces(faces, analyzerAvailable),
            () => scan is null ? null : new FakeScanner(scan), () => new FixedAge(age), NullLogger<EditResultChecker>.Instance);

    private static readonly EditContentScan Nsfw = new(new Dictionary<string, float>(), 0.9f);

    [Fact]
    public void Freizuegiges_Ergebnis_mit_Person_wird_verworfen_neutrales_behalten()
    {
        Checker(1, Nsfw).CheckResult("a.jpg", "b.jpg").Keep.Should().BeFalse();
        Checker(1, EditContentScan.Clean).CheckResult("a.jpg", "b.jpg").Keep.Should().BeTrue();
        Checker(0, Nsfw).CheckResult("a.jpg", "b.jpg").Keep.Should().BeTrue("ohne Gesicht zählt der ViT allein nicht");
    }

    [Fact]
    public void Im_Zweifel_verwerfen()
    {
        Checker(1, null).CheckResult("a.jpg", "b.jpg").Reason.Should().Contain("Prüfmodelle fehlen");
        Checker(0, EditContentScan.Clean, loaderThrows: true).CheckResult("a.jpg", "b.jpg").Keep.Should().BeFalse();
        Checker(0, EditContentScan.Clean, analyzerAvailable: false).AnalyzeSubject("a.jpg").Should().Be(new EditSubject(1, null), "ohne Gesichtserkennung keine Entwarnung");
        Checker(1, EditContentScan.Clean, age: null).AnalyzeSubject("a.jpg").MayBeYoung.Should().BeTrue();
        Checker(2, EditContentScan.Clean, age: 40).AnalyzeSubject("a.jpg").Should().Be(new EditSubject(2, 40));
    }

    private sealed class FakeLoader(bool throws) : IFaceImageLoader
    {
        public FaceImage Load(string path, int maxEdge)
            => throws ? throw new FaceImageDecodeException("kaputt") : new FaceImage(new byte[64 * 64 * 3], 64, 64);
    }

    private sealed class FakeFaces(int faces, bool available) : IFaceAnalyzerFactory
    {
        public string ModelVersion => "fake";

        public bool IsAvailable => available;

        public IFaceAnalyzer Create(bool useGpu) => new Analyzer(faces);

        private sealed class Analyzer(int faces) : IFaceAnalyzer
        {
            public string ModelVersion => "fake";

            public string ExecutionProvider => "Fake";

            public FaceAnalysis Analyze(FaceImage image, CancellationToken ct = default)
                => new(image.Width, image.Height, Enumerable.Range(0, faces)
                    .Select(_ => new AnalyzedFace(new FaceDetection(10, 10, 40, 40, 0.9f, []), new float[512], default)).ToList());

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeScanner(EditContentScan scan) : IEditContentScanner
    {
        public EditContentScan Scan(FaceImage image) => scan;

        public void Dispose()
        {
        }
    }

    private sealed class FixedAge(double? age) : IAgeEstimator
    {
        public double? EstimateAge(FaceImage image, FaceDetection face) => age;

        public void Dispose()
        {
        }
    }
}
