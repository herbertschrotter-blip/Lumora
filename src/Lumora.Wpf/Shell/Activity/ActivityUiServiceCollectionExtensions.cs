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
