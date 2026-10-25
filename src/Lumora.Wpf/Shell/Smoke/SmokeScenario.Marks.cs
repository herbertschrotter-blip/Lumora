using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Lumora.Gallery.Controls;
using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Marks;
using Lumora.Settings;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Tag- und Kategorie-Symbole“ (Mockup 16, 0.4.35) — nur in der Kopie der Testbibliothek: zwei Fotos
/// bekommen ein Tag, eines zusätzlich einen Kategorie-Wert → Marken auf den Kacheln (Tag mit Anzahl/Punkten, Kategorie),
/// Ordnerbaum zeigt die Zahl der Fotos mit Tags/Kategorien direkt im Ordner, Lightbox-Leiste nennt die Namen; Schalter in
/// Einstellungen › Darstellung aus → alles weg (Settings-Tabelle „false“), wieder an. Screenshots hell/dunkel. Aufräumen:
/// Tag und Kategorie gelöscht (Zuordnungen per Kaskade), Schalter an.
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string MarksTagName = "Smoke-Symbol";
    private const string MarksCategoryName = "Smoke-Symbolkategorie";
    private const string MarksValueName = "Wert";

    private MarkSettingsState? _markSettingsState;

    private void InitMarksSmoke(IServiceProvider services) => _markSettingsState = services.GetService<MarkSettingsState>();

    private async Task TagCategoryMarksAsync()
    {
        Check(_markSettingsState is not null, "Zustand „Symbole für Tags und Kategorien“ registriert");
        var state = _markSettingsState!;
        await state.Loaded.ConfigureAwait(true);
        Check(state.ShowOnTiles && state.ShowInLightbox && state.ShowInFolderTree, "Schalter „Symbole für Tags und Kategorien“ standardmäßig an");
        _marksTagId = null;
        _marksCategoryId = null;
        var failed = true;
        try
        {
            await TagCategoryMarksCoreAsync(state).ConfigureAwait(true);
            failed = false;
        }
        finally
        {
            state.ShowOnTiles = true;
            state.ShowInLightbox = true;
            state.ShowInFolderTree = true;
            await state.LastSave.ConfigureAwait(true);
            if (_marksTagId is { } t)
            {
                await _tags.DeleteAsync(t).ConfigureAwait(true);
            }

            if (_marksCategoryId is { } c)
            {
                await _categories.DeleteAsync(c).ConfigureAwait(true);
            }

            _settings.IsThemeSystem = true;
            _folders.SelectWholeLibraryCommand.Execute(null);
            _navigation.NavigateTo(NavigationTags.Gallery);
            var left = await DbScalarAsync<long>(
                "SELECT (SELECT COUNT(*) FROM Tags WHERE Name = $t) + (SELECT COUNT(*) FROM Categories WHERE Name = $c)",
                ("$t", MarksTagName), ("$c", MarksCategoryName)).ConfigureAwait(true);
            if (!failed)
            {
                Check(left == 0, $"Aufgeräumt: Tag und Kategorie gelöscht ({left} übrig)"); // nach einem Fehler bleibt dessen Meldung stehen
            }
        }
    }

    private int? _marksTagId;
    private int? _marksCategoryId;

    /// <summary>Kachel der aktuellen Seite (nach einem Neuladen können es neue Instanzen sein).</summary>
    private GalleryItem? PageItem(int id) => _gallery.CurrentPage.FirstOrDefault(i => i.Id == id);

    private async Task TagCategoryMarksCoreAsync(MarkSettingsState state)
    {
        // (1) Galerie mit allen Dateien, Kachelansicht; zwei Fotos aus demselben Ordner wählen
        _folders.SelectWholeLibraryCommand.Execute(null);
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount, ShortWait, "Galerie mit allen Dateien").ConfigureAwait(true);
        if (_gallery.ViewMode != GalleryViewMode.Grid)
        {
            _gallery.ViewMode = GalleryViewMode.Grid;
        }

        var pair = _gallery.CurrentPage
            .Where(i => !i.IsVideo && !i.IsArchive && !i.Missing && !i.IsInbox)
            .GroupBy(i => i.FolderId)
            .FirstOrDefault(g => g.Count() >= 2)?
            .Take(2)
            .ToList();
        Check(pair is { Count: 2 }, "Zwei Fotos aus demselben Ordner gefunden");
        var (firstId, secondId) = (pair![0].Id, pair[1].Id);
        var folderPath = pair[0].FolderPath;

        // (2) Tag an beiden, Kategorie-Wert am ersten
        var tag = await _tags.CreateAsync(MarksTagName).ConfigureAwait(true);
        _marksTagId = tag.Id;
        var category = await _categories.CreateAsync(MarksCategoryName, [MarksValueName]).ConfigureAwait(true);
        _marksCategoryId = category.Id;
        // Die Galerie lädt Tag-/Kategorie-Stammdaten nach der Nachricht im Hintergrund — erst danach zuweisen
        await WaitUntilAsync(
            () => _gallery.TagFilterEntries.Any(e => e.Tag.Id == tag.Id) && _gallery.CategoryColumns.Any(c => c.Id == category.Id),
            ShortWait,
            "Galerie kennt Tag und Kategorie").ConfigureAwait(true);
        var assigned = await _tags.AssignAsync([firstId, secondId], [tag.Id]).ConfigureAwait(true);
        var set = await _categories.SetValueAsync([firstId], category.Id, category.Values[0].Id).ConfigureAwait(true);
        Check(assigned == 2 && set == 1, $"Zugewiesen: Tag an 2 Fotos ({assigned}), Kategorie-Wert an 1 ({set})");
        var label = $"{MarksCategoryName}: {MarksValueName}";
        await WaitUntilAsync(
            () => PageItem(firstId) is { } a && PageItem(secondId) is { } b && a.HasTag(tag.Id) && b.HasTag(tag.Id) && a.Categories.Labels.Contains(label),
            ShortWait,
            "Kacheln kennen Tag und Kategorie (gebündelt je Seite)").ConfigureAwait(true);

        // (3) Marken auf den Kacheln: Tag-Marke an beiden, Kategorie-Marke nur am ersten
        var firstMarks = await TileMarksOfAsync(firstId).ConfigureAwait(true);
        var secondMarks = await TileMarksOfAsync(secondId).ConfigureAwait(true);
        Check(firstMarks.IsVisible && firstMarks.HasTagMark && firstMarks.HasCategoryMark && firstMarks.VisibleDots >= 1,
            $"Kachel 1: Tag-Marke ({firstMarks.VisibleDots} Punkt(e), Anzahl „{firstMarks.TagCountText}“) und Kategorie-Marke");
        Check(secondMarks.IsVisible && secondMarks.HasTagMark && !secondMarks.HasCategoryMark, "Kachel 2: nur Tag-Marke");
        var tip = MarkToolTips.Texts(MarkToolTips.Build(firstMarks.BuildToolTipContent())).ToList();
        Check(tip.Contains(MarksTagName) && tip.Contains(label), $"Tooltip nennt die Namen: {string.Join(", ", tip)}");

        // (4) Ordnerbaum: Zahl der Fotos mit Tags/Kategorien direkt im Ordner, Ordner gewählt → Galerie rechts mit Marken
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Folders && !_folders.IsLoading && FindFolderNode(folderPath)?.Marks is { TaggedFiles: >= 2, CategorizedFiles: >= 1 },
            ShortWait,
            "Ordnerbaum zeigt die Zahlen").ConfigureAwait(true);
        var node = FindFolderNode(folderPath)!;
        var folderTip = node.Marks!.BuildToolTipContent();
        Check(folderTip.TagsHeader.StartsWith($"Tags · {node.Marks.TaggedFiles} von {node.FileCount} ", StringComparison.Ordinal) && folderTip.Tags.Any(c => c.Name == MarksTagName),
            $"Ordner „{node.Name}“: {node.Marks.TaggedFiles} mit Tags, {node.Marks.CategorizedFiles} mit Kategorien — „{folderTip.TagsHeader}“, „{folderTip.CategoriesHeader}“");
        node.IsSelected = true;
        await WaitUntilAsync(() => !_gallery.IsLoading && PageItem(firstId) is not null, ShortWait, "Ordner in der Galerie").ConfigureAwait(true);
        await TileMarksOfAsync(firstId).ConfigureAwait(true);

        _settings.IsThemeLight = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Light && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await SnapshotAsync("26-symbole-hell").ConfigureAwait(true);
        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("26-symbole-dunkel").ConfigureAwait(true);

        // (5) Lightbox-Leiste: Tag-Chip und Kategorie mit Namen, „+ Tag / Kategorie“
        var item = PageItem(firstId)!;
        _gallery.OpenInLightboxCommand.Execute(item);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().First();
        var vm = window.ViewModel;
        await WaitUntilAsync(
            () => vm.CurrentItem?.FileId == firstId && vm.CurrentTags.Any(t => t.Id == tag.Id) && vm.CurrentCategoryLabels.Contains(label) && window.MarksBar.IsVisible,
            ShortWait,
            "Lightbox-Leiste mit Tag und Kategorie").ConfigureAwait(true);
        Check(vm.ShowMarksBar && !vm.ShowTitleMarks && vm.ShowMarksSeparator, $"Lightbox-Leiste: Tags „{string.Join(", ", vm.CurrentTags.Select(t => t.Name))}“ | „{string.Join(", ", vm.CurrentCategoryLabels)}“");
        await SnapshotAsync("26-symbole-lightbox", window).ConfigureAwait(true);

        // (6) Schalter aus → Leiste, Kachel-Marken und Ordner-Zahlen weg; gespeichert in der Settings-Tabelle
        state.ShowInLightbox = false;
        await WaitUntilAsync(() => !window.MarksBar.IsVisible && vm.ShowTitleMarks, ShortWait, "Lightbox-Leiste ausgeblendet").ConfigureAwait(true);
        vm.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        state.ShowOnTiles = false;
        state.ShowInFolderTree = false;
        await state.LastSave.ConfigureAwait(true);
        var tileMarks = await TileMarksOfAsync(firstId, expectVisible: false).ConfigureAwait(true);
        await WaitUntilAsync(() => FindFolderNode(folderPath)?.Marks is null, ShortWait, "Ordner-Zahlen ausgeblendet").ConfigureAwait(true);
        var stored = await DbRowsAsync("SELECT Key, Value FROM Settings WHERE Key LIKE 'ui.marks.%' ORDER BY Key").ConfigureAwait(true);
        Check(!tileMarks.IsVisible && stored.Count == 3 && stored.All(r => r.EndsWith("| false", StringComparison.Ordinal)),
            $"Schalter aus: Kachel-Marke weg, Ordner-Zahlen weg, Settings {string.Join("; ", stored)}");

        state.ShowOnTiles = true;
        state.ShowInFolderTree = true;
        state.ShowInLightbox = true;
        await state.LastSave.ConfigureAwait(true);
        await TileMarksOfAsync(firstId).ConfigureAwait(true);
        await WaitUntilAsync(() => FindFolderNode(folderPath)?.Marks is { TaggedFiles: >= 2 }, ShortWait, "Ordner-Zahlen wieder da").ConfigureAwait(true);
        stored = await DbRowsAsync("SELECT Key, Value FROM Settings WHERE Key LIKE 'ui.marks.%' ORDER BY Key").ConfigureAwait(true);
        Check(stored.All(r => r.EndsWith("| true", StringComparison.Ordinal)), $"Schalter wieder an: {string.Join("; ", stored)}");
    }

    /// <summary>Marke der Kachel in der SICHTBAREN Galerie (Galerie- oder Ordner-Seite); wartet auf den gewünschten Zustand.</summary>
    private async Task<TileMarksControl> TileMarksOfAsync(int fileId, bool expectVisible = true)
    {
        TileMarksControl? marks = null;
        await WaitUntilAsync(
            () =>
            {
                var view = FindVisibleGalleryView(Application.Current.MainWindow);
                var list = view is null ? null : FindVisualChild<GalleryListBox>(view);
                if (list is null || PageItem(fileId) is not { } item)
                {
                    return false;
                }

                list.ScrollIntoView(item);
                list.UpdateLayout();
                marks = list.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container ? FindVisualChild<TileMarksControl>(container) : null;
                return marks is not null && marks.IsVisible == expectVisible;
            },
            ShortWait,
            expectVisible ? "Kachel-Marke sichtbar" : "Kachel-Marke ausgeblendet").ConfigureAwait(true);
        return marks!;
    }
}
