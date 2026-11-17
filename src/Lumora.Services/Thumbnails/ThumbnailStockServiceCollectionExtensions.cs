using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Lumora.Services.Background;
using Lumora.Services.Database;
using Lumora.Services.Scanning;

namespace Lumora.Services.Thumbnails;

/// <summary>Vorschaubilder auf Vorrat (0.5.40, ADR-0074 Nachtrag 0.5.40): Schalter je Katalog und Aufgabe der Datei-Spur.</summary>
public static class ThumbnailStockServiceCollectionExtensions
{
    public static IServiceCollection AddThumbnailStock(this IServiceCollection services)
    {
        services.TryAddSingleton<IThumbnailStockSettingsStore, ThumbnailStockSettingsStore>();
        services.TryAddSingleton<IThumbnailStockService>(sp => new ThumbnailStockService(
            sp.GetRequiredService<ILibraryContextFactory>(),
            sp.GetRequiredService<IThumbnailService>(),
            sp.GetRequiredService<ILogger<ThumbnailStockService>>(),
            sp.GetRequiredService<IThumbnailStockSettingsStore>(),
            sp.GetService<IBackgroundPlanner>(),
            sp.GetService<IScanGate>(),
            sp.GetService<IMessenger>(),
            sp.GetService<IVideoThumbnailRenderer>()));
        return services;
    }
}
