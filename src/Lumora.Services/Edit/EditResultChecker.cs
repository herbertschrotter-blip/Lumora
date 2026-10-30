using Lumora.Services.Edit.Onnx;
using Lumora.Services.Faces;
using Microsoft.Extensions.Logging;

namespace Lumora.Services.Edit;

/// <summary>Dateinamen der Prüfmodelle unter <c>.lumora-ki\models\lumora\</c> (ki-install.json, ADR-0076 Punkt 7).</summary>
public static class EditModelFiles
{
    public const string Folder = "lumora";
    public const string NudeNet = "nudenet-640m.onnx";
    public const string Vit = "vit-nsfw-fp16.onnx";

    /// <summary>Altersschätzung aus buffalo_l; fehlt sie, gilt jedes Alter als unbekannt (streng).</summary>
    public const string GenderAge = "genderage.onnx";
}

/// <summary>Personen auf einem Foto finden und KI-Ergebnisse prüfen (ADR-0076 Punkte 8.3 und 9).</summary>
public interface IEditResultChecker
{
    /// <summary>Gesichter und jüngstes geschätztes Alter — für die Vorprüfung (z. B. „Outfit“ gesperrt).</summary>
    EditSubject AnalyzeSubject(string imagePath, CancellationToken ct = default);

    /// <summary>Ergebnis prüfen; Personen aus Original und Ergebnis zählen zusammen. Jeder Fehler heißt verwerfen.</summary>
    EditResultVerdict CheckResult(string originalPath, string resultPath, CancellationToken ct = default);
}

/// <summary>
/// Lädt Bilder über <see cref="IFaceImageLoader"/>, findet Gesichter mit buffalo_l (auf dem Prozessor — die Grafikkarte
/// gehört während eines Auftrags ComfyUI), schätzt das Alter und lässt die Prüfmodelle laufen. Modelle werden beim
/// ersten Bedarf geladen. Fehlen die Prüfmodelle oder schlägt etwas fehl, wird verworfen — nie durchgelassen.
/// Protokolliert nie Pfade (versteckte Sitzung).
/// </summary>
public sealed class EditResultChecker(
    IEditPolicy policy,
    IFaceImageLoader loader,
    IFaceAnalyzerFactory faces,
    Func<IEditContentScanner?> scannerFactory,
    Func<IAgeEstimator> ageFactory,
    ILogger<EditResultChecker> logger) : IEditResultChecker, IDisposable
{
    /// <summary>Längste Kante beim Laden für die Prüfung.</summary>
    public const int MaxEdge = 1280;

    private readonly Lazy<IFaceAnalyzer?> _analyzer = new(() => faces.IsAvailable ? faces.Create(useGpu: false) : null);
    private readonly Lazy<IEditContentScanner?> _scanner = new(scannerFactory);
    private readonly Lazy<IAgeEstimator> _age = new(ageFactory);

    public EditSubject AnalyzeSubject(string imagePath, CancellationToken ct = default)
    {
        try
        {
            return Subject(loader.Load(imagePath, MaxEdge), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unbekannt = vorsichtig: eine Person unbekannten Alters
            logger.LogWarning("KI-Bearbeitung: Personen nicht bestimmbar ({Type})", ex.GetType().Name);
            return new EditSubject(1, null);
        }
    }

    public EditResultVerdict CheckResult(string originalPath, string resultPath, CancellationToken ct = default)
    {
        try
        {
            if (_scanner.Value is not { } scanner)
            {
                return new EditResultVerdict(false, "Verworfen: Die Prüfmodelle fehlen im Container — ohne Prüfung zeigt Lumora kein Ergebnis.");
            }

            var result = loader.Load(resultPath, MaxEdge);
            var subject = EditSubject.Combine(AnalyzeSubject(originalPath, ct), Subject(result, ct));
            ct.ThrowIfCancellationRequested();
            var scan = scanner.Scan(result);
            var verdict = policy.CheckResult(scan, subject);
            logger.LogInformation("KI-Bearbeitung: Ergebnis {Verdict} (Personen {Faces}, jung möglich {Young})",
                verdict.Keep ? "behalten" : "verworfen", subject.FaceCount, subject.MayBeYoung);
            return verdict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("KI-Bearbeitung: Prüfung fehlgeschlagen ({Type}) — verworfen", ex.GetType().Name);
            return new EditResultVerdict(false, "Verworfen: Die Prüfung ließ sich nicht ausführen.");
        }
    }

    private EditSubject Subject(FaceImage image, CancellationToken ct)
    {
        if (_analyzer.Value is not { } analyzer)
        {
            // ohne Gesichtserkennung lässt sich keine Person ausschließen
            return new EditSubject(1, null);
        }

        var analysis = analyzer.Analyze(image, ct);
        if (analysis.Faces.Count == 0)
        {
            return EditSubject.Nobody;
        }

        double? youngest = double.MaxValue;
        foreach (var face in analysis.Faces)
        {
            var age = _age.Value.EstimateAge(image, face.Detection);
            youngest = age is null || youngest is null ? null : Math.Min(youngest.Value, age.Value);
        }

        return new EditSubject(analysis.Faces.Count, youngest);
    }

    public void Dispose()
    {
        if (_analyzer.IsValueCreated)
        {
            _analyzer.Value?.Dispose();
        }

        if (_scanner.IsValueCreated)
        {
            _scanner.Value?.Dispose();
        }

        if (_age.IsValueCreated)
        {
            _age.Value.Dispose();
        }
    }
}
