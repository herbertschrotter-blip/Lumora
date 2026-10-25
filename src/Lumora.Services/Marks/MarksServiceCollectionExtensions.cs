using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Marks;

/// <summary>
/// DI-Registrierung der Tag-/Kategorie-Symbole (Mockup 16, 0.4.35): Einstellungen und Ordner-Zählung. Wird in
/// <c>AddLumoraServices</c> mit einer Zeile aufgerufen.
/// </summary>
public static class MarksServiceCollectionExtensions
{
    public static IServiceCollection AddMarks(this IServiceCollection services)
    {
        services.TryAddSingleton<IMarkSettingsStore, MarkSettingsStore>();
        services.TryAddSingleton<IFolderMarkCounter, FolderMarkCounter>();
        return services;
    }
}
