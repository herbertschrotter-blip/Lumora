using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database.Relocation;
using Lumora.Services.Faces;
using Lumora.Services.Faces.Models;
using Lumora.Services.Infrastructure;

namespace Lumora.Services.HiddenLibraries;

/// <summary>
/// Registrierungen für die Sitzung einer versteckten Bibliothek (ADR-0049). Vor <c>AddLumoraServices</c> aufrufen
/// (dort TryAdd): Katalog-Kontext „versteckt“, Katalog-Umzug gesperrt (der Katalog bleibt im Container), Gesichtsmodelle
/// aus dem normalen Modellordner lesen (sie sind nicht persönlich und würden sonst in jeden Container geladen).
/// </summary>
public static class HiddenCatalogServiceCollectionExtensions
{
    public static IServiceCollection AddHiddenCatalog(this IServiceCollection services, IAppDataPaths hiddenPaths, string normalAppDataRoot)
    {
        services.AddSingleton<ICatalogContext>(new CatalogContext(hiddenPaths, isHidden: true));
        services.AddSingleton<ICatalogStoragePaths>(sp => sp.GetRequiredService<ICatalogContext>());
        services.TryAddSingleton<IDatabaseRelocationService>(sp => new HiddenCatalogRelocationBlocker(hiddenPaths));
        services.TryAddSingleton<IFaceModelProvider>(sp =>
        {
            var overridden = Environment.GetEnvironmentVariable(IFaceModelProvider.OverrideVariable);
            return new FaceModelProvider(
                sp.GetRequiredService<IAppDataPaths>(),
                sp.GetService<ILogger<FaceModelProvider>>(),
                modelFolder: string.IsNullOrWhiteSpace(overridden) ? Path.Combine(normalAppDataRoot, "models", "buffalo_l") : null);
        });
        // ADR-0070: Motiv-Modell und alle weiteren Modelle ebenfalls aus dem normalen Modellordner (keine Benutzerdaten;
        // vorher hätte die versteckte Sitzung SigLIP 2 ein zweites Mal in den Container geladen)
        services.TryAddSingleton<ModelPackages.IModelStorage>(new ModelPackages.ModelStorage(Path.Combine(normalAppDataRoot, "models")));
        services.TryAddSingleton<Motifs.Models.IMotifModelProvider>(sp => new Motifs.Models.MotifModelProvider(
            sp.GetRequiredService<IAppDataPaths>(),
            sp.GetService<ILogger<Motifs.Models.MotifModelProvider>>(),
            modelFolder: string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Motifs.Models.IMotifModelProvider.OverrideVariable))
                ? Path.Combine(normalAppDataRoot, "models", Motifs.Models.IMotifModelProvider.FolderName)
                : null));
        return services;
    }
}

/// <summary>Ein versteckter Katalog zieht nie um — er gehört in den Container (ADR-0049).</summary>
public sealed class HiddenCatalogRelocationBlocker : IDatabaseRelocationService
{
    public const string Reason = "Der Katalog einer versteckten Bibliothek bleibt in ihrem Container und kann nicht umziehen.";

    private readonly IAppDataPaths _paths;

    public HiddenCatalogRelocationBlocker(IAppDataPaths paths)
    {
        _paths = paths;
    }

    public string CurrentFolder => Path.GetDirectoryName(_paths.LibraryDatabasePath) ?? _paths.AppDataRoot;

    public string DefaultFolder => CurrentFolder;

    public Task<RelocationTargetCheck> CheckTargetAsync(string targetFolder, CancellationToken ct = default)
        => Task.FromResult(new RelocationTargetCheck(
            RelocationTargetState.Invalid, targetFolder, Reason, 0, null, HasEnoughSpace: false, IsCloudSynced: false, ThumbnailFiles: 0, Existing: null));

    public Task<RelocationResult> RelocateAsync(
        string targetFolder,
        RelocationMode mode,
        IProgress<RelocationProgress>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult(new RelocationResult(false, Reason, _paths.LibraryDatabasePath));
}
