using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;

namespace Lumora.Shell.Activity;

/// <summary>„Fehlende zeigen“: Galerie mit dem Filter „Ablage: Nur fehlende“ (ADR-0065) im aktuellen Bereich öffnen.</summary>
public sealed class ActivityNavigator(IServiceProvider services, INavigationService navigation) : IActivityNavigator
{
    public void ShowMissingFiles()
    {
        services.GetRequiredService<GalleryViewModel>().FilterState = new GalleryFilterState(Location: GalleryLocation.Missing);
        navigation.NavigateTo(NavigationTags.Gallery);
    }

    /// <summary>„Öffnen“ (ein Ergebnis von „Zusammenfügen“: mit seinen Quellen, Ergebnis zuerst) bzw. „Ergebnisse zeigen“ (ADR-0078).</summary>
    public void ShowFiles(System.Collections.Generic.IReadOnlyList<int> fileIds)
    {
        var merge = services.GetService<Lumora.Merge.MergeGalleryActions>();
        if (merge is null || fileIds.Count == 0)
        {
            return;
        }

        if (fileIds.Count == 1)
        {
            _ = merge.OpenResultAsync(fileIds[0], System.Windows.Application.Current?.MainWindow);
            return;
        }

        _ = OpenManyAsync(merge, fileIds);
    }

    private async System.Threading.Tasks.Task OpenManyAsync(Lumora.Merge.MergeGalleryActions merge, System.Collections.Generic.IReadOnlyList<int> fileIds)
    {
        try
        {
            var entries = await merge.LoadEntriesAsync(fileIds).ConfigureAwait(true);
            if (entries.Count > 0)
            {
                services.GetRequiredService<Lumora.Lightbox.ILightboxService>().Open(entries, 0, System.Windows.Application.Current?.MainWindow);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                services.GetService<Microsoft.Extensions.Logging.ILogger<ActivityNavigator>>() ?? (Microsoft.Extensions.Logging.ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                ex, "Aktivitäten: Ergebnisse nicht geöffnet");
        }
    }
}

/// <summary>Aktivitäts-Anzeige der Statusleiste (ADR-0067). Eine Zeile in <c>AddShell</c>.</summary>
public static class ActivityUiServiceCollectionExtensions
{
    public static IServiceCollection AddActivityUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IActivityNavigator, ActivityNavigator>();
        services.TryAddSingleton<ActivityStatusViewModel>();
        return services;
    }
}
