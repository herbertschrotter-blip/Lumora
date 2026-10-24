using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Removal;

/// <summary>DI-Registrierung „Ordner in den Papierkorb / Fehlende entfernen“ (ADR-0065); eine Zeile in <c>AddLumoraServices</c>.</summary>
public static class RemovalServiceCollectionExtensions
{
    public static IServiceCollection AddRemoval(this IServiceCollection services)
    {
        services.TryAddSingleton<IRemovalService, RemovalService>();
        return services;
    }
}
