using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Export;

/// <summary>
/// DI-Registrierung von „Exportieren (verkleinert)…“ (ADR-0068). Den JPEG-Kodierer (<see cref="IPhotoExportEncoder"/>, WIC)
/// registriert das Wpf-Projekt. Eine Zeile in <c>AddLumoraServices</c>.
/// </summary>
public static class PhotoExportServiceCollectionExtensions
{
    public static IServiceCollection AddPhotoExport(this IServiceCollection services)
    {
        services.TryAddSingleton<IPhotoExportService, PhotoExportService>();
        return services;
    }
}
