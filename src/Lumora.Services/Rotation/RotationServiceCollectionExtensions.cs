using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Rotation;

/// <summary>DI-Registrierung von „Drehen“ (EXIF-Ausrichtung, ADR-0068). Eine Zeile in <c>AddLumoraServices</c>.</summary>
public static class RotationServiceCollectionExtensions
{
    public static IServiceCollection AddPhotoRotation(this IServiceCollection services)
    {
        services.TryAddSingleton<IPhotoRotationService, PhotoRotationService>();
        return services;
    }
}
