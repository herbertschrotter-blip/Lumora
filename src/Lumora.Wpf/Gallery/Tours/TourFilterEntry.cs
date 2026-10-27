using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Services.Places.Tracks;
using Wpf.Ui.Controls;

namespace Lumora.Gallery.Tours;

/// <summary>
/// Eine Tour in der Auswahl des Filters „Tour“ (Mockup 24): Name, Zeile „Bergtour · So, 15.03.2020 · 14,2 km · ↑ 912 m ·
/// Koralpe“, Gipfel-Zeile und Zahl der Fotos im Bereich; Jahres-Überschrift vor der ersten Tour eines Jahres.
/// </summary>
public sealed partial class TourFilterEntry : ObservableObject
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public TourFilterEntry(AppTourInfo tour, bool firstOfYear)
    {
        Tour = tour;
        YearText = firstOfYear ? tour.LocalStart.Year.ToString(CultureInfo.InvariantCulture) : null;
        var parts = new List<string> { Sport(tour.Sport), tour.LocalStart.ToString("ddd, dd.MM.yyyy", German) };
        if (tour.DistanceMeters > 0)
        {
            parts.Add((tour.DistanceMeters / 1000).ToString("0.0", German) + " km");
        }

        if (tour.AscentMeters > 0)
        {
            parts.Add("↑ " + tour.AscentMeters.ToString("#,0", German) + " m");
        }

        if (!string.IsNullOrWhiteSpace(tour.Region))
        {
            parts.Add(tour.Region!);
        }

        Line = string.Join(" · ", parts);
        PeakLine = tour.Summits.Count == 0 ? null : string.Join(" · ", tour.Summits.Select(s => $"▲ {s.Name} {s.ElevationM.ToString("#,0", German)} m"));
        CountText = tour.PhotoCount switch
        {
            0 => "keine Fotos",
            1 => "1 Foto",
            _ => $"{tour.PhotoCount.ToString("#,0", German)} Fotos",
        };
        SearchText = string.Join(' ', new[] { tour.Name, tour.Region ?? string.Empty }.Concat(tour.Summits.Select(s => s.Name))).ToLowerInvariant();
    }

    public AppTourInfo Tour { get; }

    public string Name => Tour.Name;

    /// <summary>„2020“ vor der ersten Tour eines Jahres, sonst null.</summary>
    public string? YearText { get; }

    public string Line { get; }

    public string? PeakLine { get; }

    public string CountText { get; }

    public bool HasPhotos => Tour.PhotoCount > 0;

    /// <summary>Radfahren bekommt ein Rad, alles andere den Wanderer.</summary>
    public SymbolRegular Symbol => Tour.Sport is "cycling" or "road_cycling" or "mountain_biking" ? SymbolRegular.VehicleBicycle24 : SymbolRegular.Run24;

    internal string SearchText { get; }

    [ObservableProperty] private bool _isVisible = true;

    [ObservableProperty] private bool _isSelected;

    /// <summary>Jahres-Überschrift nur, wenn die Tour selbst sichtbar ist.</summary>
    public bool ShowYear => YearText is not null && IsVisible;

    partial void OnIsVisibleChanged(bool value) => OnPropertyChanged(nameof(ShowYear));

    /// <summary>Sportart in Worten (wie Lumoras eigene Spuren).</summary>
    public static string Sport(string sport) => sport switch
    {
        "hiking" => "Wandern",
        "mountain_hiking" => "Bergtour",
        "mountaineering" => "Hochtour",
        "trail_running" => "Trailrunning",
        "running" => "Laufen",
        "cycling" => "Radfahren",
        "road_cycling" => "Rennrad",
        "mountain_biking" => "Mountainbike",
        "ski_touring" => "Skitour",
        "skiing" => "Ski alpin",
        _ => "Tour",
    };
}

/// <summary>Schnellfilter oben in der Auswahl („Alle Touren“, „nur mit Fotos“, Sportarten).</summary>
public sealed record TourQuickFilter(string Key, string Label);
