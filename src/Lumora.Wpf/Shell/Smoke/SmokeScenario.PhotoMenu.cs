using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Lumora.Gallery.Controls;
using Lumora.Gallery.ViewModels;
using Lumora.Gallery.Views;
using Lumora.PhotoMenu;
using Lumora.Services.Albums;
using Lumora.Services.Covers;
using Lumora.Services.Export;
using Lumora.Settings;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritte zu ADR-0068 (Mockup 17/18, Issue #25) — nur in der Kopie der Testbibliothek:
/// <list type="number">
/// <item>„Personen-Kacheln und Titelbild“: Person (zwei Gesichter) und Album anlegen, „Als Titelbild für ▸“ setzt Person (das
/// Gesicht in DIESEM Foto), Album und Ordner, Häkchen, wieder automatisch; Personen-Seite mit Chips (27, hell/dunkel).</item>
/// <item>„Rechtsklick-Menü und Ordnerleiste“: echtes Kachel-Menü mit der abgenommenen Gliederung, „Als Titelbild für“ offen
/// (28, hell/dunkel); Leiste per Strg+B zu und auf, Ordner-Chip kombiniert mit „Nur Favoriten“ (29, hell/dunkel).</item>
/// <item>„Fotos nach außen“: Datei-Drop beim Ziehen (nur Kopieren erlaubt), Strg+C in eine abgefangene Zwischenablage (die echte
/// bleibt unberührt). Die Sperre in der versteckten Bibliothek prüft der Schritt „Versteckte Bibliothek“.</item>
/// </list>
/// Aufgeräumt: Person, Gesichter, Album, Ordner-Titelbild, Favorit, Filter, Theme.
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string CoverPersonName = "Smoke-Titelbild-Person";
    private const string CoverAlbumName = "Smoke-Titelbild-Album";

    private ICoverService? _covers;
    private IAlbumService? _coverAlbums;
    private PhotoActions? _photoActions;
    private GalleryFolderPaneState? _folderPane;
    private IServiceProvider? _photoMenuServices;

    private Lumora.Services.Catalog.ICatalogUpdateService CatalogUpdate => _photoMenuServices!.GetRequiredService<Lumora.Services.Catalog.ICatalogUpdateService>();

    private void InitPhotoMenuSmoke(IServiceProvider services)
    {
        _photoMenuServices = services;
        _covers = services.GetService<ICoverService>();
        _coverAlbums = services.GetService<IAlbumService>();
        _photoActions = services.GetService<PhotoActions>();
        _folderPane = services.GetService<GalleryFolderPaneState>();
    }

    /// <summary>Zwei Fotos aus demselben Ordner (Galerie ohne Filter).</summary>
    private async Task<(GalleryItem First, GalleryItem Second)> TwoPhotosAsync()
    {
        _folders.SelectWholeLibraryCommand.Execute(null);
        _navigation.NavigateTo(NavigationTags.Gallery);
        if (_gallery.FilterState.IsActive)
        {
            await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
        }

        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount, ShortWait, "Galerie mit allen Dateien").ConfigureAwait(true);
        if (_gallery.ViewMode != Gallery.ListView.GalleryViewMode.Grid)
        {
            _gallery.ViewMode = Gallery.ListView.GalleryViewMode.Grid;
        }

        // die Seite kann schon geladen sein, bevor die Navigation sie sichtbar gemacht hat
        await WaitUntilAsync(() => FindVisibleGalleryView(Application.Current.MainWindow) is not null, ShortWait, "Galerie sichtbar").ConfigureAwait(true);
        var pair = _gallery.CurrentPage
            .Where(i => i.MediaType == Domain.Enums.MediaType.Image && !i.Missing && !i.IsInbox && System.IO.Path.GetExtension(i.Name).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.FolderId)
            .FirstOrDefault(g => g.Count() >= 2)?
            .Take(2)
            .ToList();
        Check(pair is { Count: 2 }, "Zwei JPEG-Fotos aus demselben Ordner gefunden");
        return (pair![0], pair[1]);
    }

    private async Task<int> InsertConfirmedFaceAsync(int fileId, int personId, float quality)
        => await DbScalarAsync<int>("""
            INSERT INTO Faces (FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality, Embedding, ModelVersion, PersonId, State, CreatedAtUtc)
            VALUES ($f, 0.4, 0.3, 0.2, 0.25, zeroblob(40), 0.9, 120, 200, $q, zeroblob(2048), 'smoke', $p, 2, 0);
            SELECT last_insert_rowid();
            """, ("$f", fileId), ("$p", personId), ("$q", (double)quality)).ConfigureAwait(true);

    // ---- (1) Personen-Kacheln und Titelbild ---------------------------------------------------------------------------

    private async Task CoversAndPersonTilesAsync()
    {
        Check(_covers is not null && _coverAlbums is not null && _personsPage is not null, "Titelbild-Dienst, Alben und Personen-Seite registriert");
        int? personId = null;
        int? albumId = null;
        int? folderId = null;
        try
        {
            var (first, second) = await TwoPhotosAsync().ConfigureAwait(true);
            folderId = first.FolderId;
            personId = await DbScalarAsync<int>(
                "INSERT INTO Persons (Name, FirstName, CreatedAtUtc, CoverFaceId, IsHidden, GroupId) VALUES ($n, $n, 1, NULL, 0, (SELECT GroupId FROM Libraries WHERE Id = $lib)); SELECT last_insert_rowid();",
                ("$n", CoverPersonName), ("$lib", _libraryId)).ConfigureAwait(true);
            await InsertConfirmedFaceAsync(first.Id, personId.Value, 0.95f).ConfigureAwait(true);
            var faceInSecond = await InsertConfirmedFaceAsync(second.Id, personId.Value, 0.60f).ConfigureAwait(true);
            var album = await _coverAlbums!.CreateAsync(CoverAlbumName).ConfigureAwait(true);
            albumId = album.Id;
            await _coverAlbums.AddFilesAsync(album.Id, [first.Id, second.Id]).ConfigureAwait(true);

            // „Als Titelbild für ▸“ zeigt nur Passendes und setzt Person (Gesicht in diesem Foto), Album, Ordner
            var targets = await _covers!.GetTargetsAsync(second.Id).ConfigureAwait(true);
            var person = targets.SingleOrDefault(t => t.Kind == CoverTargetKind.Person && t.Id == personId);
            var albumTarget = targets.SingleOrDefault(t => t.Kind == CoverTargetKind.Album && t.Id == album.Id);
            var folder = targets.SingleOrDefault(t => t.Kind == CoverTargetKind.Folder);
            Check(person is { IsCurrent: false, IsManual: false } && person.FaceId == faceInSecond && albumTarget is not null && folder?.Id == first.FolderId,
                $"Ziele des Fotos: {string.Join(", ", targets.Select(t => $"{PhotoActions.KindText(t.Kind)} „{t.Name}“{(t.IsCurrent ? " ✓" : string.Empty)}"))}");
            await _covers.SetAsync(person!, second.Id).ConfigureAwait(true);
            await _covers.SetAsync(albumTarget!, second.Id).ConfigureAwait(true);
            await _covers.SetAsync(folder!, second.Id).ConfigureAwait(true);
            var personCover = await DbScalarAsync<long>("SELECT COALESCE(CoverFaceId, 0) FROM Persons WHERE Id = $p", ("$p", personId.Value)).ConfigureAwait(true);
            var albumCover = (await _coverAlbums.GetAsync(album.Id).ConfigureAwait(true))?.Cover?.FileId;
            var folderCover = await _covers.GetFolderCoverAsync(first.FolderId).ConfigureAwait(true);
            Check(personCover == faceInSecond && albumCover == second.Id && folderCover is { IsManual: true } && folderCover.FileId == second.Id,
                "Titelbild gesetzt: Person = Gesicht im Foto, Album und Ordner = dieses Foto");
            var after = await _covers.GetTargetsAsync(second.Id).ConfigureAwait(true);
            Check(after.Where(t => t.Kind != CoverTargetKind.Story).All(t => t.IsCurrent && t.IsManual), "Häkchen bei Person, Album und Ordner");

            // Personen-Seite: Kachel mit Chips, „automatisch gewählt“ erst nach dem Zurücksetzen
            await _covers.ResetAsync(CoverTargetKind.Person, personId.Value).ConfigureAwait(true);
            _navigation.NavigateTo(NavigationTags.Persons);
            await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Persons, ShortWait, "Personen-Seite").ConfigureAwait(true);
            await _personsPage!.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitUntilAsync(() => _personsPage.Persons.Any(p => p.PersonId == personId), ShortWait, "Kachel der Person").ConfigureAwait(true);
            var tile = _personsPage.Persons.First(p => p.PersonId == personId);
            Check(tile.IsCoverAutomatic && tile.ConfirmedText == "2" && tile.TileToolTip.Contains("Titelbild: automatisch", StringComparison.Ordinal),
                $"Kachel: „{tile.Name}“, Chip ✓ {tile.ConfirmedText}, automatisch gewählt; Titelbild = Gesicht {tile.ImageFaceId}");
            Check(tile.ImageFaceId != faceInSecond, "automatische Wahl nimmt das bessere Gesicht");
            _settings.IsThemeLight = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Light && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("27-personen-kacheln-hell").ConfigureAwait(true);
            _settings.IsThemeDark = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
            await SnapshotAsync("27-personen-kacheln-dunkel").ConfigureAwait(true);

            // zurück auf automatisch
            await _covers.ResetAsync(CoverTargetKind.Album, album.Id).ConfigureAwait(true);
            await _covers.ResetAsync(CoverTargetKind.Folder, first.FolderId).ConfigureAwait(true);
            var manualLeft = await DbScalarAsync<long>(
                "SELECT (SELECT COUNT(*) FROM Persons WHERE Id = $p AND CoverFaceId IS NOT NULL) + (SELECT COUNT(*) FROM Albums WHERE Id = $a AND CoverFileId IS NOT NULL) + (SELECT COUNT(*) FROM Folders WHERE Id = $d AND CoverFileId IS NOT NULL)",
                ("$p", personId.Value), ("$a", album.Id), ("$d", first.FolderId)).ConfigureAwait(true);
            Check(manualLeft == 0 && !(await _covers.GetFolderCoverAsync(first.FolderId).ConfigureAwait(true)).IsManual, "Wieder automatisch: Person, Album, Ordner");
        }
        finally
        {
            _settings.IsThemeSystem = true;
            if (albumId is { } a)
            {
                await _coverAlbums!.DeleteAsync(a).ConfigureAwait(true);
            }

            if (personId is { } p)
            {
                await DbScalarAsync<long>("DELETE FROM Faces WHERE PersonId = $p; DELETE FROM Persons WHERE Id = $p; SELECT changes();", ("$p", p)).ConfigureAwait(true);
            }

            if (folderId is { } d)
            {
                await DbScalarAsync<long>("UPDATE Folders SET CoverFileId = NULL WHERE Id = $d; SELECT changes();", ("$d", d)).ConfigureAwait(true);
            }

            _navigation.NavigateTo(NavigationTags.Gallery);
        }
    }

    // ---- (2) Rechtsklick-Menü und Ordnerleiste --------------------------------------------------------------------------

    private async Task PhotoMenuAndFolderPaneAsync()
    {
        Check(_photoActions is not null && _folderPane is not null, "Menü-Aktionen und Ordnerleiste registriert");
        GalleryItem? favorite = null;
        ContextMenu? menu = null;
        try
        {
            var (first, _) = await TwoPhotosAsync().ConfigureAwait(true);
            var view = FindVisibleGalleryView(Application.Current.MainWindow);
            Check(view is not null, "Galerie sichtbar");
            var list = FindVisualChild<GalleryListBox>(view)!;
            _gallery.ClearSelectionCommand.Execute(null);
            PageItem(first.Id)!.IsSelected = true;
            list.ScrollIntoView(PageItem(first.Id)!);
            list.UpdateLayout();
            var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(PageItem(first.Id)!);
            menu = container.ContextMenu;
            Check(menu is not null, "Kachel hat ein Rechtsklick-Menü");
            menu!.PlacementTarget = container;
            menu.IsOpen = true;
            await WaitUntilAsync(() => menu.Items.Count > 5, ShortWait, "Menü gebaut").ConfigureAwait(true);
            var headers = menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();
            var expected = new[] { "Öffnen", "Diashow ab hier", "Bewerten", "Tags", "Kategorien", "Personen", "Alben", "Story", "Als Titelbild für", "Bearbeiten", "Suchen", "Datei", "In den Papierkorb", "Eigenschaften" };
            Check(expected.All(headers.Contains) && headers.IndexOf("Bewerten") < headers.IndexOf("Bearbeiten") && headers.IndexOf("Datei") < headers.IndexOf("Eigenschaften"),
                $"Gliederung: {string.Join(" · ", headers)}");
            var cover = menu.Items.OfType<MenuItem>().First(i => (string)i.Header == "Als Titelbild für");
            await PhotoContextMenuBuilder.FillSubmenuAsync(cover).ConfigureAwait(true);
            Check(cover.Items.OfType<MenuItem>().Any(i => (string)i.Header == "Ordner"), $"„Als Titelbild für ▸“: {string.Join(", ", cover.Items.OfType<MenuItem>().Select(i => i.Header))}");
            cover.IsSubmenuOpen = true;
            await Task.Delay(250).ConfigureAwait(true);
            _settings.IsThemeLight = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Light && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("28-kontextmenue-hell").ConfigureAwait(true);
            _settings.IsThemeDark = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
            await SnapshotAsync("28-kontextmenue-dunkel").ConfigureAwait(true);
            cover.IsSubmenuOpen = false;
            menu.IsOpen = false;

            // Ordnerleiste: Strg+B zu und auf (gemerkt), Ordner-Chip + „Nur Favoriten“ kombiniert
            var page = FindAncestorOf<GalleryPage>(view!);
            Check(page is not null && _folderPane!.IsOpen, "Galerie-Seite mit offener Ordnerleiste");
            var toggle = page!.InputBindings.OfType<System.Windows.Input.KeyBinding>().Single(b => b.Key == System.Windows.Input.Key.B);
            toggle.Command.Execute(null);
            await WaitUntilAsync(() => !_folderPane!.IsOpen && ((FrameworkElement)page.FindName("PaneHost")).Visibility != Visibility.Visible, ShortWait, "Leiste zu (Strg+B)").ConfigureAwait(true);
            var stored = await DbScalarAsync<string>("SELECT COALESCE((SELECT Value FROM Settings WHERE Key = $k), '')", ("$k", GalleryFolderPaneState.SettingKey)).ConfigureAwait(true);
            toggle.Command.Execute(null);
            await WaitUntilAsync(() => _folderPane!.IsOpen && ((FrameworkElement)page.FindName("PaneHost")).IsVisible, ShortWait, "Leiste wieder auf").ConfigureAwait(true);
            Check(stored == "closed", $"Zustand der Leiste gemerkt („{stored}“)");

            favorite = PageItem(first.Id);
            await CatalogUpdate.SetFavoriteAsync([first.Id], true).ConfigureAwait(true);
            var node = FindFolderNode(first.FolderPath);
            Check(node is not null, "Ordner im Baum");
            node!.IsSelected = true;
            await WaitUntilAsync(() => _gallery.FolderId == first.FolderId && !_gallery.IsLoading && _gallery.FolderBreadcrumb.Count > 0, ShortWait, "Ordner-Chip und Pfad").ConfigureAwait(true);
            var inFolder = _gallery.TotalCount;
            _gallery.FavoritesOnly = true;
            await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.FavoritesOnly && _gallery.TotalCount >= 1 && _gallery.TotalCount < inFolder + 1, ShortWait, "Ordner UND Favoriten").ConfigureAwait(true);
            Check(_gallery.HasFolderFilter && _gallery.FolderChipText.StartsWith("Ordner: ", StringComparison.Ordinal) && _gallery.CurrentPage.Any(i => i.Id == first.Id),
                $"Chip „{_gallery.FolderChipText}“ + Favoriten: {_gallery.TotalCount} von {inFolder}; Pfad {string.Join(" › ", _gallery.FolderBreadcrumb.Select(c => c.Name))}");
            _settings.IsThemeLight = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Light && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("29-ordnerleiste-hell").ConfigureAwait(true);
            _settings.IsThemeDark = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
            await SnapshotAsync("29-ordnerleiste-dunkel").ConfigureAwait(true);

            // Kreuz am Chip → Ordner weg, Favoriten bleiben; Leiste hebt die Auswahl auf
            await _gallery.ClearFolderFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitUntilAsync(() => !_gallery.HasFolderFilter && !_gallery.IsLoading && _folders.SelectionText == "Alle Fotos der Bibliothek", ShortWait, "Chip aufgehoben, Leiste ohne Auswahl").ConfigureAwait(true);
            Check(_gallery.FavoritesOnly, "übrige Filter bleiben stehen");
        }
        finally
        {
            if (menu is not null)
            {
                menu.IsOpen = false;
            }

            if (_folderPane is { IsOpen: false } pane)
            {
                pane.ToggleCommand.Execute(null);
            }

            if (favorite is not null)
            {
                await CatalogUpdate.SetFavoriteAsync([favorite.Id], false).ConfigureAwait(true);
            }

            _gallery.FavoritesOnly = false;
            _folders.SelectWholeLibraryCommand.Execute(null);
            _settings.IsThemeSystem = true;
        }
    }

    // ---- (3) Fotos nach außen ------------------------------------------------------------------------------------------

    private async Task ExternalCopyAsync()
    {
        Check(_photoActions is not null, "Menü-Aktionen registriert");
        var (first, second) = await TwoPhotosAsync().ConfigureAwait(true);
        var view = FindVisibleGalleryView(Application.Current.MainWindow)!;
        _gallery.ClearSelectionCommand.Execute(null);
        PageItem(first.Id)!.IsSelected = true;
        PageItem(second.Id)!.IsSelected = true;

        var files = view.ExternalDragFiles(PageItem(first.Id)!);
        var (data, allowed) = ExternalFileTransfer.CreateDragData([first.Id, second.Id], files);
        var drop = data.GetFileDropList().Cast<string>().ToList();
        Check(files is { Blocked: false } && drop.Count == 2 && drop.Contains(first.SourcePath) && drop.Contains(second.SourcePath),
            $"Datei-Drop mit {drop.Count} Pfaden neben dem internen Format");
        Check(allowed == DragDropEffects.Copy && Albums.GalleryDragData.Read(data)?.Length == 2, "nach außen nur „Kopieren“ erlaubt, intern weiter FileIds");

        DataObject? clipboard = null;
        _photoActions!.ClipboardSetter = d => clipboard = d; // die echte Zwischenablage des Rechners bleibt unberührt
        try
        {
            view.CopySelectionToClipboard();
            Check(clipboard?.GetFileDropList().Count == 2, $"Strg+C: {_gallery.StatusText}");
        }
        finally
        {
            _photoActions.ClipboardSetter = null;
        }

        _gallery.ClearSelectionCommand.Execute(null);
    }

    /// <summary>Im Schritt „Versteckte Bibliothek“: kein Weg nach außen (Ziehen, Strg+C, Export).</summary>
    private async Task ExternalCopyInHiddenSessionAsync(IServiceProvider hidden, GalleryViewModel gallery)
    {
        var actions = hidden.GetService<PhotoActions>();
        var export = hidden.GetService<IPhotoExportService>();
        Check(actions is { IsExternalBlocked: true } && export is { IsBlocked: true }, "versteckt: Weg nach außen gesperrt");
        var candidates = gallery.CurrentPage.Select(i => new ExternalCandidate(i.Id, i.SourcePath, i.Missing, i.MediaType)).ToList();
        var plan = ExternalFileTransfer.Plan(candidates, actions!.IsExternalBlocked);
        var (data, allowed) = ExternalFileTransfer.CreateDragData(candidates.Select(c => c.FileId).ToArray(), plan);
        DataObject? clipboard = null;
        actions.ClipboardSetter = d => clipboard = d;
        try
        {
            var text = actions.CopyToClipboard(candidates);
            Check(!data.GetDataPresent(DataFormats.FileDrop) && allowed == ExternalFileTransfer.InternalEffects && clipboard is null && text == ExternalFileSet.BlockedText,
                $"versteckt: kein Datei-Drop, keine Zwischenablage — „{text}“");
        }
        finally
        {
            actions.ClipboardSetter = null;
        }

        var exported = await export!.ExportAsync(candidates.Select(c => c.FileId).ToList(), System.IO.Path.GetTempPath(), new PhotoExportOptions()).ConfigureAwait(true);
        Check(exported.IsBlocked && exported.Exported == 0, "versteckt: Exportieren gesperrt");
    }

    private static T? FindAncestorOf<T>(DependencyObject start)
        where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}
