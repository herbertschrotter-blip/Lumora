using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Settings.Backup;

/// <summary>Lumora beenden (nach dem Vormerken einer Wiederherstellung, ADR-0069); Tests geben eine Attrappe.</summary>
public interface IAppShutdownService
{
    void Shutdown();
}

public sealed class AppShutdownService : IAppShutdownService
{
    // Application.Shutdown schließt die Sitzung über App.OnExit (versteckte Bibliothek: Container aushängen, ADR-0049)
    public void Shutdown() => Application.Current?.Shutdown();
}

/// <summary>Karte „Katalog-Sicherung“ in Speicher &amp; Wartung (ADR-0069); eine Zeile in <c>App.ConfigureServices</c>.</summary>
public static class CatalogBackupUiServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogBackupUi(this IServiceCollection services)
    {
        // Version im Dateinamen der Sicherungen = Version aus Einstellungen › Info (eine Quelle: CHANGELOG → InformationalVersion)
        services.TryAddSingleton(new Lumora.Services.Database.Backup.CatalogBackupOptions(AppVersion: Info.AppInfo.Version));
        services.TryAddSingleton<IAppShutdownService, AppShutdownService>();
        services.TryAddSingleton<CatalogBackupSettingsViewModel>();
        return services;
    }
}
