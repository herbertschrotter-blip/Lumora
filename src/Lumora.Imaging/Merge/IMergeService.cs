namespace Lumora.Imaging.Merge;

/// <summary>Fehler von „Zusammenfügen“ mit verständlichem Grund (Oberfläche zeigt <see cref="Exception.Message"/>).</summary>
public sealed class MergeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// „Zusammenfügen“ (ADR-0078, Stufe 1: Panorama aus der Auswahl). Die Oberfläche (Fenster „Zusammenfügen“, Rechtsklick,
/// Aktionsleiste — Stufe 1 Teil B) arbeitet nur über diesen Dienst: Analyse der Auswahl, Vorschau-Sitzung mit Umschalten der
/// Aufnahmeart ohne neues Ausrichten, Speichern als neue Datei mit Rückgängig. Gerechnet wird im Hilfsprozess.
/// </summary>
public interface IMergeService
{
    /// <summary>Hilfsprozess vorhanden und OpenCV ladbar? (Menüeintrag sonst ausgegraut mit <see cref="MergeAvailability.Reason"/>.)</summary>
    Task<MergeAvailability> GetAvailabilityAsync(CancellationToken ct = default);

    /// <summary>
    /// Analyse einer Auswahl (nur Katalog und Dateisystem, schnell): Quellen nach Aufnahmezeit mit Zustand (Video, Archiv,
    /// fehlt, nur OneDrive, Laufwerk offline, RAW → JPEG-Geschwister), bei genau einem Foto die Zeit-Nachbarn, gemischte
    /// Bibliotheken, Verfahren mit Eignung (Stufe 1: nur Panorama gebaut), Hinweise.
    /// </summary>
    Task<MergeAnalysis> AnalyzeAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>Neue Vorschau-Sitzung (rechnet erst mit <see cref="IMergePreviewSession.StartAsync"/>); eine je Fenster.</summary>
    IMergePreviewSession OpenPreviewSession(MergeRequest request);

    Task<MergeSettings> LoadSettingsAsync(CancellationToken ct = default);

    /// <summary>Alle Einstellungen › Zusammenfügen schreiben (Seite Einstellungen).</summary>
    Task SaveSettingsAsync(MergeSettings settings, CancellationToken ct = default);

    /// <summary>Nur die zuletzt gewählte Farbverstärkung merken (Fenster: überschreibt keine anderen Einstellungen, 0.5.48).</summary>
    Task RememberColorBoostAsync(int percent, CancellationToken ct = default);

    /// <summary>Nur die zuletzt gewählte Form der Kugel merken (Fenster, Abnahme Nr. 4).</summary>
    Task RememberSphereFormAsync(MergeSphereForm form, CancellationToken ct = default);

    /// <summary>
    /// true, solange ein Speichern läuft — auch nach „Im Hintergrund weiterrechnen“ ohne Fenster. Ende der Katalog-Sitzung,
    /// Zusperren und Beenden warten darauf bzw. fragen „Warten / Abbrechen und …“ (0.5.48).
    /// </summary>
    bool IsSaving => false;

    /// <summary>Bricht laufende Speichern ab (nichts wird gespeichert, das Zwischenergebnis wird gelöscht).</summary>
    void CancelSaves()
    {
    }
}

/// <summary>
/// Vorschau-Sitzung (ADR-0078 Punkt 12): liest die Quellen verkleinert (≈ 1 600 px), richtet einmal aus, setzt ≈ 5 MP zusammen
/// und hält die Zwischenstände im Hilfsprozess, solange das Fenster offen ist. Jede Berechnung läuft mit Vorrang
/// (<c>ForegroundHold</c>, 2 s Nachlauf); ein offenes, untätiges Fenster hält den Hintergrund nicht an. Dateien nur im
/// Temp-Ordner des Katalogs, beim Schließen gelöscht. Nicht für parallele Aufrufe gedacht (eine Berechnung zur Zeit).
/// <c>DisposeAsync</c> (Fenster schließen) bricht eine laufende Vorschau ab; ein laufendes Speichern rechnet zu Ende (auch nach
/// <see cref="ContinueInBackground"/>) — <c>DisposeAsync</c> endet erst danach und darf deshalb ohne Warten angestoßen werden;
/// jeder weitere Aufruf wartet auf dasselbe Aufräumen. Nur das Ende der Katalog-Sitzung bricht auch ein Speichern ab.
/// <see cref="Options"/> und <see cref="Current"/> gehören zusammen: beide ändern sich erst mit einer fertigen Vorschau, und
/// <see cref="SaveAsync"/> rendert genau diese Optionen mit dieser Ausrichtung (0.5.48).
/// </summary>
public interface IMergePreviewSession : IAsyncDisposable
{
    MergeRequest Request { get; }

    /// <summary>Analyse der Auswahl (nach <see cref="StartAsync"/>).</summary>
    MergeAnalysis? Analysis { get; }

    /// <summary>Letzte Vorschau.</summary>
    MergePreview? Current { get; }

    /// <summary>Optionen der letzten Vorschau.</summary>
    MergeOptions Options { get; }

    /// <summary>true, solange gerechnet wird.</summary>
    bool IsBusy { get; }

    /// <summary>Fortschritt („Nähte suchen 4/6“), von beliebigen Threads.</summary>
    event Action<MergeProgress>? ProgressChanged;

    /// <summary>Analyse, Lesen, Ausrichten und erste Vorschau; eine gekippte Ausrichtung wird mit anderem Startwert wiederholt.</summary>
    Task<MergePreview> StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Andere Optionen (Aufnahmeart, Form, Zuschnitt): setzt nur neu zusammen, ohne neu auszurichten. Ändern sich nur
    /// <see cref="MergeOptions.ColorBoost"/> bzw. <see cref="MergeOptions.ToneMatch"/>, wird das letzte Bild nur neu getont
    /// (Bruchteile einer Sekunde) — geeignet für einen Regler.
    /// </summary>
    Task<MergePreview> UpdateAsync(MergeOptions options, CancellationToken ct = default);

    /// <summary>Schätzung für volle Auflösung (Größe bzw. größte passende Stufe, Speicher, Zeit, Platz).</summary>
    MergeEstimate Estimate();

    /// <summary>
    /// Volle Auflösung rechnen, prüfen (nie still speichern), Aufnahmedaten schreiben und als neue Datei neben dem ersten
    /// Einzelbild anlegen (CreateMerged-Batch, Rückgängig). Vorrang vom Klick bis zum Eintrag im Katalog, außer nach
    /// <see cref="ContinueInBackground"/>.
    /// </summary>
    Task<MergeSaveResult> SaveAsync(MergeSaveOptions? options = null, CancellationToken ct = default);

    /// <summary>„Im Hintergrund weiterrechnen“: Vorrang zurückgeben, mit halben Kernen weiterrechnen.</summary>
    void ContinueInBackground();

    /// <summary>Bricht die laufende Berechnung ab (zwischen Stufen, Bildern und Bändern).</summary>
    void Cancel();
}
