namespace Lumora.Services.Activity;

/// <summary>
/// Ein laufender Vorgang aus Sicht seiner Quelle (thread-sicher). Fortschritt darf beliebig oft gemeldet werden — die
/// Zentrale drosselt die Benachrichtigung der Oberfläche. Nach <see cref="Complete"/>, <see cref="Fail"/> oder
/// <see cref="Canceled"/> sind weitere Aufrufe wirkungslos; <see cref="Dismiss"/> beendet still (nicht unter „Zuletzt fertig“).
/// </summary>
public interface IActivityHandle
{
    long Id { get; }

    /// <summary>true, solange der Vorgang läuft, angehalten ist oder wartet.</summary>
    bool IsActive { get; }

    /// <summary>Fortschritt; <paramref name="total"/> 0 = unbestimmt. Ein wartender Vorgang läuft damit wieder.</summary>
    void Report(int done, int total, string? detail = null);

    /// <summary>Titel ändern (z. B. Bibliotheksname nachgeladen, Phase gewechselt).</summary>
    void SetTitle(string title);

    /// <summary>Wartet mit Grund; <paramref name="cancel"/> ersetzt für die Dauer des Wartens „Abbrechen“.</summary>
    void Wait(string reason, Action? cancel = null);

    /// <summary>Wartet nicht mehr (zurück auf „läuft“).</summary>
    void Resume();

    void SetPaused(bool paused);

    void Complete(ActivityCompletion completion);

    void Fail(string message, string? details = null);

    void Canceled(string? message = null);

    /// <summary>Still beenden: Hintergrund-Index fertig, Wartezustand vorbei — kein Eintrag unter „Zuletzt fertig“.</summary>
    void Dismiss();
}

/// <summary>
/// Zentrale aller Hintergrund-Vorgänge der Sitzung (ADR-0067, Mockup 15): WPF-frei, eine Instanz je DI-Container — der
/// Katalogwechsel (versteckte Bibliothek, Zusperren) baut den Container neu, die Liste ist dann leer. Nichts wird gespeichert.
/// Quellen melden über <see cref="IActivityHandle"/>; die Oberfläche liest <see cref="GetOverview"/> nach <see cref="Changed"/>
/// (gedrosselt, höchstens alle <see cref="ActivityCenter.NotifyInterval"/>) und bekommt <see cref="Finished"/> sofort.
/// </summary>
public interface IActivityCenter
{
    /// <summary>Startet einen Vorgang; mit Schlüssel und laufendem Vorgang desselben Schlüssels wird dieser geliefert.</summary>
    IActivityHandle Start(ActivityStartInfo info);

    /// <summary>Laufender Vorgang mit diesem Schlüssel oder null.</summary>
    IActivityHandle? Find(string key);

    ActivityOverview GetOverview();

    /// <summary>Gedrosselt; kommt von beliebigen Threads.</summary>
    event Action? Changed;

    /// <summary>Sofort beim Ende eines Vorgangs (fertig, Fehler, abgebrochen), von beliebigen Threads — für die kurze Meldung.</summary>
    event Action<ActivitySnapshot>? Finished;

    /// <summary>„Abbrechen“: ruft den Abbruch-Weg der Quelle (CancellationToken, Index-Abbruch, Warten abbrechen).</summary>
    bool Cancel(long id);

    /// <summary>„Pause“/„Fortsetzen“ bei Vorgängen, die das können (Gesichter, Motive).</summary>
    bool SetPaused(long id, bool paused);

    /// <summary>Führt „Rückgängig“ eines fertigen Vorgangs aus; liefert die Ergebnis-Zeile (oder den Fehler als Text).</summary>
    Task<string?> UndoAsync(long id, CancellationToken ct = default);

    /// <summary>Ein fertiger Vorgang wurde auf anderem Weg rückgängig gemacht: „Rückgängig“ entfällt, Zusatz am Ergebnis.</summary>
    void MarkUndone(long id, string note);

    /// <summary>Liste leeren (laufende Vorgänge bleiben).</summary>
    void ClearRecent();
}
