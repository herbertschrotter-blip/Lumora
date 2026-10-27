using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Lumora.Services.Activity;
using Lumora.Services.Database;
using Lumora.Services.Enhance.Onnx;
using Lumora.Services.Faces;
using Lumora.Services.Infrastructure;
using Lumora.Services.ModelPackages;
using Lumora.Services.Operations;

namespace Lumora.Services.Enhance;

/// <summary>
/// DI von „Foto verbessern“ (ADR-0071); eine Zeile in <c>AddLumoraServices</c> NACH <c>AddModelPackages</c>. Die vier Modelle
/// kommen als Einträge in Modelle &amp; Daten dazu (<see cref="ModelPackagesServiceCollectionExtensions.AddFileSetModelPackage"/>);
/// den Bild-Codec (WIC) registriert das Wpf-Projekt.
/// </summary>
public static class PhotoEnhanceServiceCollectionExtensions
{
    public static IServiceCollection AddPhotoEnhance(this IServiceCollection services)
    {
        foreach (var (definition, baseUri) in EnhanceModelCatalog.All)
        {
            services.AddFileSetModelPackage(definition, baseUri);
        }

        services.TryAddSingleton<IGpuGate, GpuGate>();
        services.TryAddSingleton<IEnhanceSettingsStore>(sp => new EnhanceSettingsStore(sp.GetRequiredService<ILibraryContextFactory>(), sp.GetService<IMessenger>()));
        services.TryAddSingleton<IEnhanceModelAccess>(sp => new EnhanceModelAccess(sp.GetRequiredService<IModelPackageManager>()));
        services.TryAddSingleton<IEnhanceEngineFactory>(sp => new OnnxEnhanceEngineFactory(sp.GetService<ILogger<OnnxEnhanceEngineFactory>>()));
        services.TryAddSingleton<IEnhanceFaceLocator>(sp => new FaceAnalyzerEnhanceLocator(sp.GetRequiredService<IFaceAnalyzerFactory>()));
        services.TryAddSingleton<IPhotoVariantStore>(sp => new PhotoVariantStore(sp.GetRequiredService<ILibraryContextFactory>()));
        services.TryAddSingleton<IPhotoEnhanceService>(sp => new PhotoEnhanceService(
            sp.GetRequiredService<ILibraryContextFactory>(),
            sp.GetRequiredService<ICatalogStoragePaths>(),
            sp.GetRequiredService<IEnhanceImageCodec>(),
            sp.GetRequiredService<IEnhanceEngineFactory>(),
            sp.GetRequiredService<IEnhanceModelAccess>(),
            sp.GetRequiredService<IEnhanceSettingsStore>(),
            sp.GetRequiredService<IFileOperationService>(),
            sp.GetRequiredService<IGpuGate>(),
            sp.GetService<IEnhanceFaceLocator>(),
            sp.GetService<IActivityCenter>(),
            sp.GetService<ILogger<PhotoEnhanceService>>(),
            sp.GetService<IMessenger>(),
            sp.GetService<Background.IForegroundWork>()));
        return services;
    }
}
