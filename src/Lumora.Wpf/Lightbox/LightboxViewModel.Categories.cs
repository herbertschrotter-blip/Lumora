using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Categories;
using Lumora.Services.Categories;

namespace Lumora.Lightbox;

/// <summary>
/// Kategorie-Werte in der Lightbox (ADR-0027, 0.2.23): gesetzte Werte der aktuellen Datei in der Titelleiste, alle
/// Kategorien im Info-Panel (I) und Zuweisen per Strg+K bzw. Knopf im Info-Panel über denselben Dialog wie in der
/// Galerie. Muster wie <c>LightboxViewModel.Tags.cs</c>; ohne <see cref="ICategoryService"/> (Tests) bleibt alles leer.
/// </summary>
public sealed partial class LightboxViewModel
{
    private ICategoryService? _categoryService;
    private ICategoryPickerService? _categoryPicker;
    private int _categoriesVersion;

    /// <summary>Alle Kategorien mit dem Wert der aktuellen Datei („—“ ohne Wert) für das Info-Panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCategories))]
    private IReadOnlyList<LightboxInfoRow> _categoryRows = Array.Empty<LightboxInfoRow>();

    /// <summary>Gesetzte Werte der aktuellen Datei kompakt für die Titelleiste, z. B. „XX: N · Projekt: Nord“; leer ohne Wert.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentCategoryValues), nameof(CurrentCategoriesToolTip))]
    private string _currentCategoriesText = string.Empty;

    /// <summary>True, sobald es Kategorien gibt (Abschnitt „Kategorien“ im Info-Panel).</summary>
    public bool HasCategories => CategoryRows.Count > 0;

    public bool HasCurrentCategoryValues => CurrentCategoriesText.Length > 0;

    public string CurrentCategoriesToolTip => HasCurrentCategoryValues
        ? $"Kategorien: {CurrentCategoriesText} — Strg+K zum Zuweisen"
        : "Keine Kategorie-Werte — Strg+K zum Zuweisen";

    private void InitCategories(ICategoryService? categories, ICategoryPickerService? categoryPicker)
    {
        _categoryService = categories;
        _categoryPicker = categoryPicker;
        if (categories is null)
        {
            return;
        }

        // Anlegen/Umbenennen/Löschen auf der Seite „Kategorien“ → Anzeige der aktuellen Datei neu abbilden.
        WeakReferenceMessenger.Default.Register<CategoriesChangedMessage>(
            this,
            static (recipient, message) =>
            {
                var vm = (LightboxViewModel)recipient;
                vm.RunOnUi(() => _ = vm.LoadCurrentCategoriesAsync());
            });
    }

    /// <summary>Kategorien und Werte der aktuellen Datei laden; ältere Ladevorgänge (schnelles Blättern) werden verworfen.</summary>
    private async Task LoadCurrentCategoriesAsync()
    {
        var item = CurrentItem;
        var version = ++_categoriesVersion;
        if (item is null || _categoryService is null)
        {
            ApplyCategories(Array.Empty<CategoryInfo>(), new Dictionary<int, int>());
            return;
        }

        try
        {
            var all = await _categoryService.GetAllAsync().ConfigureAwait(true);
            var map = await _categoryService.GetValuesForFilesAsync(new[] { item.FileId }).ConfigureAwait(true);
            if (_disposed || version != _categoriesVersion)
            {
                return;
            }

            ApplyCategories(all, map.TryGetValue(item.FileId, out var values) ? values : new Dictionary<int, int>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kategorie-Werte für File {FileId} konnten nicht geladen werden", item.FileId);
        }
    }

    private void ApplyCategories(IReadOnlyList<CategoryInfo> all, IReadOnlyDictionary<int, int> values)
    {
        var rows = new List<LightboxInfoRow>(all.Count);
        var assigned = new List<string>();
        foreach (var category in all)
        {
            var value = values.TryGetValue(category.Id, out var valueId) ? category.FindValue(valueId) : null;
            rows.Add(new LightboxInfoRow(category.Name, value?.Name ?? "—"));
            if (value is not null)
            {
                assigned.Add($"{category.Name}: {value.Name}");
            }
        }

        CategoryRows = rows;
        CurrentCategoriesText = string.Join(" · ", assigned);
        CurrentCategoryLabels = assigned; // Chips der Leiste unten (Mockup 16, LightboxViewModel.Marks.cs)
    }

    /// <summary>Strg+K: Kategorie-Auswahl für die aktuelle Datei; Änderungen über <see cref="ICategoryService"/> (meldet Metadata).</summary>
    [RelayCommand]
    private async Task EditCategoriesAsync()
    {
        var item = CurrentItem;
        if (item is null)
        {
            return;
        }

        if (_categoryService is null || _categoryPicker is null)
        {
            ShowStatus("Kategorie-Dienst nicht verfügbar — Kategorien können nicht zugewiesen werden.");
            return;
        }

        var service = _categoryService;
        try
        {
            var all = await service.GetAllAsync().ConfigureAwait(true);
            if (all.Count == 0)
            {
                ShowStatus("Noch keine Kategorien angelegt — unter „Kategorien“ anlegen.");
                return;
            }

            var values = await service.GetValuesForFilesAsync(new[] { item.FileId }).ConfigureAwait(true);
            var request = new CategoryPickRequest(all, values, new[] { item.FileId });
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            var result = await _categoryPicker.PickAsync(request, owner).ConfigureAwait(true);
            if (result is null || result.IsEmpty)
            {
                return;
            }

            foreach (var change in result.Changes)
            {
                await service.SetValueAsync(new[] { item.FileId }, change.CategoryId, change.ValueId).ConfigureAwait(true);
            }

            ShowStatus("Kategorien gespeichert.");
        }
        catch (Exception ex)
        {
            ShowStatus($"Kategorien konnten nicht gespeichert werden: {ex.Message}");
            _logger.LogWarning(ex, "Kategorien zuweisen fehlgeschlagen für File {FileId}", item.FileId);
        }

        await LoadCurrentCategoriesAsync().ConfigureAwait(true);
    }
}
