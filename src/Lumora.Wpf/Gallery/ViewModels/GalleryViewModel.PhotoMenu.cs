using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.PhotoMenu;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Teile des neuen Rechtsklick-Menüs, die nur die Galerie kann (ADR-0068, Mockup 17): Ergebnis von „Suchen ▸“ als Bedingung (wie
/// die Bildinhalt-Suche, mit allen übrigen Filtern UND), „Gesichter anzeigen“ (Lightbox mit Rahmen) und die Dateien der Auswahl
/// für Strg+C bzw. Ziehen nach außen.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>Die Bedingung stammt aus „Suchen ▸“ (Beschriftung „Fotos am …“ statt „Bildinhalt „…““).</summary>
    private bool _appliedSearchIsRelated;

    /// <summary>Ergebniszeile setzen (Menü-Aktionen, Strg+C).</summary>
    public void ShowStatus(string text) => SetResultStatus(text);

    /// <summary>„Suchen ▸ …“: gefundene Dateien als Bedingung zeigen (ersetzt eine Bildinhalt-Suche, Motive bleiben).</summary>
    public Task ApplyRelatedSearchAsync(string label, IReadOnlyList<int> fileIds)
    {
        if (!HasLibrary)
        {
            return Task.CompletedTask;
        }

        _contentSearchIds = fileIds.Distinct().ToList();
        _appliedContentSearch = label;
        _appliedSearchIsRelated = true;
        ContentSearchText = null;
        _logger.LogInformation("Galerie: „Suchen“ mit {Count} Treffern", _contentSearchIds.Count);
        NotifyMotifFilterChanged();
        PageIndex = 0;
        return ChangeQueryAsync();
    }

    /// <summary>„Personen ▸ Gesichter anzeigen“: Lightbox mit eingeschalteten Gesichtsrahmen öffnen.</summary>
    public async Task OpenWithFacesAsync(GalleryItem item)
    {
        try
        {
            await _viewSettings.SetAsync(Lightbox.Faces.LightboxFacesViewModel.ShowFacesKey, "true");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gesichtsrahmen-Einstellung nicht gespeichert");
        }

        OpenInLightboxCommand.Execute(item);
    }

    /// <summary>Ausgewählte Kacheln der Seite (bzw. die angeklickte) als Kandidaten für den Weg nach außen.</summary>
    public IReadOnlyList<ExternalCandidate> ExternalCandidates(GalleryItem? target)
    {
        var selected = CurrentPage.Where(i => i.IsSelected).ToList();
        if (target is not null && !target.IsSelected)
        {
            selected = [target];
        }

        return selected.Select(i => new ExternalCandidate(i.Id, i.SourcePath, i.Missing, i.MediaType)).ToList();
    }
}
