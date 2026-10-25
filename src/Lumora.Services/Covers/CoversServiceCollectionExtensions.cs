using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Covers;

/// <summary>
/// DI-Registrierung von „Als Titelbild für ▸“ und Ordner-Titelbild (ADR-0068). Wird in <c>AddLumoraServices</c> mit einer
/// Zeile aufgerufen.
/// </summary>
public static class CoversServiceCollectionExtensions
{
    public static IServiceCollection AddCovers(this IServiceCollection services)
    {
        services.TryAddSingleton<ICoverService, CoverService>();
        return services;
    }
}
