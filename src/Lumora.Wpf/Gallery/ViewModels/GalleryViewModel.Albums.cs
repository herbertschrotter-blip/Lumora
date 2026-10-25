using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Albums;
using Lumora.Gallery.Data;
using Lumora.Services.Albums;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Alben und Ordner-Verknüpfungen in der Galerie (ADR-0054): Chip „Album“ in der Zeile „Inhalt“ (eigenes Album = Bedingung
/// in „Es gilt“, intelligentes Album = lädt seinen Filter), „Als intelligentes Album speichern…“, Zu Album hinzufügen
/// (Kontextmenü, Aktionsleiste, Strg+L), Aus Album entfernen, Titelbild, „Hier verknüpfen…“ und „Verknüpfung entfernen“,
/// Ordneransicht „mit Verknüpfungen“/„nur echte Dateien“ mit Zähler „12 Dateien · 5 verknüpft“ und Ketten-Symbol je
/// Kachel. Ohne <see cref="AlbumActions"/> (Tests) ist alles aus.
/// </summary>
public sealed partial class GalleryViewModel
{
    private AlbumActions? _albumActions;
    private long _albumMarksSequence;
    private AlbumInfo? _openedSmartAlbum;
    private string? _lastAlbumName;

    /// <summary>Alben der Gruppe für Filter-Flyout und Menü „Zu Album hinzufügen“.</summary>
    public ObservableCollection<AlbumFilterEntry> AlbumFilterEntries { get; } = new();

    /// <summary>Eigene Alben (Ziele für „Zu Album hinzufügen“).</summary>
    public IEnumerable<AlbumFilterEntry> ManualAlbumEntries => AlbumFilterEntries.Where(e => !e.IsSmart);

    public bool HasAlbumSupport => _albumActions is not null;

    /// <summary>Filter „Album“: gewähltes eigenes Album.</summary>
    [ObservableProperty] private int? _selectedAlbumId;

    /// <summary>Ordneransicht: verknüpfte Fotos mit anzeigen (Standard) oder nur echte Dateien.</summary>
    [ObservableProperty] private bool _includeFolderLinks = true;

    /// <summary>Treffer, die nur über eine Verknüpfung im gewählten Ordner erscheinen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderLinksText), nameof(HasFolderLinks), nameof(ShowFolderLinksHeader))]
    private int _folderLinkedCount;

    /// <summary>
    /// Schalter der Ordneransicht nur, wo er etwas bewirkt: im gewählten Ordner gibt es Verknüpfungen (bzw. sie sind
    /// gerade ausgeblendet).
    /// </summary>
    public bool ShowFolderLinksHeader => HasFolderFilter && (FolderLinkedCount > 0 || !IncludeFolderLinks || _folderHasLinks);

    /// <summary>Der gewählte Ordner(-Teilbaum) hat Verknüpfungen (auch wenn die Filter gerade keine davon zeigen).</summary>
    private bool _folderHasLinks;

    public bool HasAlbumFilter => SelectedAlbumId is not null;

    public bool ShowAlbumFilter => HasAlbumSupport && (AlbumFilterEntries.Count > 0 || HasAlbumFilter);

    public bool HasFolderLinks => FolderLinkedCount > 0;

    /// <summary>„12 Dateien · 5 verknüpft“ in der Ordneransicht (nur mit Verknüpfungen).</summary>
    public string FolderLinksText
    {
        get
        {
            var real = Math.Max(0, TotalCount - FolderLinkedCount);
            return $"{real:N0} {(real == 1 ? "Datei" : "Dateien")} · {FolderLinkedCount:N0} verknüpft";
        }
    }

    /// <summary>Wert im Chip „Album: …“.</summary>
    public string AlbumFilterValueText
        => SelectedAlbumName is { } name ? name : _openedSmartAlbum is { } smart ? $"{smart.Name} (Filter)" : "alle";

    private string? SelectedAlbumName => AlbumFilterEntries.FirstOrDefault(e => e.AlbumId == SelectedAlbumId)?.Name;

    /// <summary>Bedingung der „Es gilt“-Zeile; null ohne Album-Filter.</summary>
    private string? AlbumConditionLabel => SelectedAlbumId is null ? null : $"Album „{SelectedAlbumName ?? "?"}“";

    /// <summary>Aus dem Konstruktor (optional; Tests ohne).</summary>
    private void InitAlbums(AlbumActions? actions)
    {
        _albumActions = actions;
        if (actions is null)
        {
            return;
        }

        _messenger.Register<GalleryViewModel, AlbumsChangedMessage>(this, static (r, m) => r.RunOnUi(() => r.HandleAlbumsChangedAsync(m)));
        _messenger.Register<GalleryViewModel, FolderLinksChangedMessage>(this, static (r, m) => r.RunOnUi(() => r.HandleFolderLinksChangedAsync(m)));
    }

    /// <summary>Album- und Verknüpfungs-Teil der Abfrage.</summary>
    private GalleryQuery WithAlbums(GalleryQuery query) => query with { AlbumId = SelectedAlbumId, IncludeFolderLinks = IncludeFolderLinks };

    partial void OnSelectedAlbumIdChanged(int? value)
    {
        NotifyAlbumFilterChanged();
        if (!_suppressReload && _isInitialized)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    partial void OnIncludeFolderLinksChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFolderLinksHeader));
        if (!_suppressReload && _isInitialized && HasFolderFilter)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    private void NotifyAlbumFilterChanged()
    {
        foreach (var entry in AlbumFilterEntries)
        {
            entry.IsSelected = entry.AlbumId == SelectedAlbumId || (SelectedAlbumId is null && entry.AlbumId == _openedSmartAlbum?.Id);
        }

        OnPropertyChanged(nameof(HasAlbumFilter));
        OnPropertyChanged(nameof(ShowAlbumFilter));
        OnPropertyChanged(nameof(AlbumFilterValueText));
        OnPropertyChanged(nameof(EmptyMessage));
        RefreshFilterIndicators();
    }

    /// <summary>Filter „Album“ aufheben (ohne Neuladen; Bibliothekswechsel, „Filter zurücksetzen“).</summary>
    private void ResetAlbumFilter()
    {
        var suppress = _suppressReload;
        _suppressReload = true;
        try
        {
            SelectedAlbumId = null;
            _openedSmartAlbum = null;
        }
        finally
        {
            _suppressReload = suppress;
        }

        NotifyAlbumFilterChanged();
    }

    // ---- Album-Liste ------------------------------------------------------------------------------------------------

    /// <summary>Alben der Gruppe laden (Bibliothekswechsel, Änderungen); ein gelöschtes gewähltes Album hebt den Filter auf.</summary>
    private async Task LoadAlbumsAsync()
    {
        if (_albumActions is null || !HasLibrary)
        {
            return;
        }

        try
        {
            var albums = await _albumActions.Albums.GetAllAsync(evaluateSmart: false); // intelligente ohne Auswertung (nur Namen)
            AlbumFilterEntries.Clear();
            foreach (var album in albums)
            {
                AlbumFilterEntries.Add(new AlbumFilterEntry(album.Id, album.Name, album.FileCount, album.IsSmart));
            }

            if (SelectedAlbumId is { } id && AlbumFilterEntries.All(e => e.AlbumId != id))
            {
                ResetAlbumFilter();
                ScheduleReload();
            }

            if (_openedSmartAlbum is { } smart && AlbumFilterEntries.All(e => e.AlbumId != smart.Id))
            {
                _openedSmartAlbum = null; // Bereich gewechselt (ADR-0055): nicht in ein Album der alten Gruppe speichern
            }

            OnPropertyChanged(nameof(ManualAlbumEntries));
            NotifyAlbumFilterChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alben konnten nicht geladen werden");
        }
    }

    private async Task HandleAlbumsChangedAsync(AlbumsChangedMessage m)
    {
        await LoadAlbumsAsync();
        if (HasAlbumFilter && (m.AlbumId is null || m.AlbumId == SelectedAlbumId) && m.Kind is AlbumsChangeKind.ItemsChanged or AlbumsChangeKind.Deleted)
        {
            ScheduleReload();
        }
    }

    private Task HandleFolderLinksChangedAsync(FolderLinksChangedMessage m)
    {
        if (HasLibrary && HasFolderFilter && InScope(m.LibraryId))
        {
            ScheduleReload();
        }

        return Task.CompletedTask;
    }

    // ---- Filter „Album“ und Öffnen ----------------------------------------------------------------------------------

    /// <summary>Eintrag im Flyout gewählt: eigenes Album = Bedingung (erneut gewählt = aufheben), intelligentes = Filter laden.</summary>
    [RelayCommand]
    private Task SelectAlbumFilterAsync(AlbumFilterEntry? entry)
    {
        if (entry is null)
        {
            return Task.CompletedTask;
        }

        if (entry.IsSmart)
        {
            return OpenAlbumAsync(entry.AlbumId);
        }

        _openedSmartAlbum = null;
        SelectedAlbumId = SelectedAlbumId == entry.AlbumId ? null : entry.AlbumId;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ClearAlbumFilterAsync()
    {
        if (!HasAlbumFilter && _openedSmartAlbum is null)
        {
            return Task.CompletedTask;
        }

        var had = HasAlbumFilter;
        ResetAlbumFilter();
        return had ? ChangeQueryAsync() : Task.CompletedTask;
    }

    /// <summary>
    /// Album in der Galerie öffnen (Seite „Alben“, Flyout): alle Filter und der Ordnerfilter werden aufgehoben; ein eigenes
    /// Album wird zur Bedingung „Album …“, ein intelligentes lädt seinen gespeicherten Filter (Bearbeiten = Filter ändern
    /// und neu speichern).
    /// </summary>
    public async Task OpenAlbumAsync(int albumId)
    {
        if (_albumActions is null || !HasLibrary)
        {
            return;
        }

        var album = await _albumActions.Albums.GetAsync(albumId);
        if (album is null)
        {
            SetResultStatus("Das Album gibt es nicht mehr.");
            await LoadAlbumsAsync();
            return;
        }

        if (album.IsSmart && album.Filter is { } filter)
        {
            await ApplySmartFilterAsync(filter, album);
            return;
        }

        var suppress = _suppressReload;
        _suppressReload = true;
        try
        {
            ResetAllFiltersCore();
            SelectedAlbumId = album.Id;
        }
        finally
        {
            _suppressReload = suppress;
        }

        NotifyAlbumFilterChanged();
        _logger.LogInformation("Galerie: Album {Id} geöffnet ({Count} Dateien)", album.Id, album.FileCount);
        await ChangeQueryAsync();
    }

    /// <summary>Alle Filter, die Suche und den Ordnerfilter aufheben (ohne Neuladen).</summary>
    private void ResetAllFiltersCore()
    {
        _searchDebounceCts?.Cancel();
        ResetFileFilter();
        ResetTagFilter();
        ResetPersonFilter();
        ResetCategoryFilter();
        ResetMotifFilter();
        SelectedAlbumId = null;
        _openedSmartAlbum = null;
        SearchTerm = null;
        _appliedSearchTerm = null;
        FolderId = null;
        FolderName = null;
        IncludeSubfolders = true;
    }

    /// <summary>
    /// Gespeicherten Filter auf die Galerie anwenden: alle Teile über ihre Namen in die Flyouts übernehmen. Begriffe, die es
    /// nicht mehr gibt, fallen weg (Hinweis in der Statuszeile).
    /// </summary>
    public async Task ApplySmartFilterAsync(SmartAlbumFilter filter, AlbumInfo? album = null)
    {
        var missing = 0;
        var suppress = _suppressReload;
        _suppressReload = true;
        try
        {
            ResetAllFiltersCore();
            _openedSmartAlbum = album;
            var media = Enum.TryParse<GalleryMediaKind>(filter.Media, out var m) ? m : GalleryMediaKind.All;
            var location = Enum.TryParse<GalleryLocation>(filter.Location, out var l) ? l : GalleryLocation.Anywhere;
            FilterState = new GalleryFilterState(media, filter.FavoritesOnly, location, filter.MinRating)
            {
                Place = filter.Place is { } p ? new GalleryPlaceFilter(p.PlaceIds, p.IncludeWithoutLocation, p.IncludeWithoutPlaceName, p.Label) : null,
            };
            SearchTerm = string.IsNullOrWhiteSpace(filter.SearchTerm) ? null : filter.SearchTerm.Trim();

            missing += Select(TagFilterEntries, filter.Tags, e => e.Name, (e, v) => e.IsSelected = v);
            TagFilterMode = Enum.TryParse<GalleryTagMode>(filter.TagMode, out var tm) ? tm : GalleryTagMode.Any;
            missing += Select(PersonFilterEntries, filter.Persons, e => e.Name, (e, v) => e.IsSelected = v);
            PersonFilterMode = Enum.TryParse<GalleryPersonMode>(filter.PersonMode, out var pm) ? pm : GalleryPersonMode.Any;
            PersonFilterIncludeSuggestions = filter.PersonIncludeSuggestions;
            missing += Select(RelationFilterEntries, filter.Relations, e => e.Name, (e, v) => e.IsSelected = v);
            missing += Select(MotifFilterEntries, filter.Motifs, e => e.Name, (e, v) => e.IsSelected = v);
            MotifFilterMode = Enum.TryParse<GalleryMotifMode>(filter.MotifMode, out var mm) ? mm : GalleryMotifMode.Any;
            foreach (var category in filter.Categories)
            {
                var group = CategoryFilterGroups.FirstOrDefault(g => string.Equals(g.Name, category.Name, StringComparison.OrdinalIgnoreCase));
                if (group is null)
                {
                    missing++;
                    continue;
                }

                missing += Select(group.Entries.Where(e => !e.IsNone).ToList(), category.Values, e => e.Name, (e, v) => e.IsSelected = v);
                foreach (var none in group.Entries.Where(e => e.IsNone))
                {
                    none.IsSelected = category.IncludeNone;
                }
            }

            if (filter.AlbumName is { } albumName)
            {
                var entry = AlbumFilterEntries.FirstOrDefault(e => !e.IsSmart && string.Equals(e.Name, albumName, StringComparison.OrdinalIgnoreCase));
                SelectedAlbumId = entry?.AlbumId;
                missing += entry is null ? 1 : 0;
            }

            if (filter.FolderPath is { } folderPath)
            {
                var folder = await _source.FindFolderAsync(folderPath, ScopeLibraryIds, CancellationToken.None);
                if (folder is { } f)
                {
                    FolderId = f.Id;
                    FolderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
                    IncludeSubfolders = filter.IncludeSubfolders ?? true;
                }
                else
                {
                    missing++;
                }
            }
        }
        finally
        {
            _suppressReload = suppress;
        }

        NotifyTagFilterChanged();
        NotifyPersonFilterChanged();
        NotifyRelationFilterChanged();
        NotifyMotifFilterChanged();
        NotifyCategoryFilterChanged();
        NotifyAlbumFilterChanged();
        _logger.LogInformation("Galerie: intelligentes Album {Id} geöffnet ({Missing} Begriffe fehlen)", album?.Id, missing);
        await ChangeQueryAsync();
        if (missing > 0)
        {
            SetResultStatus(missing == 1
                ? "Ein Begriff des Albums existiert nicht mehr und wurde weggelassen."
                : $"{missing} Begriffe des Albums existieren nicht mehr und wurden weggelassen.");
        }

        static int Select<T>(IEnumerable<T> entries, IReadOnlyList<string> names, Func<T, string> nameOf, Action<T, bool> set)
        {
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var found = 0;
            foreach (var entry in entries)
            {
                var hit = wanted.Contains(nameOf(entry));
                set(entry, hit);
                found += hit ? 1 : 0;
            }

            return Math.Max(0, wanted.Count - found);
        }
    }

    /// <summary>Der aktuelle Galerie-Filter als speicherbarer Filter (Begriffe über ihre Namen).</summary>
    public async Task<SmartAlbumFilter> CaptureSmartFilterAsync()
    {
        string? folderPath = null;
        if (FolderId is { } folderId)
        {
            folderPath = (await _source.GetFolderInfoAsync(folderId, CancellationToken.None))?.Path;
        }

        var personModeIgnoresSelection = PersonFilterMode.IgnoresPersonSelection();
        return new SmartAlbumFilter
        {
            Media = MediaFilter == GalleryMediaKind.All ? null : MediaFilter.ToString(),
            FavoritesOnly = FavoritesOnly,
            Location = LocationFilter == GalleryLocation.Anywhere ? null : LocationFilter.ToString(),
            MinRating = MinRating,
            SearchTerm = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim(),
            Tags = TagFilterMode == GalleryTagMode.None ? [] : TagFilterEntries.Where(e => e.IsSelected).Select(e => e.Name).ToList(),
            TagMode = TagFilterMode == GalleryTagMode.Any ? null : TagFilterMode.ToString(),
            Persons = personModeIgnoresSelection ? [] : PersonFilterEntries.Where(e => e.IsSelected).Select(e => e.Name).ToList(),
            PersonMode = PersonFilterMode == GalleryPersonMode.Any ? null : PersonFilterMode.ToString(),
            PersonIncludeSuggestions = PersonFilterIncludeSuggestions,
            Relations = RelationFilterEntries.Where(e => e.IsSelected).Select(e => e.Name).ToList(),
            Categories = CategoryFilterGroups.Where(g => g.IsActive)
                .Select(g => new SmartAlbumCategory(g.Name, g.Entries.Where(e => e.IsSelected && !e.IsNone).Select(e => e.Name).ToList(), g.Entries.Any(e => e.IsNone && e.IsSelected)))
                .ToList(),
            Motifs = MotifFilterEntries.Where(e => e.IsSelected).Select(e => e.Name).ToList(),
            MotifMode = MotifFilterMode == GalleryMotifMode.Any ? null : MotifFilterMode.ToString(),
            Place = _placeFilter is { IsActive: true } place
                ? new SmartAlbumPlace(place.PlaceIds, place.IncludeWithoutLocation, place.IncludeWithoutPlaceName, place.Label)
                : null,
            FolderPath = folderPath,
            IncludeSubfolders = folderPath is null ? null : IncludeSubfolders,
            AlbumName = SelectedAlbumName,
        };
    }

    /// <summary>„Als intelligentes Album speichern…“ (Kopfzeile): Name abfragen, bei gleichem Namen Rückfrage „ersetzen“.</summary>
    [RelayCommand]
    private async Task SaveAsSmartAlbumAsync()
    {
        if (_albumActions is null || !HasLibrary)
        {
            return;
        }

        var filter = await CaptureSmartFilterAsync();
        if (filter.IsEmpty)
        {
            SetResultStatus("Erst einen Filter setzen — ein intelligentes Album ohne Filter zeigte alles.");
            return;
        }

        var name = await _albumActions.Dialogs.PromptTextAsync(
            "Als intelligentes Album speichern",
            $"Name des Albums — es füllt sich selbst mit allem, was gilt: {string.Join(" UND ", filter.Describe())}",
            _openedSmartAlbum?.Name ?? string.Empty,
            "Speichern");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var existing = await _albumActions.Albums.FindByNameAsync(name);
            var replace = false;
            if (existing is { IsSmart: true })
            {
                replace = await _albumActions.Dialogs.ConfirmAsync(
                    "Album ersetzen?",
                    $"Das intelligente Album „{existing.Name}“ gibt es schon. Seinen Filter durch den aktuellen ersetzen?",
                    "Ersetzen");
                if (!replace)
                {
                    return;
                }
            }

            var saved = await _albumActions.Albums.SaveSmartAsync(name, filter, replace);
            _openedSmartAlbum = saved;
            SetResultStatus($"Intelligentes Album „{saved.Name}“ gespeichert ({saved.FileCount ?? TotalCount:N0} Dateien).");
            NotifyAlbumFilterChanged();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await _albumActions.Dialogs.ShowErrorAsync("Album nicht gespeichert", ex.Message);
        }
    }

    // ---- Zu Album hinzufügen / entfernen / Titelbild ---------------------------------------------------------------

    /// <summary>Menü: Auswahl zu einem eigenen Album hinzufügen.</summary>
    [RelayCommand]
    private Task AddSelectionToAlbumAsync(AlbumFilterEntry? entry)
        => entry is null || entry.IsSmart ? Task.CompletedTask : AddToAlbumAsync(entry.AlbumId, entry.Name, SelectedIdsSnapshot());

    /// <summary>Menü „Neues Album…“ bzw. Strg+L: Name eingeben (vorhandenes oder neues Album) und die Auswahl hinzufügen.</summary>
    [RelayCommand]
    private async Task AddSelectionToNamedAlbumAsync()
    {
        var ids = SelectedIdsSnapshot();
        if (_albumActions is null || ids.Count == 0)
        {
            return;
        }

        var name = await _albumActions.Dialogs.PromptTextAsync(
            "Zu Album hinzufügen",
            $"{(ids.Count == 1 ? "1 Datei" : $"{ids.Count:N0} Dateien")} zu welchem Album? Ein neuer Name legt das Album an — die Fotos werden nur verknüpft, nicht kopiert.",
            _lastAlbumName ?? string.Empty,
            "Hinzufügen");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunOperationAsync("Zu Album hinzufügen", async () =>
        {
            var album = await _albumActions.Albums.FindByNameAsync(name) ?? await _albumActions.Albums.CreateAsync(name);
            if (album.IsSmart)
            {
                await _albumActions.Dialogs.ShowErrorAsync("Intelligentes Album", $"„{album.Name}“ füllt sich selbst über seinen Filter — Fotos lassen sich nicht einzeln hinzufügen.");
                return;
            }

            await AddToAlbumCoreAsync(album.Id, album.Name, ids);
        });
    }

    /// <summary>Öffentlich für das Ablegen auf ein Album und den Rauchtest.</summary>
    public Task AddToAlbumAsync(int albumId, string name, IReadOnlyList<int> ids)
        => ids.Count == 0 || _albumActions is null
            ? Task.CompletedTask
            : RunOperationAsync("Zu Album hinzufügen", () => AddToAlbumCoreAsync(albumId, name, ids));

    private async Task AddToAlbumCoreAsync(int albumId, string name, IReadOnlyList<int> ids)
    {
        var added = await _albumActions!.Albums.AddFilesAsync(albumId, ids);
        if (added > 0)
        {
            Lumora.Services.Usage.UsageReport.Used(ids); // Nutzungs-Signale (ADR-0072)
        }
        _lastAlbumName = name;
        var rest = ids.Count - added;
        SetResultStatus(rest == 0
            ? $"{added:N0} {(added == 1 ? "Datei" : "Dateien")} zu „{name}“ hinzugefügt (verknüpft, nicht kopiert)."
            : $"{added:N0} zu „{name}“ hinzugefügt, {rest:N0} waren schon drin oder gehören zu einer anderen Bibliothek.");
    }

    /// <summary>Album-Ansicht: Auswahl aus dem Album entfernen (die Fotos bleiben).</summary>
    [RelayCommand]
    private async Task RemoveSelectionFromAlbumAsync()
    {
        var ids = SelectedIdsSnapshot();
        if (_albumActions is null || SelectedAlbumId is not { } albumId || ids.Count == 0)
        {
            return;
        }

        await RunOperationAsync("Aus Album entfernen", async () =>
        {
            var removed = await _albumActions.Albums.RemoveFilesAsync(albumId, ids);
            SetResultStatus($"{removed:N0} aus „{SelectedAlbumName}“ entfernt — die Fotos selbst bleiben, wo sie sind.");
        });
    }

    /// <summary>Album-Ansicht: Datei als Titelbild des Albums.</summary>
    [RelayCommand]
    private async Task SetAlbumCoverAsync(GalleryItem? item)
    {
        if (_albumActions is null || SelectedAlbumId is not { } albumId || item is null)
        {
            return;
        }

        await _albumActions.Albums.SetCoverAsync(albumId, item.Id);
        Lumora.Services.Usage.UsageReport.Used([item.Id]); // Nutzungs-Signale (ADR-0072)
        SetResultStatus($"„{item.Name}“ ist jetzt das Titelbild von „{SelectedAlbumName}“.");
    }

    // ---- Ordner-Verknüpfungen --------------------------------------------------------------------------------------

    /// <summary>„Hier verknüpfen…“: Zielordner wählen, Auswahl dort zusätzlich zeigen (nicht kopieren).</summary>
    [RelayCommand]
    private async Task LinkSelectionToFolderAsync()
    {
        var ids = SelectedIdsSnapshot();
        if (_albumActions is null || ids.Count == 0)
        {
            return;
        }

        var path = _albumActions.Dialogs.PickFolder("Ordner wählen, in dem die Auswahl zusätzlich erscheinen soll (verknüpfen, nicht kopieren)");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await RunOperationAsync("Hier verknüpfen", async () =>
        {
            var folder = await _source.FindFolderAsync(path, ScopeLibraryIds, CancellationToken.None);
            if (folder is null)
            {
                var libraryId = _currentItems.GetValueOrDefault(ids[0])?.LibraryId is { } lib and > 0 ? lib : _libraryId;
                var created = await _fileOps.CreateFolderAsync(libraryId, Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? path, Path.GetFileName(path.TrimEnd('\\', '/')));
                if (!created.Success || created.FolderId is null)
                {
                    await _albumActions.Dialogs.ShowErrorAsync("Hier verknüpfen", created.Error ?? "Der Ordner liegt in keiner Wurzel der Bibliothek.");
                    return;
                }

                folder = (created.FolderId.Value, libraryId);
            }

            await LinkToFolderCoreAsync(folder.Value.Id, Path.GetFileName(path.TrimEnd('\\', '/')), ids);
        });
    }

    /// <summary>Öffentlich für das Ablegen im Ordnerbaum und den Rauchtest.</summary>
    public Task<FolderLinkResult?> LinkToFolderAsync(int folderId, string folderName, IReadOnlyList<int> ids)
        => _albumActions is null || ids.Count == 0 ? Task.FromResult<FolderLinkResult?>(null) : LinkToFolderCoreAsync(folderId, folderName, ids);

    private async Task<FolderLinkResult?> LinkToFolderCoreAsync(int folderId, string folderName, IReadOnlyList<int> ids)
    {
        var result = await _albumActions!.Links.LinkAsync(folderId, ids);
        var parts = new List<string> { $"{result.Linked:N0} mit „{folderName}“ verknüpft (nicht kopiert)" };
        if (result.AlreadyThere > 0)
        {
            parts.Add($"{result.AlreadyThere:N0} lagen schon dort");
        }

        if (result.OtherLibrary > 0)
        {
            parts.Add($"{result.OtherLibrary:N0} gehören zu einer anderen Bibliothek (nicht erlaubt)");
        }

        SetResultStatus(string.Join(", ", parts) + ".");
        return result;
    }

    /// <summary>Ordneransicht: Verknüpfung der Auswahl aus dem gewählten Ordner (samt Unterordnern) entfernen.</summary>
    [RelayCommand]
    private async Task UnlinkSelectionFromFolderAsync()
    {
        var ids = SelectedIdsSnapshot().Where(id => _currentItems.GetValueOrDefault(id)?.IsLinked == true).ToList();
        if (_albumActions is null || FolderId is not { } folderId || ids.Count == 0)
        {
            return;
        }

        await RunOperationAsync("Verknüpfung entfernen", async () =>
        {
            var removed = await _albumActions.Links.UnlinkAsync(folderId, ids, IncludeSubfolders);
            SetResultStatus($"{removed:N0} Verknüpfung(en) aus „{FolderName}“ entfernt — die Fotos selbst bleiben, wo sie sind.");
        });
    }

    // ---- Kennzeichen „verknüpft“ und Zähler ------------------------------------------------------------------------

    /// <summary>Nach dem Laden einer Seite: Ketten-Symbol der Kacheln und Zähler „n verknüpft“.</summary>
    private async Task ApplyFolderLinkMarksAsync(GalleryQuery query, IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        var sequence = Interlocked.Increment(ref _albumMarksSequence);
        try
        {
            var linked = query.HasFolder && query.IncludeFolderLinks
                ? await _source.GetLinkedHereAsync(query, fileIds, ct)
                : (IReadOnlySet<int>)new HashSet<int>();
            var count = query.HasFolder && query.IncludeFolderLinks ? await _source.GetFolderLinkedCountAsync(query, ct) : 0;
            var hasLinks = query.HasFolder && await _source.FolderHasLinksAsync(query, ct);
            if (ct.IsCancellationRequested || sequence != Interlocked.Read(ref _albumMarksSequence))
            {
                return;
            }

            await DispatchAsync(() =>
            {
                foreach (var id in fileIds)
                {
                    if (_currentItems.TryGetValue(id, out var item))
                    {
                        item.IsLinked = linked.Contains(id);
                    }
                }

                _folderHasLinks = hasLinks;
                FolderLinkedCount = count;
                OnPropertyChanged(nameof(ShowFolderLinksHeader));
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verknüpfte Dateien der Seite konnten nicht ermittelt werden");
        }
    }
}
