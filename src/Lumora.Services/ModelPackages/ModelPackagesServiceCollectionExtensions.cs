using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Lumora.Services.Activity;
using Lumora.Services.Assistant;
using Lumora.Services.Faces.Models;
using Lumora.Services.Infrastructure;
using Lumora.Services.Motifs.Models;

namespace Lumora.Services.ModelPackages;

/// <summary>
/// DI des Update-Managers „Modelle &amp; Daten“ (ADR-0070). Eine Zeile in <c>AddLumoraServices</c> (nach Gesichtern, Motiven,
/// Assistent). Je Katalog-Sitzung ein Manager; die Modellordner sind in jeder Sitzung dieselben (<see cref="IModelStorage"/>,
/// die versteckte Sitzung registriert vorab den normalen Ordner).
/// </summary>
public static class ModelPackagesServiceCollectionExtensions
{
    public static IServiceCollection AddModelPackages(this IServiceCollection services)
    {
        services.TryAddSingleton<IModelStorage>(sp => new ModelStorage(Path.Combine(sp.GetRequiredService<IAppDataPaths>().AppDataRoot, "models")));
        services.TryAddSingleton<IModelDownloader>(_ => new HttpModelDownloader());
        services.TryAddSingleton<IModelMetadataClient>(_ => new HttpModelMetadataClient());

        // Feste Einträge in Anzeigereihenfolge; weitere Module hängen sich mit AddModelPackage/AddFileSetModelPackage an.
        services.AddModelPackage(sp => new FaceModelPackageSource(sp.GetRequiredService<IFaceModelProvider>(), sp.GetService<ILogger<FaceModelPackageSource>>()));
        services.AddModelPackage(sp => new MotifModelPackageSource(sp.GetRequiredService<IMotifModelProvider>(), sp.GetService<ILogger<MotifModelPackageSource>>()));
        services.AddModelPackage(_ => new GeoNamesPackageSource());
        services.AddModelPackage(sp => new MapTilesPackageSource(sp.GetRequiredService<ICatalogStoragePaths>()));
        services.AddModelPackage(sp => new OllamaPackageSource(() => sp.GetService<IAssistantRuntime>()));

        services.TryAddSingleton<IModelPackageManager>(sp => new ModelPackageManager(
            sp.GetServices<IModelPackageSource>(),
            sp.GetRequiredService<IModelMetadataClient>(),
            sp.GetService<IActivityCenter>(),
            sp.GetService<ILogger<ModelPackageManager>>(),
            sp.GetService<TimeProvider>()));
        return services;
    }

    /// <summary>Weiteren Eintrag anhängen (Reihenfolge = Registrierung innerhalb seiner Gruppe).</summary>
    public static IServiceCollection AddModelPackage(this IServiceCollection services, Func<IServiceProvider, IModelPackageSource> factory)
    {
        services.AddSingleton(factory);
        return services;
    }

    /// <summary>
    /// Neues Modell aus einzelnen Dateien (z. B. „Foto verbessern“): Definition mit FolderName und Files (Größe + SHA-256),
    /// Basisadresse des Herstellers. Laden nur nach Bestätigung in Einstellungen › Modelle &amp; Daten bzw. über
    /// <see cref="IModelPackageManager.InstallAsync"/>; Nutzer hören auf <see cref="ModelPackageChangedMessage"/>.
    /// </summary>
    public static IServiceCollection AddFileSetModelPackage(this IServiceCollection services, ModelPackageDefinition definition, Uri baseUri)
        => services.AddModelPackage(sp => new FileSetModelPackageSource(
            definition,
            baseUri,
            sp.GetRequiredService<IModelStorage>(),
            sp.GetRequiredService<IModelDownloader>(),
            sp.GetService<ILogger<FileSetModelPackageSource>>(),
            sp.GetService<IMessenger>()));
}
