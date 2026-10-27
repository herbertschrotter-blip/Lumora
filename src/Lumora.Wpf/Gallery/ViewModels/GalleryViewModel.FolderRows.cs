using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Data;
using Lumora.Gallery.FolderRows;
using Lumora.Gallery.ListView;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Ansicht „Ordner“ (Mockup 21, abgenommen 2026-10-27): Ist ein Ordner mit Unterordnern geöffnet, zeigt jede Zeile einen
/// Unterordner (Set) — erstes und letztes Foto immer außen, dazwischen zufällige, so viele wie passen
/// (<see cref="FolderRowLayout"/>). Fotos direkt im Ordner bekommen eine eigene erste Zeile, Unterordner eines Sets eine
/// eingerückte Zeile (eine Ebene). Der Galerie-Filter gilt in jeder Zeile. Die Zeilenhöhe ist der Größenregler; die Auswahl
/// bleibt bis „Neu mischen“ (Startwert in den Einstellungen) bzw. „Andere Fotos zeigen“ einer Zeile. Ohne Unterordner
/// zeigt die Ansicht wie „Kacheln“.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>Startwert der Zufallsauswahl (bleibt über Neustarts gleich, bis „Neu mischen“).</summary>
    public const string FolderRowsSeedKey = "ui.gallery.folderRowsSeed";

    /// <summary>Höchstens so viele Kandidaten zwischen erstem und letztem Foto je Zeile.</summary>
    internal const int FolderRowMaxCandidates = 48;

    /// <summary>Einrückung der Unterordner eines Sets (DIP), passend zur Vorlage.</summary>
    internal const double FolderRowIndent = 24;

    private readonly Dictionary<int, List<GalleryItem>> _rowItems = new();
    private volatile HashSet<int> _rowIdsSnapshot = new();
    private readonly HashSet<int> _rowThumbnailsRequested = new();
    private int _folderRowsSeed;
    private double _folderRowsWidth;
    private bool _folderRowsRelayoutScheduled;

    /// <summary>Zeilen der Ansicht „Ordner“.</summary>
    public ObservableCollection<FolderRowViewModel> FolderRows { get; } = new();

    /// <summary>Die Ansicht „Ordner“ zeigt gerade Zeilen (Ordner mit Unterordnern geöffnet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowGalleryList), nameof(IsEmpty))]
    private bool _showFolderRows;

    public bool IsFolderRowsView => ViewMode == GalleryViewMode.Folders;

    /// <summary>Breite der Zeilen (von der Ansicht gemeldet); bei Änderung wird neu verteilt.</summary>
    public double FolderRowsWidth
    {
        get => _folderRowsWidth;
        set
        {
            if (Math.Abs(value - _folderRowsWidth) < 1)
            {
                return;
            }

            _folderRowsWidth = value;
            RelayoutFolderRows();
        }
    }

    partial void OnViewModeChanged(GalleryViewMode oldValue, GalleryViewMode newValue)
    {
        OnPropertyChanged(nameof(IsFolderRowsView));
        if (_isInitialized && (oldValue == GalleryViewMode.Folders || newValue == GalleryViewMode.Folders))
        {
            _ = ReloadTotalAndPageAsync(restoreSelection: false); // Zeilen auf- bzw. abbauen
        }
    }

    /// <summary>
    /// Im Modus „Ordner“ mit geöffnetem Ordner: Unterordner als Zeilen zeigen. false = keine Zeilen (anderer Modus, kein
    /// Ordner oder keine Unterordner) — dann lädt die Galerie wie gewohnt.
    /// </summary>
    private async Task<bool> TryShowFolderRowsAsync(GalleryQuery query, CancellationToken ct)
    {
        // „Unterordner einschließen“ aus = nur dieser Ordner → Kacheln (so öffnet auch die Zeile „Fotos in …“ ihre Fotos)
        if (ViewMode != GalleryViewMode.Folders || query.FolderId is not { } folderId || !query.IncludeSubfolders)
        {
            ClearFolderRows();
            return false;
        }

        var subfolders = await _source.GetSubfoldersAsync(_libraryId, folderId, ct);
        if (ct.IsCancellationRequested || subfolders.Count == 0)
        {
            ClearFolderRows();
            return false;
        }

        if (_folderRowsSeed == 0)
        {
            _folderRowsSeed = await LoadFolderRowsSeedAsync();
        }

        ClearItems();
        ClearSelectionState();
        ClearFolderRows();
        if (ShowSubfolders)
        {
            ShowSubfolders = false;
            Subfolders.Clear();
        }

        ShowFolderRows = true;
        var rowQuery = query with { Sort = GallerySort.CapturedDesc, CategorySort = null };

        // Fotos direkt im geöffneten Ordner zuerst
        await AddFolderRowAsync(new FolderRowViewModel(folderId, $"Fotos in „{FolderName}“", null, includeSubfolders: false, level: 0,
            rowQuery with { FolderId = folderId, IncludeSubfolders = false }), ct);
        foreach (var sub in subfolders)
        {
            if (ct.IsCancellationRequested)
            {
                return true;
            }

            await AddFolderRowAsync(new FolderRowViewModel(sub.Id, sub.Name, sub.Path, includeSubfolders: true, level: 0,
                rowQuery with { FolderId = sub.Id, IncludeSubfolders = true }), ct);
            var inner = await _source.GetSubfoldersAsync(_libraryId, sub.Id, ct);
            foreach (var child in inner)
            {
                await AddFolderRowAsync(new FolderRowViewModel(child.Id, child.Name, child.Path, includeSubfolders: true, level: 1,
                    rowQuery with { FolderId = child.Id, IncludeSubfolders = true }), ct);
            }
        }

        var sets = FolderRows.Count(r => r.IncludeSubfolders && !r.IsNested);
        SetLoadStatus($"{sets:N0} Ordner · {TotalCount:N0} {(TotalCount == 1 ? "Foto" : "Fotos")}");
        _logger.LogInformation("Ansicht Ordner: FolderId={FolderId} Zeilen={Rows}", folderId, FolderRows.Count);
        return true;
    }

    /// <summary>Ids der Zeile lesen, Kandidaten wählen und laden; leere Zeilen (Filter) fallen weg.</summary>
    private async Task AddFolderRowAsync(FolderRowViewModel row, CancellationToken ct)
    {
        var newestFirst = await _source.GetFileIdsAsync(row.Query, null, ct);
        if (ct.IsCancellationRequested || newestFirst.Count == 0)
        {
            return;
        }

        row.IdsAscending = newestFirst.Reverse().ToList();
        row.Positions.Clear();
        for (var i = 0; i < row.IdsAscending.Count; i++)
        {
            row.Positions[row.IdsAscending[i]] = i;
        }

        await PickFolderRowAsync(row, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        FolderRows.Add(row);
        RelayoutFolderRow(row);
    }

    /// <summary>Erstes, letztes und zufällige Kandidaten wählen (Startwert + Zeile) und als Items laden.</summary>
    private async Task PickFolderRowAsync(FolderRowViewModel row, CancellationToken ct)
    {
        // fest über Neustarts (HashCode.Combine wäre je Prozess anders)
        var seed = unchecked((((_folderRowsSeed * 31) + row.FolderId) * 31 + (row.IncludeSubfolders ? 1 : 0)) * 31 + row.Salt);
        var pick = FolderRowLayout.Pick(row.IdsAscending, FolderRowMaxCandidates, seed);
        var dtos = await _source.LoadFilesByIdsAsync(pick.All.ToList(), ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var items = dtos.ToDictionary(d => d.Id, d => new GalleryItem(d));
        UnregisterRowItems(row);
        row.First = pick.First is { } f && items.TryGetValue(f, out var first) ? first : null;
        row.Last = pick.Last is { } l && items.TryGetValue(l, out var last) ? last : null;
        row.MiddleRandom.Clear();
        row.MiddleRandom.AddRange(pick.Middle.Where(items.ContainsKey).Select(id => items[id]));
        foreach (var item in row.Candidates)
        {
            RegisterRowItem(item);
        }

        row.DateText = FormatDateRange(row.First?.CaptureDate?.Value?.Local, row.Last?.CaptureDate?.Value?.Local);
        PublishRowIds();
    }

    private void RelayoutFolderRows()
    {
        foreach (var row in FolderRows)
        {
            RelayoutFolderRow(row);
        }
    }

    /// <summary>Fotos der Zeile auf Breite und Zeilenhöhe verteilen; fehlende Vorschaubilder anfordern.</summary>
    private void RelayoutFolderRow(FolderRowViewModel row)
    {
        var width = _folderRowsWidth - (row.IsNested ? FolderRowIndent : 0);
        if (width <= 0)
        {
            return;
        }

        FolderRowCandidate? Candidate(GalleryItem? item)
            => item is null ? null : new FolderRowCandidate(item.Id, row.Positions.GetValueOrDefault(item.Id), item.LayoutAspectRatio);

        var byId = row.Candidates.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var slots = FolderRowLayout.Fit(
            Candidate(row.First),
            Candidate(row.Last),
            row.MiddleRandom.Select(i => Candidate(i)!).ToList(),
            row.TotalCount,
            width,
            TileSize);

        row.Tiles.Clear();
        foreach (var slot in slots)
        {
            row.Tiles.Add(new FolderRowTile(row, byId[slot.Id], Math.Floor(slot.Width), slot.Edge));
        }

        row.InfoText = row.Tiles.Count >= row.TotalCount
            ? $"{row.TotalCount:N0} {(row.TotalCount == 1 ? "Foto" : "Fotos")} · alle gezeigt"
            : $"{row.TotalCount:N0} Fotos · {row.Tiles.Count:N0} gezeigt";
        RequestRowThumbnails(row);
    }

    private void RequestRowThumbnails(FolderRowViewModel row)
    {
        var missing = row.Tiles.Select(t => t.Item).Where(i => i.Thumbnail is null && _rowThumbnailsRequested.Add(i.Id)).ToList();
        if (missing.Count > 0)
        {
            FireAndForget(() => RequestThumbnailsAsync(missing));
        }
    }

    private void RegisterRowItem(GalleryItem item)
    {
        if (!_rowItems.TryGetValue(item.Id, out var list))
        {
            _rowItems[item.Id] = list = new List<GalleryItem>(1);
        }

        list.Add(item);
        item.PropertyChanged += OnRowItemPropertyChanged;
    }

    private void UnregisterRowItems(FolderRowViewModel row)
    {
        foreach (var item in row.Candidates)
        {
            item.PropertyChanged -= OnRowItemPropertyChanged;
            if (_rowItems.TryGetValue(item.Id, out var list) && list.Remove(item) && list.Count == 0)
            {
                _rowItems.Remove(item.Id);
                _rowThumbnailsRequested.Remove(item.Id); // neu gewählte Instanz derselben Datei wieder anfordern
            }
        }
    }

    private void PublishRowIds() => _rowIdsSnapshot = new HashSet<int>(_rowItems.Keys);

    /// <summary>Vorschaubild eines Zeilen-Fotos fertig (UI-Thread, aus <see cref="FlushReadyThumbnails"/>).</summary>
    private void AssignRowThumbnail(int fileId, System.Windows.Media.ImageSource image)
    {
        if (_rowItems.TryGetValue(fileId, out var items))
        {
            foreach (var item in items)
            {
                item.Thumbnail = image;
            }
        }
    }

    /// <summary>Seitenverhältnis erst mit dem Vorschaubild bekannt → Zeilen einmal je Dispatcher-Runde neu verteilen.</summary>
    private void OnRowItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GalleryItem.LayoutAspectRatio) || _folderRowsRelayoutScheduled)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            RelayoutFolderRows();
            return;
        }

        _folderRowsRelayoutScheduled = true;
        _ = dispatcher.InvokeAsync(
            () =>
            {
                _folderRowsRelayoutScheduled = false;
                RelayoutFolderRows();
            },
            DispatcherPriority.Background);
    }

    private void ClearFolderRows()
    {
        foreach (var row in FolderRows)
        {
            UnregisterRowItems(row);
        }

        FolderRows.Clear();
        _rowItems.Clear();
        _rowThumbnailsRequested.Clear();
        PublishRowIds();
        ShowFolderRows = false;
    }

    /// <summary>„Neu mischen“: neuer Startwert für alle Zeilen (gespeichert), erstes und letztes bleiben.</summary>
    [RelayCommand]
    private async Task ShuffleFolderRowsAsync()
    {
        _folderRowsSeed = Random.Shared.Next(1, int.MaxValue);
        FireAndForget(() => _viewSettings.SetAsync(FolderRowsSeedKey, _folderRowsSeed.ToString(CultureInfo.InvariantCulture)));
        foreach (var row in FolderRows.ToList())
        {
            row.Salt = 0;
            await RepickFolderRowAsync(row);
        }
    }

    /// <summary>„Andere Fotos zeigen“ einer Zeile.</summary>
    [RelayCommand]
    private Task ShuffleFolderRowAsync(FolderRowViewModel? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        row.Salt++;
        return RepickFolderRowAsync(row);
    }

    private async Task RepickFolderRowAsync(FolderRowViewModel row)
    {
        try
        {
            await PickFolderRowAsync(row, CancellationToken.None);
            RelayoutFolderRow(row);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ordner-Zeile {FolderId} konnte nicht neu gemischt werden", row.FolderId);
        }
    }

    /// <summary>Klick auf den Namen: den Ordner öffnen (zeigt wieder Zeilen, wenn er selbst Unterordner hat).</summary>
    [RelayCommand]
    private Task OpenFolderRowAsync(FolderRowViewModel? row)
        => row is null ? Task.CompletedTask : ApplyFolderFilterAsync(row.FolderId, row.IncludeSubfolders, row.IncludeSubfolders ? row.Name : FolderName);

    /// <summary>Klick auf ein Foto: Lightbox mit allen Fotos dieses Sets in Aufnahme-Reihenfolge.</summary>
    [RelayCommand]
    private async Task OpenFolderRowTileAsync(FolderRowTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        try
        {
            var dtos = await _source.LoadFilesByIdsAsync(tile.Row.IdsAscending, CancellationToken.None);
            var entries = dtos.Select(d => ToLightboxEntry(new GalleryItem(d))).ToList();
            var start = Math.Max(0, entries.FindIndex(e => e.FileId == tile.Item.Id));
            var owner = OwnerWindowResolver?.Invoke() ?? Application.Current?.MainWindow;
            _lightbox.Open(entries, start, owner);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lightbox konnte nicht geöffnet werden: {ex.Message}";
            _logger.LogError(ex, "Lightbox aus Ordner-Zeile {FolderId} fehlgeschlagen", tile.Row.FolderId);
        }
    }

    /// <summary>Diashow über alle Fotos eines Sets (Aufnahme-Reihenfolge).</summary>
    [RelayCommand]
    private void StartFolderRowSlideshow(FolderRowViewModel? row)
    {
        if (row is null || _slideshow is null || row.TotalCount == 0)
        {
            return;
        }

        _logger.LogInformation("Diashow aus Ordner-Zeile {FolderId}: {Count} Dateien", row.FolderId, row.TotalCount);
        _slideshow.Start(row.IdsAscending, 0, row.Name);
    }

    private async Task<int> LoadFolderRowsSeedAsync()
    {
        try
        {
            var stored = await _viewSettings.GetAsync(FolderRowsSeedKey);
            if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) && seed != 0)
            {
                return seed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Startwert der Ansicht Ordner nicht lesbar");
        }

        var created = Random.Shared.Next(1, int.MaxValue);
        FireAndForget(() => _viewSettings.SetAsync(FolderRowsSeedKey, created.ToString(CultureInfo.InvariantCulture)));
        return created;
    }

    /// <summary>„12. Mai 2024“, „12.–14. Mai 2024“, „28. Mai – 3. Jun. 2024“ bzw. über Jahre „…2023 – …2024“.</summary>
    internal static string FormatDateRange(DateTime? from, DateTime? to)
    {
        if (from is not { } a)
        {
            return string.Empty;
        }

        var culture = CultureInfo.GetCultureInfo("de-DE");
        var b = to ?? a;
        if (b < a)
        {
            (a, b) = (b, a);
        }

        if (a.Date == b.Date)
        {
            return a.ToString("d. MMMM yyyy", culture);
        }

        if (a.Year == b.Year && a.Month == b.Month)
        {
            return $"{a.Day}.–{b.ToString("d. MMMM yyyy", culture)}";
        }

        return a.Year == b.Year
            ? $"{a.ToString("d. MMM", culture)} – {b.ToString("d. MMM yyyy", culture)}"
            : $"{a.ToString("d. MMM yyyy", culture)} – {b.ToString("d. MMM yyyy", culture)}";
    }
}
