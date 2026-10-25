using Lumora.Services.Motifs.Models;

namespace Lumora.Services.ModelPackages;

/// <summary>
/// Die fest hinterlegten Einträge von Einstellungen › Modelle &amp; Daten (ADR-0070). Versionen, Größen und Prüfsummen sind
/// geprüfte Werte dieser Lumora-Version; ein neueres Modell kommt mit einer neuen Lumora-Version (neue Definition) und zeigt
/// dann bei geladenen Dateien „Update verfügbar“.
/// <para>
/// Neues Modell (z. B. „Foto verbessern“): Definition mit <see cref="ModelPackageDefinition.FolderName"/> und
/// <see cref="ModelPackageDefinition.Files"/> (Größe + SHA-256 je Datei) anlegen und mit
/// <see cref="ModelPackagesServiceCollectionExtensions.AddFileSetModelPackage"/> registrieren — Liste, Bestätigung, Laden,
/// Prüfen, Entfernen und Fortschritt kommen dann vom Manager.
/// </para>
/// </summary>
public static class ModelPackageCatalog
{
    public const string FacesId = "faces.buffalo_l";
    public const string MotifsId = "motifs.siglip2-base";
    public const string GeoNamesId = "data.geonames";
    public const string MapTilesId = "data.maptiles";
    public const string AssistantId = "external.ollama";

    /// <summary>
    /// InsightFace buffalo_l (ADR-0035): Archiv v0.7 (288 621 354 Bytes, SHA-256 in <c>FaceModelDownloadInfo.BuffaloL</c>),
    /// davon nur Erkennung und Fingerabdruck. Prüfsummen der entpackten Dateien aus dem geprüften Archiv (2026-10-25).
    /// </summary>
    public static ModelPackageDefinition Faces { get; } = new(
        FacesId,
        "Gesichtserkennung",
        "InsightFace buffalo_l",
        "Gesichter finden und Personen wiedererkennen.",
        ModelPackageGroup.Model,
        "0.7",
        new ModelPackageOrigin("InsightFace (deepinsight)", new Uri("https://github.com/deepinsight/insightface")),
        "Modelle nur für die nicht-kommerzielle Nutzung freigegeben (Programmcode MIT)",
        LicenseRestricted: true,
        DownloadSize: 288_621_354,
        FolderName: "buffalo_l",
        Files:
        [
            new("det_10g.onnx", 16_923_827, "5838f7fe053675b1c7a08b633df49e7af5495cee0493c7dcf6697200b85b5b91"),
            new("w600k_r50.onnx", 174_383_860, "4c06341c33c2ca1f86781dab0e829f88ad5b64be9fba56e56bc9ebdefc619e43"),
        ],
        UpdateSource: null,
        Note: "Aus dem Archiv buffalo_l.zip werden nur det_10g.onnx (Erkennung) und w600k_r50.onnx (Fingerabdruck) entpackt.");

    /// <summary>SigLIP 2 base als ONNX (ADR-0052): Dateien und Prüfsummen wie <see cref="MotifModelDownloadInfo.Siglip2Base"/>.</summary>
    public static ModelPackageDefinition Motifs { get; } = new(
        MotifsId,
        "Motiv-Erkennung",
        "SigLIP 2 base",
        "Motive wie „Strand“ oder „Hund“ erkennen und danach suchen.",
        ModelPackageGroup.Model,
        "2026-10-21",
        new ModelPackageOrigin("Google · ONNX-Fassung von Hugging Face (onnx-community)", new Uri("https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX")),
        "Apache-2.0 (auch kommerziell nutzbar)",
        DownloadSize: MotifModelDownloadInfo.Siglip2Base.TotalSize,
        FolderName: IMotifModelProvider.FolderName,
        Files: MotifModelDownloadInfo.Siglip2Base.Files.Select(f => new ModelPackageFile(f.RelativePath, f.Size, f.Sha256, f.Required)).ToList(),
        UpdateSource: new ModelUpdateSource(
            ModelUpdateSourceKind.HuggingFaceFiles,
            new Uri("https://huggingface.co/api/models/onnx-community/siglip2-base-patch16-224-ONNX/tree/main/")),
        Note: "Bildmodell und Sprachmodell (ONNX), Tokenizer und vier kleine Konfigurationsdateien.");

    /// <summary>GeoNames cities5000, eingebettet (ADR-0050) — nichts zu laden.</summary>
    public static ModelPackageDefinition GeoNames { get; } = new(
        GeoNamesId,
        "Ortsnamen",
        "GeoNames cities5000",
        "Ortsnamen offline aus GPS-Koordinaten (Orte ab 5 000 Einwohnern, Länder, Regionen).",
        ModelPackageGroup.Data,
        "Stand 2026-10 (mit Lumora ausgeliefert)",
        new ModelPackageOrigin("GeoNames", new Uri("https://www.geonames.org/")),
        "CC BY 4.0 (Namensnennung)");

    /// <summary>Kartenkacheln (ADR-0051/0055): bei Bedarf geladen, im Zwischenspeicher des Katalogs (Grenze 500 MB).</summary>
    public static ModelPackageDefinition MapTiles { get; } = new(
        MapTilesId,
        "Kartenkacheln",
        "OpenStreetMap · CARTO · MapTiler",
        "Kartenbilder für „Orte“ — werden beim Ansehen geladen und zwischengespeichert (Grenze 500 MB).",
        ModelPackageGroup.Data,
        "laufend (je Kachel)",
        new ModelPackageOrigin("OpenStreetMap-Mitwirkende, CARTO, MapTiler", new Uri("https://www.openstreetmap.org/copyright")),
        "© OpenStreetMap-Mitwirkende (ODbL); CARTO und MapTiler nach deren Bedingungen");

    /// <summary>Sprachmodell des Story-Assistenten über Ollama (ADR-0063) — extern, nur Status.</summary>
    public static ModelPackageDefinition Assistant { get; } = new(
        AssistantId,
        "Story-Assistent",
        "Sprachmodell über Ollama",
        "Sätze wie „Urlaub mit Oma 2019“ verstehen — läuft im lokalen Ollama, das sein Modell selbst verwaltet.",
        ModelPackageGroup.External,
        "wird von Ollama verwaltet",
        new ModelPackageOrigin("Ollama", new Uri("https://ollama.com/")),
        "je Modell (siehe Einstellungen › Assistent)");
}
