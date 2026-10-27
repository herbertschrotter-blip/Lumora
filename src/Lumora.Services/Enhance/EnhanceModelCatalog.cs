using Lumora.Services.ModelPackages;

namespace Lumora.Services.Enhance;

/// <summary>
/// Die Modelle von „Foto verbessern“ als Einträge in Einstellungen › Modelle &amp; Daten (ADR-0070, ADR-0071). Größen und
/// SHA-256 stammen aus den LFS-Metadaten der Hugging-Face-Dateilisten (nur Metadaten abgefragt, 2026-10-25); geladen wird je
/// Datei über den Manager nach Bestätigung, gegen die Prüfsumme geprüft und erst dann abgelegt. Die Adressen zeigen auf einen
/// festen Commit der Quelle (nie „main“), damit eine geänderte Datei beim Hersteller nicht ungeprüft ankommt.
/// <para>
/// CodeFormer ist bewusst nicht dabei (Lizenz S-Lab 1.0, nur nicht-kommerziell). Ausnahme seit 0.5.11: Remacri (Modell „Haut“,
/// CC-BY-NC-SA) — von Herbert für die private Nutzung ausdrücklich gewählt, in der Liste als eingeschränkt markiert. Einfärben nutzt DeOldify „artistic“ (MIT,
/// ≈ 255 MB) statt DDColor (Apache-2.0, aber ≈ 980 MB).
/// </para>
/// </summary>
public static class EnhanceModelCatalog
{
    public const string UpscaleId = "enhance.upscale";
    public const string UpscaleNaturalId = "enhance.upscale-natural";
    public const string UpscaleClearId = "enhance.upscale-clear";
    public const string UpscaleClearSoftId = "enhance.upscale-clear-soft";
    public const string UpscalePhotoId = "enhance.upscale-photo";
    public const string UpscaleSiaxId = "enhance.upscale-siax";
    public const string UpscaleSkinId = "enhance.upscale-skin";
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

    /// <summary>
    /// 4x-ClearRealityV1 als offizielles fp32-ONNX des Urhebers (Kim2091, Hugging Face), fester Commit; seit 2026-02-11
    /// Apache-2.0 (vorher CC-BY-NC-SA). Zwei Fassungen in eigenen Unterordnern.
    /// </summary>
    public static readonly Uri ClearRealityBase = new("https://huggingface.co/Kim2091/ClearRealityV1/resolve/6bfd32e08342ad5d8ae5ac2fd6629a72d525c76e/ONNX/fp32/");

    public static readonly Uri ClearRealitySoftBase = new("https://huggingface.co/Kim2091/ClearRealityV1/resolve/6bfd32e08342ad5d8ae5ac2fd6629a72d525c76e/ONNX/Soft_fp32/");

    /// <summary>4xNomosWebPhoto_RealPLKSR als offizielles fp32-ONNX im GitHub-Release des Urhebers (Philip Hofmann).</summary>
    public static readonly Uri NomosWebPhotoBase = new("https://github.com/Phhofm/models/releases/download/4xNomosWebPhoto_RealPLKSR/");

    /// <summary>
    /// Community-ONNX-Fassungen (yuvraj108c, Hugging Face, fester Commit vom 2026-06-07; fp32, dynamische Größe laut
    /// Umwandlungsskript) von Siax und Remacri — Herbert hat beide am 2026-10-27 gewählt, die Prüfsumme hält die Datei fest.
    /// </summary>
    public static readonly Uri UpscalerOnnxBase = new("https://huggingface.co/yuvraj108c/ComfyUI-Upscaler-Onnx/resolve/4b81f911b425479a7209b0a89c9493abfd661892/");

    /// <summary>LaMa als ONNX (Carve, Hugging Face), fester Commit vom 2024-05-15.</summary>
    public static readonly Uri LamaBase = new("https://huggingface.co/Carve/LaMa-ONNX/resolve/c3c0c9e468934d62e79c329e35d82dd09ff8c444/");

    public const string UpscaleFile = "real_esrgan_x4.onnx";
    public const string UpscaleNaturalFile = "realesr-general-x4v3.onnx";
    public const string UpscaleClearFile = "4x-ClearRealityV1-fp32-opset17.onnx";
    public const string UpscaleClearSoftFile = "4x-ClearRealityV1_Soft-fp32-opset17.onnx";
    public const string UpscalePhotoFile = "4xNomosWebPhoto_RealPLKSR_fp32_opset17.onnx";
    public const string UpscaleSiaxFile = "4x_NMKD-Siax_200k.onnx";
    public const string UpscaleSkinFile = "4x_foolhardy_Remacri.onnx";
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

    public static ModelPackageDefinition UpscaleClear { get; } = new(
        UpscaleClearId,
        "Hochskalieren (Klar)",
        "4x-ClearRealityV1",
        "Fotos 2× oder 4× vergrößern, weich und natürlich — gut für Gesichter und Haare („Foto verbessern“, Modell „Klar“).",
        ModelPackageGroup.Model,
        "V1 fp32 (ONNX)",
        new ModelPackageOrigin("Kim2091 (ClearRealityV1) · offizielle ONNX-Fassung", new Uri("https://huggingface.co/Kim2091/ClearRealityV1")),
        "Apache-2.0 (auch kommerziell nutzbar; seit 2026-02-11, davor CC-BY-NC-SA)",
        DownloadSize: 1_718_947,
        FolderName: "enhance-upscale-clear",
        Files: [new(UpscaleClearFile, 1_718_947, "bbce9d5a653281cfae07788d620bd4ec45712709bdf6349a06a893159efd97ce")],
        Note: "Eine sehr kleine ONNX-Datei (SPAN). Für eher klare Fotos ohne viel Korn; bei starker Tiefenunschärfe sind Artefakte möglich.");

    public static ModelPackageDefinition UpscaleClearSoft { get; } = new(
        UpscaleClearSoftId,
        "Hochskalieren (Klar weich)",
        "4x-ClearRealityV1 Soft",
        "Fotos 2× oder 4× vergrößern, noch weicher mit möglichst wenig Artefakten („Foto verbessern“, Modell „Klar weich“).",
        ModelPackageGroup.Model,
        "V1 Soft fp32 (ONNX)",
        new ModelPackageOrigin("Kim2091 (ClearRealityV1) · offizielle ONNX-Fassung", new Uri("https://huggingface.co/Kim2091/ClearRealityV1")),
        "Apache-2.0 (auch kommerziell nutzbar; seit 2026-02-11, davor CC-BY-NC-SA)",
        DownloadSize: 1_718_947,
        FolderName: "enhance-upscale-clear-soft",
        Files: [new(UpscaleClearSoftFile, 1_718_947, "2cc924f6ff7ad908e03ea4efcea45aeaa6380a7859ccfccbf253192566dabb36")],
        Note: "Eine sehr kleine ONNX-Datei (SPAN), die weichere Fassung von „Klar“.");

    public static ModelPackageDefinition UpscalePhoto { get; } = new(
        UpscalePhotoId,
        "Hochskalieren (Foto)",
        "4xNomosWebPhoto RealPLKSR",
        "Handy- und Webfotos mit Rauschen, Unschärfe und JPEG-Spuren 2× oder 4× vergrößern („Foto verbessern“, Modell „Foto“).",
        ModelPackageGroup.Model,
        "RealPLKSR fp32 (ONNX 2024-05)",
        new ModelPackageOrigin("Philip Hofmann (Phhofm) · offizielle ONNX-Fassung", new Uri("https://github.com/Phhofm/models")),
        "CC-BY-4.0 (auch kommerziell nutzbar, Namensnennung: Philip Hofmann)",
        DownloadSize: 29_805_849,
        FolderName: "enhance-upscale-photo",
        Files: [new(UpscalePhotoFile, 29_805_849, "146669e66cd3b22a4b97b0cf83f9bb2bcf20cd4ffd51fe76f91b95a4a8002df6")],
        Note: "Eine ONNX-Datei (RealPLKSR). Der Urheber nennt keine Prüfsumme; sie wurde am 2026-10-27 aus der geladenen Datei gebildet.");

    public static ModelPackageDefinition UpscaleSiax { get; } = new(
        UpscaleSiaxId,
        "Hochskalieren (Siax)",
        "4x NMKD Siax 200k",
        "Saubere Fotos 2× oder 4× vergrößern („Foto verbessern“, Modell „Siax“).",
        ModelPackageGroup.Model,
        "200k fp32 (ONNX 2026-06)",
        new ModelPackageOrigin("Nmkd (Siax) · ONNX-Fassung der Community (yuvraj108c)", new Uri("https://openmodeldb.info/models/4x-NMKD-Siax-CX")),
        "WTFPL (auch kommerziell nutzbar)",
        DownloadSize: 71_636_302,
        FolderName: "enhance-upscale-siax",
        Files: [new(UpscaleSiaxFile, 71_636_302, "0e090d7dba554acc00b2c8fdf0524b9b8acd52de5daada4aba7f5c4ec50dcd78")],
        Note: "Eine ONNX-Datei (ESRGAN, so groß und schnell wie „Scharf“). Community-Umwandlung; die Prüfsumme hält die Datei fest.");

    public static ModelPackageDefinition UpscaleSkin { get; } = new(
        UpscaleSkinId,
        "Hochskalieren (Haut)",
        "4x foolhardy Remacri",
        "Fotos 2× oder 4× vergrößern mit mehr Detail und weniger Glätten auf Haut („Foto verbessern“, Modell „Haut“).",
        ModelPackageGroup.Model,
        "Remacri fp32 (ONNX 2026-06)",
        new ModelPackageOrigin("FoolhardyVEVO (Remacri) · ONNX-Fassung der Community (yuvraj108c)", new Uri("https://openmodeldb.info/models/4x-Remacri")),
        "CC-BY-NC-SA-4.0 — nur nicht-kommerziell (für die private Nutzung am 2026-10-27 zugelassen)",
        LicenseRestricted: true,
        DownloadSize: 71_636_302,
        FolderName: "enhance-upscale-skin",
        Files: [new(UpscaleSkinFile, 71_636_302, "6bb362a994e57a256347fb7e7d347d85f9ef7149665804a8e13bff5828b37f35")],
        Note: "Eine ONNX-Datei (ESRGAN, so groß und schnell wie „Scharf“). Community-Umwandlung; die Prüfsumme hält die Datei fest.");

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
        (UpscaleClear, ClearRealityBase),
        (UpscaleClearSoft, ClearRealitySoftBase),
        (UpscalePhoto, NomosWebPhotoBase),
        (UpscaleSiax, UpscalerOnnxBase),
        (UpscaleSkin, UpscalerOnnxBase),
        (Faces, FaceFusionBase),
        (Colorize, FaceFusionBase),
        (Inpaint, LamaBase),
    ];

    /// <summary>Modell-Id des Werkzeugs (null ohne Modell).</summary>
    public static string? ModelIdOf(EnhanceTool tool) => tool switch
    {
        EnhanceTool.Upscale => UpscaleId,
        EnhanceTool.UpscaleNatural => UpscaleNaturalId,
        EnhanceTool.UpscaleClear => UpscaleClearId,
        EnhanceTool.UpscaleClearSoft => UpscaleClearSoftId,
        EnhanceTool.UpscalePhoto => UpscalePhotoId,
        EnhanceTool.UpscaleSiax => UpscaleSiaxId,
        EnhanceTool.UpscaleSkin => UpscaleSkinId,
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
        EnhanceTool.UpscaleClear => UpscaleClearFile,
        EnhanceTool.UpscaleClearSoft => UpscaleClearSoftFile,
        EnhanceTool.UpscalePhoto => UpscalePhotoFile,
        EnhanceTool.UpscaleSiax => UpscaleSiaxFile,
        EnhanceTool.UpscaleSkin => UpscaleSkinFile,
        EnhanceTool.Faces => FacesFile,
        EnhanceTool.Colorize => ColorizeFile,
        EnhanceTool.Erase => InpaintFile,
        _ => null,
    };

    public static ModelPackageDefinition? DefinitionOf(EnhanceTool tool) => tool switch
    {
        EnhanceTool.Upscale => Upscale,
        EnhanceTool.UpscaleNatural => UpscaleNatural,
        EnhanceTool.UpscaleClear => UpscaleClear,
        EnhanceTool.UpscaleClearSoft => UpscaleClearSoft,
        EnhanceTool.UpscalePhoto => UpscalePhoto,
        EnhanceTool.UpscaleSiax => UpscaleSiax,
        EnhanceTool.UpscaleSkin => UpscaleSkin,
        EnhanceTool.Faces => Faces,
        EnhanceTool.Colorize => Colorize,
        EnhanceTool.Erase => Inpaint,
        _ => null,
    };
}
