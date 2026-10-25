using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Lumora.Services.Infrastructure;

namespace Lumora.Services.Database.Backup;

/// <summary>Katalog-Sicherung (ADR-0069); eine Zeile in <c>AddLumoraServices</c>: <c>services.AddCatalogBackup();</c>.</summary>
public static class CatalogBackupServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogBackup(this IServiceCollection services)
    {
        // Je Katalog-Sitzung (ADR-0049) mit deren IAppDataPaths: normal <App-Wurzel>\backups, versteckt .lumora\backups
        services.TryAddSingleton<ICatalogBackupService>(sp => new CatalogBackupService(
            sp.GetRequiredService<IAppDataPaths>(),
            sp.GetService<ILogger<CatalogBackupService>>(),
            sp.GetService<CatalogBackupOptions>(), // Wpf registriert die App-Version (AppInfo.Version)
            time: null,
            factory: sp.GetService<ILibraryContextFactory>()));
        return services;
    }
}
