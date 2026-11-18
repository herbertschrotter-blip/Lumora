using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Lumora.Imaging.Merge;
using Lumora.Imaging.Merge.Panorama;
using Lumora.Imaging.Worker;
using Lumora.Services.Activity;
using Lumora.Services.Albums;
using Lumora.Services.Background;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Operations;

namespace Lumora.Imaging;

/// <summary>
/// DI von <c>Lumora.Imaging</c> (ADR-0078 Punkt 2): eine Zeile <c>services.AddImaging()</c> in App.xaml.cs NACH
/// <c>AddLumoraServices</c> (Services kennt Imaging nicht). Stufe 1 registriert das Zusammenfügen (<see cref="AddMerge"/>); die
/// Serien-Erkennung (<c>AddSeries()</c>) kommt mit Stufe 2. Nichts startet hier einen Hilfsprozess.
/// </summary>
public static class ImagingServiceCollectionExtensions
{
    public static IServiceCollection AddImaging(this IServiceCollection services)
    {
        services.AddMerge();
        return services;
    }

    public static IServiceCollection AddMerge(this IServiceCollection services)
    {
        services.TryAddSingleton(new ImagingWorkerOptions());
        services.TryAddSingleton<IImagingWorkerHost>(sp => new ImagingWorkerHost(
            sp.GetRequiredService<ICatalogStoragePaths>(),
            sp.GetRequiredService<ImagingWorkerOptions>(),
            sp.GetService<ILogger<ImagingWorkerHost>>()));
        services.TryAddSingleton<IPanoramaStitcher>(sp => new WorkerPanoramaStitcher(sp.GetRequiredService<IImagingWorkerHost>()));
        services.TryAddSingleton<IMergeSettingsStore>(sp => new MergeSettingsStore(sp.GetRequiredService<ILibraryContextFactory>()));
        services.TryAddSingleton<IMergeService>(sp => new MergeService(
            sp.GetRequiredService<ILibraryContextFactory>(),
            sp.GetRequiredService<IImagingWorkerHost>(),
            sp.GetRequiredService<IPanoramaStitcher>(),
            sp.GetRequiredService<IFileOperationService>(),
            sp.GetRequiredService<IMergeSettingsStore>(),
            sp.GetRequiredService<ICatalogStoragePaths>(),
            sp.GetService<IAlbumService>(),
            sp.GetService<IActivityCenter>(),
            sp.GetService<IForegroundWork>(),
            sp.GetService<IMessenger>(),
            sp.GetService<ILogger<MergeService>>()));
        return services;
    }
}
