using Lumora.Services.ModelPackages;

namespace Lumora.Services.Enhance;

/// <summary>
/// Bereitschaft der Werkzeug-Modelle über den Update-Manager (ADR-0070): „geladen“ und „Update verfügbar“ zählen als
/// bereit, „defekt“ nicht. Ein nicht registrierter Eintrag heißt „Quelle folgt“ (nicht ladbar). Die Modelle liegen auch in
/// der versteckten Sitzung im normalen Modellordner.
/// </summary>
public sealed class EnhanceModelAccess(IModelPackageManager manager) : IEnhanceModelAccess
{
    public async Task<EnhanceModelState> GetStateAsync(EnhanceTool tool, CancellationToken ct)
    {
        var id = EnhanceModelCatalog.ModelIdOf(tool);
        if (id is null)
        {
            return EnhanceModelState.Ready;
        }

        if (manager.Find(id) is null)
        {
            return EnhanceModelState.NoSource;
        }

        var status = await manager.GetStatusAsync(id, ct).ConfigureAwait(false);
        return status.IsInstalled ? EnhanceModelState.Ready
            : status.State == ModelPackageState.Broken ? EnhanceModelState.Broken
            : EnhanceModelState.NotInstalled;
    }

    public string? GetModelPath(EnhanceTool tool)
    {
        var id = EnhanceModelCatalog.ModelIdOf(tool);
        var file = EnhanceModelCatalog.FileOf(tool);
        return id is null || file is null || manager.Find(id)?.Folder is not { } folder ? null : Path.Combine(folder, file);
    }
}
