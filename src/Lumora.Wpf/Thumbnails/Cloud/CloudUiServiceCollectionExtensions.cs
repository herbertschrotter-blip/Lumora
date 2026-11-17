using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Thumbnails;

namespace Lumora.Thumbnails.Cloud;

/// <summary>
/// OneDrive-Bibliothek, WPF-Teil (ADR-0077): Shell-Vorschaubilder für Platzhalter (Galerie über den ThumbnailService,
/// 1024-px-Vorschau in der Lightbox). Eine Zeile in <c>App.ConfigureServices</c>; die übrigen Cloud-Dienste registriert
/// <c>AddLumoraServices</c> (<c>AddCloudFiles</c>).
/// </summary>
public static class CloudUiServiceCollectionExtensions
{
    public static IServiceCollection AddCloudUi(this IServiceCollection services)
    {
        services.TryAddSingleton<ShellThumbnailExtractor>();

        // Lightbox auf eigenen STA-Threads, Galerie auf den übrigen — die Vorschau wartet nie hinter der Galerie (0.5.41)
        services.TryAddSingleton<ICloudPreviewSource>(sp => sp.GetRequiredService<ShellThumbnailExtractor>().Lightbox);
        services.TryAddSingleton<ICloudThumbnailRenderer>(sp => new ShellCloudThumbnailRenderer(sp.GetRequiredService<ShellThumbnailExtractor>()));
        return services;
    }
}
