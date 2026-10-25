using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Related;

/// <summary>DI-Registrierung von „Suchen ▸“ im Rechtsklick-Menü (ADR-0068). Eine Zeile in <c>AddLumoraServices</c>.</summary>
public static class RelatedServiceCollectionExtensions
{
    public static IServiceCollection AddRelatedPhotoSearch(this IServiceCollection services)
    {
        services.TryAddSingleton<IRelatedPhotoSearch, RelatedPhotoSearch>();
        return services;
    }
}
