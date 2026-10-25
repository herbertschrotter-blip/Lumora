using System.ComponentModel;
using System.Linq;
using System.Windows.Controls;
using Lumora.Libraries;
using Lumora.Settings.CategoryManagement;
using Lumora.Settings.Info;
using Lumora.Settings.Sections;
using Lumora.Settings.TagManagement;
using Lumora.Shell;

namespace Lumora.Settings;

/// <summary>
/// Seite „Einstellungen“ mit Reitern (ADR-0042). Bettet die bisherigen Seiten Bibliothek, Tags und Kategorien als Reiter
/// ein (Singletons aus DI, sie laden sich beim Anzeigen ihres Reiters selbst neu), hängt die Ordnerüberwachung unter die
/// Bibliotheksliste und hält den gewählten Reiter mit <see cref="SettingsSectionState"/> abgeglichen. Lädt Pfade,
/// Cache-Größe und Status des <see cref="SettingsViewModel"/> bei jedem Anzeigen neu.
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly SettingsSectionState _sections;

    public SettingsPage(
        SettingsViewModel viewModel,
        SettingsSectionState sections,
        LibraryPage library,
        TagManagementPage tags,
        CategoryManagementPage categories,
        InfoViewModel info,
        Shell.HiddenLibraries.HiddenLibrariesViewModel? hiddenLibraries = null,
        Motifs.MotifSettingsViewModel? motifs = null,
        Places.Styles.MapStyleSettingsViewModel? mapStyles = null,
        Stories.StorySettingsViewModel? stories = null,
        Places.TrackSettings.PlacesSettingsViewModel? places = null,
        Stories.Assistant.AssistantSettingsViewModel? assistant = null,
        Shell.HiddenLibraries.SecurityCheck.SecurityCheckViewModel? securityCheck = null,
        Removal.MissingFilesSettingsViewModel? missingFiles = null,
        Exclusions.ScanExclusionsSettingsViewModel? scanExclusions = null,
        Marks.MarkSettingsState? markSettings = null,
        Backup.CatalogBackupSettingsViewModel? catalogBackup = null)
    {
        _sections = sections;
        InitializeComponent();
        DataContext = viewModel;
        MotifsView.DataContext = motifs; // Reiter „Motive“ (ADR-0052); ohne Dienst leer
        AppearanceView.MapSection.DataContext = mapStyles; // Darstellung › Karte (ADR-0055); ohne Dienst ausgeblendet
        AppearanceView.MapSection.Visibility = mapStyles is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        // Darstellung › Symbole für Tags und Kategorien (Mockup 16); ohne Zustand ausgeblendet
        AppearanceView.MarksSection.DataContext = markSettings;
        AppearanceView.MarksSection.Visibility = markSettings is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        StoriesView.DataContext = stories; // Reiter „Storys“ samt Diashow (ADR-0060, ADR-0059); ohne Dienst leer
        PlacesView.DataContext = places; // Reiter „Orte“: Garmin-Aktivitäten, Zeitabgleich (ADR-0062); ohne Dienst leer
        AssistantView.DataContext = assistant; // Reiter „Assistent“ (ADR-0063); ohne Dienst leer
        // Speicher & Wartung › Fehlende Dateien (ADR-0065); ohne Dienst ausgeblendet
        StorageView.MissingFilesCard.DataContext = missingFiles;
        StorageView.MissingFilesCard.Visibility = missingFiles is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        // ADR-0069: Speicher & Wartung › Katalog-Sicherung; ohne Dienst ausgeblendet
        StorageView.CatalogBackupCard.DataContext = catalogBackup;
        StorageView.CatalogBackupCard.Visibility = catalogBackup is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        Loaded += (_, _) => _ = catalogBackup?.RefreshAsync();

        LibraryTab.Content = library;
        TagsTab.Content = tags;
        CategoriesTab.Content = categories;
        InfoView.DataContext = info;
        // Unter der Bibliotheksliste: Ordnerüberwachung und — falls angebunden — Ausschlüsse beim Einlesen (ADR-0066)
        var footer = new StackPanel();
        footer.Children.Add(new SettingsWatchSection { DataContext = viewModel });
        if (scanExclusions is not null)
        {
            footer.Children.Add(new SettingsScanExclusionsSection { DataContext = scanExclusions });
        }

        library.SetFooter(footer);
        Shell.HiddenLibraries.HiddenLibrariesUi.AddSettingsTab(Tabs, hiddenLibraries); // nur mit „_hidden“ (ADR-0049)
        Shell.HiddenLibraries.SecurityCheck.SecurityCheckUi.AddSettingsTab(Tabs, securityCheck); // nur in der versteckten Sitzung (ADR-0064)

        SelectTab(sections.SelectedSection);
        Tabs.SelectionChanged += OnTabsSelectionChanged;
        sections.PropertyChanged += OnSectionsPropertyChanged;

        Loaded += (_, _) => _ = viewModel.LoadCommand.ExecuteAsync(null);
        Loaded += (_, _) => _ = missingFiles?.RefreshAsync(); // ADR-0065
        Loaded += (_, _) => _ = scanExclusions?.RefreshAsync(); // ADR-0066
    }

    /// <summary>Tag des angezeigten Reiters (Rauchtest, Tests).</summary>
    internal string? SelectedTabTag => (Tabs.SelectedItem as TabItem)?.Tag as string;

    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged steigt auch aus Listen und Auswahlfeldern in den Reitern auf — nur die Reiterleiste zählt
        if (!ReferenceEquals(e.OriginalSource, Tabs))
        {
            return;
        }

        if (SelectedTabTag is { } tag && tag != _sections.SelectedSection)
        {
            _sections.SelectedSection = tag;
        }
    }

    private void OnSectionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsSectionState.SelectedSection))
        {
            SelectTab(_sections.SelectedSection);
        }
    }

    private void SelectTab(string section)
    {
        var tab = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Tag, section));
        if (tab is not null && !tab.IsSelected)
        {
            tab.IsSelected = true;
        }
    }
}
