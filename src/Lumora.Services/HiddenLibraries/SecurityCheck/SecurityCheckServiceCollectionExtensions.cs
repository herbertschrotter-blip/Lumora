using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>DI des Sicherheits-Checks (ADR-0064) — nur für den Container einer versteckten Sitzung aufrufen.</summary>
public static class SecurityCheckServiceCollectionExtensions
{
    /// <param name="containerDrive">Laufwerk der geöffneten versteckten Bibliothek.</param>
    /// <param name="sources">Leser; null = die Windows-Leser (Rauchtest/Tests: Fakes).</param>
    /// <param name="launcher">Starter; null = Windows-Oberfläche über die Positivliste (Rauchtest: Fake).</param>
    public static IServiceCollection AddSecurityCheck(
        this IServiceCollection services,
        Func<char?> containerDrive,
        SecurityCheckSources? sources = null,
        ISecuritySettingsLauncher? launcher = null)
    {
        var resolved = sources ?? new SecurityCheckSources(
            new WindowsRegistryReader(),
            new ShellVolumePropertyReader(),
            new LocalUserFileReader(),
            new VeraCryptLocator());
        services.AddSingleton(resolved);
        services.AddSingleton<ISecurityCheck>(sp => new BitLockerCheck(resolved));
        services.AddSingleton<ISecurityCheck>(sp => new HibernationCheck(resolved));
        services.AddSingleton<ISecurityCheck>(sp => new FastStartupCheck(resolved));
        services.AddSingleton<ISecurityCheck>(sp => new PageFileCheck(resolved));
        services.AddSingleton<ISecurityCheck>(sp => new RecentItemsCheck(resolved));
        services.AddSingleton<ISecurityCheck>(sp => new ActivityHistoryCheck(resolved));
        services.AddSingleton<ISecurityCheck>(sp => new ThumbnailCheck(resolved));
        services.AddSingleton<ISecurityCheck>(sp => new SearchIndexCheck(resolved));
        services.AddSingleton<ISecurityCheck>(sp => new VeraCryptHistoryCheck(resolved));
        services.AddSingleton<ISecurityCheck, ExitAfterLockCheck>();
        services.TryAddSingleton<ISecurityCheckSettingsStore>(sp => new SecurityCheckSettingsStore(sp.GetRequiredService<ILibraryContextFactory>()));
        services.TryAddSingleton<ISecuritySettingsLauncher>(_ => launcher ?? new SecuritySettingsLauncher(new ShellLauncher(), resolved.VeraCrypt));
        services.TryAddSingleton<ISecurityCheckService>(sp => new SecurityCheckService(
            sp.GetServices<ISecurityCheck>(),
            sp.GetRequiredService<ISecurityCheckSettingsStore>(),
            containerDrive,
            sp.GetRequiredService<ILogger<SecurityCheckService>>()));
        services.AddHostedService<SecurityCheckStartup>();
        return services;
    }
}
