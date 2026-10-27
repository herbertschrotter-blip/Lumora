using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Messaging;

namespace Lumora.Shell.Smoke;

/// <summary>Rauchtest Ansicht „Ordner“ (Mockup 21): Unterordner als Zeilen mit erstem und letztem Foto außen.</summary>
internal sealed partial class SmokeScenario
{
    private async Task FolderRowsAsync()
    {
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitForGalleryAsync("Galerie für die Ansicht Ordner").ConfigureAwait(true);

        var yearId = await FolderIdAsync(Path.Combine(_root, "2024")).ConfigureAwait(true);
        int CountIn(params string[] parts) => Directory.EnumerateFiles(Path.Combine([_root, .. parts]))
            .Count(f => _formats.IsSupported(Path.GetExtension(f)));
        var expected = new[] { ("Familie", CountIn("2024", "Familie")), ("Urlaub Italien", CountIn("2024", "Urlaub Italien")) };
        var total = expected.Sum(e => e.Item2);

        _messenger.Send(new FolderSelectedMessage(_libraryId, yearId, true));
        await WaitUntilAsync(() => _gallery.FolderId == yearId && !_gallery.IsLoading && _gallery.TotalCount == total, ShortWait,
            $"Ordner 2024 ({total} Dateien)").ConfigureAwait(true);

        // (1) Umschalten: letzter Eintrag im Segment, Zeilen statt Seite
        Check(_gallery.ViewModeOptions[^1] is { Mode: GalleryViewMode.Folders, Label: "Ordner" },
            $"Ansicht-Segment: {string.Join(" | ", _gallery.ViewModeOptions.Select(o => o.Label))}");
        _gallery.ViewMode = GalleryViewMode.Folders;
        await WaitUntilAsync(
            () => _gallery.ShowFolderRows && !_gallery.IsLoading && _gallery.FolderRows.Count == expected.Length
                  && _gallery.FolderRows.All(r => r.Tiles.Count > 0),
            ShortWait,
            "Ordner-Zeilen aufgebaut").ConfigureAwait(true);
        Check(!_gallery.ShowGrid && !_gallery.ShowCollage && !_gallery.ShowList && _gallery.IsTileSizeAdjustable,
            "Zeilen sichtbar, Kacheln ausgeblendet, Regler = Zeilenhöhe");

        // (2) Je Zeile: richtiger Ordner, Anzahl, erstes und letztes außen, Breite randvoll oder alle gezeigt
        var names = _gallery.FolderRows.Select(r => (r.Name, r.TotalCount)).OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();
        Check(names.SequenceEqual(expected), $"Zeilen: {string.Join(", ", names.Select(n => $"{n.Name} ({n.TotalCount})"))}");
        foreach (var row in _gallery.FolderRows)
        {
            var width = row.Tiles.Sum(t => t.Width) + 4 * (row.Tiles.Count - 1);
            Check(row.Tiles[0].IsFirst && row.Tiles[^1].IsLast, $"{row.Name}: erstes und letztes außen");
            Check(row.Tiles.Count == row.TotalCount ? width <= _gallery.FolderRowsWidth + 1 : Math.Abs(width - _gallery.FolderRowsWidth) <= row.Tiles.Count + 1,
                $"{row.Name}: {row.Tiles.Count} von {row.TotalCount} Fotos, Breite {width:N0} von {_gallery.FolderRowsWidth:N0} DIP");
        }

        await WaitUntilAsync(() => _gallery.FolderRows.SelectMany(r => r.Tiles).All(t => t.Item.Thumbnail is not null), ShortWait,
            "Vorschaubilder der Zeilen").ConfigureAwait(true);
        await SnapshotAsync("21-galerie-ordner-zeilen").ConfigureAwait(true);

        // (3) Neu mischen: erstes und letztes bleiben
        var edges = _gallery.FolderRows.Select(r => (r.Tiles[0].Item.Id, r.Tiles[^1].Item.Id)).ToArray();
        await _gallery.ShuffleFolderRowsCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(_gallery.FolderRows.Select(r => (r.Tiles[0].Item.Id, r.Tiles[^1].Item.Id)).SequenceEqual(edges), "Neu mischen: erstes und letztes bleiben");
        await Task.Delay(400).ConfigureAwait(true); // Einstellungen werden im Hintergrund geschrieben
        var storedMode = await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = $k", ("$k", GalleryViewSettingsStore.ViewModeKey)).ConfigureAwait(true);
        var seed = await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = $k", ("$k", GalleryViewModel.FolderRowsSeedKey)).ConfigureAwait(true);
        Check(storedMode == nameof(GalleryViewMode.Folders) && !string.IsNullOrEmpty(seed), $"Gespeichert: Ansicht {storedMode}, Startwert {seed}");

        // (4) Name öffnet den Ordner; ohne Unterordner zeigt die Ansicht Kacheln
        var familie = _gallery.FolderRows.Single(r => r.Name == "Familie");
        await _gallery.OpenFolderRowCommand.ExecuteAsync(familie).ConfigureAwait(true);
        await WaitUntilAsync(() => _gallery.FolderName == "Familie" && !_gallery.IsLoading && !_gallery.ShowFolderRows && _gallery.ShowGrid,
            ShortWait, "Ordner Familie geöffnet → Kacheln").ConfigureAwait(true);
        Check(_gallery.TotalCount == expected[0].Item2 && _gallery.FolderRows.Count == 0, $"Familie: {_gallery.TotalCount} Fotos in Kacheln");

        _gallery.ViewMode = GalleryViewMode.Grid;
        await _gallery.ClearFolderFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Ordnerfilter nach der Ansicht Ordner aufgehoben").ConfigureAwait(true);
    }
}
