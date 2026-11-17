using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Cloud;
using Lumora.Services.Messaging;
using Lumora.Services.Thumbnails;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// OneDrive-Bibliothek in der Galerie (ADR-0077, Mockup 27):
/// <list type="bullet">
/// <item><b>Wolken-Marke</b>: nach dem Laden einer Seite liest die Galerie die Attribute ihrer Dateien (kein Download) und
/// setzt <see cref="GalleryItem.IsCloudOnly"/>.</item>
/// <item><b>Vorschaubilder</b>: lokale Dateien wie bisher die ganze Seite; Platzhalter zuerst die sichtbaren Kacheln plus
/// <see cref="CloudLookahead"/> davor und danach (fehlende per Shell-Abruf, der dauert bis zu Sekunden), alle übrigen der
/// Seite nur aus dem Cache (kein Shell-Abruf, nichts eingereiht — Nachbesserung 0.5.41). Ändert sich der sichtbare Bereich,
/// fordert die Galerie nach kurzer Ruhe (<see cref="CloudRangeDelay"/>) die neu sichtbaren nach.</item>
/// <item><b>Rechtsklick</b> „Immer auf diesem Gerät behalten“ / „Speicherplatz freigeben“ für die ausgewählten Dateien in
/// OneDrive, mit Statusmeldung; die Marke folgt, sobald Windows fertig ist (<see cref="CloudFilesChangedMessage"/>).</item>
/// </list>
/// Ohne <see cref="ICloudFileService"/> (Tests) bleibt alles wie vor 0.5.41.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>So viele Platzhalter vor und nach dem sichtbaren Bereich werden mit angefordert.</summary>
    internal const int CloudLookahead = 12;

    /// <summary>Ruhezeit nach dem Scrollen, bevor neu sichtbare Platzhalter angefordert werden.</summary>
    internal static readonly TimeSpan CloudRangeDelay = TimeSpan.FromMilliseconds(150);

    private ICloudFileService? _cloudFiles;
    private int _cloudRangeVersion;

    private void InitCloud(ICloudFileService? cloudFiles)
    {
        _cloudFiles = cloudFiles;
        if (cloudFiles is not null)
        {
            _messenger.Register<GalleryViewModel, CloudFilesChangedMessage>(
                this, static (r, m) => r.RunOnUi(() => r.HandleCloudFilesChangedAsync(m)));
        }
    }

    /// <summary>
    /// Ersatz für das Anfordern der ganzen Seite (Thread-Pool): Dateien unter einer Cloud-Wurzel zuerst einordnen
    /// (Platzhalter nur im sichtbaren Bereich), alle übrigen sofort anfordern wie bisher — erst danach deren Attribute lesen,
    /// damit eine Seite lauter lokaler Dateien kein Vorschaubild später bekommt. Ein Platzhalter außerhalb bekannter Wurzeln
    /// wird trotzdem nie heruntergeladen: der Vorschaubild-Dienst prüft selbst die Attribute.
    /// </summary>
    private async Task RequestThumbnailsWithCloudAsync(List<GalleryItem> ordered)
    {
        if (_cloudFiles is not { } cloud)
        {
            await RequestThumbnailsAsync(ordered);
            return;
        }

        var underRoots = ordered.Where(i => cloud.MayBeCloud(i.SourcePath) || i.IsCloudFile).ToList();
        var states = ClassifyCloud(cloud, underRoots);
        await ApplyCloudStatesAsync(states);
        var onlineOnly = states.Where(s => s.State.IsOnlineOnly).Select(s => s.Item.Id).ToHashSet();
        await RequestThumbnailsAsync(onlineOnly.Count == 0 ? ordered : ordered.Where(i => !onlineOnly.Contains(i.Id)).ToList());
        if (onlineOnly.Count > 0)
        {
            // sichtbare zuerst (fehlende per Shell-Abruf); alle übrigen der Seite nur aus dem Cache — sofort zeigen, ohne
            // Shell-Abruf und ohne Warteschlange, sonst blitzen bei jedem Besuch leere Kacheln auf (Nachbesserung 0.5.41)
            var visible = await RequestVisibleCloudThumbnailsAsync();
            await ReportCachedThumbnailsAsync(ordered.Where(i => onlineOnly.Contains(i.Id) && !visible.Contains(i.Id)).ToList());
        }

        if (underRoots.Count < ordered.Count)
        {
            var rest = ordered.Except(underRoots).ToList();
            await ApplyCloudStatesAsync(ClassifyCloud(cloud, rest)); // nur noch die Marke
        }
    }

    /// <summary>Nur Cache-Treffer melden lassen (<see cref="IThumbnailService.ReportCachedThumbnailsAsync"/>).</summary>
    private async Task ReportCachedThumbnailsAsync(IReadOnlyList<GalleryItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var requests = items.Select(ToThumbnailRequest).ToList();
        try
        {
            await _thumbnails.ReportCachedThumbnailsAsync(requests);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vorschaubilder aus dem Cache: Abfrage fehlgeschlagen");
        }
    }

    private static Task ApplyCloudStatesAsync(List<(GalleryItem Item, CloudFileState State)> states)
        => states.Count == 0
            ? Task.CompletedTask
            : DispatchAsync(() =>
            {
                foreach (var (item, state) in states)
                {
                    item.ApplyCloudState(state);
                }
            });

    /// <summary>Zustand je Datei aus den Attributen (Netzlaufwerke ausgenommen: dort gibt es keine Platzhalter).</summary>
    internal static List<(GalleryItem Item, CloudFileState State)> ClassifyCloud(ICloudFileService cloud, IReadOnlyList<GalleryItem> items)
    {
        var result = new List<(GalleryItem, CloudFileState)>();
        foreach (var item in items)
        {
            if (item.SourcePath.StartsWith(@"\\", StringComparison.Ordinal) && !cloud.MayBeCloud(item.SourcePath))
            {
                continue;
            }

            var state = cloud.GetState(item.SourcePath);
            if (state.IsCloud || item.IsCloudFile)
            {
                result.Add((item, state));
            }
        }

        return result;
    }

    /// <summary>Aus <see cref="ReportVisibleRange"/>: nach kurzer Ruhe die neu sichtbaren Platzhalter anfordern.</summary>
    private void OnVisibleRangeChangedForCloud()
    {
        if (_cloudFiles is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _cloudRangeVersion);
        FireAndForget(async () =>
        {
            await Task.Delay(CloudRangeDelay);
            if (version == Volatile.Read(ref _cloudRangeVersion))
            {
                await RequestVisibleCloudThumbnailsAsync();
            }
        });
    }

    /// <returns>Ids der angeforderten Platzhalter.</returns>
    private async Task<HashSet<int>> RequestVisibleCloudThumbnailsAsync()
    {
        List<GalleryItem> wanted = [];
        await DispatchAsync(() => wanted = CloudItemsInView(CurrentPage, _visibleFirstIndex, _visibleLastIndex, CloudLookahead));
        if (wanted.Count > 0)
        {
            await Task.Run(() => RequestThumbnailsAsync(wanted));
        }

        return wanted.Select(i => i.Id).ToHashSet();
    }

    /// <summary>
    /// Platzhalter ohne Vorschaubild im sichtbaren Bereich [first, last] ± <paramref name="lookahead"/>, sichtbare zuerst, sonst
    /// nach Abstand. Ohne bekannten Bereich die ersten 2 × <paramref name="lookahead"/> Kacheln.
    /// </summary>
    internal static List<GalleryItem> CloudItemsInView(IList<GalleryItem> page, int first, int last, int lookahead)
    {
        if (page.Count == 0)
        {
            return [];
        }

        if (first < 0 || last < first)
        {
            first = 0;
            last = Math.Min(page.Count - 1, (2 * lookahead) - 1);
            lookahead = 0;
        }

        var from = Math.Max(0, first - lookahead);
        var to = Math.Min(page.Count - 1, last + lookahead);
        var found = new List<(GalleryItem Item, int Distance)>();
        for (var i = from; i <= to; i++)
        {
            var item = page[i];
            if (item.IsCloudOnly && item.Thumbnail is null)
            {
                found.Add((item, i < first ? first - i : i > last ? i - last : 0));
            }
        }

        return found.OrderBy(f => f.Distance).Select(f => f.Item).ToList();
    }

    /// <summary>Windows hat Dateien geladen bzw. freigegeben: Marken der betroffenen Kacheln neu lesen.</summary>
    private async Task HandleCloudFilesChangedAsync(CloudFilesChangedMessage message)
    {
        if (_cloudFiles is not { } cloud)
        {
            return;
        }

        var items = message.FileIds.Select(id => _currentItems.GetValueOrDefault(id)).OfType<GalleryItem>().ToList();
        if (items.Count == 0)
        {
            return;
        }

        var states = await Task.Run(() => items.Select(i => (Item: i, State: cloud.GetState(i.SourcePath))).ToList());
        foreach (var (item, state) in states)
        {
            item.ApplyCloudState(state);
        }
    }

    /// <summary>Enthält die Auswahl (bzw. diese Ids) Dateien in OneDrive? Für das Rechtsklick-Menü.</summary>
    internal bool HasCloudFilesIn(IReadOnlyList<int> fileIds)
        => _cloudFiles is not null && fileIds.Any(id => _currentItems.TryGetValue(id, out var item) && item.IsCloudFile);

    [RelayCommand]
    private Task KeepSelectionOnDeviceAsync() => SetSelectionPinAsync(CloudPinAction.KeepOnDevice);

    [RelayCommand]
    private Task FreeSelectionSpaceAsync() => SetSelectionPinAsync(CloudPinAction.FreeUpSpace);

    private async Task SetSelectionPinAsync(CloudPinAction action)
    {
        if (_cloudFiles is not { } cloud)
        {
            return;
        }

        var files = SelectedIdsInPageOrder()
            .Select(id => _currentItems.GetValueOrDefault(id))
            .OfType<GalleryItem>()
            .Select(i => new CloudFileRef(i.LibraryId != 0 ? i.LibraryId : _libraryId, i.Id, i.SourcePath))
            .ToList();
        if (files.Count == 0)
        {
            ShowStatus("Keine Datei ausgewählt.");
            return;
        }

        var result = await Task.Run(() => cloud.SetPinState(files, action));
        ShowStatus(result.StatusText);
        await HandleCloudFilesChangedAsync(new CloudFilesChangedMessage(_libraryId, files.Select(f => f.FileId).ToList(), BecameLocal: false));
    }
}
