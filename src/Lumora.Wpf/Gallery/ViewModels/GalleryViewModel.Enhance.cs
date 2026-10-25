using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Enhance;
using Lumora.Gallery.Data;
using Lumora.Services.Enhance;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// „Foto verbessern“ in der Galerie (ADR-0071, Mockup 13): Rechtsklick „Bearbeiten ▸ Foto verbessern…“ (ein Foto → Lightbox mit
/// der Leiste „Verbessern“, mehrere → Dialog und Lauf im Hintergrund), Marke „2 Fassungen“ (Klick öffnet Original und Fassungen
/// in der Lightbox) und „beste Fassung zeigen“ (Originale mit Fassung ausgeblendet). Ohne Dienst (Tests) wirkungslos.
/// </summary>
public sealed partial class GalleryViewModel
{
    private EnhanceGalleryActions? _enhance;
    private bool _hideVariantOriginals;
    private long _variantMarksSequence;

    public bool HasEnhance => _enhance is not null;

    private void InitEnhance(EnhanceGalleryActions? actions)
    {
        _enhance = actions;
        if (actions is null)
        {
            return;
        }

        _messenger.Register<GalleryViewModel, EnhanceSettingsChangedMessage>(this, static (r, m) => r.RunOnUi(() => r.HandleEnhanceSettingsChangedAsync(m)));
        FireAndForget(async () =>
        {
            var settings = await actions.Settings.LoadAsync();
            if (settings.ShowBestVariant != _hideVariantOriginals)
            {
                await DispatchAsync(() => _hideVariantOriginals = settings.ShowBestVariant);
                if (_isInitialized)
                {
                    await DispatchAsync(() => ScheduleReload());
                }
            }
        });
    }

    /// <summary>Abfrage um „beste Fassung zeigen“ ergänzen.</summary>
    private GalleryQuery WithVariants(GalleryQuery query) => _hideVariantOriginals ? query with { HideVariantOriginals = true } : query;

    private Task HandleEnhanceSettingsChangedAsync(EnhanceSettingsChangedMessage m)
    {
        if (m.Settings.ShowBestVariant == _hideVariantOriginals)
        {
            return Task.CompletedTask;
        }

        _hideVariantOriginals = m.Settings.ShowBestVariant;
        return HasLibrary && _isInitialized ? ChangeQueryAsync() : Task.CompletedTask;
    }

    /// <summary>Nach dem Laden einer Seite: Marke „2 Fassungen“.</summary>
    private async Task ApplyVariantMarksAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (_enhance is null || fileIds.Count == 0)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _variantMarksSequence);
        try
        {
            var groups = await _enhance.Variants.GetGroupsAsync(fileIds, ct);
            if (ct.IsCancellationRequested || sequence != Interlocked.Read(ref _variantMarksSequence))
            {
                return;
            }

            await DispatchAsync(() =>
            {
                foreach (var id in fileIds)
                {
                    if (_currentItems.TryGetValue(id, out var item))
                    {
                        item.Variants = groups.GetValueOrDefault(id);
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fassungen der Seite konnten nicht ermittelt werden");
        }
    }

    /// <summary>Marke „2 Fassungen“: Original und Fassungen in der Lightbox (Original zuerst, Start auf der angeklickten Datei).</summary>
    [RelayCommand]
    private async Task OpenVariantsAsync(GalleryItem? item)
    {
        if (_enhance is null || item?.Variants is not { } group)
        {
            return;
        }

        try
        {
            var entries = await _enhance.LoadEntriesAsync(group.AllFileIds);
            if (entries.Count == 0)
            {
                return;
            }

            var start = Math.Max(0, entries.ToList().FindIndex(e => e.FileId == item.Id));
            _lightbox.Open(entries, start, OwnerWindowResolver?.Invoke() ?? Application.Current?.MainWindow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fassungen konnten nicht geöffnet werden");
            SetResultStatus($"Fassungen konnten nicht geöffnet werden: {ex.Message}");
        }
    }

    /// <summary>Rechtsklick „Bearbeiten ▸ Foto verbessern…“: ein Foto → Lightbox mit Leiste, mehrere → Dialog + Lauf.</summary>
    public async Task EnhanceFromMenuAsync(GalleryItem target, IReadOnlyList<int> ids)
    {
        if (_enhance is null)
        {
            return;
        }

        if (ids.Count <= 1)
        {
            OpenInLightboxCommand.Execute(target);
            _lightbox.ShowEnhancePanel();
            return;
        }

        await EnhanceManyAsync(ids);
    }

    /// <summary>Mehrere Fotos: Dialog (Werkzeuge, Zeitschätzung), dann Lauf im Hintergrund (Fortschritt in den Aktivitäten).</summary>
    public async Task EnhanceManyAsync(IReadOnlyList<int> ids)
    {
        if (_enhance is null || ids.Count == 0)
        {
            return;
        }

        var options = await _enhance.Dialog.ShowAsync(ids, OwnerWindowResolver?.Invoke());
        if (options is null)
        {
            return;
        }

        SetResultStatus($"Foto verbessern: {ids.Count:N0} Fotos im Hintergrund — Fortschritt in den Aktivitäten.");
        try
        {
            var result = await Task.Run(() => _enhance.Service.RunBatchAsync(ids, options));
            SetResultStatus(result.Canceled
                ? $"Foto verbessern abgebrochen — {result.Saved:N0} gespeichert."
                : $"Foto verbessern: {result.Saved:N0} neue Fassungen" + (result.Skipped > 0 ? $", {result.Skipped:N0} übersprungen" : string.Empty)
                  + (result.Failed > 0 ? $", {result.Failed:N0} fehlgeschlagen" : string.Empty) + " — die Originale bleiben unverändert.");
        }
        catch (EnhanceModelMissingException ex)
        {
            SetResultStatus(ex.Message);
        }
        catch (Exception ex) when (ex is EnhanceException or InvalidOperationException or System.IO.IOException)
        {
            _logger.LogWarning(ex, "Foto verbessern über mehrere Fotos fehlgeschlagen");
            SetResultStatus($"Foto verbessern fehlgeschlagen: {ex.Message}");
        }
    }
}
