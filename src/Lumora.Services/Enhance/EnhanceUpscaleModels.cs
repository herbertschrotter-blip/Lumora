namespace Lumora.Services.Enhance;

/// <summary>Ein wählbares Hochskalier-Modell: Wahl, Engine (Werkzeug mit eigener Modelldatei), Knopf-Text und Hinweis.</summary>
public sealed record EnhanceUpscaleChoice(EnhanceUpscaleModel Model, EnhanceTool Engine, string Label, string Hint);

/// <summary>
/// Die Hochskalier-Modelle von „Foto verbessern“ (0.5.7: Scharf/Natürlich; 0.5.11: Klar, Klar weich, Foto, Siax, Haut). Alle
/// rechnen 4× mit Werten 0…1 über denselben <c>OnnxUpscaler</c>; je Foto wählbar, die letzte Wahl merkt sich Einstellungen ›
/// Verbessern. Reihenfolge = Reihenfolge der Knöpfe und des Rückfalls, wenn das gewählte Modell fehlt.
/// </summary>
public static class EnhanceUpscaleModels
{
    public static IReadOnlyList<EnhanceUpscaleChoice> All { get; } =
    [
        new(EnhanceUpscaleModel.Sharp, EnhanceTool.Upscale, "Scharf", "Real-ESRGAN x4plus — kräftig geschärft, glättet Haut"),
        new(EnhanceUpscaleModel.Natural, EnhanceTool.UpscaleNatural, "Natürlich", "Real-ESRGAN general x4v3 — erhält Hautstruktur und Korn"),
        new(EnhanceUpscaleModel.Clear, EnhanceTool.UpscaleClear, "Klar", "ClearReality — weich und natürlich, gut für Gesichter und Haare; für klare Fotos ohne viel Korn"),
        new(EnhanceUpscaleModel.ClearSoft, EnhanceTool.UpscaleClearSoft, "Klar weich", "ClearReality Soft — noch weicher, möglichst wenig Artefakte"),
        new(EnhanceUpscaleModel.Photo, EnhanceTool.UpscalePhoto, "Foto", "Nomos WebPhoto — für Handy- und Webfotos mit Rauschen, Unschärfe und JPEG-Spuren"),
        new(EnhanceUpscaleModel.Siax, EnhanceTool.UpscaleSiax, "Siax", "NMKD Siax — universell für saubere Fotos"),
        new(EnhanceUpscaleModel.Skin, EnhanceTool.UpscaleSkin, "Haut", "Remacri — mehr Detail und weniger Glätten auf Haut; nur nicht-kommerziell"),
    ];

    /// <summary>Alle Engines zum Hochskalieren (Werkzeuge mit eigener Modelldatei).</summary>
    public static IReadOnlyList<EnhanceTool> Engines { get; } = All.Select(c => c.Engine).ToList();

    public static bool IsEngine(EnhanceTool tool) => All.Any(c => c.Engine == tool);

    public static EnhanceTool EngineOf(EnhanceUpscaleModel model) => All.FirstOrDefault(c => c.Model == model)?.Engine ?? EnhanceTool.Upscale;

    public static EnhanceUpscaleChoice Of(EnhanceTool engine) => All.First(c => c.Engine == engine);
}
