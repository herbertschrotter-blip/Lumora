using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Database;

namespace Lumora.Services.Merge;

/// <summary>
/// Katalog-Teil von „Zusammenfügen“ (ADR-0078) ohne OpenCV: Abfragen für Ergebnis-Marke, Lightbox und „Suchen ▸“. Das
/// Rechnen samt Merge-Dienst registriert <c>Lumora.Imaging</c> (<c>AddImaging()</c> in App.xaml.cs).
/// </summary>
public static class MergeCatalogServiceCollectionExtensions
{
    public static IServiceCollection AddMergeCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton<IMergedPhotoStore>(sp => new MergedPhotoStore(sp.GetRequiredService<ILibraryContextFactory>()));
        return services;
    }
}
