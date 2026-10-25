using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Database;
using Lumora.Services.Operations;

namespace Lumora.Services.Activity;

/// <summary>
/// Aktivitäts-Anzeige (ADR-0067): Zentrale, Brücke der Fortschrittsquellen und der Datei-Vorgangs-Dekorierer. Eine Zeile in
/// <c>AddLumoraServices</c> (vor <c>AddOperations</c>); gestartet wird die Brücke mit <see cref="StartActivities"/> beim
/// Sitzungsstart, bevor Hintergrund-Dienste melden. Je DI-Container (= Katalog-Sitzung) eine eigene, leere Liste.
/// </summary>
public static class ActivityServiceCollectionExtensions
{
    public static IServiceCollection AddActivities(this IServiceCollection services)
    {
        services.TryAddSingleton<IActivityCenter>(sp => new ActivityCenter(sp.GetService<TimeProvider>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<ActivityCenter>>()));
        services.TryAddSingleton<ActivityBridge>();

        // Datei-Vorgänge: der FileOperationService selbst bleibt unverändert, IFileOperationService ist der Dekorierer.
        // TryAdd — Tests und Rauchtest können eigene Implementierungen vorab registrieren.
        services.TryAddSingleton<FileOperationService>();
        services.TryAddSingleton<IFileOperationService>(sp => new ActivityTrackingFileOperationService(
            sp.GetRequiredService<FileOperationService>(),
            sp.GetRequiredService<IActivityCenter>(),
            FolderNameLookup(sp.GetRequiredService<ILibraryContextFactory>())));
        return services;
    }

    /// <summary>Nachrichten abonnieren (App-Start je Sitzung, eine Zeile).</summary>
    public static void StartActivities(IServiceProvider services) => services.GetService<ActivityBridge>()?.Start();

    /// <summary>Name eines Katalog-Ordners (nur für Texte der Anzeige).</summary>
    private static Func<int, CancellationToken, Task<string?>> FolderNameLookup(ILibraryContextFactory factory)
        => async (folderId, ct) =>
        {
            await using var ctx = factory.Create();
            return await ctx.Folders.AsNoTracking()
                .Where(f => f.Id == folderId)
                .Select(f => f.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        };
}
