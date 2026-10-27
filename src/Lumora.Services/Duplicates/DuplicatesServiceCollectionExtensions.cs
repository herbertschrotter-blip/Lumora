using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Duplicates;

/// <summary>
/// Doppelte erkennen (ADR-0053): Hintergrund-Index (Prüfsummen, Bild-Fingerabdrücke), Prüfsummen aller Dateien im Leerlauf
/// (ADR-0073), Abfragen, Aufräumen mit Rückgängig, Einstellungen. Eine Zeile in <c>AddLumoraServices</c>; den Bild-Lader (<c>IFaceImageLoader</c>, WIC)
/// registriert das Wpf-Projekt, den Start (<see cref="IDuplicateIndexService.Start"/>) übernimmt <c>App.xaml.cs</c>.
/// </summary>
public static class DuplicatesServiceCollectionExtensions
{
    public static IServiceCollection AddDuplicates(this IServiceCollection services)
    {
        services.TryAddSingleton<IDuplicateIndexService, DuplicateIndexService>();
        services.TryAddSingleton<IChecksumBackfillService, ChecksumBackfillService>();
        services.TryAddSingleton<IDuplicateService, DuplicateService>();
        services.TryAddSingleton<IDuplicateCleanupService, DuplicateCleanupService>();
        services.TryAddSingleton<IDuplicateSettingsStore, DuplicateSettingsStore>();
        return services;
    }
}
