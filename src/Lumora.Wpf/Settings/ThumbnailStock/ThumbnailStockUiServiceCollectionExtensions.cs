using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Settings.ThumbnailStock;

/// <summary>Schalter „Vorschaubilder auf Vorrat erstellen“ in Speicher &amp; Wartung (0.5.40); eine Zeile in <c>App.ConfigureServices</c>.</summary>
public static class ThumbnailStockUiServiceCollectionExtensions
{
    public static IServiceCollection AddThumbnailStockUi(this IServiceCollection services)
    {
        services.TryAddSingleton<ThumbnailStockSettingsViewModel>();
        return services;
    }
}
