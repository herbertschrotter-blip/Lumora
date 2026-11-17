using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Cloud;

/// <summary>
/// OneDrive-Bibliothek (ADR-0077): Platzhalter-Zustand, „Immer behalten“/„Speicherplatz freigeben“, Laden des Originals und
/// Aufnahmedatum ohne Download. Das Vorschaubild über die Shell (<c>ICloudThumbnailRenderer</c>) registriert das Wpf-Projekt.
/// </summary>
public static class CloudServiceCollectionExtensions
{
    public static IServiceCollection AddCloudFiles(this IServiceCollection services)
    {
        services.TryAddSingleton<ICloudFileSystem, WindowsCloudFileSystem>();
        services.TryAddSingleton<ICloudSyncRoots>(_ => CloudSyncRoots.FromSystem());
        services.TryAddSingleton<ICloudFileService>(sp => new CloudFileService(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CloudFileService>>(),
            sp.GetRequiredService<ICloudFileSystem>(),
            sp.GetRequiredService<ICloudSyncRoots>()));
        services.TryAddSingleton<ICloudFileHydrator, CloudFileHydrator>();
        services.TryAddSingleton<ICloudPropertyReader, ShellCloudPropertyReader>();
        return services;
    }
}
