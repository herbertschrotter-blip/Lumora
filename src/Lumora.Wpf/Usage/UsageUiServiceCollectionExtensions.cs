using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Usage;

/// <summary>Nutzungs-Signale in der Oberfläche (ADR-0072): Karte „Nutzung lernen“ in Einstellungen › Storys.</summary>
public static class UsageUiServiceCollectionExtensions
{
    public static IServiceCollection AddUsageUi(this IServiceCollection services)
    {
        services.TryAddSingleton<UsageSettingsViewModel>();
        return services;
    }
}
