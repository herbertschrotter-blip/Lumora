using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Export;

namespace Lumora.PhotoMenu;

/// <summary>
/// DI-Registrierung des Rechtsklick-Menüs (ADR-0068): gemeinsame Aktionen für Galerie, Liste und Lightbox und der WIC-Kodierer
/// für „Exportieren (verkleinert)“. Eine Zeile in <c>App.xaml.cs</c>.
/// </summary>
public static class PhotoMenuServiceCollectionExtensions
{
    public static IServiceCollection AddPhotoMenu(this IServiceCollection services)
    {
        services.TryAddSingleton<IPhotoExportEncoder, WicPhotoExportEncoder>();
        services.TryAddSingleton<PhotoActions>();
        return services;
    }
}
