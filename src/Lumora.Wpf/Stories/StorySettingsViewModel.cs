using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Services.Library;
using Lumora.Services.Places;
using Lumora.Services.Stories;
using Lumora.Slideshow;

namespace Lumora.Stories;

/// <summary>Treffer der Ortssuche für „Zu Hause“.</summary>
public sealed record StoryHomeCandidate(PlaceInfo Place, double Latitude, double Longitude)
{
    public string Label => string.IsNullOrEmpty(Place.Region) ? $"{Place.Name}, {Place.Country}" : $"{Place.Name}, {Place.Region}, {Place.Country}";
}

/// <summary>
/// Einstellungen › Storys (ADR-0060, Mockup 09): Story-Arten an/aus, Zu Hause (automatisch = häufigster Ort, oder einen
/// Ort über die Ortssuche wählen), Pause bis zum neuen Ereignis, Mindestgröße, Highlights-Anteil; dazu der Abschnitt
/// „Diashow“ (aus „Darstellung“ hierher verschoben). Jede Änderung wird sofort gespeichert (<c>stories.*</c>); die
/// Erkennung rechnet danach neu (<see cref="Services.Messaging.StorySettingsChangedMessage"/>).
/// </summary>
public sealed partial class StorySettingsViewModel : ObservableObject
{
    private readonly IStorySettingsStore _store;
    private readonly ILogger<StorySettingsViewModel> _logger;
    private readonly IPlaceLookup? _places;
    private readonly IReverseGeocoder? _geocoder;
    private readonly StoryHomeCache? _homes;
    private readonly ILibraryScope? _scope;
    private StorySettings _current = StorySettings.Default;
    private bool _loading;

    public StorySettingsViewModel(
        IStorySettingsStore store,
        ILogger<StorySettingsViewModel> logger,
        SlideshowSettingsViewModel? slideshow = null,
        IPlaceLookup? places = null,
        IReverseGeocoder? geocoder = null,
        StoryHomeCache? homes = null,
        ILibraryScope? scope = null,
        Lumora.Usage.UsageSettingsViewModel? usage = null)
    {
        _store = store;
        _logger = logger;
        Slideshow = slideshow;
        Usage = usage; // Karte „Nutzung lernen“ (ADR-0072)
        _places = places;
        _geocoder = geocoder;
        _homes = homes;
        _scope = scope;
        _selectedPause = PauseOptions.First(o => o.Value == StorySettings.Default.PauseHours);
        _selectedMinFiles = MinFilesOptions.First(o => o.Value == StorySettings.Default.MinFiles);
        _selectedShare = ShareOptions.First(o => Math.Abs(o.Value - StorySettings.Default.HighlightShare) < 0.001);
    }

    /// <summary>Abschnitt „Diashow“ (ADR-0059), hierher verschoben.</summary>
    public SlideshowSettingsViewModel? Slideshow { get; }

    public bool HasSlideshow => Slideshow is not null;

    /// <summary>Karte „Nutzung lernen“ (ADR-0072, Mockup 20); null ohne Nutzungs-Signale.</summary>
    public Lumora.Usage.UsageSettingsViewModel? Usage { get; }

    public bool HasUsage => Usage is not null;

    public ObservableCollection<SlideshowOption<int>> PauseOptions { get; } =
    [
        new(3, "3 Stunden"),
        new(6, "6 Stunden"),
        new(12, "12 Stunden"),
        new(24, "24 Stunden"),
    ];

    public ObservableCollection<SlideshowOption<int>> MinFilesOptions { get; } =
    [
        new(8, "8 Fotos"),
        new(12, "12 Fotos"),
        new(20, "20 Fotos"),
        new(40, "40 Fotos"),
    ];

    public ObservableCollection<SlideshowOption<double>> ShareOptions { get; } =
    [
        new(0.08, "Knapp (~8 %)"),
        new(0.15, "Automatisch (~15 %)"),
        new(0.30, "Großzügig (~30 %)"),
    ];

    [ObservableProperty] private bool _eventsEnabled = true;
    [ObservableProperty] private bool _memoriesEnabled = true;
    [ObservableProperty] private bool _personsEnabled = true;
    [ObservableProperty] private bool _placesEnabled = true;
    [ObservableProperty] private SlideshowOption<int> _selectedPause;
    [ObservableProperty] private SlideshowOption<int> _selectedMinFiles;
    [ObservableProperty] private SlideshowOption<double> _selectedShare;
    [ObservableProperty] private string? _savedText;

    /// <summary>„Automatisch (Graz)“ bzw. „Graz, Steiermark, Österreich“.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeAutomatic))]
    private string _homeText = "Automatisch";

    [ObservableProperty] private bool _isChoosingHome;

    [ObservableProperty] private string _homeSearch = string.Empty;

    public ObservableCollection<StoryHomeCandidate> HomeCandidates { get; } = new();

    public bool IsHomeAutomatic => _current.HomeOrAutomatic.IsAutomatic;

    public bool CanSearchPlaces => _geocoder is not null;

    /// <summary>Text der Personen-Story: „ab 30 Fotos“.</summary>
    public string PersonsHint => $"Jahre mit vielen bestätigten Fotos einer Person (ab {_current.MinPersonFiles:N0} Fotos)";

    public async Task LoadAsync()
    {
        if (Usage is not null)
        {
            _ = Usage.LoadAsync(); // ADR-0072
        }

        StorySettings settings;
        try
        {
            settings = await _store.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Story-Einstellungen nicht lesbar");
            return;
        }

        _loading = true;
        try
        {
            _current = settings;
            EventsEnabled = settings.EventsEnabled;
            MemoriesEnabled = settings.MemoriesEnabled;
            PersonsEnabled = settings.PersonsEnabled;
            PlacesEnabled = settings.PlacesEnabled;
            SelectedPause = Pick(PauseOptions, settings.PauseHours, h => $"{h} Stunden");
            SelectedMinFiles = Pick(MinFilesOptions, settings.MinFiles, n => $"{n} Fotos");
            SelectedShare = ShareOptions.FirstOrDefault(o => Math.Abs(o.Value - settings.HighlightShare) < 0.001)
                ?? AddOption(ShareOptions, settings.HighlightShare, $"{settings.HighlightShare * 100:N0} %");
            SavedText = null;
            await RefreshHomeTextAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(PersonsHint));
        }
        finally
        {
            _loading = false;
        }
    }

    private static SlideshowOption<int> Pick(ObservableCollection<SlideshowOption<int>> options, int value, Func<int, string> label)
        => options.FirstOrDefault(o => o.Value == value) ?? AddOption(options, value, label(value));

    private static SlideshowOption<T> AddOption<T>(ObservableCollection<SlideshowOption<T>> options, T value, string label)
    {
        var option = new SlideshowOption<T>(value, label);
        options.Add(option);
        return option;
    }

    private async Task RefreshHomeTextAsync()
    {
        var home = _current.HomeOrAutomatic;
        if (!home.IsAutomatic)
        {
            HomeText = home.PlaceId is { } id && _places?.GetPlace(id) is { } place ? $"{place.Name}, {place.Country}" : "Gewählter Ort";
            return;
        }

        string? name = null;
        if (_scope is not null && _homes is not null && _places is not null)
        {
            var scope = await _scope.GetCurrentAsync().ConfigureAwait(true);
            if (_homes.TryGet(scope.GroupId, out var detected) && detected?.PlaceId is { } placeId)
            {
                name = _places.GetPlace(placeId)?.Name;
            }
        }

        HomeText = name is null ? "Automatisch (häufigster Ort)" : $"Automatisch ({name})";
    }

    /// <summary>Aktueller Stand als Einstellungen (Zu Hause bleibt, wie gewählt).</summary>
    public StorySettings ToSettings() => (_current with
    {
        EventsEnabled = EventsEnabled,
        MemoriesEnabled = MemoriesEnabled,
        PersonsEnabled = PersonsEnabled,
        PlacesEnabled = PlacesEnabled,
        PauseHours = SelectedPause.Value,
        MinFiles = SelectedMinFiles.Value,
        HighlightShare = SelectedShare.Value,
    }).Normalize();

    partial void OnEventsEnabledChanged(bool value) => Save();

    partial void OnMemoriesEnabledChanged(bool value) => Save();

    partial void OnPersonsEnabledChanged(bool value) => Save();

    partial void OnPlacesEnabledChanged(bool value) => Save();

    partial void OnSelectedPauseChanged(SlideshowOption<int> value) => Save();

    partial void OnSelectedMinFilesChanged(SlideshowOption<int> value) => Save();

    partial void OnSelectedShareChanged(SlideshowOption<double> value) => Save();

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        _ = SaveAsync();
    }

    /// <summary>Speichert den aktuellen Stand (öffentlich für Tests).</summary>
    public async Task SaveAsync()
    {
        try
        {
            _current = ToSettings();
            await _store.SaveAsync(_current).ConfigureAwait(true);
            SavedText = "Gespeichert — die Storys werden im Hintergrund neu berechnet.";
        }
        catch (Exception ex)
        {
            SavedText = $"Nicht gespeichert: {ex.Message}";
            _logger.LogWarning(ex, "Story-Einstellungen konnten nicht gespeichert werden");
        }
    }

    // ---- Zu Hause --------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ChooseHome()
    {
        IsChoosingHome = true;
        HomeCandidates.Clear();
    }

    [RelayCommand]
    private void CancelChooseHome()
    {
        IsChoosingHome = false;
        HomeSearch = string.Empty;
        HomeCandidates.Clear();
    }

    partial void OnHomeSearchChanged(string value)
    {
        HomeCandidates.Clear();
        if (_geocoder is null || string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
        {
            return;
        }

        try
        {
            foreach (var (place, lat, lon) in _geocoder.Search(value.Trim(), 8))
            {
                HomeCandidates.Add(new StoryHomeCandidate(place, lat, lon));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException)
        {
            _logger.LogDebug(ex, "Ortssuche für „Zu Hause“ fehlgeschlagen");
        }
    }

    [RelayCommand]
    public async Task SetHomeAsync(StoryHomeCandidate? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        _current = _current with { Home = new StoryHomeSetting(candidate.Place.PlaceId, candidate.Latitude, candidate.Longitude) };
        CancelChooseHome();
        await SaveAsync().ConfigureAwait(true);
        await RefreshHomeTextAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsHomeAutomatic));
    }

    [RelayCommand]
    public async Task SetHomeAutomaticAsync()
    {
        _current = _current with { Home = StoryHomeSetting.Automatic };
        CancelChooseHome();
        await SaveAsync().ConfigureAwait(true);
        await RefreshHomeTextAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsHomeAutomatic));
    }
}
