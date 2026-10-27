using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Metadata;
using Lumora.Services.Scanning;

namespace Lumora.Services.Background;

/// <summary>
/// Hintergrund-Planer (ADR-0074): Datei-Spur mit Vorrang, Pause je Aufgabe und für alle, Warten beim Einlesen und beim
/// Katalog-Umzug, Lesen passend zum Laufwerk; dazu der gemeinsame Lesedurchgang für Datum, Orte und Genauigkeit.
/// </summary>
public static class BackgroundServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundPlanner(this IServiceCollection services)
    {
        services.TryAddSingleton<IStorageKindProbe, StorageKindProbe>();
        services.TryAddSingleton<IBackgroundPlanner>(sp => new BackgroundPlanner(
            sp.GetRequiredService<IScanGate>(),
            sp.GetService<ILibraryContextFactory>() as IDatabaseAccessGate,
            sp.GetRequiredService<IStorageKindProbe>(),
            sp.GetService<IMessenger>(),
            sp.GetService<ILogger<BackgroundPlanner>>()));
        services.TryAddSingleton<IFileMetadataProbe, FileMetadataProbe>();
        return services;
    }
}
