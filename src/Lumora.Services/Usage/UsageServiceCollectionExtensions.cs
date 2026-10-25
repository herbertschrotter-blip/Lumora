using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;

namespace Lumora.Services.Usage;

/// <summary>
/// Nutzungs-Signale (ADR-0072): ein Dienst je Katalog-Sitzung für Melden (<see cref="IUsageSignals"/>) und Lesen
/// (<see cref="IUsageService"/>). Eine Zeile in <c>AddLumoraServices</c>; <c>App.xaml.cs</c> erzeugt den Dienst beim Start
/// (<see cref="StartUsageSignals"/>), damit <see cref="Messaging.UsageSignalMessage"/> von Anfang an ankommt.
/// </summary>
public static class UsageServiceCollectionExtensions
{
    public static IServiceCollection AddUsageSignals(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new UsageSignalService(
            sp.GetRequiredService<ILibraryContextFactory>(),
            sp.GetService<ILogger<UsageSignalService>>(),
            sp.GetService<IMessenger>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IUsageSignals>(sp => sp.GetRequiredService<UsageSignalService>());
        services.TryAddSingleton<IUsageService>(sp => sp.GetRequiredService<UsageSignalService>());
        return services;
    }

    /// <summary>Dienst erzeugen (Messenger-Empfang) und „Nutzung lernen“ einmal lesen.</summary>
    public static void StartUsageSignals(IServiceProvider services)
    {
        if (services.GetService<UsageSignalService>() is { } usage)
        {
            _ = usage.GetSettingsAsync().ContinueWith(static _ => { }, TaskScheduler.Default);
        }
    }
}
