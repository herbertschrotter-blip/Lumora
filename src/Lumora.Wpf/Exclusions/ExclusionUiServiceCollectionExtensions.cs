using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Folders;

namespace Lumora.Exclusions;

/// <summary>Oberfläche „Ausschlüsse beim Einlesen“ und „Ordner verschieben“ (ADR-0066); aufgerufen aus <c>AddFolderActions</c>.</summary>
public static class ExclusionUiServiceCollectionExtensions
{
    public static IServiceCollection AddExclusionUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IFolderEditDialogs, FolderEditDialogs>(); // Rauchtest setzt vorher seinen Fake
        services.TryAddSingleton<ScanExclusionActions>();
        services.TryAddSingleton<ScanExclusionsSettingsViewModel>();
        services.TryAddSingleton<FolderMoveActions>();
        return services;
    }
}
