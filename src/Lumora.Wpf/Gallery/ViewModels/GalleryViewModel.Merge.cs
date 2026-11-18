using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Imaging.Merge;
using Lumora.Merge;
using Lumora.Services.Messaging;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// „Zusammenfügen“ in der Galerie (ADR-0078 Punkte 7 und 9, Stufe 1 Teil B): Rechtsklick „Bearbeiten ▸ n Fotos
/// zusammenfügen…“ bzw. „Zusammenfügen…“ (ein Foto → Zeit-Nachbarn im Fenster), Aktionsleiste ab 2 gewählten Fotos, gesperrt bei
/// gemischten Bibliotheken (mit Grund), „auch ins Album …“ aus einem eigenen Album, Ergebnis-Marke „Panorama · aus n Fotos“ (je
/// Seite gebündelt, Klick öffnet Ergebnis und Quellen in der Lightbox). Ohne Anbindung (Tests) wirkungslos.
/// </summary>
public sealed partial class GalleryViewModel
{
    private MergeGalleryActions? _merge;
    private long _mergeMarksSequence;
    private string? _mergeSelectionBlock;

    public bool HasMerge => _merge is not null;

    /// <summary>Knopf „Zusammenfügen…“ in der Aktionsleiste: ab 2 gewählten Fotos.</summary>
    public bool ShowMergeAction => _merge is not null && SelectedCount >= MergeLimits.MinSources;

    /// <summary>Tooltip des Knopfs: Zweck bzw. Grund der Sperre.</summary>
    public string MergeActionToolTip => _mergeSelectionBlock ?? MergeTexts.MenuToolTip;

    private void InitMerge(MergeGalleryActions? actions)
    {
        _merge = actions;
        if (actions is null)
        {
            return;
        }

        // gespeichertes Ergebnis: Marken der Seite nachtragen (die neue Datei bringt FilesChanged → Neuladen)
        _messenger.Register<GalleryViewModel, MergeSavedMessage>(this, static (r, _) => r.RunOnUi(() => r.RefreshMergeMarksAsync()));
    }

    private void OnSelectionChangedForMerge()
    {
        if (_merge is null)
        {
            return;
        }

        // Sperrgrund nur ab 2 gewählten und nur einmal je Änderung (Strg+A auf großen Seiten)
        _mergeSelectionBlock = SelectedCount >= MergeLimits.MinSources ? MergeBlockReasonForSelection() : null;
        OnPropertyChanged(nameof(ShowMergeAction));
        OnPropertyChanged(nameof(MergeActionToolTip));
        MergeSelectionCommand.NotifyCanExecuteChanged();
    }

    private Task RefreshMergeMarksAsync() => ApplyMergeMarksAsync(_currentItems.Keys.ToList(), CancellationToken.None);

    /// <summary>Nach dem Laden einer Seite: Ergebnis-Marken gebündelt (eine Abfrage je Seite).</summary>
    private async Task ApplyMergeMarksAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (_merge is null || fileIds.Count == 0)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _mergeMarksSequence);
        try
        {
            var marks = await _merge.Store.GetMarksAsync(fileIds, ct);
            if (ct.IsCancellationRequested || sequence != Interlocked.Read(ref _mergeMarksSequence))
            {
                return;
            }

            await DispatchAsync(() =>
            {
                foreach (var id in fileIds)
                {
                    if (_currentItems.TryGetValue(id, out var item))
                    {
                        item.MergeMark = marks.GetValueOrDefault(id);
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ergebnis-Marken der Seite konnten nicht ermittelt werden");
        }
    }

    /// <summary>Ergebnis-Marke: Ergebnis und Quellen in der Lightbox, Ergebnis zuerst.</summary>
    [RelayCommand]
    private async Task OpenMergeResultAsync(GalleryItem? item)
    {
        if (_merge is null || item is not { IsMergeResult: true })
        {
            return;
        }

        await _merge.OpenResultAsync(item.Id, OwnerWindowResolver?.Invoke() ?? Application.Current?.MainWindow);
    }

    /// <summary>Grund der Sperre für diese Auswahl (null = frei); Menü und Aktionsleiste.</summary>
    public string? MergeBlockReason(GalleryItem target, IReadOnlyList<int> ids)
    {
        if (_merge is null)
        {
            return null;
        }

        var selection = ids.Select(id => _currentItems.GetValueOrDefault(id) ?? (id == target.Id ? target : null))
            .Where(i => i is not null)
            .Select(i => (i!.LibraryId, i.MediaType, i.Missing))
            .ToList();
        return _merge.BlockReason(selection);
    }

    private string? MergeBlockReasonForSelection()
    {
        var ids = SelectedIdsInPageOrder();
        return ids.Count == 0 || !_currentItems.TryGetValue(ids[0], out var first) ? null : MergeBlockReason(first, ids);
    }

    /// <summary>Rechtsklick „Bearbeiten ▸ … zusammenfügen…“.</summary>
    public Task MergeFromMenuAsync(GalleryItem target, IReadOnlyList<int> ids) => StartMergeAsync(ids.Count == 0 ? [target.Id] : ids);

    /// <summary>Aktionsleiste „Zusammenfügen…“ (ab 2 gewählten Fotos).</summary>
    [RelayCommand(CanExecute = nameof(CanMergeSelection))]
    private Task MergeSelectionAsync() => StartMergeAsync(SelectedIdsInPageOrder());

    private bool CanMergeSelection() => _merge is not null && SelectedCount >= MergeLimits.MinSources && _mergeSelectionBlock is null;

    private async Task StartMergeAsync(IReadOnlyList<int> ids)
    {
        if (_merge is null || ids.Count == 0)
        {
            return;
        }

        var first = _currentItems.GetValueOrDefault(ids[0]);
        if (first is not null && MergeBlockReason(first, ids) is { } reason)
        {
            SetResultStatus(reason);
            return;
        }

        // nur eigene Alben (ADR-0054): intelligente Alben haben keinen Haken „auch ins Album“
        var albumId = SelectedAlbumId;
        await _merge.StartAsync(new MergeStart(ids, albumId, albumId is null ? null : SelectedAlbumName), OwnerWindowResolver?.Invoke());
    }

    /// <summary>Album, aus dem die Lightbox geöffnet wird (sie übernimmt es für „Zusammenfügen…“, ADR-0078 Punkt 7).</summary>
    internal (int? Id, string? Name) CurrentAlbum => SelectedAlbumId is { } id ? (id, SelectedAlbumName) : (null, null);
}
