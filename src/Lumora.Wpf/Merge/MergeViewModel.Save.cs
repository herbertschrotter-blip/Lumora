using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge;
using Lumora.Imaging.Protocol;

namespace Lumora.Merge;

/// <summary>
/// Speichern im Fenster „Zusammenfügen“ (ADR-0078 Punkte 8, 10, 12; Mockup 28 Ansicht 11): Schrittliste mit Fortschritt und
/// Vorrang (nur solange gerechnet wird), „Im Hintergrund weiterrechnen“ (gibt den Vorrang zurück, schließt das Fenster, rechnet
/// mit halben Kernen weiter), Abbrechen, danach die Fertig-Ansicht mit Kennzeichnung „aus n Fotos zusammengefügt“, Öffnen, In der
/// Galerie zeigen, Rückgängig, Danach verbessern… und Im Explorer zeigen (versteckte Sitzung: gesperrt).
/// </summary>
public sealed partial class MergeViewModel
{
    private static readonly WorkerStep[] SaveSteps =
        [WorkerStep.Load, WorkerStep.Exposure, WorkerStep.Seams, WorkerStep.Deghost, WorkerStep.Warp, WorkerStep.Blend, WorkerStep.Finish, WorkerStep.Write];

    private MergeSaveResult? _saved;

    [ObservableProperty] private string _saveTitle = string.Empty;

    [ObservableProperty] private string _saveSubtitle = string.Empty;

    [ObservableProperty] private string _saveProgressText = string.Empty;

    [ObservableProperty] private double _saveFraction;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueInBackgroundCommand))]
    private bool _inBackground;

    [ObservableProperty] private string _doneTitle = string.Empty;

    [ObservableProperty] private string _madeFromText = string.Empty;

    [ObservableProperty] private string _doneNote = string.Empty;

    [ObservableProperty] private string _doneDuration = string.Empty;

    /// <summary>Ergebnis-Datei (Fertig-Ansicht).</summary>
    public int ResultFileId => _saved?.ResultFileId ?? 0;

    public bool CanShowInExplorer => _results is { IsExplorerBlocked: false };

    public string ExplorerToolTip => _results?.IsExplorerBlocked == true
        ? "In einer versteckten Bibliothek öffnet Lumora keine Explorer-Fenster."
        : "Ordner des Ergebnisses im Explorer öffnen";

    private DateTime _saveStarted;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var session = _session;
        var preview = Preview;
        if (session is null || preview is null || !preview.CanSave)
        {
            return;
        }

        _saveStarted = DateTime.UtcNow;
        var count = CheckedIds.Count - preview.DroppedFileIds.Count;
        SaveTitle = string.Create(De, $"{MergeTexts.Method(MergeMethod.Panorama)} zusammenfügen · {count:N0} Fotos");
        SaveSubtitle = MergeTexts.Projection(preview.Projection)
                       + (preview.Projection == PanoramaProjection.Spherical360 ? " · " + MergeTexts.SphereForm(preview.SphereForm) : string.Empty)
                       + " · " + (Sizes.FirstOrDefault(s => s.IsSelected)?.Title ?? "Volle Auflösung") + " · mit Vorrang (Hintergrund wartet)";
        Steps.Clear();
        foreach (var step in SaveSteps)
        {
            Steps.Add(new MergeStepItem(MergeTexts.Step(step)));
        }

        Steps.Add(new MergeStepItem("Eintragen (Datei, Katalog" + (HasAlbum && AlsoAlbum ? $", Album „{_start.AlbumName}“" : string.Empty) + ")"));
        SaveFraction = 0;
        SaveProgressText = "Ausrichtung aus der Vorschau — es wird nicht neu ausgerichtet.";
        InBackground = false;
        Phase = MergePhase.Saving;
        RemoveNotes(MergeNoteKind.Error);
        RemoveNotes(MergeNoteKind.Success);
        MergeSaveResult result;
        try
        {
            // nie den Lebenszyklus-Abbruch weitergeben: Schließen des Fensters bricht ein Speichern nicht ab
            result = await session.SaveAsync(new MergeSaveOptions(CurrentOptions, CopyTags, CopyRating, HasAlbum && AlsoAlbum ? _start.AlbumId : null), CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Phase = MergePhase.Setup;
            Notes.Add(new MergeNoteItem("Abgebrochen — nichts gespeichert.", MergeNoteKind.Info));
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // jeder Fehler endet in der Vorschau mit Meldung — nie ein hängendes „Speichern läuft“ (0.5.48)
            _logger.LogWarning(ex, "Zusammenfügen: Speichern fehlgeschlagen");
            Phase = MergePhase.Setup;
            Notes.Add(new MergeNoteItem("Nicht gespeichert: " + (ex is MergeException or IOException or InvalidOperationException ? ex.Message : ex.GetType().Name), MergeNoteKind.Error));
            return;
        }

        if (!result.Success)
        {
            Phase = MergePhase.Setup;
            Notes.Add(new MergeNoteItem(result.Error ?? "Nicht gespeichert.", MergeNoteKind.Error));
            return;
        }

        _saved = result;
        ShowDone(result, count, preview);
    }

    /// <summary>Speichern nur, was die Bühne zeigt: fertige Vorschau zu genau den eingestellten Optionen (0.5.48).</summary>
    private bool CanSave() => Phase == MergePhase.Setup && !IsLocked && !IsComputing && !_selectionDirty && _ready && Preview is { CanSave: true } && _session is not null
                              && _applied == CurrentOptions;

    private void OnSaveProgress(MergeProgress progress)
    {
        if (Phase != MergePhase.Saving)
        {
            return;
        }

        SaveFraction = progress.Fraction;
        var index = Array.IndexOf(SaveSteps, progress.Step);
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (index < 0)
            {
                continue;
            }

            step.State = i < index ? MergeStepState.Done : i == index ? MergeStepState.Running : MergeStepState.Pending;
            step.Detail = i == index && progress.Count > 1 ? string.Create(De, $"{progress.Index:N0} / {progress.Count:N0}") : i < index ? "fertig" : string.Empty;
        }

        var elapsed = DateTime.UtcNow - _saveStarted;
        var rest = progress.Fraction > 0.05 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - progress.Fraction) / progress.Fraction) : (TimeSpan?)null;
        SaveProgressText = string.Create(De, $"{Math.Round(progress.Fraction * 100):0} %") + (rest is { } r ? " · noch " + MergeTexts.Duration(r) : string.Empty)
                           + (InBackground ? " · im Hintergrund" : " · Vorrang");
    }

    private void ShowDone(MergeSaveResult result, int count, MergePreview preview)
    {
        foreach (var step in Steps)
        {
            step.State = MergeStepState.Done;
        }

        var name = result.Path is { } p ? Path.GetFileName(p) : TargetName;
        DoneTitle = $"{MergeTexts.Method(MergeMethod.Panorama)} gespeichert";
        MadeFromText = MergeTexts.MadeFrom(count);
        DoneDuration = "Zusammenfügen fertig · " + MergeTexts.Duration(DateTime.UtcNow - _saveStarted).TrimStart('≈', ' ');
        DoneRows.Clear();
        DoneRows.Add(new MergeInfoRow("Datei", name));
        DoneRows.Add(new MergeInfoRow("Größe", string.Create(De, $"{result.Width:N0} × {result.Height:N0} · {MergeTexts.Megapixels(result.Width, result.Height)}")
                                                + (result.Scale < 0.999 ? string.Create(De, $" ({Math.Round(result.Scale * 100):0} %)") : string.Empty)));
        DoneRows.Add(new MergeInfoRow("Aufnahmeart", MergeTexts.Projection(preview.Projection)
                                                      + (preview.Projection == PanoramaProjection.Spherical360 ? " · " + MergeTexts.SphereForm(preview.SphereForm) : string.Empty)));
        DoneRows.Add(new MergeInfoRow("Aufnahme", "Zeit und Kamera vom ersten Bild mit Angabe, Ort samt Höhe vom ersten mit Ort"));
        DoneRows.Add(new MergeInfoRow("Übernommen", (CopyRating ? "höchste Sterne, Favorit wenn eine Quelle Favorit ist" : "keine Sterne, kein Favorit")
                                                    + (CopyTags ? " · gemeinsame Tags" : " · keine Tags")));
        DoneRows.Add(new MergeInfoRow("Farbe", "kräftiger " + MergeTexts.ColorBoost(ColorBoost)));
        DoneRows.Add(new MergeInfoRow("Quellen", "unverändert, mit dem Ergebnis verknüpft"));
        if (HasAlbum && AlsoAlbum)
        {
            DoneRows.Add(new MergeInfoRow("Album", $"„{_start.AlbumName}“ (Verknüpfung)"));
        }

        DoneNote = "Rückgängig legt die neue Datei in den Papierkorb und nimmt Katalogzeile und Verknüpfungen mit. Die Einzelbilder bleiben unverändert.";
        Notes.Clear();
        Notes.Add(new MergeNoteItem($"{DoneTitle} · {PlaceText}" + (HasAlbum && AlsoAlbum ? $", auch im Album „{_start.AlbumName}“" : string.Empty), MergeNoteKind.Success));
        foreach (var note in result.Notes)
        {
            Notes.Add(new MergeNoteItem(note.Text, MergeNoteKind.Info));
        }

        Phase = MergePhase.Done;
        OnPropertyChanged(nameof(ResultFileId));
        OpenCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>„Im Hintergrund weiterrechnen“: Vorrang zurück, halbe Kerne, Fenster zu (Fortschritt in der Aktivitäts-Anzeige).</summary>
    [RelayCommand(CanExecute = nameof(CanContinueInBackground))]
    private void ContinueInBackground()
    {
        _session?.ContinueInBackground();
        InBackground = true;
        SaveSubtitle = SaveSubtitle.Replace(" · mit Vorrang (Hintergrund wartet)", " · im Hintergrund", StringComparison.Ordinal);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanContinueInBackground() => !InBackground;

    [RelayCommand]
    private void CancelSave() => _session?.Cancel();

    private bool HasResult() => _saved is { Success: true };

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task OpenAsync()
    {
        if (_results is not null && _saved is { } saved)
        {
            await _results.OpenResultAsync(saved.ResultFileId, Owner?.Invoke()).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ShowInGalleryAsync()
    {
        if (_results is not null && _saved is { } saved)
        {
            await _results.ShowInGalleryAsync(saved.ResultFileId, saved.Path is { } p ? Path.GetFileName(p) : TargetName).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Rückgängig: Ergebnis in den Papierkorb, zurück zur Vorschau (die Sitzung bleibt, nochmals speichern geht). Scheitert es,
    /// bleibt die Fertig-Ansicht mit Meldung — das Ergebnis ist ja noch da (0.5.48).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task UndoAsync()
    {
        if (_results is null || _saved is not { BatchId: > 0 } saved)
        {
            return;
        }

        MergeUndoOutcome outcome;
        try
        {
            outcome = await _results.UndoAsync(saved.BatchId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Zusammenfügen: Rückgängig fehlgeschlagen");
            outcome = new MergeUndoOutcome(false, "Rückgängig fehlgeschlagen: " + (ex is IOException or InvalidOperationException ? ex.Message : ex.GetType().Name));
        }

        if (!outcome.Succeeded)
        {
            RemoveNotes(MergeNoteKind.Error);
            Notes.Add(new MergeNoteItem(outcome.Message, MergeNoteKind.Error));
            return;
        }

        _saved = null;
        Phase = MergePhase.Setup;
        Notes.Clear();
        Notes.Add(new MergeNoteItem("Rückgängig: " + outcome.Message, MergeNoteKind.Info));
        OnPropertyChanged(nameof(ResultFileId));
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task EnhanceAfterAsync()
    {
        if (_results is not null && _saved is { } saved)
        {
            await _results.EnhanceAsync(saved.ResultFileId, Owner?.Invoke()).ConfigureAwait(true);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        if (_results is not null && _saved?.Path is { } path)
        {
            var error = _results.ShowInExplorer(path);
            if (error is not null)
            {
                Notes.Add(new MergeNoteItem(error, MergeNoteKind.Warning));
            }
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
