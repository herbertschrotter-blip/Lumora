using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Albums;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Albums;
using Lumora.Settings;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Alben“ (ADR-0054), ohne Dialoge (Dienste und öffentliche ViewModel-Methoden): Album anlegen, drei
/// Fotos hinzufügen, Galerie gefiltert (Bedingung „Album …“), Seite „Alben“ mit Kachel; ein Foto aus „Familie“ nach
/// „Urlaub Italien“ verknüpfen → Ordneransicht zeigt es mit Ketten-Kennzeichen, Zähler, „nur echte Dateien“; Original
/// verschieben → Verknüpfung und Album bleiben, Rückgängig; intelligentes Album aus dem Galerie-Filter speichern und
/// öffnen (Filter geladen, gleiche Treffer); Export in einen Temp-Ordner (echte Kopien). Danach aufgeräumt.
/// </summary>
internal sealed partial class SmokeScenario
{
    private AlbumActions? _albumActions;
    private AlbumsViewModel? _albumsPage;

    private void InitAlbumsSmoke(IServiceProvider services)
    {
        _albumActions = services.GetService<AlbumActions>();
        _albumsPage = services.GetService<AlbumsViewModel>();
    }

    private async Task AlbumsAsync()
    {
        Check(_albumActions is not null && _albumsPage is not null, "Alben-Dienste und Seite vorhanden");
        var albums = _albumActions!.Albums;
        var exportDir = Path.Combine(_paths.AppDataRoot, "smoke-album-export");
        AlbumInfo? album = null;
        AlbumInfo? smart = null;
        int? linkedFile = null;
        var urlaubFolder = 0;
        try
        {
            // (1) Album anlegen + 3 Fotos hinzufügen (2 aus „Familie“, 1 aus „Urlaub Italien“)
            var familiePath = Path.Combine(_root, "2024", "Familie");
            var urlaubPath = Path.Combine(_root, "2024", "Urlaub Italien");
            var familie = await ImageIdsInAsync(familiePath, 2).ConfigureAwait(true);
            var urlaub = await ImageIdsInAsync(urlaubPath, 1).ConfigureAwait(true);
            Check(familie.Length == 2 && urlaub.Length == 1, $"Testfotos gefunden: {familie.Length} in Familie, {urlaub.Length} in Urlaub");
            album = await albums.CreateAsync("Smoke-Album").ConfigureAwait(true);
            var ids = familie.Concat(urlaub).ToArray();
            await _gallery.AddToAlbumAsync(album.Id, album.Name, ids).ConfigureAwait(true);
            var items = await DbScalarAsync<long>("SELECT COUNT(*) FROM AlbumItems WHERE AlbumId = $a", ("$a", album.Id)).ConfigureAwait(true);
            Check(items == 3, $"DB: 3 Fotos im Album verknüpft ({items})");
            Check(File.Exists(Path.Combine(familiePath, Path.GetFileName(await PathOfAsync(familie[0]).ConfigureAwait(true)))), "Foto liegt weiter in seinem Ordner (nicht kopiert/verschoben)");

            // (2) Album öffnen → Galerie gefiltert, Bedingung „Album …“
            _navigation.NavigateTo(NavigationTags.Gallery);
            await _gallery.OpenAlbumAsync(album.Id).ConfigureAwait(true);
            await WaitForGalleryAsync("Galerie mit Album-Filter").ConfigureAwait(true);
            Check(_gallery.HasAlbumFilter && _gallery.TotalCount == 3 && _gallery.CurrentPage.Select(i => i.Id).OrderBy(i => i).SequenceEqual(ids.OrderBy(i => i)),
                $"Galerie zeigt genau die 3 Album-Fotos ({_gallery.TotalCount})");
            Check(_gallery.ActiveConditions.Any(c => c.Kind == GalleryFilterConditionKind.Album && c.Label.Contains("Smoke-Album", StringComparison.Ordinal)),
                $"„Es gilt“: {string.Join(" UND ", _gallery.ActiveConditions.Select(c => c.Label))}");
            Check(_gallery.ShowAlbumFilter && _gallery.AlbumFilterValueText == "Smoke-Album", $"Chip „Album: {_gallery.AlbumFilterValueText}“");
            await SnapshotAsync("50-alben-galerie-gefiltert").ConfigureAwait(true);

            // (3) Seite „Alben“
            _navigation.NavigateTo(NavigationTags.Albums);
            await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Albums, ShortWait, "Seite Alben").ConfigureAwait(true);
            await _albumsPage!.LoadAsync().ConfigureAwait(true);
            var tile = _albumsPage.Albums.FirstOrDefault(a => a.Id == album.Id);
            Check(tile is { Info.FileCount: 3, IsSmart: false }, $"Kachel „{tile?.Name}“: {tile?.CountText}");
            await WaitUntilAsync(() => tile!.Image is not null, ShortWait, "Titelbild der Kachel").ConfigureAwait(true);
            await SnapshotAsync("51-alben-seite").ConfigureAwait(true);

            // (4) Foto aus „Familie“ nach „Urlaub Italien“ verknüpfen → Ordneransicht mit Ketten-Kennzeichen und Zähler
            urlaubFolder = await FolderIdAsync(urlaubPath).ConfigureAwait(true);
            linkedFile = familie[0];
            var realInUrlaub = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE FolderId = $d", ("$d", urlaubFolder)).ConfigureAwait(true);
            var link = await _gallery.LinkToFolderAsync(urlaubFolder, "Urlaub Italien", [linkedFile.Value]).ConfigureAwait(true);
            Check(link is { Linked: 1 }, $"Verknüpft: {link?.Linked}");
            await _gallery.ClearAlbumFilterCommand.ExecuteAsync(null).ConfigureAwait(true); // sonst gälte Album UND Ordner
            _navigation.NavigateTo(NavigationTags.Folders);
            await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery && !_folders.IsLoading && _folders.Roots.Count > 0, ShortWait, "Ordnerbaum").ConfigureAwait(true);
            var urlaubNode = FindNode(urlaubPath);
            Check(urlaubNode is not null, "Knoten „Urlaub Italien“ im Baum");
            urlaubNode!.IsSelected = true;
            await WaitUntilAsync(
                () => _gallery.FolderId == urlaubFolder && !_gallery.IsLoading && _gallery.TotalCount == realInUrlaub + 1 && _gallery.FolderLinkedCount == 1,
                ShortWait,
                $"Ordneransicht: {realInUrlaub} echte + 1 verknüpfte Datei").ConfigureAwait(true);
            await WaitUntilAsync(() => _gallery.CurrentPage.Any(i => i.Id == linkedFile && i.IsLinked), ShortWait, "Ketten-Kennzeichen auf der Kachel").ConfigureAwait(true);
            Check(_gallery.CurrentPage.Count(i => i.IsLinked) == 1, "genau eine Kachel als verknüpft gekennzeichnet");
            Check(_gallery.ShowFolderLinksHeader && _gallery.FolderLinksText.Contains("1 verknüpft", StringComparison.Ordinal), $"Zähler „{_gallery.FolderLinksText}“");
            await SnapshotAsync("52-ordner-verknuepft").ConfigureAwait(true);
            _settings.IsThemeDark = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
            await SnapshotAsync("53-ordner-verknuepft-dunkel").ConfigureAwait(true);
            _navigation.NavigateTo(NavigationTags.Albums);
            await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Albums, ShortWait, "Seite Alben (dunkel)").ConfigureAwait(true);
            await SnapshotAsync("54-alben-seite-dunkel").ConfigureAwait(true);
            _settings.IsThemeSystem = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);
            _navigation.NavigateTo(NavigationTags.Folders);
            await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "zurück zur Ordner-Seite").ConfigureAwait(true);

            _gallery.IncludeFolderLinks = false;
            await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == realInUrlaub && _gallery.CurrentPage.All(i => i.Id != linkedFile), ShortWait, "„nur echte Dateien“").ConfigureAwait(true);
            _gallery.IncludeFolderLinks = true;
            await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == realInUrlaub + 1, ShortWait, "„mit Verknüpfungen“").ConfigureAwait(true);

            // (5) Original verschieben → Verknüpfung und Album bleiben; Rückgängig
            var originalPath = await PathOfAsync(linkedFile.Value).ConfigureAwait(true);
            var moveTarget = Path.Combine(_root, "2025", "Screenshots");
            var moved = await _fileOps.MoveAsync([linkedFile.Value], moveTarget).ConfigureAwait(true);
            Check(moved.Succeeded == 1 && File.Exists(Path.Combine(moveTarget, Path.GetFileName(originalPath))), "Original verschoben");
            var linksAfterMove = await DbScalarAsync<long>("SELECT COUNT(*) FROM FolderLinks WHERE FolderId = $d AND FileId = $f", ("$d", urlaubFolder), ("$f", linkedFile.Value)).ConfigureAwait(true);
            var itemAfterMove = await DbScalarAsync<long>("SELECT COUNT(*) FROM AlbumItems WHERE AlbumId = $a AND FileId = $f", ("$a", album.Id), ("$f", linkedFile.Value)).ConfigureAwait(true);
            Check(linksAfterMove == 1 && itemAfterMove == 1, "Verknüpfung und Album-Eintrag bleiben nach dem Verschieben (FileId stabil)");
            await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.CurrentPage.Any(i => i.Id == linkedFile && i.IsLinked), ShortWait, "verschobenes Original erscheint weiter verknüpft").ConfigureAwait(true);
            var undo = await _fileOps.UndoLastAsync().ConfigureAwait(true);
            Check(undo is { Succeeded: 1 } && File.Exists(originalPath), "Rückgängig: Original wieder in „Familie“");

            // (6) Intelligentes Album aus dem Galerie-Filter (Bilder in „2024“ mit Unterordnern)
            _navigation.NavigateTo(NavigationTags.Gallery);
            var year = Path.Combine(_root, "2024");
            var yearNode = FindNode(year);
            Check(yearNode is not null, "Knoten „2024“");
            _gallery.FilterState = new GalleryFilterState(GalleryMediaKind.Images);
            await _gallery.ClearAlbumFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
            yearNode!.IsSelected = true;
            await WaitUntilAsync(() => _gallery.FolderId == yearNode.Id && !_gallery.IsLoading, ShortWait, "Galerie: Bilder in 2024").ConfigureAwait(true);
            var expected = _gallery.TotalCount;
            var filter = await _gallery.CaptureSmartFilterAsync().ConfigureAwait(true);
            Check(filter is { Media: "Images", IncludeSubfolders: true } && filter.FolderPath is not null, $"Filter erfasst: {string.Join(" UND ", filter.Describe())}");
            smart = await albums.SaveSmartAsync("Smoke-Bilder 2024", filter).ConfigureAwait(true);
            Check(smart is { IsSmart: true } && smart.FileCount == expected, $"Intelligentes Album: {smart.FileCount} Dateien (Galerie {expected})");
            await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
            await _gallery.OpenAlbumAsync(smart.Id).ConfigureAwait(true);
            await WaitForGalleryAsync("intelligentes Album geöffnet").ConfigureAwait(true);
            Check(_gallery.MediaFilter == GalleryMediaKind.Images && _gallery.FolderId == yearNode.Id && _gallery.TotalCount == expected,
                $"Filter geladen: {_gallery.MediaFilter}, Ordner {_gallery.FolderName}, {_gallery.TotalCount} Treffer");
            await SnapshotAsync("55-alben-intelligent-geoeffnet").ConfigureAwait(true);

            // (7) Export in einen Temp-Ordner: echte Kopien
            await _albumsPage.LoadAsync().ConfigureAwait(true);
            var exportTile = _albumsPage.Albums.First(a => a.Id == album.Id);
            var result = await _albumsPage.ExportToAsync(exportTile, exportDir).ConfigureAwait(true);
            var exported = Directory.Exists(exportDir) ? Directory.GetFiles(exportDir).Length : 0;
            Check(result is { Copied: 3, Failed: 0, Canceled: false } && exported == 3, $"Export: {result?.Copied} kopiert, {exported} Dateien im Ziel");
            Check(File.Exists(originalPath), "Export lässt die Originale liegen");
        }
        finally
        {
            if (linkedFile is { } file && urlaubFolder != 0)
            {
                await _albumActions.Links.UnlinkAsync(urlaubFolder, [file]).ConfigureAwait(true);
            }

            foreach (var created in new[] { album, smart })
            {
                if (created is not null)
                {
                    await albums.DeleteAsync(created.Id).ConfigureAwait(true);
                }
            }

            if (Directory.Exists(exportDir))
            {
                Directory.Delete(exportDir, recursive: true);
            }

            await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
            _folders.SelectWholeLibraryCommand.Execute(null);
            await WaitForGalleryAsync("Galerie nach dem Aufräumen").ConfigureAwait(true);
        }

        var left = await DbScalarAsync<long>("SELECT (SELECT COUNT(*) FROM Albums) + (SELECT COUNT(*) FROM FolderLinks)").ConfigureAwait(true);
        Check(left == 0, "aufgeräumt: keine Alben/Verknüpfungen übrig");
    }

    private async Task<int[]> ImageIdsInAsync(string folder, int take)
        => (await DbRowsAsync(
                "SELECT f.Id FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE d.LibraryId = $lib AND d.Path = $p AND f.MediaType = 0 ORDER BY f.Name LIMIT $n",
                ("$lib", _libraryId), ("$p", folder), ("$n", take)).ConfigureAwait(true))
            .Select(r => int.Parse(r, CultureInfo.InvariantCulture)).ToArray();

    private async Task<string> PathOfAsync(int fileId)
        => (await DbRowsAsync("SELECT d.Path, f.Name FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id = $f", ("$f", fileId)).ConfigureAwait(true))
            .Select(r => r.Split(" | ")).Select(p => Path.Combine(p[0], p[1])).Single();

    /// <summary>Knoten des Ordnerbaums über den Pfad (klappt die Vorfahren auf).</summary>
    private Folders.FolderNode? FindNode(string path)
    {
        Folders.FolderNode? Visit(System.Collections.Generic.IEnumerable<Folders.FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder)
                {
                    continue;
                }

                if (string.Equals(node.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }

                if (path.StartsWith(node.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || node.IsLibraryHeader)
                {
                    node.IsExpanded = true;
                    if (Visit(node.Children) is { } hit)
                    {
                        return hit;
                    }
                }
            }

            return null;
        }

        return Visit(_folders.Roots);
    }
}
