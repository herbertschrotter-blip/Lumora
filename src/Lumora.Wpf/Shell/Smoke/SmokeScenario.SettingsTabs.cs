using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Settings;
using Lumora.Settings.Info;
using Lumora.Shell.Branding;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest zu Navigation und Einstellungs-Reitern (0.2.44, ADR-0042): Hilfen zum Öffnen eines Reiters (die früheren
/// Seiten Bibliothek/Tags/Kategorien sind Reiter der Einstellungen) und der Schritt „Einstellungen-Reiter und Info“ —
/// linke Leiste ohne Bibliothek/Tags/Kategorien, alle Reiter öffnen und laden, Info zeigt die Version des obersten
/// CHANGELOG-Eintrags, Bibliotheks-Umschalter, „Tags verwalten…“ von außen, „Eingang hinzufügen…“ auf der Ordner-Seite.
/// Seit 0.2.49 (ADR-0047) zusätzlich der Produktname Lumora: Fenstertitel und -symbol, Programmdatei, Schriftzug oben in
/// der Leiste und im Info-Kopf (Farben folgen Hell/Dunkel), Versionstexte „Lumora x.y.z“.
/// </summary>
internal sealed partial class SmokeScenario
{
    private SettingsSectionState _settingsSections = null!;
    private SettingsPage _settingsPage = null!;
    private InfoViewModel _info = null!;
    private LibrarySwitcherViewModel _switcher = null!;

    private void InitSettingsTabs(IServiceProvider services)
    {
        _settingsSections = services.GetRequiredService<SettingsSectionState>();
        _settingsPage = services.GetRequiredService<SettingsPage>();
        _info = services.GetRequiredService<InfoViewModel>();
        _switcher = services.GetRequiredService<LibrarySwitcherViewModel>();
    }

    /// <summary>Einstellungen zeigen den Reiter (Seite, Zustand und angezeigter Reiter stimmen überein).</summary>
    private bool IsOnSettingsSection(string section) =>
        _navigation.CurrentTag == NavigationTags.Settings
        && _settingsSections.SelectedSection == section
        && _settingsPage.SelectedTabTag == section;

    /// <summary>Öffnet einen Reiter der Einstellungen wie ein Aufruf von außen (NavigateTo mit Reiter-Tag).</summary>
    private async Task OpenSettingsSectionAsync(string section, string what)
    {
        _navigation.NavigateTo(section);
        await WaitUntilAsync(() => IsOnSettingsSection(section), ShortWait, what).ConfigureAwait(true);
    }

    private async Task SettingsTabsAndInfoAsync()
    {
        // (1) Linke Leiste: Galerie, Ordner, Personen, Orte, Sortieren; unten Einstellungen — Bibliothek/Tags/Kategorien sind weg
        if (Application.Current.MainWindow is MainWindow main)
        {
            var items = main.MainNav.MenuItems.OfType<NavigationViewItem>().Select(i => i.TargetPageTag).ToList();
            var footer = main.MainNav.FooterMenuItems.OfType<NavigationViewItem>().Select(i => i.TargetPageTag).ToList();
            Check(items.SequenceEqual([NavigationTags.Gallery /* Ordner seit 0.4.44 als Leiste in der Galerie (ADR-0068) */, NavigationTags.Albums /* ADR-0054 */, NavigationTags.Stories /* ADR-0060 */, NavigationTags.Persons, NavigationTags.Places, NavigationTags.Duplicates /* ADR-0053 */, NavigationTags.Sorter]),
                $"Navigation: {string.Join(", ", items)}");
            Check(footer.SequenceEqual([NavigationTags.Settings]), $"Fußleiste: {string.Join(", ", footer)}");
            Check(main.MainNav.PaneHeader is LibrarySwitcher, "Bibliotheks-Umschalter oben in der Leiste");

            // Produktname Lumora (ADR-0047): Titel, Fenstersymbol, Programmdatei, Schriftzug im Kopf der Leiste
            var exeName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
            Check(main.Title == LumoraBrand.ProductName && main.Icon is not null && LumoraAppIcon.WindowIcon is not null,
                $"Fenstertitel „{main.Title}“ mit Lumora-Symbol");
            Check(string.Equals(exeName, LumoraBrand.ExecutableName, StringComparison.OrdinalIgnoreCase), $"Programmdatei {exeName}");
            Check(main.MainNav.PaneHeader is LibrarySwitcher switcher
                  && switcher.FindName("Wordmark") is LumoraWordmark { IsLoaded: true, ActualWidth: > 40 } navMark
                  && navMark.FontSize == 22,
                "Schriftzug „lumora“ oben in der Leiste");
        }
        else
        {
            Check(false, "Hauptfenster nicht gefunden");
        }

        // (2) Umschalter zeigt die aktive Bibliothek
        var active = await _libraries.GetActiveAsync().ConfigureAwait(true);
        await _switcher.RefreshAsync().ConfigureAwait(true);
        Check(active is not null && _switcher.HasLibrary && _switcher.ActiveName == active.Name
              && _switcher.Libraries.Count(l => l.IsActive) == 1 && _switcher.CaptionText == "Bibliothek wechseln",
            $"Umschalter: {_switcher.ActiveName} ({_switcher.Libraries.Count} Bibliotheken)");
        _switcher.ManageCommand.Execute(null);
        await WaitUntilAsync(() => IsOnSettingsSection(NavigationTags.Library), ShortWait, "„Bibliotheken verwalten…“").ConfigureAwait(true);
        Check(_library.Libraries.Any(l => l.Id == _libraryId), "„Bibliotheken verwalten…“ öffnet Einstellungen › Bibliotheken & Eingänge mit der Liste");

        // (3) Alle Reiter nacheinander öffnen; jeder Inhalt wird geladen
        foreach (var section in NavigationTags.SettingsSections)
        {
            await OpenSettingsSectionAsync(section, $"Reiter {section}").ConfigureAwait(true);
            await WaitUntilAsync(
                () => _settingsPage.FindName("Tabs") is TabControl { SelectedContent: FrameworkElement { IsLoaded: true } },
                ShortWait,
                $"Inhalt von {section} geladen").ConfigureAwait(true);
        }

        Check(_settingsPage.SelectedTabTag == NavigationTags.SettingsInfo, "letzter Reiter = Info");

        // (4) Info: Version = oberster CHANGELOG-Eintrag (neben der App), Build, System, Bausteine
        await WaitUntilAsync(() => _info.SystemInfo.Count == 5, ShortWait, "Info geladen").ConfigureAwait(true); // 5. Zeile „Datenordner“ (ADR-0069)
        Check(_info.SystemInfo.Any(l => l.Label == "Datenordner" && l.Value.Contains("LUMORA_APPDATA", StringComparison.Ordinal)),
            "Info › Datenordner: Umgebungsvariable (Rauchtest), ohne Pfad"); // ADR-0069
        var changelog = AppInfo.FindChangelog(AppInfo.AppDirectory);
        var top = changelog is null ? null : AppInfo.ReadTopChangelogVersion(await File.ReadAllTextAsync(changelog).ConfigureAwait(true));
        Check(top is not null && _info.Version == top && _info.HeaderText.StartsWith($"Version {top}", StringComparison.Ordinal),
            $"Info-Version {_info.Version} = oberster CHANGELOG-Eintrag {top ?? "(CHANGELOG.md fehlt neben der App)"}: {_info.HeaderText}");
        Check(_info.HasChangelog && _info.OpenChangelogCommand.CanExecute(null), "Knopf „Änderungsprotokoll“ sichtbar");
        Check(_info.SystemInfo.Any(l => l.Label == "Katalog" && l.Value.Contains("Dateien", StringComparison.Ordinal)),
            $"Katalog: {_info.SystemInfo.FirstOrDefault(l => l.Label == "Katalog")?.Value}");
        var components = string.Join(" | ", _info.Components.Select(c => c.Value));
        Check(components.Contains("WPF-UI 4.", StringComparison.Ordinal) && components.Contains("SharpCompress 0.", StringComparison.Ordinal)
              && components.Contains("ONNX Runtime DirectML 1.", StringComparison.Ordinal) && components.Contains("nur nicht-kommerziell", StringComparison.Ordinal),
            $"Bausteine: {components}");
        var versionInfo = _info.BuildVersionInfo();
        Check(versionInfo.StartsWith($"{LumoraBrand.ProductName} {top}", StringComparison.Ordinal) && !versionInfo.Contains('@'), "Versionsinfo ohne E-Mail-Adresse");

        // Info-Kopf: großes Symbol + Schriftzug, Vorgänger nennt den früheren Namen
        var infoMark = (_settingsPage.FindName("InfoView") as InfoSection)?.FindName("Wordmark") as LumoraWordmark;
        Check(_info.AppName == LumoraBrand.ProductName && infoMark is { IsLoaded: true, ActualWidth: > 60 },
            $"Info-Kopf „{_info.AppName}“ mit Schriftzug ({infoMark?.ActualWidth:N0} px)");
        Check(_info.Development.Any(l => l.Label == "Vorgänger" && l.Value.Contains(LumoraBrand.FormerProductName, StringComparison.Ordinal)),
            $"Info › Vorgänger nennt „{LumoraBrand.FormerProductName}“");

        _settings.IsThemeLight = true;
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await WaitUntilAsync(() => WordmarkStartsWith(infoMark, LumoraPalette.Light), ShortWait, "Schriftzug hell").ConfigureAwait(true);
        await SnapshotAsync("30-einstellungen-info-hell").ConfigureAwait(true);
        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await WaitUntilAsync(() => WordmarkStartsWith(infoMark, LumoraPalette.Dark), ShortWait, "Schriftzug dunkel").ConfigureAwait(true);
        Check(WordmarkStartsWith(infoMark, LumoraPalette.Dark), "Schriftzug folgt dem dunklen Farbschema");
        await SnapshotAsync("31-einstellungen-info-dunkel").ConfigureAwait(true);
        await OpenSettingsSectionAsync(NavigationTags.Library, "Bibliotheken & Eingänge").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        await SnapshotAsync("32-einstellungen-bibliotheken-dunkel").ConfigureAwait(true);
        _settings.IsThemeLight = true;
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        await SnapshotAsync("33-einstellungen-bibliotheken-hell").ConfigureAwait(true);
        _settings.IsThemeSystem = true;

        // (5) Von außen auf einen Reiter (wie „Tags verwalten…“ im Galerie-Menü): erst Galerie, dann Tags
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie").ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Tags);
        await WaitUntilAsync(() => IsOnSettingsSection(NavigationTags.Tags), ShortWait, "„Tags verwalten…“ → Einstellungen › Tags").ConfigureAwait(true);
        Check(IsOnSettingsSection(NavigationTags.Tags), "„Tags verwalten…“ öffnet Einstellungen › Tags");

        // (6) Seite „Ordner“: „Eingang hinzufügen…“ im Kopf. Nur verfügbar prüfen — der Ablauf öffnet die echte
        // Ordnerauswahl der Bibliotheksverwaltung (IDialogService); er ist in NavigationAndSettingsTabsTests mit Fakes abgedeckt.
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_folders.IsLoading, ShortWait, "Ordner-Seite").ConfigureAwait(true);
        Check(_folders.CanShowAddInbox && _folders.AddInboxCommand.CanExecute(null), "„Eingang hinzufügen…“ auf der Ordner-Seite verfügbar");
        await SnapshotAsync("34-ordner-eingang-hinzufuegen").ConfigureAwait(true);

        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "zurück zur Galerie").ConfigureAwait(true);
    }

    /// <summary>Schriftzug zeigt den Verlauf der Palette (erste Farbe = Indigo des Farbschemas).</summary>
    private static bool WordmarkStartsWith(LumoraWordmark? mark, LumoraPalette palette)
        => mark?.WordmarkBrush is System.Windows.Media.LinearGradientBrush { GradientStops.Count: > 0 } brush
           && brush.GradientStops[0].Color == palette.Indigo;
}
