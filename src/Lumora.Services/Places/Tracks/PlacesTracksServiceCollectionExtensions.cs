using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Suggestions;

namespace Lumora.Services.Places.Tracks;

/// <summary>
/// DI für ungenaue Orte und GPS-Spuren (ADR-0062): Genauigkeits-Prüfung im Hintergrund, Spuren speichern und einlesen,
/// automatisches Ergänzen (Ordner, USB), Einstellungen und Vorschläge. Aufgerufen aus <see cref="PlacesServiceCollectionExtensions.AddPlaces"/>.
/// </summary>
public static class PlacesTracksServiceCollectionExtensions
{
    public static IServiceCollection AddPlacesTracks(this IServiceCollection services)
    {
        services.TryAddSingleton<IGeoAccuracyService, GeoAccuracyService>();
        services.TryAddSingleton<IGpsTrackStore, GpsTrackStore>();
        services.TryAddSingleton<IGeoTrackSettingsStore, GeoTrackSettingsStore>();
        services.TryAddSingleton<IGpsTrackImportService, GpsTrackImportService>();
        services.TryAddSingleton<IGpsTrackAutoImport, GpsTrackAutoImport>();
        services.TryAddSingleton<IGeoAssignService>(sp => sp.GetRequiredService<GeoLocationService>());
        services.TryAddSingleton<IGeoSuggestionService, GeoSuggestionService>();
        // Spuren aus verbundenen Apps (ADR-0073 Stufe 7): die App-Verbindung hängt ihren Abgleich hier ein
        services.TryAddSingleton<ExternalTrackSyncHub>();
        services.TryAddSingleton<IExternalTrackSync>(sp => sp.GetRequiredService<ExternalTrackSyncHub>());
        return services;
    }

    /// <summary>Nach der DB-Initialisierung jeder Katalog-Sitzung: Prüfung und automatisches Einlesen starten.</summary>
    public static void StartPlacesTracks(IServiceProvider services)
    {
        services.GetRequiredService<IGeoAccuracyService>().Start();
        services.GetRequiredService<IGpsTrackAutoImport>().Start();
    }
}
