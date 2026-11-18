using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge;
using Lumora.Merge;
using Lumora.Services.Merge;

namespace Lumora.Lightbox;

/// <summary>
/// „Zusammenfügen“ in der Lightbox (ADR-0078 Punkte 7 und 10, Mockup 28 Ansicht 14; Stufe 1 Teil B): Zeile „Zusammengefügt aus
/// n Fotos · Panorama, Kugel 360° · Quellen zeigen · Neu berechnen…“ unter dem Bild und Info-Abschnitt „Zusammengefügt“ bei einem
/// Ergebnis; Rechtsklick „Bearbeiten ▸ Zusammenfügen…“ mit dem Album der Galerie, aus der die Lightbox geöffnet wurde.
/// </summary>
public sealed partial class LightboxViewModel
{
    private MergeGalleryActions? _merge;
    private int _mergeInfoVersion;
    private (int? Id, string? Name) _mergeAlbum;

    public bool HasMerge => _merge is not null;

    /// <summary>Info-Abschnitt „Zusammengefügt“.</summary>
    public ObservableCollection<LightboxInfoRow> MergeInfoRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMergeInfo), nameof(MergeRowText))]
    private MergedPhotoInfo? _mergeInfo;

    public bool HasMergeInfo => MergeInfo is not null;

    /// <summary>„Zusammengefügt aus 26 Fotos · Panorama, Kugel 360°“.</summary>
    public string MergeRowText => MergeInfo is { } i ? MergeTexts.LightboxRow(i.Method, i.Projection, i.SourceCount) : string.Empty;

    /// <summary>Album, aus dem die Lightbox geöffnet wurde (vom <see cref="LightboxService"/> gesetzt).</summary>
    public int? MergeAlbumId => _mergeAlbum.Id;

    private void InitMerge(MergeGalleryActions? merge) => _merge = merge;

    /// <summary>Album der Galerie übernehmen (null = ohne Album, etwa aus einer Story geöffnet).</summary>
    public void SetMergeAlbum(int? albumId, string? albumName) => _mergeAlbum = (albumId, albumName);

    /// <summary>Beim Blättern: Zeile und Abschnitt nachladen (jüngster Auftrag gewinnt).</summary>
    private void UpdateMergeForCurrent()
    {
        var version = ++_mergeInfoVersion;
        MergeInfo = null;
        MergeInfoRows.Clear();
        if (_merge is null || CurrentItem is not { IsImage: true } item)
        {
            return;
        }

        _ = LoadMergeInfoAsync(item, version);
    }

    private async Task LoadMergeInfoAsync(LightboxItem item, int version)
    {
        try
        {
            var info = await _merge!.Store.GetInfoAsync(item.FileId).ConfigureAwait(true);
            if (_disposed || version != _mergeInfoVersion || !ReferenceEquals(item, CurrentItem) || info is null)
            {
                return;
            }

            MergeInfo = info;
            var recipe = MergeRecipe.FromJson(info.RecipeJson);
            var de = CultureInfo.GetCultureInfo("de-DE");
            MergeInfoRows.Add(new LightboxInfoRow("Verfahren", MergeTexts.Method(info.Method)));
            if (info.Projection is { } p)
            {
                MergeInfoRows.Add(new LightboxInfoRow("Aufnahmeart", MergeTexts.Projection(p)
                    + (p == PanoramaProjection.Spherical360 && recipe is not null ? " · " + MergeTexts.SphereForm(recipe.SphereForm) : string.Empty)));
            }

            if (recipe is not null)
            {
                MergeInfoRows.Add(new LightboxInfoRow("Zuschnitt", MergeTexts.Crop(recipe.Options.Crop)));
                MergeInfoRows.Add(new LightboxInfoRow("Farbe kräftiger", MergeTexts.ColorBoost(recipe.ColorBoost)));
                if (recipe.Scale < 0.999)
                {
                    MergeInfoRows.Add(new LightboxInfoRow("Größe", string.Create(de, $"{Math.Round(recipe.Scale * 100):0} % der vollen Auflösung")));
                }
            }

            MergeInfoRows.Add(new LightboxInfoRow("Quelle", "JPEG"));
            var missing = info.MissingSources;
            MergeInfoRows.Add(new LightboxInfoRow("Quellen", string.Create(de, $"{info.SourceCount:N0} · ")
                + (missing == 0 ? "alle vorhanden" : string.Create(de, $"{missing:N0} nicht mehr in der Bibliothek"))));
            MergeInfoRows.Add(new LightboxInfoRow("Erstellt", info.CreatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", de) + (recipe?.App is { Length: > 0 } app ? " mit " + app : string.Empty)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lightbox: Zusammengefügt-Angaben nicht lesbar (FileId {FileId})", item.FileId);
        }
    }

    /// <summary>„Quellen zeigen“: Ergebnis und Quellen in der Lightbox, Start beim ersten Einzelbild.</summary>
    [RelayCommand]
    private Task ShowMergeSourcesAsync()
        => _merge is not null && MergeInfo is { } info ? _merge.ShowSourcesAsync(info.FileId, null) : Task.CompletedTask;

    /// <summary>„Neu berechnen…“: Fenster mit den Quellen und dem Rezept; speichert als weitere Datei.</summary>
    [RelayCommand]
    private Task RecomputeMergeAsync()
        => _merge is not null && MergeInfo is { } info ? _merge.RecomputeAsync(info.FileId, null, _mergeAlbum.Id, _mergeAlbum.Name) : Task.CompletedTask;

    /// <summary>Rechtsklick „Bearbeiten ▸ Zusammenfügen…“ (ein Foto → Zeit-Nachbarn im Fenster).</summary>
    public Task StartMergeAsync()
        => _merge is not null && CurrentItem is { IsImage: true } item
            ? _merge.StartAsync(new MergeStart([item.FileId], _mergeAlbum.Id, _mergeAlbum.Name), null)
            : Task.CompletedTask;

    /// <summary>Sperrgrund für das Menü (fehlende Datei, nicht verfügbar, Speichern läuft); null = frei.</summary>
    public string? MergeBlockReason(LightboxItem item, bool missing)
        => _merge?.BlockReason([(0, item.MediaType, missing)]);
}
