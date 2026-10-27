using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Tours;
using Lumora.Services.Messaging;

namespace Lumora.Lightbox;

/// <summary>
/// Abschnitt „Tour“ in der Info-Leiste der Lightbox (App-Verbindung Stufe 7 Teil B, Mockup 24): wurde das Foto während einer
/// Tour einer verbundenen App aufgenommen, stehen Name, Sportart, Länge und Höhenmeter, die Gipfel mit Ankunftszeit und der
/// Link „Alle n Fotos dieser Tour“ (setzt den Galerie-Filter „Tour“ und schließt die Lightbox).
/// </summary>
public sealed partial class LightboxViewModel
{
    private static readonly CultureInfo TourCulture = CultureInfo.GetCultureInfo("de-DE");
    private int _tourVersion;
    private int? _tourId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTour))]
    private string? _tourName;

    [ObservableProperty] private string? _tourDetails;

    [ObservableProperty] private string? _tourPeaks;

    [ObservableProperty] private string? _tourLinkText;

    public bool HasTour => TourName is not null;

    private void QueueTour(LightboxItem item)
    {
        if (_geoSuggestions?.Tours is null)
        {
            return;
        }

        var version = ++_tourVersion;
        _ = LoadTourAsync(item, version);
    }

    private async Task LoadTourAsync(LightboxItem item, int version)
    {
        try
        {
            var context = _geoSuggestions!;
            var tour = await context.Tours!.ForFileAsync(item.FileId).ConfigureAwait(true);
            int? count = null;
            if (tour is not null)
            {
                var scope = await context.Scope.GetCurrentAsync().ConfigureAwait(true);
                count = (await context.Tours.PhotosAsync(tour.Id, scope.LibraryIds).ConfigureAwait(true))?.Photos.Count;
            }

            if (_disposed || version != _tourVersion || !ReferenceEquals(item, CurrentItem))
            {
                return;
            }

            _tourId = tour?.Id;
            TourName = tour?.Name;
            if (tour is null)
            {
                TourDetails = TourPeaks = TourLinkText = null;
                return;
            }

            var parts = new System.Collections.Generic.List<string> { TourFilterEntry.Sport(tour.Sport) };
            if (tour.DistanceMeters > 0)
            {
                parts.Add((tour.DistanceMeters / 1000).ToString("0.0", TourCulture) + " km");
            }

            if (tour.AscentMeters > 0)
            {
                parts.Add("↑ " + tour.AscentMeters.ToString("#,0", TourCulture) + " m");
            }

            TourDetails = string.Join(" · ", parts);
            TourPeaks = tour.Summits.Count == 0
                ? null
                : string.Join(" · ", tour.Summits.Select(s =>
                    $"{s.Name} {s.ElevationM.ToString("#,0", TourCulture)} m (erreicht {DateTime.SpecifyKind(s.ArrivalUtc, DateTimeKind.Utc).AddMinutes(tour.StartOffsetMinutes).ToString("HH:mm", TourCulture)})"));
            TourLinkText = count is int n && n > 0 ? $"Alle {n.ToString("#,0", TourCulture)} Fotos dieser Tour" : "Fotos dieser Tour";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lightbox: Tour nicht geladen");
        }
    }

    /// <summary>„Alle n Fotos dieser Tour“: Galerie-Filter „Tour“ setzen, Lightbox schließen.</summary>
    [RelayCommand]
    private void ShowTourInGallery()
    {
        if (_tourId is not { } id)
        {
            return;
        }

        WeakReferenceMessenger.Default.Send(new ShowTourInGalleryMessage(id));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
