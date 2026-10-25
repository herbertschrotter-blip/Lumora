using Lumora.Services.Faces;

namespace Lumora.Services.Enhance;

/// <summary>
/// Lesen und Schreiben der Bilder für „Foto verbessern“ (ADR-0071). Die Implementierung lebt im Wpf-Projekt (WIC), weil
/// Services kein WPF referenzieren darf (CLAUDE.md). Muss von mehreren Threads aufrufbar sein.
/// </summary>
public interface IEnhanceImageCodec
{
    /// <summary>RGB24, EXIF-Ausrichtung angewandt, längste Kante höchstens <paramref name="maxEdge"/> (kleinere werden nicht vergrößert).</summary>
    EnhanceImage Load(string path, int maxEdge);

    /// <summary>Schreibt ein JPEG (ohne Metadaten; die übernimmt <see cref="JpegExifCopier"/>).</summary>
    void SaveJpeg(EnhanceImage image, string path, int quality);
}

/// <summary>Eingabe eines Modell-Schritts.</summary>
/// <param name="Factor">Hochskalieren: 2 oder 4.</param>
/// <param name="Strength">Gesichter: 0…1.</param>
/// <param name="Mask">Entfernen: ein Byte je Pixel (≠ 0 = füllen), gleiche Maße wie das Bild.</param>
/// <param name="Faces">Gesichter: 5 Landmarken je Gesicht in Bildpixeln (Augen, Nase, Mundwinkel).</param>
public sealed record EnhanceStepInput(
    EnhanceImage Image,
    int Factor = 4,
    double Strength = 1,
    byte[]? Mask = null,
    IReadOnlyList<FacePoint[]>? Faces = null);

/// <summary>Ein geladenes Modell eines Werkzeugs (ONNX Runtime bzw. im Test/Rauchtest ein Fake). Ein Lauf zur Zeit.</summary>
public interface IEnhanceEngine : IDisposable
{
    EnhanceTool Tool { get; }

    /// <summary>„DirectML“, „CPU“, „Fake“.</summary>
    string ExecutionProvider { get; }

    EnhanceImage Process(EnhanceStepInput input, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Erzeugt Modelle aus den Dateien im Modellordner (nach dem Laden in Modelle &amp; Daten).</summary>
public interface IEnhanceEngineFactory
{
    /// <summary>Lädt das Modell <paramref name="modelPath"/>; <paramref name="useGpu"/> = DirectML mit Rückfall auf die CPU.</summary>
    IEnhanceEngine Create(EnhanceTool tool, string modelPath, bool useGpu);
}

/// <summary>Findet Gesichter (5 Landmarken) für „Gesichter restaurieren“ und „Automatisch“.</summary>
public interface IEnhanceFaceLocator
{
    /// <summary>true, wenn die Gesichtserkennung geladen ist.</summary>
    bool IsAvailable { get; }

    IReadOnlyList<FacePoint[]> Locate(EnhanceImage image, CancellationToken ct);
}

/// <summary>Zustand eines Werkzeug-Modells (über den Update-Manager, im Rauchtest ein Fake).</summary>
public enum EnhanceModelState
{
    Ready = 0,
    NotInstalled = 1,
    Broken = 2,

    /// <summary>Keine verlässliche ONNX-Quelle mit Prüfsumme — nicht ladbar.</summary>
    NoSource = 3,
}

/// <summary>Wo die Modelle der Werkzeuge liegen und ob sie bereit sind (ADR-0070: Bereitschaft über <c>IModelPackageManager</c>).</summary>
public interface IEnhanceModelAccess
{
    Task<EnhanceModelState> GetStateAsync(EnhanceTool tool, CancellationToken ct);

    /// <summary>Voller Pfad der Modelldatei (auch wenn sie noch fehlt); null ohne Modell.</summary>
    string? GetModelPath(EnhanceTool tool);
}
