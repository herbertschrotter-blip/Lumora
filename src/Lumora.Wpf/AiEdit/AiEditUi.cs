using System.Linq;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Services.AiEdit;
using Lumora.Settings.Sections;
using Lumora.Shell;
using Lumora.Shell.HiddenLibraries;

namespace Lumora.AiEdit;

/// <summary>Reiter und DI der KI-Bearbeitung (ADR-0076) — beides nur in der geöffneten versteckten Sitzung.</summary>
public static class AiEditUi
{
    /// <summary>
    /// Fügt den Reiter „KI-Bearbeitung“ hinter „Sicherheits-Check“ (sonst hinter „Versteckte Bibliotheken“, sonst vor
    /// „Info“) ein — nur bei <c>{ IsHiddenModeEnabled: true, IsHiddenSessionActive: true }</c>.
    /// </summary>
    public static void AddSettingsTab(TabControl tabs, AiEditSettingsViewModel? viewModel)
    {
        if (viewModel is not { IsHiddenModeEnabled: true, IsHiddenSessionActive: true })
        {
            return;
        }

        var section = new SettingsAiEditSection { DataContext = viewModel };
        var tab = new TabItem
        {
            Header = "KI-Bearbeitung",
            Tag = NavigationTags.SettingsAiEdit,
            Content = section,
        };
        var items = tabs.Items.OfType<TabItem>().ToList();
        var after = items.FirstOrDefault(t => Equals(t.Tag, NavigationTags.SettingsSecurityCheck))
                    ?? items.FirstOrDefault(t => Equals(t.Tag, NavigationTags.SettingsHiddenLibraries));
        var info = items.FirstOrDefault(t => Equals(t.Tag, NavigationTags.SettingsInfo));
        var index = after is not null ? tabs.Items.IndexOf(after) + 1 : info is null ? tabs.Items.Count : tabs.Items.IndexOf(info);
        tabs.Items.Insert(index, tab);
        // Erkennen und Platz rechnen erst, wenn der Reiter gewählt ist — nicht schon beim Öffnen der Einstellungen
        section.Loaded += (_, _) => _ = viewModel.LoadAsync();
    }

    /// <summary>Nur im Container einer versteckten Sitzung (<see cref="HiddenLibrariesServiceCollectionExtensions.AddCatalogSession"/>).</summary>
    public static IServiceCollection AddAiEditUi(this IServiceCollection services, ICatalogSessionManager sessions)
    {
        services.AddAiEdit(() => sessions.ActiveLibrary?.DriveRoot);
        services.AddSingleton<AiEditSettingsViewModel>();
        return services;
    }
}
