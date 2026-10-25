using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Scanning.Exclusions;

/// <summary>DI-Registrierung der Ausschlüsse beim Einlesen (ADR-0066); aus <c>AddLumoraServices</c> mit einer Zeile aufgerufen.</summary>
public static class ScanExclusionServiceCollectionExtensions
{
    public static IServiceCollection AddScanExclusions(this IServiceCollection services)
    {
        services.TryAddSingleton<IScanExclusionStore, ScanExclusionStore>();
        services.TryAddSingleton<IScanExclusionService, ScanExclusionService>();
        return services;
    }
}
