using System;
using System.Collections.Generic;

namespace Lumora.Shell;

/// <summary>
/// Tags der Navigationsziele. Seiten der linken Leiste (NavigationViewItem.TargetPageTag in MainWindow.xaml): Galerie (mit
/// Ordnerleiste), Alben, Storys, Personen, Orte, Doppelte, Sortieren, Einstellungen. Die übrigen Tags sind Reiter der Einstellungen (ADR-0042) — der
/// <see cref="INavigationService"/> öffnet dafür die Einstellungen und wählt den Reiter; <see cref="INavigationService.CurrentTag"/>
/// ist dann <see cref="Settings"/>, der Reiter steht in <see cref="SettingsSectionState.SelectedSection"/>.
/// </summary>
public static class NavigationTags
{
    public const string Gallery = "gallery";
    /// <summary>
    /// Früher Seite „Ordner“; seit 0.4.44 (ADR-0068, Mockup 18) öffnet <see cref="INavigationService.NavigateTo"/> damit die
    /// Galerie mit offener Ordnerleiste (<see cref="INavigationService.CurrentTag"/> ist dann <see cref="Gallery"/>).
    /// </summary>
    public const string Folders = "folders";
    public const string Persons = "persons"; // Seite „Personen“ (ADR-0037)
    public const string Places = "places"; // Seite „Orte“ (ADR-0051)
    public const string Duplicates = "duplicates"; // Seite „Doppelte“ (ADR-0053)
    public const string Albums = "albums"; // Seite „Alben“ (ADR-0054)
    public const string Stories = "stories"; // Seite „Storys“ (ADR-0060)
    public const string Sorter = "sorter";
    public const string Settings = "settings";

    // ---- Reiter der Einstellungen (ADR-0042), in Anzeigereihenfolge ----

    /// <summary>Einstellungen › Bibliotheken &amp; Eingänge (bis 0.2.43 eigene Seite „Bibliothek“).</summary>
    public const string Library = "library";

    /// <summary>Einstellungen › Tags (bis 0.2.43 eigene Seite).</summary>
    public const string Tags = "tags";

    /// <summary>Einstellungen › Kategorien (bis 0.2.43 eigene Seite).</summary>
    public const string Categories = "categories";

    public const string SettingsFaces = "settings-faces";

    /// <summary>Einstellungen › Motive (ADR-0052).</summary>
    public const string SettingsMotifs = "settings-motifs";

    /// <summary>Einstellungen › Verbessern (ADR-0071): Modelle, Speicherort, Dateiname, Metadaten, beste Fassung, Grafikkarte.</summary>
    public const string SettingsEnhance = "settings-enhance";
    public const string SettingsArchives = "settings-archives";
    public const string SettingsAppearance = "settings-appearance";

    /// <summary>Einstellungen › Storys (ADR-0060): Story-Arten, Erkennung, Highlights, Diashow.</summary>
    public const string SettingsStories = "settings-stories";

    /// <summary>Einstellungen › Orte (ADR-0062): Garmin-Aktivitäten (GPS-Spuren) und Zeitabgleich.</summary>
    public const string SettingsPlaces = "settings-places";
    /// <summary>Einstellungen › Assistent (ADR-0063): Ollama, Sprachmodell, Grafikkarte, Verlauf, Ausprobieren.</summary>
    public const string SettingsAssistant = "settings-assistant";
    /// <summary>Einstellungen › Modelle &amp; Daten (ADR-0070): Modelle und Datenpakete laden, prüfen, entfernen, Update suchen.</summary>
    public const string SettingsModels = "settings-models";
    public const string SettingsStorage = "settings-storage";
    /// <summary>Einstellungen › Verbundene Apps (ADR-0073, Mockup 11): andere Apps auf diesem PC finden, koppeln, Rechte, trennen.</summary>
    public const string SettingsConnectedApps = "settings-apps";
    public const string SettingsInfo = "settings-info";

    /// <summary>Alle Reiter der Einstellungen in Anzeigereihenfolge.</summary>
    public static IReadOnlyList<string> SettingsSections { get; } =
    [
        Library, Tags, Categories, SettingsFaces, SettingsMotifs, SettingsEnhance /* ADR-0071 */, SettingsArchives, SettingsAppearance, SettingsStories, SettingsAssistant /* ADR-0063 */, SettingsPlaces, SettingsModels /* ADR-0070 */, SettingsStorage, SettingsConnectedApps /* ADR-0073 */, SettingsInfo,
    ];

    /// <summary>
    /// Einstellungen › Versteckte Bibliotheken (ADR-0049) — nur mit Startschalter <c>_hidden</c> vorhanden, deshalb nicht in
    /// <see cref="SettingsSections"/>.
    /// </summary>
    public const string SettingsHiddenLibraries = "settings-hidden";

    /// <summary>Einstellungen › Sicherheits-Check (ADR-0064) — nur in der geöffneten versteckten Sitzung, deshalb nicht in <see cref="SettingsSections"/>.</summary>
    public const string SettingsSecurityCheck = "settings-security";

    /// <summary>true, wenn <paramref name="tag"/> ein Reiter der Einstellungen ist (keine eigene Seite).</summary>
    public static bool IsSettingsSection(string? tag)
    {
        if (string.Equals(tag, SettingsHiddenLibraries, StringComparison.Ordinal) || string.Equals(tag, SettingsSecurityCheck, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var section in SettingsSections)
        {
            if (string.Equals(section, tag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Programmatische Navigation zwischen den Seiten der Shell. Singleton; von jedem Thread aufrufbar
/// (wird auf den UI-Thread gestellt). Aufrufe vor dem Anbinden des Hauptfensters werden gemerkt.
/// </summary>
public interface INavigationService
{
    /// <summary>Tag der aktuell angezeigten Seite oder null, solange noch nicht navigiert wurde (nie ein Reiter-Tag).</summary>
    string? CurrentTag { get; }

    /// <summary>Wird nach jeder Navigation (Klick oder NavigateTo) mit dem Ziel-Tag ausgelöst (UI-Thread).</summary>
    event EventHandler<string>? Navigated;

    /// <summary>
    /// Navigiert zur Seite mit dem Tag (siehe <see cref="NavigationTags"/>); ein Reiter-Tag der Einstellungen öffnet die
    /// Einstellungen auf diesem Reiter.
    /// </summary>
    void NavigateTo(string tag);
}
