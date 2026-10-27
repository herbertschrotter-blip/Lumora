using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Data.RawSql;
using Lumora.Gallery.Data;
using Wpf.Ui.Controls;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Galerie-Filter unabhängig kombinierbar (0.2.43, ADR-0041): Dateiart, nur Favoriten, Ablage und Mindest-Bewertung
/// als eigene Eigenschaften statt eines Einzel-Enums; zusammen mit Tag-, Personen-, Kategorie- und Suchfilter gelten
/// alle mit UND. Dazu die Anzeige der Filterkarte: Zähler für „Filter zurücksetzen“, Bedingungen in Worten
/// („Es gilt: Bilder UND Favoriten …“, jede einzeln entfernbar) und die Trefferzeile unter dem Titel.
/// Der Ordnerfilter (Auswahl im Ordnerbaum) ist kein Filter in diesem Sinn und bleibt beim Zurücksetzen erhalten.
/// </summary>
public sealed partial class GalleryViewModel
{
    private const int SearchDebounceMs = 450;

    private static readonly GalleryMediaOption[] s_mediaOptions =
    [
        new(GalleryMediaKind.All, "Alle", SymbolRegular.Empty, "Alle Dateiarten"),
        new(GalleryMediaKind.Images, "Bilder", SymbolRegular.Image24, "Nur Bilder"),
        new(GalleryMediaKind.Videos, "Videos", SymbolRegular.Video24, "Nur Videos"),
        new(GalleryMediaKind.Archives, "Archive", SymbolRegular.FolderZip24, "Nur ZIP-, RAR- und 7z-Archive"),
    ];

    private static readonly GalleryRatingOption[] s_ratingOptions =
    [
        new(0, "egal"),
        new(1, "ab 1 Stern"),
        new(2, "ab 2 Sternen"),
        new(3, "ab 3 Sternen"),
        new(4, "ab 4 Sternen"),
        new(5, "nur 5 Sterne"),
    ];

    private static readonly GalleryLocationOption[] s_locationOptions =
    [
        new(GalleryLocation.Anywhere, "Überall", "Eingänge und Bibliothek"),
        new(GalleryLocation.Inbox, "Noch im Eingang", "Dateien in Eingängen (unsortierte Quellordner)"),
        new(GalleryLocation.Library, "In der Bibliothek", "Dateien im Bibliotheksordner"),
        new(GalleryLocation.Missing, "Nur fehlende", "Dateien, die es auf der Platte nicht mehr gibt — lassen sich aus der Bibliothek entfernen"), // ADR-0065
    ];

    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>Suchtext, mit dem die zuletzt geladene Trefferliste gesucht hat (null = keine Suche).</summary>
    private string? _appliedSearchTerm;

    // ---- Datei-Filter --------------------------------------------------------------------------

    /// <summary>Dateiart (Segment „Alle | Bilder | Videos | Archive“).</summary>
    [ObservableProperty] private GalleryMediaKind _mediaFilter = GalleryMediaKind.All;

    /// <summary>Umschalter „Nur Favoriten“.</summary>
    [ObservableProperty] private bool _favoritesOnly;

    /// <summary>Ablage: überall / noch im Eingang / in der Bibliothek (ADR-0020).</summary>
    [ObservableProperty] private GalleryLocation _locationFilter = GalleryLocation.Anywhere;

    /// <summary>Mindest-Bewertung 0–5 (0 = egal).</summary>
    [ObservableProperty] private int _minRating;

    public IReadOnlyList<GalleryMediaOption> MediaOptions => s_mediaOptions;

    public IReadOnlyList<GalleryRatingOption> RatingOptions => s_ratingOptions;

    public IReadOnlyList<GalleryLocationOption> LocationOptions => s_locationOptions;

    /// <summary>
    /// Datei-Teil des Filters als ein Wert. Setzen übernimmt alle vier Teile und lädt einmal neu (Rauchtest, Tests).
    /// </summary>
    public GalleryFilterState FilterState
    {
        get => new(MediaFilter, FavoritesOnly, LocationFilter, MinRating) { Place = _placeFilter, Geo = _geoFilter, Duplicates = DuplicateFilter, LovedOnly = LovedOnly, Tour = _tourFilter }; // Ort/Karte: ADR-0050, Doppelte: ADR-0053, Lieblinge: ADR-0072, Tour: ADR-0073
        set
        {
            var suppress = _suppressReload;
            _suppressReload = true;
            try
            {
                MediaFilter = value.Media;
                FavoritesOnly = value.FavoritesOnly;
                LocationFilter = value.Location;
                MinRating = value.EffectiveMinRating;
                DuplicatesOnly = value.Duplicates is not null; // ADR-0053
                LovedOnly = value.LovedOnly; // ADR-0072
                SetPlaceFiltersFromState(value); // GalleryViewModel.Places.cs
                SetTourFromState(value); // GalleryViewModel.Tours.cs
            }
            finally
            {
                _suppressReload = suppress;
            }

            NotifyFileFilterChanged();
            NotifyPlaceFilterChanged();
            NotifyTourFilterChanged();
            if (!_suppressReload && _isInitialized)
            {
                FireAndForget(ChangeQueryAsync);
            }
        }
    }

    /// <summary>Favoriten- oder Bewertungs-Filter aktiv: Metadaten-Änderungen können die Trefferliste ändern.</summary>
    private bool FileFilterDependsOnMetadata => FavoritesOnly || MinRating > 0;

    public bool HasRatingFilter => MinRating > 0;

    public bool HasLocationFilter => LocationFilter != GalleryLocation.Anywhere;

    /// <summary>Wert im Knopf „Bewertung: …“.</summary>
    public string RatingFilterText => s_ratingOptions.FirstOrDefault(o => o.MinRating == MinRating)?.Label ?? "egal";

    /// <summary>Wert im Knopf „Ablage: …“ (Langform wie in der Auswahl).</summary>
    public string LocationFilterText => s_locationOptions.First(o => o.Location == LocationFilter).Label;

    /// <summary>Kurzer Wert im Chip „Ablage: …“ (Überall / Eingang / Bibliothek), damit die Zeile „Datei“ bei 1280 DIP einzeilig bleibt.</summary>
    public string LocationChipText => LocationFilter switch
    {
        GalleryLocation.Inbox => "Eingang",
        GalleryLocation.Library => "Bibliothek",
        GalleryLocation.Missing => "Fehlt", // ADR-0065
        _ => "Überall",
    };

    /// <summary>Kurzer Wert im Chip „Bewertung: …“ („egal“, „ab 3 ★“, „5 ★“).</summary>
    public string RatingChipText => MinRating switch
    {
        0 => "egal",
        GalleryFilterState.MaxRating => "5 ★",
        _ => $"ab {MinRating} ★",
    };

    partial void OnMediaFilterChanged(GalleryMediaKind value) => OnFileFilterPartChanged();

    partial void OnFavoritesOnlyChanged(bool value) => OnFileFilterPartChanged();

    partial void OnLocationFilterChanged(GalleryLocation value) => OnFileFilterPartChanged();

    partial void OnMinRatingChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, GalleryFilterState.MaxRating);
        if (clamped != value)
        {
            MinRating = clamped; // löst den Handler erneut mit dem gültigen Wert aus
            return;
        }

        OnFileFilterPartChanged();
    }

    private void OnFileFilterPartChanged()
    {
        NotifyFileFilterChanged();
        if (!_suppressReload && _isInitialized)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    private void NotifyFileFilterChanged()
    {
        OnPropertyChanged(nameof(FilterState));
        OnPropertyChanged(nameof(HasRatingFilter));
        OnPropertyChanged(nameof(HasLocationFilter));
        OnPropertyChanged(nameof(RatingFilterText));
        OnPropertyChanged(nameof(LocationFilterText));
        OnPropertyChanged(nameof(LocationChipText));
        OnPropertyChanged(nameof(RatingChipText));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(ShowRemoveMissingAction)); // „Aus der Bibliothek entfernen“ (ADR-0065)
        RefreshFilterIndicators();
    }

    /// <summary>Datei-Filter auf „keiner“ (ohne Neuladen; Aufrufer lädt).</summary>
    private void ResetFileFilter()
    {
        var suppress = _suppressReload;
        _suppressReload = true;
        try
        {
            MediaFilter = GalleryMediaKind.All;
            FavoritesOnly = false;
            LocationFilter = GalleryLocation.Anywhere;
            MinRating = 0;
            DuplicatesOnly = false; // ADR-0053
            LovedOnly = false; // ADR-0072
            ResetPlaceFilterCore(); // Ort und Kartenausschnitt (ADR-0050)
            ResetTourFilterCore(); // Tour (ADR-0073 Stufe 7)
        }
        finally
        {
            _suppressReload = suppress;
        }

        NotifyFileFilterChanged();
        NotifyPlaceFilterChanged();
        NotifyTourFilterChanged();
    }

    /// <summary>Knopf „Bewertung“: Eintrag gewählt.</summary>
    [RelayCommand]
    private void SetMinRating(GalleryRatingOption? option)
    {
        if (option is not null)
        {
            MinRating = option.MinRating;
        }
    }

    /// <summary>Knopf „Ablage“: Eintrag gewählt.</summary>
    [RelayCommand]
    private void SetLocation(GalleryLocationOption? option)
    {
        if (option is not null)
        {
            LocationFilter = option.Location;
        }
    }

    internal static string EmptyMessageForFileFilter(GalleryFilterState filter)
    {
        if (filter.Tour is not null)
        {
            return "Keine Fotos aus der Zeit dieser Tour."; // ADR-0073 Stufe 7
        }

        if (filter.Geo is { IsActive: true })
        {
            return "Keine Dateien in diesem Kartenausschnitt."; // ADR-0050
        }

        if (filter.Place is { IsActive: true })
        {
            return "Keine Dateien an diesem Ort.";
        }

        if (filter.Media != GalleryMediaKind.All)
        {
            return filter.Media switch
            {
                GalleryMediaKind.Images => "Keine Bilder in dieser Bibliothek.",
                GalleryMediaKind.Videos => "Keine Videos in dieser Bibliothek.",
                _ => "Keine Archive (ZIP, RAR, 7z …) in dieser Bibliothek.",
            };
        }

        if (filter.Duplicates is not null)
        {
            return DuplicatesEmptyMessage; // ADR-0053
        }

        if (filter.LovedOnly)
        {
            return LovedEmptyMessage; // ADR-0072
        }

        if (filter.FavoritesOnly)
        {
            return "Noch keine Favoriten — markiere Dateien mit dem Herz.";
        }

        if (filter.EffectiveMinRating > 0)
        {
            return $"Keine Dateien {s_ratingOptions[filter.EffectiveMinRating].Label} — bewerte Dateien mit den Sternen auf der Kachel.";
        }

        return filter.Location switch
        {
            GalleryLocation.Inbox => "Nichts mehr im Eingang — alle Dateien sind in der Bibliothek. Eingänge verwaltest du unter „Bibliothek“.",
            GalleryLocation.Missing => "Keine fehlenden Dateien — alles, was die Bibliothek kennt, liegt auch auf der Platte.", // ADR-0065
            GalleryLocation.Library => "Noch nichts in der Bibliothek — wähle Dateien und verschiebe sie mit „In die Bibliothek…“ (Strg+M) oder übernimm einen Ordner im Ordnerbaum.",
            _ => "Diese Bibliothek enthält noch keine Dateien. Starte unter „Bibliothek“ einen Scan.",
        };
    }

    // ---- Werte der Inhalt-Chips (Tags / Personen / Kategorien) --------------------------------------

    /// <summary>Wert im Chip „Tags: …“ („alle“ ohne Filter).</summary>
    public string TagFilterValueText => ValueOf(TagFilterText, "Tags");

    /// <summary>Wert im Chip „Personen: …“.</summary>
    public string PersonFilterValueText => ValueOf(PersonFilterText, "Personen");

    /// <summary>Wert im Chip „Kategorien: …“.</summary>
    public string CategoryFilterValueText => HasCategoryFilter ? CategoryFilterText : "alle";

    private static string ValueOf(string buttonText, string prefix)
        => buttonText.StartsWith(prefix + ": ", StringComparison.Ordinal) ? buttonText[(prefix.Length + 2)..] : "alle";

    /// <summary>
    /// Aus <c>OnPropertyChanged</c> (GalleryViewModel.Categories.cs): ändert sich die Beschriftung eines Inhalt-Filters,
    /// folgen dessen Chip-Wert, Zähler und Bedingungszeile.
    /// </summary>
    private void OnFilterTextChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(TagFilterText):
                OnPropertyChanged(nameof(TagFilterValueText));
                RefreshFilterIndicators();
                break;
            case nameof(PersonFilterText):
                OnPropertyChanged(nameof(PersonFilterValueText));
                RefreshFilterIndicators();
                break;
            case nameof(CategoryFilterText):
                OnPropertyChanged(nameof(CategoryFilterValueText));
                RefreshFilterIndicators();
                break;
        }
    }

    // ---- Zähler, „Es gilt“-Zeile, Zurücksetzen ---------------------------------------------------

    /// <summary>Bedingungen der Zeile „Es gilt: … UND …“ (leer ohne Filter).</summary>
    public ObservableCollection<GalleryFilterCondition> ActiveConditions { get; } = new();

    /// <summary>Anzahl wirkender Filter (Datei-Teile, Tags, Personen, je Kategorie, Suche) — Zähler am Knopf „Filter zurücksetzen“.</summary>
    public int ActiveFilterCount => ActiveConditions.Count;

    public bool HasActiveFilters => ActiveFilterCount > 0;

    /// <summary>Bedingungen neu aufbauen (nach jeder Filteränderung und jedem Laden).</summary>
    private void RefreshFilterIndicators()
    {
        var conditions = BuildConditions();
        if (!conditions.SequenceEqual(ActiveConditions))
        {
            ActiveConditions.Clear();
            foreach (var condition in conditions)
            {
                ActiveConditions.Add(condition);
            }
        }

        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ResultCountText));
        OnPropertyChanged(nameof(EmptyMessage));
        ResetFiltersCommand.NotifyCanExecuteChanged();
    }

    private List<GalleryFilterCondition> BuildConditions()
    {
        var list = new List<(GalleryFilterConditionKind Kind, string Label, int? CategoryId)>();
        if (_appliedSearchTerm is { } term)
        {
            list.Add((GalleryFilterConditionKind.Search, $"Suche „{term}“", null));
        }

        if (MediaFilter != GalleryMediaKind.All)
        {
            list.Add((GalleryFilterConditionKind.Media, s_mediaOptions.First(o => o.Media == MediaFilter).Label, null));
        }

        if (FavoritesOnly)
        {
            list.Add((GalleryFilterConditionKind.Favorites, "Favoriten", null));
        }

        if (DuplicatesOnly)
        {
            list.Add((GalleryFilterConditionKind.Duplicates, DuplicatesConditionLabel, null)); // ADR-0053
        }

        if (LovedOnly)
        {
            list.Add((GalleryFilterConditionKind.Loved, LovedConditionLabel, null)); // ADR-0072
        }

        if (MinRating > 0)
        {
            list.Add((GalleryFilterConditionKind.Rating, s_ratingOptions[MinRating].Label, null));
        }

        if (LocationFilter != GalleryLocation.Anywhere)
        {
            list.Add((GalleryFilterConditionKind.Location, LocationFilter switch { GalleryLocation.Inbox => "noch im Eingang", GalleryLocation.Missing => "nur fehlende", _ => "in der Bibliothek" }, null)); // „nur fehlende“: ADR-0065
        }

        list.AddRange(PlaceConditions().Select(c => (c.Kind, c.Label, (int?)null))); // Ort, Kartenausschnitt (ADR-0050)
        list.AddRange(TourConditions().Select(c => (c.Kind, c.Label, (int?)null))); // Tour (ADR-0073 Stufe 7)

        if (HasTagFilter)
        {
            list.Add((GalleryFilterConditionKind.Tags, TagConditionLabel(), null));
        }

        if (HasPersonFilter)
        {
            list.Add((GalleryFilterConditionKind.Persons, PersonConditionLabel(), null));
        }

        if (RelationConditionLabel is { } relationLabel) // Beziehung (0.2.47, GalleryViewModel.Relations.cs)
        {
            list.Add((GalleryFilterConditionKind.Relations, relationLabel, null));
        }

        if (MotifConditionLabel() is { } motifLabel) // Motive (ADR-0052, GalleryViewModel.Motifs.cs)
        {
            list.Add((GalleryFilterConditionKind.Motifs, motifLabel, null));
        }

        if (AlbumConditionLabel is { } albumLabel) // Album (ADR-0054, GalleryViewModel.Albums.cs)
        {
            list.Add((GalleryFilterConditionKind.Album, albumLabel, null));
        }

        foreach (var group in CategoryFilterGroups.Where(g => g.IsActive))
        {
            var values = group.Entries.Where(e => e.IsSelected).Select(e => e.IsNone ? "ohne Wert" : $"„{e.Name}“");
            list.Add((GalleryFilterConditionKind.Category, $"{group.Name}: {string.Join(" oder ", values)}", group.CategoryId));
        }

        return list.Select((c, i) => new GalleryFilterCondition(c.Kind, c.Label, c.CategoryId, i == 0)).ToList();
    }

    private string TagConditionLabel()
    {
        if (TagFilterMode == GalleryTagMode.None)
        {
            return "ohne Tag";
        }

        var names = TagFilterEntries.Where(e => e.IsSelected).Select(e => $"„{e.Name}“").ToList();
        return names.Count switch
        {
            1 => $"Tag {names[0]}",
            _ => TagFilterMode == GalleryTagMode.All
                ? $"Tags {string.Join(" und ", names)}"
                : $"Tag {string.Join(" oder ", names)}",
        };
    }

    private string PersonConditionLabel()
    {
        var suffix = PersonFilterIncludeSuggestions && PersonFilterMode != GalleryPersonMode.AnyFace ? " (mit Vorschlägen)" : string.Empty;
        switch (PersonFilterMode)
        {
            case GalleryPersonMode.NoPerson:
                return "ohne benannte Person" + suffix;
            case GalleryPersonMode.UnnamedFaces:
                return "unbenannte Gesichter" + suffix;
            case GalleryPersonMode.AnyFace:
                return "irgendein Gesicht";
        }

        var names = PersonFilterEntries.Where(e => e.IsSelected).Select(e => $"„{e.Name}“").ToList();
        var text = names.Count switch
        {
            1 => $"Person {names[0]}",
            _ => PersonFilterMode == GalleryPersonMode.All
                ? $"Personen {string.Join(" und ", names)}"
                : $"Person {string.Join(" oder ", names)}",
        };
        return text + suffix;
    }

    /// <summary>„Filter zurücksetzen“: Datei-Filter, Tags, Personen, Kategorien und Suche — die Ordnerauswahl bleibt.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveFilters))]
    private Task ResetFiltersAsync()
    {
        _searchDebounceCts?.Cancel();
        var suppress = _suppressReload;
        _suppressReload = true;
        try
        {
            ResetFileFilter();
            ResetTagFilter();
            ResetPersonFilter();
            ResetCategoryFilter();
            ResetMotifFilter(); // ADR-0052
            ResetAlbumFilter(); // ADR-0054
            SearchTerm = null;
        }
        finally
        {
            _suppressReload = suppress;
        }

        _appliedSearchTerm = null;
        RefreshFilterIndicators();
        _logger.LogInformation("Galerie: alle Filter zurückgesetzt");
        return ChangeQueryAsync();
    }

    /// <summary>× an einer Bedingung der Zeile „Es gilt“: entfernt genau diese Bedingung.</summary>
    [RelayCommand]
    private Task RemoveFilterConditionAsync(GalleryFilterCondition? condition)
    {
        if (condition is null)
        {
            return Task.CompletedTask;
        }

        switch (condition.Kind)
        {
            case GalleryFilterConditionKind.Search:
                return ClearSearchAsync();
            case GalleryFilterConditionKind.Media:
                MediaFilter = GalleryMediaKind.All;
                break;
            case GalleryFilterConditionKind.Favorites:
                FavoritesOnly = false;
                break;
            case GalleryFilterConditionKind.Rating:
                MinRating = 0;
                break;
            case GalleryFilterConditionKind.Duplicates:
                DuplicatesOnly = false; // ADR-0053
                break;
            case GalleryFilterConditionKind.Loved:
                LovedOnly = false; // ADR-0072
                break;
            case GalleryFilterConditionKind.Location:
                LocationFilter = GalleryLocation.Anywhere;
                break;
            case GalleryFilterConditionKind.Tags:
                return ClearTagFilterAsync();
            case GalleryFilterConditionKind.Persons:
                return ClearPersonFilterAsync();
            case GalleryFilterConditionKind.Relations:
                return ClearRelationFilterAsync();
            case GalleryFilterConditionKind.Place:
                return ClearPlaceFilterAsync(); // ADR-0050
            case GalleryFilterConditionKind.MapArea:
                return ClearMapAreaFilterAsync();
            case GalleryFilterConditionKind.Tour:
                return ClearTourFilterAsync(); // ADR-0073 Stufe 7
            case GalleryFilterConditionKind.Motifs:
                return ClearMotifFilterAsync(); // ADR-0052
            case GalleryFilterConditionKind.Album:
                return ClearAlbumFilterAsync(); // ADR-0054
            case GalleryFilterConditionKind.Category when condition.CategoryId is { } categoryId:
                var group = CategoryFilterGroups.FirstOrDefault(g => g.CategoryId == categoryId);
                if (group is null || !group.IsActive)
                {
                    break;
                }

                foreach (var entry in group.Entries)
                {
                    entry.IsSelected = false;
                }

                return ApplyCategoryFilterAsync();
        }

        return Task.CompletedTask;
    }

    // ---- Trefferzeile unter dem Titel ----------------------------------------------------------------

    /// <summary>Dateien nur mit Ordnerfilter (Bezugsgröße „von N Dateien“).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultCountText))]
    private int _unfilteredCount;

    /// <summary>„ von 1.248 Dateien“ bzw. „ Dateien“ hinter der fett gesetzten Trefferzahl.</summary>
    public string ResultCountText
        => HasActiveFilters
            ? $" von {UnfilteredCount:N0} {(UnfilteredCount == 1 ? "Datei" : "Dateien")}"
            : TotalCount == 1 ? " Datei" : " Dateien";

    /// <summary>Nach dem Zählen: angewendete Suche merken, Bezugsgröße zählen (nur mit Filtern nötig), Anzeige auffrischen.</summary>
    private async Task UpdateFilterSummaryAsync(GalleryQuery query, int total, CancellationToken ct)
    {
        _appliedSearchTerm = query.HasSearch ? query.SearchTerm : null;
        var filtered = query.HasSearch || query.Filter.IsActive || query.HasTagFilter || query.HasPersonFilter
                       || query.ActiveCategoryFilters.Count > 0 || query.HasMotifFilter || query.AlbumId is not null;
        if (!filtered)
        {
            UnfilteredCount = total;
        }
        else
        {
            try
            {
                UnfilteredCount = await _source.GetUnfilteredCountAsync(query, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                UnfilteredCount = total;
                _logger.LogWarning(ex, "Bezugsgröße der Trefferzeile konnte nicht gezählt werden");
            }
        }

        RefreshFilterIndicators();
    }

    // ---- Suche: Enter sucht sofort, sonst nach kurzer Pause ----------------------------------------

    partial void OnSearchTermChanged(string? value)
    {
        _searchDebounceCts?.Cancel();
        if (_suppressReload || !_isInitialized || !HasLibrary)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        FireAndForget(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ApplyDebouncedSearchAsync(value);
        });
    }

    /// <summary>
    /// Suche nach der Tipp-Pause: nur wenn sich der wirksame Suchtext geändert hat. Text ohne Buchstaben/Ziffern (z. B.
    /// „--“ mitten im Tippen) bleibt stehen und sucht nicht — die Meldung dazu gibt es erst bei Enter.
    /// </summary>
    private Task ApplyDebouncedSearchAsync(string? value)
    {
        if (!string.Equals(value, SearchTerm, StringComparison.Ordinal))
        {
            return Task.CompletedTask; // inzwischen weitergetippt
        }

        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed is not null && FtsQueryBuilder.FromUserInput(trimmed) is null)
        {
            return Task.CompletedTask;
        }

        return string.Equals(trimmed, _appliedSearchTerm, StringComparison.Ordinal) ? Task.CompletedTask : ChangeQueryAsync();
    }
}
