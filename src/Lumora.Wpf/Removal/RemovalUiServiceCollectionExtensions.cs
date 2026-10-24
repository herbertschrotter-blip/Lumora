using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Removal;

/// <summary>Oberfläche „Ordner in den Papierkorb / Fehlende entfernen“ (ADR-0065); aufgerufen aus <c>AddFolderActions</c>.</summary>
public static class RemovalUiServiceCollectionExtensions
{
    public static IServiceCollection AddRemovalUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IRemovalDialogs, RemovalDialogs>(); // Rauchtest setzt vorher seinen Fake
        services.TryAddSingleton<RemovalActions>();
        services.TryAddSingleton<MissingFilesSettingsViewModel>();
        return services;
    }
}
