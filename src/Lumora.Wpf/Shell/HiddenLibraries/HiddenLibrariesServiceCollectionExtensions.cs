using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.Containers;
using Lumora.Services.Infrastructure;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>Was eine Katalog-Sitzung über sich wissen muss (ADR-0049).</summary>
/// <param name="Sessions">Der Sitzungsverwalter des Prozesses (in jedem Container dieselbe Instanz).</param>
/// <param name="IsHidden">Container einer versteckten Bibliothek (Pfade = <c>.lumora</c>).</param>
/// <param name="NormalAppDataRoot">App-Wurzel des normalen Katalogs (Gesichtsmodelle).</param>
public sealed record CatalogSessionSetup(ICatalogSessionManager Sessions, bool IsHidden, string NormalAppDataRoot);

/// <summary>DI der Katalog-Sitzung und der versteckten Bibliotheken (ADR-0049); eine Zeile in <c>App.ConfigureServices</c>.</summary>
public static class HiddenLibrariesServiceCollectionExtensions
{
    /// <summary>Vor <c>AddLumoraServices</c> aufrufen (dort TryAdd für Katalog-Kontext, Umzug, Modelle).</summary>
    public static IServiceCollection AddCatalogSession(this IServiceCollection services, IAppDataPaths paths, CatalogSessionSetup setup)
    {
        services.AddSingleton(setup.Sessions);
        if (setup.IsHidden)
        {
            services.AddHiddenCatalog(paths, setup.NormalAppDataRoot);
            // Sicherheits-Check (ADR-0064): Prüfungen, Einstellungen, Reiter — nur in der versteckten Sitzung, sonst keine Spur
            SecurityCheck.SecurityCheckUi.AddSecurityCheckUi(services, setup.Sessions);
            // KI-Bearbeitung mit ComfyUI (ADR-0076): Laufzeit, Einstellungen, Reiter — nur versteckt, alles im Container
            Lumora.AiEdit.AiEditUi.AddAiEditUi(services, setup.Sessions);
        }

        services.TryAddSingleton<IHiddenLibraryDialogs, HiddenLibraryDialogs>();
        services.AddContainerAssistant(setup.Sessions);
        services.AddSingleton<HiddenLibrariesViewModel>();
        return services;
    }

    /// <summary>
    /// Container-Assistent (ADR-0061): neue BitLocker-VHDX bzw. VeraCrypt-Container. Einhängen über die Container-Werkzeuge
    /// des Sitzungsverwalters (im Rauchtest der Fake); alles andere startet erst auf Klick im Assistenten.
    /// </summary>
    private static IServiceCollection AddContainerAssistant(this IServiceCollection services, ICatalogSessionManager sessions)
    {
        services.TryAddSingleton<IProcessOutputRunner, ProcessOutputRunner>();
        services.TryAddSingleton<IContainerCreation>(sp => new ContainerCreationService(
            sessions.Containers,
            new VeraCryptLocator(),
            new VolumeDeviceQuery(),
            new LocalDriveSpaceSource(),
            new ElevatedProcessRunner(),
            new ShellLauncher(),
            new BitLockerPlatform(sp.GetRequiredService<IProcessOutputRunner>()),
            new WingetVeraCryptInstaller(sp.GetRequiredService<IProcessOutputRunner>())));
        services.TryAddSingleton<IContainerFilePicker, ContainerFilePicker>();
        services.TryAddSingleton<IContainerAssistant, ContainerAssistant>();
        return services;
    }
}
