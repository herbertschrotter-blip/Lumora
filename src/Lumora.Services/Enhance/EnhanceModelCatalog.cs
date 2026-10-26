using Lumora.Services.ModelPackages;

namespace Lumora.Services.Enhance;

/// <summary>
/// Die Modelle von „Foto verbessern“ als Einträge in Einstellungen › Modelle &amp; Daten (ADR-0070, ADR-0071). Größen und
/// SHA-256 stammen aus den LFS-Metadaten der Hugging-Face-Dateilisten (nur Metadaten abgefragt, 2026-10-25); geladen wird je
/// Datei über den Manager nach Bestätigung, gegen die Prüfsumme geprüft und erst dann abgelegt. Die Adressen zeigen auf einen
/// festen Commit der Quelle (nie „main“), damit eine geänderte Datei beim Hersteller nicht ungeprüft ankommt.
/// <para>
/// CodeFormer ist bewusst nicht dabei (Lizenz S-Lab 1.0, nur nicht-kommerziell). Einfärben nutzt DeOldify „artistic“ (MIT,
/// ≈ 255 MB) statt DDColor (Apache-2.0, aber ≈ 980 MB).
/// </para>
/// </summary>
public static class EnhanceModelCatalog
{
    public const string UpscaleId = "enhance.upscale";
    public const string UpscaleNaturalId = "enhance.upscale-natural";
    public const string FacesId = "enhance.faces";
    public const string ColorizeId = "enhance.colorize";
    public const string InpaintId = "enhance.inpaint";

    /// <summary>ONNX-Fassungen von facefusion (Hugging Face), fester Commit vom 2024-11-07.</summary>
    public static readonly Uri FaceFusionBase = new("https://huggingface.co/facefusion/models-3.0.0/resolve/728b9659bd9691bf32cbf7f61af478d94b7ba81e/");

    /// <summary>
    /// Real-ESRGAN general x4v3 als ONNX (Hugging Face „CoderViking“, fester Commit vom 2026-07-16; Umwandlungsskript aus den
    /// offiziellen Gewichten liegt dort bei). Eine Community-Fassung — Herbert hat sie am 2026-10-26 als Quelle gewählt; die
    /// Prüfsumme hält die Datei fest.
    /// </summary>
    public static readonly Uri GeneralX4v3Base = new("https://huggingface.co/CoderViking/realesr-general-x4v3-onnx/resolve/c6a971706797c7502945a2b4c4274fce4900d4ab/");

    /// <summary>LaMa als ONNX (Carve, Hugging Face), fester Commit vom 2024-05-15.</summary>
    public static readonly Uri LamaBase = new("https://huggingface.co/Carve/LaMa-ONNX/resolve/c3c0c9e468934d62e79c329e35d82dd09ff8c444/");

    public const string UpscaleFile = "real_esrgan_x4.onnx";
    public const string UpscaleNaturalFile = "realesr-general-x4v3.onnx";
    public const string FacesFile = "gfpgan_1.4.onnx";
    public const string ColorizeFile = "deoldify_artistic.onnx";
    public const string InpaintFile = "lama_fp32.onnx";

    public static ModelPackageDefinition Upscale { get; } = new(
        UpscaleId,
        "Hochskalieren",
        "Real-ESRGAN x4plus",
        "Fotos 2× oder 4× vergrößern und dabei schärfen („Foto verbessern“).",
        ModelPackageGroup.Model,
        "x4plus (ONNX 2024-11)",
        new ModelPackageOrigin("Xintao Wang (Real-ESRGAN) · ONNX-Fassung von facefusion", new Uri("https://github.com/xinntao/Real-ESRGAN")),
        "BSD-3-Clause (auch kommerziell nutzbar)",
        DownloadSize: 69_539_503,
        FolderName: "enhance-upscale",
        Files: [new(UpscaleFile, 69_539_503, "afa7fa42eed0315a3d7b2a54e79e4e44a672d3c0889c2828c4f84c9ab91edb0b")],
        Note: "Eine ONNX-Datei (Real-ESRGAN x4plus); 2× rechnet 4× und verkleinert danach.");

    public static ModelPackageDefinition UpscaleNatural { get; } = new(
        UpscaleNaturalId,
        "Hochskalieren (natürlich)",
        "Real-ESRGAN general x4v3",
        "Fotos 2× oder 4× vergrößern und dabei Haut und feine Struktur erhalten („Foto verbessern“, Modell „Natürlich“).",
        ModelPackageGroup.Model,
        "general x4v3 (ONNX 2026-07)",
        new ModelPackageOrigin("Xintao Wang (Real-ESRGAN) · ONNX-Fassung der Community (CoderViking)", new Uri("https://github.com/xinntao/Real-ESRGAN")),
        "BSD-3-Clause (auch kommerziell nutzbar)",
        DownloadSize: 4_866_417,
        FolderName: "enhance-upscale-natural",
        Files: [new(UpscaleNaturalFile, 4_866_417, "1940a93ee08283a0a7286183186357b1688fe9fa8ede74604b424586aaddf112")],
        Note: "Eine kleine ONNX-Datei (SRVGGNetCompact, beliebige Bildgröße). Community-Umwandlung der offiziellen Gewichte; die Prüfsumme hält die Datei fest.");

    public static ModelPackageDefinition Faces { get; } = new(
        FacesId,
        "Gesichter restaurieren",
        "GFPGAN v1.4",
        "Unscharfe oder beschädigte Gesichter wiederherstellen („Foto verbessern“; findet die Gesichter mit der Gesichtserkennung).",
        ModelPackageGroup.Model,
        "1.4 (ONNX 2024-11)",
        new ModelPackageOrigin("Tencent ARC (GFPGAN) · ONNX-Fassung von facefusion", new Uri("https://github.com/TencentARC/GFPGAN")),
        "Apache-2.0 (auch kommerziell nutzbar)",
        DownloadSize: 340_299_087,
        FolderName: "enhance-faces",
        Files: [new(FacesFile, 340_299_087, "accc4757b26bdb89b32b4d3500d4f79c9dff97c1dd7c7104bf9dcb95e3311385")],
        Note: "Eine ONNX-Datei (GFPGAN v1.4, 512 × 512). Zum Finden der Gesichter braucht es zusätzlich die Gesichtserkennung.");

    public static ModelPackageDefinition Colorize { get; } = new(
        ColorizeId,
        "Einfärben",
        "DeOldify (artistic)",
        "Schwarz-Weiß-Fotos einfärben („Foto verbessern“).",
        ModelPackageGroup.Model,
        "artistic (ONNX 2024-11)",
        new ModelPackageOrigin("Jason Antic (DeOldify) · ONNX-Fassung von facefusion", new Uri("https://github.com/jantic/DeOldify")),
        "MIT (auch kommerziell nutzbar)",
        DownloadSize: 255_044_725,
        FolderName: "enhance-colorize",
        Files: [new(ColorizeFile, 255_044_725, "9ac296cf05fecbdb604f50211f632b722402bc1f9a96ee5a8987b01c0c3c688f")],
        Note: "Eine ONNX-Datei (DeOldify artistic). Die Helligkeit bleibt aus dem Original, das Modell liefert nur die Farbe.");

    public static ModelPackageDefinition Inpaint { get; } = new(
        InpaintId,
        "Entfernen",
        "LaMa (big-lama)",
        "Störendes und Kratzer entfernen und die Stelle passend füllen („Foto verbessern“).",
        ModelPackageGroup.Model,
        "big-lama fp32 (ONNX 2024-05)",
        new ModelPackageOrigin("Samsung AI (LaMa, advimman) · ONNX-Fassung von Carve", new Uri("https://github.com/advimman/lama")),
        "Apache-2.0 (auch kommerziell nutzbar)",
        DownloadSize: 208_044_816,
        FolderName: "enhance-inpaint",
        Files: [new(InpaintFile, 208_044_816, "1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6")],
        Note: "Eine ONNX-Datei (LaMa big-lama, 512 × 512).");

    /// <summary>Alle Einträge mit Basisadresse (Reihenfolge der Leiste).</summary>
    public static IReadOnlyList<(ModelPackageDefinition Definition, Uri BaseUri)> All { get; } =
    [
        (Upscale, FaceFusionBase),
        (UpscaleNatural, GeneralX4v3Base),
        (Faces, FaceFusionBase),
        (Colorize, FaceFusionBase),
        (Inpaint, LamaBase),
    ];

    /// <summary>Modell-Id des Werkzeugs (null ohne Modell).</summary>
    public static string? ModelIdOf(EnhanceTool tool) => tool switch
    {
        EnhanceTool.Upscale => UpscaleId,
        EnhanceTool.UpscaleNatural => UpscaleNaturalId,
        EnhanceTool.Faces => FacesId,
        EnhanceTool.Colorize => ColorizeId,
        EnhanceTool.Erase => InpaintId,
        _ => null,
    };

    /// <summary>Datei des Modells im Modellordner.</summary>
    public static string? FileOf(EnhanceTool tool) => tool switch
    {
        EnhanceTool.Upscale => UpscaleFile,
        EnhanceTool.UpscaleNatural => UpscaleNaturalFile,
        EnhanceTool.Faces => FacesFile,
        EnhanceTool.Colorize => ColorizeFile,
        EnhanceTool.Erase => InpaintFile,
        _ => null,
    };

    public static ModelPackageDefinition? DefinitionOf(EnhanceTool tool) => tool switch
    {
        EnhanceTool.Upscale => Upscale,
        EnhanceTool.UpscaleNatural => UpscaleNatural,
        EnhanceTool.Faces => Faces,
        EnhanceTool.Colorize => Colorize,
        EnhanceTool.Erase => Inpaint,
        _ => null,
    };
}
