using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Data;
using Lumora.Gallery.Tours;
using Lumora.Services.Messaging;
using Lumora.Services.Places.Tracks;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Galerie-Filter „Tour“ (App-Verbindung Stufe 7 Teil B, ADR-0073, Mockup 24): Chip „Tour ▾“ in der Zeile „Inhalt“, Auswahl
/// der Touren verbundener Apps nach Jahren (neueste zuerst) mit Sportart, Datum, Länge, Höhenmetern, Region, Gipfeln und der
/// Zahl der Fotos im Bereich, Suche und Schnellfilter. Gewählt gilt der Zeitraum der Tour (Zeitabgleich wie bei „Ungenaue
/// Orte“, <see cref="IAppTourReader"/>) UND mit allen anderen Filtern; „Es gilt“ nennt die Tour. Über den Fotos die
/// Tour-Leiste mit Zahlen, Gipfeln, Zeitstrahl, „Auf der Karte“ und „Im Gipfelbuch öffnen“. Aus der Lightbox kommt
/// <see cref="ShowTourInGalleryMessage"/>. Wechselt der Bibliotheksbereich, werden die Fotos der Tour neu bestimmt.
/// </summary>
public sealed partial class GalleryViewModel
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static readonly IReadOnlyList<TourQuickFilter> s_tourQuickFilters =
    [
        new("all", "Alle Touren"),
        new("photos", "nur mit Fotos"),
        new("mountain_hiking", "Bergtour"),
        new("hiking", "Wandern"),
        new("cycling", "Radfahren"),
        new("mountaineering", "Hochtour"),
    ];

    private IAppTourReader? _tourReader;
    private Lumora.AppConnect.AppConnectRuntime? _appConnect;
    private Lumora.Shell.INavigationService? _tourNavigation;
    private GalleryTourFilter? _tourFilter;
    private AppTourInfo? _activeTour;
    private long _tourSequence;
    private bool _anyTours;

    /// <summary>Touren in der Auswahl (flach; Sichtbarkeit über Suche und Schnellfilter).</summary>
    public ObservableCollection<TourFilterEntry> TourFilterEntries { get; } = new();

    public IReadOnlyList<TourQuickFilter> TourQuickFilters => s_tourQuickFilters;

    [ObservableProperty] private string? _tourFilterSearch;

    [ObservableProperty] private TourQuickFilter _selectedTourQuickFilter = s_tourQuickFilters[0];

    [ObservableProperty] private bool _isLoadingTours;

    /// <summary>„412 Touren aus dem Gipfelbuch“ bzw. null ohne Touren.</summary>
    [ObservableProperty] private string? _toursSummaryText;

    /// <summary>Gibt es überhaupt Touren (sonst Leer-Zustand mit Weg zu „Verbundene Apps“)?</summary>
    [ObservableProperty] private bool _hasTours;

    /// <summary>Marken des Zeitstrahls der aktiven Tour (Fotos und Gipfel).</summary>
    [ObservableProperty] private IReadOnlyList<TourTimelineMark> _tourTimelineMarks = [];

    /// <summary>
    /// Chip „Tour“ sichtbar, sobald es Touren gibt (wie Alben und Kategorien) — ohne Touren bleibt die Zeile „Inhalt“ bei
    /// 1280 DIP einzeilig; mit allen anderen Chips bricht sie dann in zwei Zeilen um.
    /// </summary>
    public bool ShowTourFilter => _tourReader is not null && (_anyTours || _tourFilter is not null);

    public bool HasTourFilter => _tourFilter is not null;

    /// <summary>Wert im Chip („Tour: Handalm über Weinebene“ bzw. „Tour“).</summary>
    public string TourFilterChipText => _tourFilter is { Label: var label } ? "Tour: " + label : "Tour";

    public string? ActiveTourTitle => _activeTour?.Name;

    /// <summary>„Bergtour · So, 15.03.2020 · 08:12–14:40 · 14,2 km · ↑ 912 m · ▲ Handalm 1 853 m · Koralpe“.</summary>
    public string? ActiveTourFacts => _activeTour is { } t ? TourFacts(t) : null;

    public string? ActiveTourStartText => _activeTour?.LocalStart.ToString("HH:mm", German);

    public string? ActiveTourEndText => _activeTour?.LocalEnd.ToString("HH:mm", German);

    public bool CanShowTourOnMap => _activeTour?.Bounds is not null;

    public bool CanOpenTourApp => _activeTour is not null && _appConnect?.Client is not null;

    /// <summary>„Im Gipfelbuch öffnen“ (Anzeigename der App).</summary>
    public string OpenTourAppText => _activeTour is { } t && _appConnect?.Connections?.Find(t.AppId) is { } c ? $"Im {c.DisplayName} öffnen" : "In der App öffnen";

    private void InitTours(IAppTourReader? reader, Lumora.AppConnect.AppConnectRuntime? appConnect, Lumora.Shell.INavigationService? navigation)
    {
        _tourReader = reader;
        _appConnect = appConnect;
        _tourNavigation = navigation;
        _messenger.Register<GalleryViewModel, ShowTourInGalleryMessage>(this, static (r, m) => r.RunOnUi(() => r.ShowTourAsync(m.TourId)));
        // Ein Abgleich mit neuen Spuren meldet sich so — dann kann der Chip „Tour“ erscheinen
        _messenger.Register<GalleryViewModel, GpsTracksChangedMessage>(this, static (r, _) => r.RunOnUi(r.RefreshAnyToursAsync));
        if (reader is not null)
        {
            FireAndForget(RefreshAnyToursAsync);
        }
    }

    /// <summary>Gibt es Touren? Bestimmt die Sichtbarkeit des Chips „Tour“.</summary>
    private async Task RefreshAnyToursAsync()
    {
        if (_tourReader is null)
        {
            return;
        }

        SetAnyTours(await _tourReader.AnyAsync());
    }

    private void SetAnyTours(bool any)
    {
        if (_anyTours != any)
        {
            _anyTours = any;
            OnPropertyChanged(nameof(ShowTourFilter));
        }
    }

    partial void OnTourFilterSearchChanged(string? value) => UpdateTourVisibility();

    partial void OnSelectedTourQuickFilterChanged(TourQuickFilter value) => UpdateTourVisibility();

    /// <summary>Auswahl geöffnet: Touren mit Fotozahlen des Bereichs laden (immer frisch — ein Abgleich kann Touren bringen).</summary>
    [RelayCommand]
    private async Task LoadTourFilterAsync()
    {
        if (_tourReader is null)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _tourSequence);
        IsLoadingTours = true;
        try
        {
            var tours = await _tourReader.ListAsync(HasLibrary ? ScopeLibraryIds : []);
            if (sequence != Volatile.Read(ref _tourSequence))
            {
                return;
            }

            TourFilterEntries.Clear();
            int? year = null;
            foreach (var tour in tours)
            {
                var first = tour.LocalStart.Year != year;
                year = tour.LocalStart.Year;
                TourFilterEntries.Add(new TourFilterEntry(tour, first) { IsSelected = tour.Id == _tourFilter?.TourId });
            }

            HasTours = tours.Count > 0;
            SetAnyTours(HasTours);
            ToursSummaryText = HasTours ? $"{tours.Count.ToString("#,0", German)} {(tours.Count == 1 ? "Tour" : "Touren")} aus {AppNames(tours)}" : null;
            UpdateTourVisibility();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Touren konnten nicht geladen werden");
        }
        finally
        {
            IsLoadingTours = false;
        }
    }

    /// <summary>Tour in der Auswahl angeklickt.</summary>
    [RelayCommand]
    private Task SelectTourAsync(TourFilterEntry? entry) => entry is null ? Task.CompletedTask : ShowTourAsync(entry.Tour.Id);

    /// <summary>Filter „Tour“ auf <paramref name="tourId"/> setzen (Auswahl, Lightbox); zeigt die Galerie.</summary>
    internal async Task ShowTourAsync(int tourId)
    {
        if (_tourReader is null)
        {
            return;
        }

        try
        {
            var result = await _tourReader.PhotosAsync(tourId, HasLibrary ? ScopeLibraryIds : []);
            if (result is not { } found)
            {
                return;
            }

            ApplyTour(found.Tour, found.Photos);
            _tourNavigation?.NavigateTo(Lumora.Shell.NavigationTags.Gallery);
            _logger.LogInformation("Galerie: Filter Tour mit {Count} Datei(en)", found.Photos.Count);
            if (!_suppressReload && _isInitialized)
            {
                await ChangeQueryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Galerie: Tour konnte nicht gewählt werden");
        }
    }

    /// <summary>× an der Bedingung bzw. „Leeren“.</summary>
    [RelayCommand]
    private Task ClearTourFilterAsync()
    {
        if (_tourFilter is null)
        {
            return Task.CompletedTask;
        }

        ApplyTour(null, []);
        return !_suppressReload && _isInitialized ? ChangeQueryAsync() : Task.CompletedTask;
    }

    /// <summary>„Touren abgleichen“ in der Auswahl: gleich holen (die App darf dafür im Hintergrund starten).</summary>
    [RelayCommand]
    private async Task SyncToursAsync()
    {
        if (_appConnect?.Tours is not { } sync)
        {
            return;
        }

        IsLoadingTours = true;
        try
        {
            await Task.Run(() => sync.SyncAllAsync(mayStartApps: true));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Galerie: Touren nicht abgeglichen");
        }
        finally
        {
            IsLoadingTours = false;
        }

        await LoadTourFilterAsync();
    }

    /// <summary>Leer-Zustand: Einstellungen › Verbundene Apps.</summary>
    [RelayCommand]
    private void OpenConnectedApps() => _tourNavigation?.NavigateTo(Lumora.Shell.NavigationTags.SettingsConnectedApps);

    /// <summary>Tour-Leiste „Auf der Karte“: Seite „Orte“ auf den Rahmen der Tour.</summary>
    [RelayCommand]
    private void ShowTourOnMap()
    {
        if (_activeTour?.Bounds is { } bounds)
        {
            _messenger.Send(new ShowAreaOnMapMessage(bounds));
        }
    }

    /// <summary>Tour-Leiste „Im Gipfelbuch öffnen“: die App öffnen (läuft sie, zeigt ihr zweiter Start das Fenster).</summary>
    [RelayCommand]
    private void OpenTourApp()
    {
        if (_activeTour is { } tour)
        {
            _appConnect?.Client?.Open(tour.AppId);
        }
    }

    private void ApplyTour(AppTourInfo? tour, IReadOnlyList<AppTourPhoto> photos)
    {
        _activeTour = tour;
        _tourFilter = tour is null ? null : new GalleryTourFilter(tour.Id, photos.Select(p => p.FileId).ToList(), tour.Name);
        TourTimelineMarks = tour is null ? [] : Marks(tour, photos);
        foreach (var entry in TourFilterEntries)
        {
            entry.IsSelected = entry.Tour.Id == tour?.Id;
        }

        NotifyTourFilterChanged();
    }

    /// <summary>Bereich gewechselt: Fotos der aktiven Tour im neuen Bereich bestimmen, Zahlen der Auswahl neu.</summary>
    private void OnScopeChangedForTours()
    {
        if (_activeTour is { } tour && _tourReader is not null)
        {
            FireAndForget(async () =>
            {
                var result = await _tourReader.PhotosAsync(tour.Id, HasLibrary ? ScopeLibraryIds : []);
                if (_activeTour?.Id != tour.Id)
                {
                    return; // inzwischen aufgehoben oder eine andere Tour gewählt
                }

                ApplyTour(result?.Tour, result?.Photos ?? []);
                if (_isInitialized)
                {
                    await ChangeQueryAsync();
                }
            });
        }
    }

    /// <summary>Aus <c>ResetFileFilter</c> (ohne Neuladen).</summary>
    private void ResetTourFilterCore()
    {
        _activeTour = null;
        _tourFilter = null;
        TourTimelineMarks = [];
        foreach (var entry in TourFilterEntries)
        {
            entry.IsSelected = false;
        }
    }

    /// <summary>Aus dem Setter von <c>FilterState</c> (Tests, Rauchtest): nur Id-Liste und Text, ohne Tour-Leiste.</summary>
    private void SetTourFromState(GalleryFilterState value)
    {
        _tourFilter = value.Tour;
        if (value.Tour is null)
        {
            _activeTour = null;
            TourTimelineMarks = [];
        }
    }

    private void NotifyTourFilterChanged()
    {
        OnPropertyChanged(nameof(HasTourFilter));
        OnPropertyChanged(nameof(ShowTourFilter));
        OnPropertyChanged(nameof(TourFilterChipText));
        OnPropertyChanged(nameof(ActiveTourTitle));
        OnPropertyChanged(nameof(ActiveTourFacts));
        OnPropertyChanged(nameof(ActiveTourStartText));
        OnPropertyChanged(nameof(ActiveTourEndText));
        OnPropertyChanged(nameof(CanShowTourOnMap));
        OnPropertyChanged(nameof(CanOpenTourApp));
        OnPropertyChanged(nameof(OpenTourAppText));
        OnPropertyChanged(nameof(FilterState));
        OnPropertyChanged(nameof(EmptyMessage));
        RefreshFilterIndicators();
    }

    /// <summary>Bedingung für die „Es gilt“-Zeile: „Tour „Handalm“ · 15.03.2020 08:12–14:40“.</summary>
    private IEnumerable<(GalleryFilterConditionKind Kind, string Label)> TourConditions()
    {
        if (_tourFilter is { } filter)
        {
            var when = _activeTour is { } t ? $" · {t.LocalStart.ToString("dd.MM.yyyy HH:mm", German)}–{t.LocalEnd.ToString("HH:mm", German)}" : string.Empty;
            yield return (GalleryFilterConditionKind.Tour, $"Tour „{filter.Label}“{when}");
        }
    }

    private void UpdateTourVisibility()
    {
        var search = TourFilterSearch?.Trim().ToLowerInvariant();
        var quick = SelectedTourQuickFilter?.Key ?? "all";
        foreach (var entry in TourFilterEntries)
        {
            entry.IsVisible = (string.IsNullOrEmpty(search) || entry.SearchText.Contains(search, StringComparison.Ordinal))
                              && quick switch
                              {
                                  "all" => true,
                                  "photos" => entry.HasPhotos,
                                  "cycling" => entry.Tour.Sport is "cycling" or "road_cycling" or "mountain_biking",
                                  _ => entry.Tour.Sport == quick,
                              };
        }
    }

    private string AppNames(IReadOnlyList<AppTourInfo> tours)
    {
        var names = tours.Select(t => t.AppId).Distinct(StringComparer.Ordinal)
            .Select(id => _appConnect?.Connections?.Find(id)?.DisplayName ?? id).ToList();
        return names.Count == 1 ? "dem " + names[0] : string.Join(", ", names);
    }

    internal static string TourFacts(AppTourInfo t)
    {
        var parts = new List<string>
        {
            TourFilterEntry.Sport(t.Sport),
            $"{t.LocalStart.ToString("ddd, dd.MM.yyyy · HH:mm", German)}–{t.LocalEnd.ToString("HH:mm", German)}",
        };
        if (t.DistanceMeters > 0)
        {
            parts.Add((t.DistanceMeters / 1000).ToString("0.0", German) + " km");
        }

        if (t.AscentMeters > 0)
        {
            parts.Add("↑ " + t.AscentMeters.ToString("#,0", German) + " m");
        }

        parts.AddRange(t.Summits.Select(s => $"▲ {s.Name} {s.ElevationM.ToString("#,0", German)} m"));
        if (!string.IsNullOrWhiteSpace(t.Region))
        {
            parts.Add(t.Region!);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Zeitstrahl: je Foto ein Strich, je Gipfel eine Marke mit Ankunftszeit (Anteil 0…1 der Tourdauer).</summary>
    internal static IReadOnlyList<TourTimelineMark> Marks(AppTourInfo tour, IReadOnlyList<AppTourPhoto> photos)
    {
        var start = new DateTimeOffset(DateTime.SpecifyKind(tour.StartUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var span = Math.Max(1, new DateTimeOffset(DateTime.SpecifyKind(tour.EndUtc, DateTimeKind.Utc)).ToUnixTimeSeconds() - start);
        var marks = photos.Select(p => new TourTimelineMark(Math.Clamp((p.Utc - start) / (double)span, 0, 1), null)).ToList();
        foreach (var summit in tour.Summits)
        {
            var at = new DateTimeOffset(DateTime.SpecifyKind(summit.ArrivalUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var local = DateTime.SpecifyKind(summit.ArrivalUtc, DateTimeKind.Utc).AddMinutes(tour.StartOffsetMinutes);
            marks.Add(new TourTimelineMark(Math.Clamp((at - start) / (double)span, 0, 1), $"▲ {summit.Name} {local.ToString("HH:mm", German)}"));
        }

        return marks;
    }
}

/// <summary>Marke im Zeitstrahl der Tour-Leiste: Foto (<paramref name="Label"/> null) oder Gipfel.</summary>
public sealed record TourTimelineMark(double Fraction, string? Label);
