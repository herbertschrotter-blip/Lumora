using Lumora.Services.Faces;

namespace Lumora.Services.Enhance;

/// <summary>
/// Findet Gesichter für „Gesichter restaurieren“ mit der vorhandenen Gesichtserkennung (buffalo_l, ADR-0035) — derselbe
/// Detektor, ein eigener Analysator (wird bei <see cref="Release"/> bzw. Dispose freigegeben, damit kein Grafikspeicher liegen
/// bleibt). Ohne geladene Gesichtserkennung <see cref="IsAvailable"/> = false.
/// </summary>
public sealed class FaceAnalyzerEnhanceLocator(IFaceAnalyzerFactory factory) : IEnhanceFaceLocator, IDisposable
{
    /// <summary>Gesichter unter diesem Erkennungswert zählen nicht (wie die Erkennung selbst).</summary>
    public const float MinScore = 0.5f;

    private readonly object _gate = new();
    private IFaceAnalyzer? _analyzer;

    public bool UseGpu { get; set; } = true;

    public bool IsAvailable => factory.IsAvailable;

    public IReadOnlyList<FacePoint[]> Locate(EnhanceImage image, CancellationToken ct)
    {
        IFaceAnalyzer analyzer;
        lock (_gate)
        {
            analyzer = _analyzer ??= factory.Create(UseGpu);
        }

        var analysis = analyzer.Analyze(image.AsFaceImage(), ct);
        return analysis.Faces.Where(f => f.Detection.Score >= MinScore).Select(f => f.Detection.Landmarks).ToList();
    }

    /// <summary>Analysator freigeben (nach einem Lauf bzw. beim Schließen der Leiste).</summary>
    public void Release()
    {
        IFaceAnalyzer? old;
        lock (_gate)
        {
            old = _analyzer;
            _analyzer = null;
        }

        old?.Dispose();
    }

    public void Dispose() => Release();
}
