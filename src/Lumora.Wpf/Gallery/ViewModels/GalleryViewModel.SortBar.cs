using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Wpf.Ui.Controls;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Sortier- und Ansichtsleiste (0.2.43, ADR-0041): Feld und Richtung getrennt („Sortieren [Änderungsdatum ▾]
/// [↓ Neueste zuerst]“) statt einer kombinierten Liste. Grundlage bleibt <see cref="GalleryViewModel.SelectedSort"/>
/// (Listenansicht und Rauchtest setzen weiter direkt); die Leiste bildet sie nur ab. Kategorien erscheinen als weitere
/// Felder (Sortierung nach dem Wert, ADR-0027/0031). Ein neues Feld wählt dessen übliche Richtung (Datum: neueste zuerst,
/// Name: A → Z, Größe: größte zuerst, Kategorie: Werte-Reihenfolge); der Richtungsknopf kehrt sie um.
/// </summary>
public sealed partial class GalleryViewModel
{
    private static readonly GallerySortFieldOption[] s_fixedSortFields =
    [
        new(GallerySortField.Captured, "Aufnahmedatum"), // ADR-0046: Standard
        new(GallerySortField.Modified, "Änderungsdatum"),
        new(GallerySortField.Name, "Name"),
        new(GallerySortField.Size, "Größe"),
    ];

    private static readonly GalleryViewModeOption[] s_viewModeOptions =
    [
        new(GalleryViewMode.Grid, "Kacheln", SymbolRegular.Grid24, "Kacheln: gleich große Quadrate"),
        new(GalleryViewMode.Collage, "Collage", SymbolRegular.Board24, "Collage: echte Seitenverhältnisse, Reihen randvoll"), // ADR-0044
        new(GalleryViewMode.List, "Liste", SymbolRegular.AppsListDetail24, "Liste mit Spalten"),
        new(GalleryViewMode.Folders, "Ordner", SymbolRegular.Folder24, "Ordner: je Unterordner eine Zeile – erstes, letztes und zufällige Fotos"), // Mockup 21
    ];

    /// <summary>Felder der Sortier-Auswahl: Datum, Name, Größe und je Kategorie ein Eintrag.</summary>
    public ObservableCollection<GallerySortFieldOption> SortFields { get; } = new(s_fixedSortFields);

    /// <summary>Einträge des Ansicht-Segments (Reihenfolge = Anzeige).</summary>
    public IReadOnlyList<GalleryViewModeOption> ViewModeOptions => s_viewModeOptions;

    /// <summary>Gewähltes Sortierfeld (abgeleitet aus <see cref="SelectedSort"/>).</summary>
    public GallerySortFieldOption SelectedSortField
    {
        get
        {
            // Robust auch während SortFields umgebaut wird (die ComboBox liest dabei nach): feste Felder bleiben immer drin.
            if (SelectedSort.Category is { } category
                && SortFields.FirstOrDefault(f => f.Field == GallerySortField.Category && f.CategoryId == category.CategoryId) is { } categoryField)
            {
                return categoryField;
            }

            var sortField = FieldOf(SelectedSort.Sort);
            return SortFields.FirstOrDefault(f => f.Field == sortField) ?? s_fixedSortFields.First(f => f.Field == sortField);
        }
        set
        {
            if (value is null || ReferenceEquals(value, SelectedSortField))
            {
                return;
            }

            if (value.Field == GallerySortField.Category && value.CategoryId is { } categoryId)
            {
                if (SelectedSort.Category?.CategoryId != categoryId)
                {
                    SortByCategory(categoryId);
                }

                return;
            }

            SelectSort(SortFor(value.Field, DefaultDescending(value.Field)));
        }
    }

    /// <summary>Absteigend (Datum: neueste zuerst, Name: Z → A, Größe: größte zuerst, Kategorie: Werte rückwärts).</summary>
    public bool SortDescending
        => SelectedSort.Category is { } category ? category.Descending : SelectedSort.Sort is GallerySort.ModifiedDesc or GallerySort.NameDesc or GallerySort.SizeDesc or GallerySort.CapturedDesc;

    /// <summary>Beschriftung des Richtungsknopfs.</summary>
    public string SortDirectionText
    {
        get
        {
            if (SelectedSort.Category is not null)
            {
                return SortDescending ? "Werte rückwärts" : "Werte der Reihe nach";
            }

            return SelectedSort.Sort switch
            {
                GallerySort.ModifiedDesc or GallerySort.CapturedDesc => "Neueste zuerst",
                GallerySort.ModifiedAsc or GallerySort.CapturedAsc => "Älteste zuerst",
                GallerySort.NameAsc => "A → Z",
                GallerySort.NameDesc => "Z → A",
                GallerySort.SizeDesc => "Größte zuerst",
                _ => "Kleinste zuerst",
            };
        }
    }

    public SymbolRegular SortDirectionSymbol => SortDescending ? SymbolRegular.ArrowSortDown24 : SymbolRegular.ArrowSortUp24;

    /// <summary>Richtungsknopf: kehrt die Sortierung um (Feld bleibt).</summary>
    [RelayCommand]
    private void ToggleSortDirection()
    {
        if (SelectedSort.Category is { } category)
        {
            SortByCategory(category.CategoryId); // gleiche Kategorie → Richtung wechseln
            return;
        }

        SelectSort(SortFor(FieldOf(SelectedSort.Sort), !SortDescending));
    }

    private void SelectSort(GallerySort sort)
        => SelectedSort = SortOptions.First(o => o.Sort == sort && o.Category is null);

    private void NotifySortBarChanged()
    {
        OnPropertyChanged(nameof(SelectedSortField));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(SortDirectionText));
        OnPropertyChanged(nameof(SortDirectionSymbol));
    }

    /// <summary>Kategorien als Sortierfelder (nach dem Laden der Kategorien).</summary>
    private void UpdateCategorySortFields()
    {
        var wanted = s_fixedSortFields
            .Concat(CategoryColumns.Select(c => new GallerySortFieldOption(GallerySortField.Category, $"Kategorie: {c.Name}", c.Id)))
            .ToList();
        if (wanted.SequenceEqual(SortFields))
        {
            return;
        }

        // Nur die Kategorie-Einträge hinter den festen Feldern tauschen — die Liste wird nie leer (ComboBox-Auswahl bleibt gültig).
        for (var i = SortFields.Count - 1; i >= s_fixedSortFields.Length; i--)
        {
            SortFields.RemoveAt(i);
        }

        foreach (var field in wanted.Skip(s_fixedSortFields.Length))
        {
            SortFields.Add(field);
        }

        NotifySortBarChanged();
    }

    internal static GallerySortField FieldOf(GallerySort sort) => sort switch
    {
        GallerySort.NameAsc or GallerySort.NameDesc => GallerySortField.Name,
        GallerySort.SizeAsc or GallerySort.SizeDesc => GallerySortField.Size,
        GallerySort.CapturedAsc or GallerySort.CapturedDesc => GallerySortField.Captured,
        _ => GallerySortField.Modified,
    };

    internal static bool DefaultDescending(GallerySortField field) => field != GallerySortField.Name && field != GallerySortField.Category;

    internal static GallerySort SortFor(GallerySortField field, bool descending) => field switch
    {
        GallerySortField.Name => descending ? GallerySort.NameDesc : GallerySort.NameAsc,
        GallerySortField.Size => descending ? GallerySort.SizeDesc : GallerySort.SizeAsc,
        GallerySortField.Captured => descending ? GallerySort.CapturedDesc : GallerySort.CapturedAsc,
        _ => descending ? GallerySort.ModifiedDesc : GallerySort.ModifiedAsc,
    };
}
