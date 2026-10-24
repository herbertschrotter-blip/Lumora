using System.IO;
using System.Linq;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Services.HiddenLibraries.SecurityCheck;
using Lumora.Settings.Sections;

namespace Lumora.Shell.HiddenLibraries.SecurityCheck;

/// <summary>Reiter und DI des Sicherheits-Checks (ADR-0064) — beides nur in der geöffneten versteckten Sitzung.</summary>
public static class SecurityCheckUi
{
    /// <summary>
    /// Fügt den Reiter „Sicherheits-Check“ hinter „Versteckte Bibliotheken“ (sonst vor „Info“) ein — nur bei
    /// <c>{ IsHiddenModeEnabled: true, IsHiddenSessionActive: true }</c>, wie der Reiter der versteckten Bibliotheken.
    /// </summary>
    public static void AddSettingsTab(TabControl tabs, SecurityCheckViewModel? viewModel)
    {
        if (viewModel is not { IsHiddenModeEnabled: true, IsHiddenSessionActive: true })
        {
            return;
        }

        var section = new SettingsSecurityCheckSection { DataContext = viewModel };
        var tab = new TabItem
        {
            Header = "Sicherheits-Check",
            Tag = NavigationTags.SettingsSecurityCheck,
            Content = section,
        };
        var items = tabs.Items.OfType<TabItem>().ToList();
        var hidden = items.FirstOrDefault(t => Equals(t.Tag, NavigationTags.SettingsHiddenLibraries));
        var info = items.FirstOrDefault(t => Equals(t.Tag, NavigationTags.SettingsInfo));
        var index = hidden is not null ? tabs.Items.IndexOf(hidden) + 1 : info is null ? tabs.Items.Count : tabs.Items.IndexOf(info);
        tabs.Items.Insert(index, tab);
        // Inhalt lädt erst, wenn der Reiter gewählt ist (TabControl zeigt nur den gewählten Inhalt) — nicht schon beim Öffnen der Einstellungen
        section.Loaded += (_, _) => _ = viewModel.LoadAsync();
    }

    /// <summary>
    /// Nur im Container einer versteckten Sitzung (<see cref="HiddenLibrariesServiceCollectionExtensions.AddCatalogSession"/>).
    /// Im Rauchtest feste Fake-Leser und ein Starter, der nichts öffnet.
    /// </summary>
    public static IServiceCollection AddSecurityCheckUi(this IServiceCollection services, ICatalogSessionManager sessions)
    {
        var smoke = SmokeTestRunner.IsRequested;
        services.AddSecurityCheck(
            () => DriveLetterOf(sessions.ActiveLibrary?.DriveRoot),
            smoke ? Smoke.SmokeSecurityCheckFakes.Sources() : null,
            smoke ? new Smoke.SmokeSecurityCheckFakes.Launcher() : null);
        services.AddSingleton<SecurityCheckViewModel>();
        return services;
    }

    internal static char? DriveLetterOf(string? driveRoot)
    {
        var root = string.IsNullOrEmpty(driveRoot) ? null : Path.GetPathRoot(driveRoot);
        return root is { Length: >= 2 } && root[1] == ':' && char.IsAsciiLetter(root[0]) ? char.ToUpperInvariant(root[0]) : null;
    }
}
