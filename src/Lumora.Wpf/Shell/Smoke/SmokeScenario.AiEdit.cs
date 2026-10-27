using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Lumora.AiEdit;
using Lumora.Services.AiEdit;
using Lumora.Settings;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Teil „KI-Bearbeitung“ (ADR-0076 Stufe 1) im Schritt „Versteckte Bibliothek“: Reiter hinter „Sicherheits-Check“,
/// Laufzeit erkennt im Test-Container keine Installation („nicht startbar“) und legt dabei nichts an — gestartet wird nie.
/// In der normalen Sitzung: kein Reiter, kein Dienst, keine Einstellung im normalen Katalog.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task AiEditInHiddenSessionAsync(IServiceProvider hidden, string containerRoot)
    {
        var navigation = hidden.GetRequiredService<INavigationService>();
        var sections = hidden.GetRequiredService<SettingsSectionState>();
        var page = hidden.GetRequiredService<SettingsPage>();
        var runtime = hidden.GetRequiredService<IComfyRuntime>();
        var vm = hidden.GetRequiredService<AiEditSettingsViewModel>();

        navigation.NavigateTo(NavigationTags.SettingsAiEdit);
        await WaitUntilAsync(
            () => navigation.CurrentTag == NavigationTags.Settings && sections.SelectedSection == NavigationTags.SettingsAiEdit
                  && page.SelectedTabTag == NavigationTags.SettingsAiEdit,
            ShortWait,
            "Reiter KI-Bearbeitung").ConfigureAwait(true);
        var tags = page.Tabs.Items.OfType<TabItem>().Select(t => t.Tag as string).ToList();
        Check(tags.IndexOf(NavigationTags.SettingsAiEdit) == tags.IndexOf(NavigationTags.SettingsSecurityCheck) + 1, "Reiter hinter „Sicherheits-Check“");
        await WaitUntilAsync(() => vm.SpaceText != "wird berechnet …", ShortWait, "KI-Bearbeitung geladen").ConfigureAwait(true);
        Check(runtime.Status.State == ComfyRuntimeState.Unavailable && !vm.CanStart && !vm.CanStop,
            $"ohne Installation nicht startbar: {runtime.Status.State}");
        Check(!Directory.Exists(Path.Combine(containerRoot, Lumora.Services.Infrastructure.CatalogFolders.AiEditFolderName)),
            "Erkennen legt nichts im Container an");
        await SnapshotAsync("72-ki-bearbeitung").ConfigureAwait(true);
        navigation.NavigateTo(NavigationTags.Gallery);
    }

    private async Task NoAiEditInNormalSessionAsync(SmokeScenario next, IServiceProvider services)
    {
        next._navigation.NavigateTo(NavigationTags.Settings);
        await WaitUntilAsync(() => next._navigation.CurrentTag == NavigationTags.Settings, ShortWait, "normale Einstellungen").ConfigureAwait(true);
        var tags = next._settingsPage.Tabs.Items.OfType<TabItem>().Select(t => t.Tag as string).ToList();
        Check(!tags.Contains(NavigationTags.SettingsAiEdit), "normale Sitzung: kein Reiter KI-Bearbeitung");
        Check(services.GetService<IComfyRuntime>() is null && services.GetService<AiEditSettingsViewModel>() is null, "normale Sitzung: kein KI-Dienst");
        var rows = await next.DbScalarAsync<int>("SELECT COUNT(*) FROM Settings WHERE Key LIKE 'aiedit.%'").ConfigureAwait(true);
        Check(rows == 0, "normaler Katalog: keine Einstellung der KI-Bearbeitung");
        next._navigation.NavigateTo(NavigationTags.Gallery);
    }
}
