using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Marks;

/// <summary>
/// DI-Registrierung der Tag-/Kategorie-Symbole in der Oberfläche (Mockup 16, 0.4.35): ein <see cref="MarkSettingsState"/> je
/// Sitzung, den Galerie, Lightbox, Ordnerbaum und Einstellungs-Karte teilen. Aufruf aus <c>AddGallery</c>.
/// </summary>
public static class MarksUiServiceCollectionExtensions
{
    public static IServiceCollection AddMarksUi(this IServiceCollection services)
    {
        services.TryAddSingleton<MarkSettingsState>();
        return services;
    }
}
