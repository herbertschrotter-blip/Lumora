using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Messaging;
using Lumora.Services.Usage;

namespace Lumora.Lightbox;

/// <summary>
/// Nutzungs-Signale in der Lightbox (ADR-0072): misst je gezeigter Datei die Zeit, in der das Fenster vorne ist
/// (<see cref="UsageViewTracker"/>; das Fenster meldet Aktivieren/Deaktivieren/Minimieren über <see cref="SetUsageActive"/>),
/// Zoom über <see cref="IsZoomFit"/> → false. Geöffnet aus einer Story zählt gedämpft, aus „Doppelte“ gar nicht
/// (<see cref="UsageContext"/>, gesetzt von <see cref="LightboxService"/>). Zeile „Nutzung“ im Info-Bereich: was Lumora über
/// das Foto gelernt hat (nur mit „Nutzung lernen“).
/// </summary>
public sealed partial class LightboxViewModel
{
    internal const string UsageLabel = "Nutzung";

    private IUsageService? _usageService;
    private UsageViewTracker? _usage;
    private int _usageRowVersion;

    private void InitUsage(IUsageSignals? signals, IUsageService? service)
    {
        _usageService = service;
        _usage = signals?.CreateTracker(UsageViewContext.Normal);
        if (service is not null)
        {
            WeakReferenceMessenger.Default.Register<LightboxViewModel, UsageChangedMessage>(
                this,
                static (r, _) => r._dispatcher.InvokeAsync(() =>
                {
                    if (!r._disposed && r.CurrentItem is { } item)
                    {
                        r.QueueUsageRow(item);
                    }
                }));
        }
    }

    /// <summary>Messung der Ansichten (Rauchtest: eigene Uhr über <see cref="UsageViewTracker.UseTime"/>).</summary>
    internal UsageViewTracker? UsageTracker => _usage;

    /// <summary>Woher die Lightbox geöffnet wurde (zählt voll, gedämpft oder gar nicht).</summary>
    public UsageViewContext UsageContext
    {
        get => _usage?.Context ?? UsageViewContext.None;
        set
        {
            if (_usage is not null && _usage.Context != value)
            {
                _usage.Stop(); // Ansicht des alten Kontexts abschließen
                _usage.Context = value;
            }
        }
    }

    /// <summary>Fenster vorne (Aktiviert, nicht minimiert) bzw. im Hintergrund.</summary>
    public void SetUsageActive(bool active) => _usage?.SetActive(active);

    /// <summary>Aus <c>NavigateTo</c>: neue Datei messen (Archive nicht).</summary>
    private void NoteUsageShow(LightboxItem? item) => _usage?.Show(item is { IsArchive: false } ? item.FileId : null);

    private void NoteUsageZoom() => _usage?.NoteZoom();

    private void StopUsage() => _usage?.Stop();

    /// <summary>Nach <c>BuildFileInfo</c>: Zeile „Nutzung“ nachladen (jüngster Auftrag gewinnt).</summary>
    private void QueueUsageRow(LightboxItem item)
    {
        if (_usageService is null)
        {
            return;
        }

        var version = ++_usageRowVersion;
        _ = LoadUsageRowAsync(item, version);
    }

    private async Task LoadUsageRowAsync(LightboxItem item, int version)
    {
        try
        {
            var totals = await _usageService!.GetTotalsAsync(item.FileId).ConfigureAwait(true);
            if (_disposed || version != _usageRowVersion || !ReferenceEquals(item, CurrentItem))
            {
                return;
            }

            var text = UsageScoring.Describe(totals);
            var existing = FileInfoRows.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Label == UsageLabel);
            if (text is null)
            {
                if (existing.r is not null)
                {
                    FileInfoRows.RemoveAt(existing.i);
                }

                return;
            }

            var row = new LightboxInfoRow(UsageLabel, text);
            if (existing.r is not null)
            {
                FileInfoRows[existing.i] = row;
            }
            else
            {
                FileInfoRows.Add(row);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lightbox: Nutzung nicht lesbar (FileId {FileId})", item.FileId);
        }
    }
}
