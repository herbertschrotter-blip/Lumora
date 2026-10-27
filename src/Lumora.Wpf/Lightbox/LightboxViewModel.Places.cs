using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Messaging;
using Lumora.Services.Places;

namespace Lumora.Lightbox;

/// <summary>
/// Ort in der Info-Leiste der Lightbox (ADR-0050): Zeile „Ort“ nach „Aufgenommen“, z. B. „Wien, Österreich (48,2082° N,
/// 16,3738° O)“ — nur, wenn die Datei Koordinaten hat. Asynchron aus dem Katalog; nach „Ort setzen…“, Rückgängig oder
/// dem Nachtragen (<see cref="GeoLocationsChangedMessage"/>) neu.
/// </summary>
public sealed partial class LightboxViewModel
{
    internal const string PlaceLabel = "Ort";

    private IGeoCatalogReader? _geoReader;
    private int _placeRowVersion;

    private void InitPlaces(IGeoCatalogReader? geoReader)
    {
        _geoReader = geoReader;
        if (geoReader is null)
        {
            return;
        }

        WeakReferenceMessenger.Default.Register<LightboxViewModel, GeoLocationsChangedMessage>(
            this,
            static (r, m) => r._dispatcher.InvokeAsync(() => r.OnGeoLocationsChanged(m)));
    }

    private void OnGeoLocationsChanged(GeoLocationsChangedMessage message)
    {
        if (!_disposed && CurrentItem is { } item && message.FileIds.Contains(item.FileId))
        {
            QueuePlaceRow(item);
        }
    }

    /// <summary>Nach <c>BuildFileInfo</c>: Zeile „Ort“ nachladen (jüngster Auftrag gewinnt).</summary>
    private void QueuePlaceRow(LightboxItem item)
    {
        if (_geoReader is null)
        {
            return;
        }

        var version = ++_placeRowVersion;
        _ = LoadPlaceRowAsync(item, version);
        QueueGeoHint(item); // „Position geschätzt“ (ADR-0062)
        QueueTour(item); // Abschnitt „Tour“ (ADR-0073 Stufe 7, Mockup 24)
    }

    private async Task LoadPlaceRowAsync(LightboxItem item, int version)
    {
        try
        {
            var info = await _geoReader!.GetAsync(item.FileId).ConfigureAwait(true);
            if (_disposed || version != _placeRowVersion || !ReferenceEquals(item, CurrentItem))
            {
                return;
            }

            var existing = FileInfoRows.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Label == PlaceLabel);
            if (info?.Text is not { } text)
            {
                if (existing.r is not null)
                {
                    FileInfoRows.RemoveAt(existing.i); // Ort entfernt
                }

                return;
            }

            var newRow = new LightboxInfoRow(PlaceLabel, text);
            if (existing.r is not null)
            {
                FileInfoRows[existing.i] = newRow;
                return;
            }

            var anchor = FileInfoRows.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Label == CaptureDateLabel);
            if (anchor.r is null)
            {
                anchor = FileInfoRows.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Label == "Geändert");
            }

            FileInfoRows.Insert(anchor.r is not null ? anchor.i + 1 : FileInfoRows.Count, newRow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ort für die Lightbox nicht lesbar (FileId {FileId})", item.FileId);
        }
    }
}
