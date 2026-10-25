using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Enhance;

namespace Lumora.Enhance;

/// <summary>
/// „Foto verbessern“ in der Oberfläche (ADR-0071): WIC-Codec hinter dem Services-Vertrag, Lightbox-Leiste (je Fenster über die
/// Fabrik), Stapel-Dialog, Galerie-Anbindung, Einstellungen › Verbessern. Eine Zeile in <c>App.ConfigureServices</c>.
/// </summary>
public static class EnhanceUiServiceCollectionExtensions
{
    public static IServiceCollection AddEnhanceUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IEnhanceImageCodec, WicEnhanceImageCodec>();
        services.TryAddSingleton<EnhancePanelFactory>();
        services.TryAddSingleton<IEnhanceBatchDialogService, EnhanceBatchDialogService>();
        services.TryAddSingleton<EnhanceGalleryActions>();
        services.TryAddSingleton<EnhanceSettingsViewModel>();
        if (Shell.SmokeTestRunner.IsRequested)
        {
            Shell.Smoke.SmokeEnhance.Register(services); // Fake-Modelle (kleine Bildoperationen), kein echtes Modell, kein Download
        }

        return services;
    }
}
