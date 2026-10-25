using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Categories;
using Lumora.Gallery.Data;
using Lumora.Services.Categories;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Kategorien in der Galerie (ADR-0027): Filter-Dropdown (je Kategorie Werte + „ohne Wert“), Kontextmenü zum Zuweisen
/// (ein Wert je Kategorie, „Kein Wert“ entfernt), Werte je Datei der Page für die Listenspalten.
/// </summary>
public sealed partial class GalleryViewModel
{
    private readonly Dictionary<int, CategoryInfo> _categoryById = new();
    private IReadOnlyList<CategoryInfo> _categoryColumns = [];

    /// <summary>Kategorien in Anzeigereihenfolge — je eine Spalte der Listenansicht.</summary>
    public IReadOnlyList<CategoryInfo> CategoryColumns
    {
        get => _categoryColumns;
        private set => SetProperty(ref _categoryColumns, value);
    }

    public bool HasCategories => CategoryColumns.Count > 0;

    /// <summary>Gruppen des Filter-Dropdowns.</summary>
    public ObservableCollection<CategoryFilterGroup> CategoryFilterGroups { get; } = new();

    /// <summary>Untermenüs des Kontextmenüs „Kategorie → Wert“.</summary>
    public ObservableCollection<CategoryMenuGroup> CategoryMenuGroups { get; } = new();

    public bool HasCategoryFilter => CategoryFilterGroups.Any(g => g.IsActive);

    /// <summary>Beschriftung des Filter-Knopfs: „Kategorien“ oder „XX: N, NN · Projekt: ohne Wert“.</summary>
    public string CategoryFilterText
    {
        get
        {
            var parts = CategoryFilterGroups.Select(g => g.SelectionText).Where(t => t is not null).ToList();
            return parts.Count == 0 ? "Kategorien" : string.Join(" · ", parts);
        }
    }

    /// <summary>Häkchen im Filter-Dropdown geändert → Filter anwenden.</summary>
    [RelayCommand]
    private Task ApplyCategoryFilterAsync()
    {
        NotifyCategoryFilterChanged();
        return _suppressReload || !_isInitialized ? Task.CompletedTask : ChangeQueryAsync();
    }

    [RelayCommand]
    private Task ClearCategoryFilterAsync()
    {
        if (!HasCategoryFilter)
        {
            return Task.CompletedTask;
        }

        ResetCategoryFilter();
        return ChangeQueryAsync();
    }

    private void ResetCategoryFilter()
    {
        foreach (var entry in CategoryFilterGroups.SelectMany(g => g.Entries))
        {
            entry.IsSelected = false;
        }

        NotifyCategoryFilterChanged();
    }

    private void NotifyCategoryFilterChanged()
    {
        OnPropertyChanged(nameof(HasCategoryFilter));
        OnPropertyChanged(nameof(CategoryFilterText));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>Filter für die Abfrage; null ohne Auswahl.</summary>
    private IReadOnlyList<GalleryCategoryFilter>? SelectedCategoryFilters()
    {
        var filters = CategoryFilterGroups
            .Where(g => g.IsActive)
            .Select(g => new GalleryCategoryFilter(
                g.CategoryId,
                g.Entries.Where(e => e.IsSelected && e.ValueId is not null).Select(e => e.ValueId!.Value).ToList(),
                g.Entries.Any(e => e.IsSelected && e.IsNone)))
            .ToList();
        return filters.Count == 0 ? null : filters;
    }

    /// <summary>Kategorien und Zähler laden; Filter-Häkchen bleiben je Wert (bzw. „ohne Wert“ je Kategorie) erhalten.</summary>
    private async Task LoadCategoriesAsync()
    {
        try
        {
            var categories = await _categories.GetAllAsync();
            var counts = await _categories.GetFileCountsAsync();
            var selected = CategoryFilterGroups
                .SelectMany(g => g.Entries)
                .Where(e => e.IsSelected)
                .Select(e => (e.CategoryId, e.ValueId))
                .ToHashSet();

            _categoryById.Clear();
            CategoryFilterGroups.Clear();
            CategoryMenuGroups.Clear();
            foreach (var category in categories)
            {
                _categoryById[category.Id] = category;
                var filterEntries = category.Values
                    .Select(v => new CategoryFilterEntry(category.Id, v.Id, v.Name, counts.GetValueOrDefault(v.Id)) { IsSelected = selected.Contains((category.Id, v.Id)) })
                    .Append(new CategoryFilterEntry(category.Id, null, "ohne Wert", 0) { IsSelected = selected.Contains((category.Id, null)) })
                    .ToList();
                CategoryFilterGroups.Add(new CategoryFilterGroup(category.Id, category.Name, filterEntries));

                var menuEntries = category.Values
                    .Select(v => new CategoryMenuEntry(category.Id, category.Name, v.Id, v.Name))
                    .Append(new CategoryMenuEntry(category.Id, category.Name, null, "Kein Wert"))
                    .ToList();
                CategoryMenuGroups.Add(new CategoryMenuGroup(category.Id, category.Name, menuEntries));
            }

            CategoryColumns = categories;
            UpdateCategorySortFields(); // Sortierleiste: Kategorien als Felder (GalleryViewModel.SortBar.cs)
            OnPropertyChanged(nameof(HasCategories));
            NotifyCategoryFilterChanged();
            UpdateCategoryMenuStates();
            _logger.LogDebug("Kategorien geladen: {Count}", categories.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kategorien konnten nicht geladen werden");
        }
    }

    /// <summary>Stammdaten geändert (Seite „Kategorien“): Liste neu laden, Werte der Page neu abbilden, ggf. Filter neu anwenden.</summary>
    private async Task HandleCategoriesChangedAsync(CategoriesChangedMessage m)
    {
        var hadFilter = HasCategoryFilter;
        await LoadCategoriesAsync();
        UpdateCategorySortAfterChange(m);
        foreach (var item in CurrentPage)
        {
            if (item.Categories.Count > 0)
            {
                item.Categories = MapCategories(item.Categories.ValueIds);
            }
        }

        if (hadFilter && HasLibrary && m.Kind is CategoriesChangeKind.Deleted or CategoriesChangeKind.ValuesChanged)
        {
            NotifyCategoryFilterChanged();
            ScheduleReload();
        }
    }

    /// <summary>Kategorie-Werte der Dateien (Batch) laden und den Items der Page zuordnen.</summary>
    private async Task ApplyCategoriesToItemsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0 || _categoryById.Count == 0)
        {
            return;
        }

        try
        {
            var map = await _categories.GetValuesForFilesAsync(fileIds, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await DispatchAsync(() =>
            {
                foreach (var id in fileIds)
                {
                    if (_currentItems.TryGetValue(id, out var item))
                    {
                        item.Categories = map.TryGetValue(id, out var values) ? MapCategories(values) : GalleryItemCategories.Empty;
                    }
                }

                UpdateCategoryMenuStates();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kategorie-Werte der Seite konnten nicht geladen werden");
        }
    }

    private GalleryItemCategories MapCategories(IEnumerable<KeyValuePair<int, int>> categoryToValue)
    {
        var values = new Dictionary<int, (int, string)>();
        foreach (var (categoryId, valueId) in categoryToValue)
        {
            if (_categoryById.TryGetValue(categoryId, out var category) && category.FindValue(valueId) is { } value)
            {
                values[categoryId] = (valueId, value.Name);
            }
        }

        if (values.Count == 0)
        {
            return GalleryItemCategories.Empty;
        }

        // Beschriftungen „Kategorie: Wert“ in der Reihenfolge der Kategorien (Kategorie-Marke, Mockup 16)
        var labels = new List<string>(values.Count);
        foreach (var category in CategoryColumns)
        {
            if (values.TryGetValue(category.Id, out var value))
            {
                labels.Add($"{category.Name}: {value.Item2}");
            }
        }

        return new GalleryItemCategories(values, labels);
    }

    /// <summary>Zustand je Menüeintrag bezogen auf die Auswahl (alle / einige / keine haben diesen Wert).</summary>
    private void UpdateCategoryMenuStates()
    {
        if (CategoryMenuGroups.Count == 0)
        {
            return;
        }

        var selected = new List<GalleryItem>(_selectedIds.Count);
        foreach (var id in _selectedIds)
        {
            if (_currentItems.TryGetValue(id, out var item))
            {
                selected.Add(item);
            }
        }

        foreach (var entry in CategoryMenuGroups.SelectMany(g => g.Entries))
        {
            if (selected.Count == 0)
            {
                entry.State = false;
                continue;
            }

            var count = selected.Count(item => item.Categories.ValueIdOf(entry.CategoryId) == entry.ValueId);
            entry.State = count == 0 ? false : count == selected.Count ? true : null;
        }
    }

    /// <summary>Kontextmenü „Kategorie → Wert“: setzt den Wert bei allen ausgewählten Dateien („Kein Wert“ entfernt ihn).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task SetSelectionCategoryValueAsync(CategoryMenuEntry? entry)
    {
        var ids = SelectedIdsSnapshot();
        if (entry is null || ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunOperationAsync("Kategorie", async () =>
        {
            var changed = await _categories.SetValueAsync(ids, entry.CategoryId, entry.ValueId);
            await ApplyCategoriesToItemsAsync(ids.Where(_currentItems.ContainsKey).ToList(), CancellationToken.None);
            SetResultStatus(entry.IsNone
                ? $"{entry.CategoryName}: Wert bei {ids.Count:N0} Datei(en) entfernt ({changed:N0} geändert)."
                : $"{entry.CategoryName} = „{entry.Name}“ bei {ids.Count:N0} Datei(en) gesetzt ({changed:N0} geändert).");
            if (HasCategoryFilter || IsCategorySorted)
            {
                ScheduleReload();
            }
        });
    }

    /// <summary>
    /// Strg+K: Kategorie-Auswahl-Dialog für die Auswahl (je Kategorie Wert, „Kein Wert“ oder „gemischt — nicht
    /// ändern“). Die Ausgangswerte kommen aus der DB, damit auch ausgewählte Dateien außerhalb der Page zählen.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task EditSelectionCategoriesAsync()
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0)
        {
            return;
        }

        if (CategoryColumns.Count == 0)
        {
            SetResultStatus("Noch keine Kategorien angelegt — unter „Kategorien“ anlegen.");
            return;
        }

        IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> values;
        try
        {
            values = await _categories.GetValuesForFilesAsync(ids);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kategorie-Werte der Auswahl konnten nicht geladen werden");
            SetResultStatus($"Kategorie-Werte konnten nicht geladen werden: {ex.Message}");
            return;
        }

        var request = new CategoryPickRequest(CategoryColumns, values, ids);
        var owner = OwnerWindowResolver?.Invoke() ?? System.Windows.Application.Current?.MainWindow;
        var result = await _categoryPicker.PickAsync(request, owner);
        if (result is null || result.IsEmpty)
        {
            return;
        }

        await RunOperationAsync("Kategorien", async () =>
        {
            var changed = 0;
            foreach (var change in result.Changes)
            {
                changed += await _categories.SetValueAsync(ids, change.CategoryId, change.ValueId);
            }

            await ApplyCategoriesToItemsAsync(ids.Where(_currentItems.ContainsKey).ToList(), CancellationToken.None);
            SetResultStatus($"Kategorien bei {ids.Count:N0} Datei(en) gesetzt: {result.Changes.Count} Kategorie(n), {changed:N0} Zuordnung(en) geändert.");
            if (HasCategoryFilter || IsCategorySorted)
            {
                ScheduleReload();
            }
        });
    }

    // ---- Sortierung nach Kategorie (Listenansicht, serverseitig) ------------------------------------

    private readonly ObservableCollection<GallerySortOption> _sortOptions = new(s_sortOptions);

    /// <summary>True, wenn die Trefferliste nach einer Kategorie sortiert ist.</summary>
    public bool IsCategorySorted => SelectedSort.Category is not null;

    /// <summary>
    /// Kopfzeilenklick auf eine Kategorie-Spalte: erst aufsteigend (Werte-Reihenfolge der Verwaltung), erneuter Klick
    /// kehrt um. Sortiert die ganze Trefferliste in der DB; die Toolbar zeigt die Sortierung als eigenen Eintrag.
    /// </summary>
    public void SortByCategory(int categoryId)
    {
        if (!_categoryById.TryGetValue(categoryId, out var category))
        {
            return;
        }

        var next = GalleryCategorySortLogic.Next(SelectedSort.Category, categoryId);
        var option = new GallerySortOption(SelectedSort.Sort, GalleryCategorySortLogic.Label(category.Name, next.Descending), next);
        _sortOptions.Add(option);
        SelectedSort = option;
    }

    /// <summary>
    /// Aufräumen der Sortier-Auswahl erst NACH der Änderungsmeldung von <see cref="SelectedSort"/>: würde der zuvor
    /// gewählte Eintrag schon in <c>OnSelectedSortChanged</c> entfernt, verlöre die gebundene ComboBox ihr SelectedItem
    /// und schriebe null zurück (Rauchtest 0.2.23).
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SelectedSort) && SelectedSort is { } selected)
        {
            RemoveUnusedCategorySortOptions(selected);
        }

        OnFilterTextChanged(e.PropertyName); // Chip-Werte und „Es gilt“-Zeile (GalleryViewModel.Filters.cs, ADR-0041)
    }

    /// <summary>Nur die gewählte Kategorie-Sortierung bleibt in der Auswahlliste; feste Einträge bleiben immer.</summary>
    private void RemoveUnusedCategorySortOptions(GallerySortOption selected)
    {
        for (var i = _sortOptions.Count - 1; i >= 0; i--)
        {
            if (_sortOptions[i].Category is not null && !ReferenceEquals(_sortOptions[i], selected))
            {
                _sortOptions.RemoveAt(i);
            }
        }

        OnPropertyChanged(nameof(IsCategorySorted));
    }

    /// <summary>
    /// Nach Stammdaten-Änderungen: gelöschte Sortier-Kategorie → Standard-Sortierung; umbenannt → Beschriftung ohne
    /// Neuladen; Werte geändert (Reihenfolge, gelöscht) → Page neu laden.
    /// </summary>
    private void UpdateCategorySortAfterChange(CategoriesChangedMessage m)
    {
        if (SelectedSort.Category is not { } sort)
        {
            return;
        }

        if (!_categoryById.TryGetValue(sort.CategoryId, out var category))
        {
            SelectedSort = s_sortOptions[0];
            return;
        }

        var label = GalleryCategorySortLogic.Label(category.Name, sort.Descending);
        if (label != SelectedSort.Label)
        {
            var renamed = SelectedSort with { Label = label };
            _sortOptions.Add(renamed);
            var suppress = _suppressReload;
            _suppressReload = true;
            try
            {
                SelectedSort = renamed;
            }
            finally
            {
                _suppressReload = suppress;
            }
        }

        if (HasLibrary && m.Kind == CategoriesChangeKind.ValuesChanged)
        {
            ScheduleReload();
        }
    }
}

/// <summary>Reine Logik der Kategorie-Sortierung (Kopfzeilenklick, Beschriftung) — ohne ViewModel testbar.</summary>
public static class GalleryCategorySortLogic
{
    /// <summary>Gleiche Kategorie → Richtung wechseln, sonst aufsteigend.</summary>
    public static GalleryCategorySort Next(GalleryCategorySort? current, int categoryId)
        => current is { } c && c.CategoryId == categoryId
            ? c with { Descending = !c.Descending }
            : new GalleryCategorySort(categoryId, Descending: false);

    /// <summary>Eintrag der Sortier-Auswahl, z. B. „XX (Kategorie) ↑“.</summary>
    public static string Label(string categoryName, bool descending)
        => $"{categoryName} (Kategorie) {(descending ? "↓" : "↑")}";
}
