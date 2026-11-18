using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Imaging.Merge;

namespace Lumora.Merge;

/// <summary>
/// Oberfläche von „Zusammenfügen“ (ADR-0078 Punkt 2, Stufe 1 Teil B): Fenster, Galerie-/Lightbox-Anbindung, Filmstreifen,
/// Einstellungen › Zusammenfügen. Eine Zeile in <c>App.ConfigureServices</c> nach <c>AddImaging()</c>.
/// </summary>
public static class MergeUiServiceCollectionExtensions
{
    public static IServiceCollection AddMergeUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IMergeWindowService, MergeWindowService>();
        services.TryAddSingleton<IMergeThumbnailLoader, MergeThumbnailLoader>();
        services.TryAddSingleton<MergeGalleryActions>();
        services.TryAddSingleton(sp => new MergeSessionGuard(sp.GetRequiredService<IMergeService>(), sp.GetService<Lumora.Shell.IDialogService>()));
        services.TryAddSingleton(sp => new MergeSettingsViewModel(sp.GetRequiredService<IMergeService>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<MergeSettingsViewModel>>()));
        return services;
    }
}
