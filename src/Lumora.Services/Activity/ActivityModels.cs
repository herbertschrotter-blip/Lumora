namespace Lumora.Services.Activity;

/// <summary>Art eines Vorgangs (Symbol, Wichtigkeit, Abschnitt in der Aktivitäts-Anzeige, ADR-0067).</summary>
public enum ActivityKind
{
    /// <summary>Datei-Vorgang: Verschieben, Umbenennen, Papierkorb, Fehlende/Ausgeschlossene entfernen, Ordner verschieben, Rückgängig.</summary>
    FileOperation = 0,

    /// <summary>„In die Bibliothek übernehmen“ aus einem Eingang (ADR-0034).</summary>
    Adoption = 1,

    /// <summary>Sortierer-Lauf (mehrere Datei-Batches als ein Vorgang).</summary>
    Sorter = 2,

    /// <summary>Einlesen (voll oder inkrementell).</summary>
    Scan = 3,

    /// <summary>Automatisches Entpacken (ADR-0032).</summary>
    Archive = 4,

    /// <summary>Einlesen von GPS-Spuren (ADR-0062).</summary>
    GpsTracks = 5,

    /// <summary>Vorschaubilder erstellen.</summary>
    Thumbnails = 6,

    /// <summary>Doppelte-Index (ADR-0053).</summary>
    Duplicates = 7,

    /// <summary>Gesichter erkennen (ADR-0035).</summary>
    Faces = 8,

    /// <summary>Motive erkennen (ADR-0052).</summary>
    Motifs = 9,

    /// <summary>Aufnahmedatum nachtragen (ADR-0046).</summary>
    CaptureDates = 10,

    /// <summary>Orte nachtragen bzw. Genauigkeit prüfen (ADR-0050/0062).</summary>
    Places = 11,

    /// <summary>Foto-Güte (ADR-0057).</summary>
    Quality = 12,

    /// <summary>Story-Erkennung (ADR-0058).</summary>
    Stories = 13,

    /// <summary>Ordnerüberwachung: automatische Aktualisierung steht an bzw. ist angehalten.</summary>
    Watch = 14,

    /// <summary>Änderungen warten auf die Schreibsperre des Katalogs (ADR-0030).</summary>
    CatalogWrite = 15,

    /// <summary>Modell laden (Einstellungen › Modelle &amp; Daten, ADR-0070).</summary>
    ModelDownload = 16,
}

/// <summary>Zustand eines Vorgangs.</summary>
public enum ActivityState
{
    Running = 0,
    Paused = 1,
    Waiting = 2,

    /// <summary>Fertig ohne Auffälligkeiten.</summary>
    Succeeded = 3,

    /// <summary>Fertig mit Übersprungenen/Hinweisen (Details).</summary>
    Warning = 4,

    Failed = 5,
    Canceled = 6,
}

/// <summary>Aktionen an einem fertigen Vorgang.</summary>
public enum ActivityActionKind
{
    /// <summary>„Rückgängig“ über den vorhandenen Weg (FileOperationService-Undo, Sortierer-Lauf).</summary>
    Undo = 0,

    /// <summary>„Fehlende zeigen“ (Galerie-Filter „Nur fehlende“) — führt die Oberfläche aus.</summary>
    ShowMissing = 1,

    /// <summary>„Details“ bei Fehlern/Übersprungenen — die Oberfläche zeigt <see cref="ActivitySnapshot.Details"/>.</summary>
    Details = 2,
}

/// <summary>
/// Eine Aktion; <paramref name="Run"/> nur bei <see cref="ActivityActionKind.Undo"/> (liefert die Ergebnis-Zeile), die übrigen
/// führt die Oberfläche selbst aus.
/// </summary>
public sealed record ActivityAction(ActivityActionKind Kind, string Label, Func<CancellationToken, Task<string>>? Run = null)
{
    public static ActivityAction Undo(Func<CancellationToken, Task<string>> run) => new(ActivityActionKind.Undo, "Rückgängig", run);

    public static ActivityAction ShowMissing { get; } = new(ActivityActionKind.ShowMissing, "Fehlende zeigen");

    public static ActivityAction Details { get; } = new(ActivityActionKind.Details, "Details");
}

/// <summary>
/// Beschreibung beim Start eines Vorgangs. <paramref name="Key"/> macht ihn eindeutig (z. B. „faces“, „scan:3“) — ein
/// zweiter Start mit demselben Schlüssel liefert den laufenden. <paramref name="UserInitiated"/>: vom Benutzer gestartet
/// (kurze Meldung beim Fertigwerden, wichtiger in der Statusleiste). <paramref name="KeepInRecent"/>: nach dem Ende unter
/// „Zuletzt fertig“ (sonst nur bei Fehlern, Hinweisen oder Abbruch).
/// </summary>
public sealed record ActivityStartInfo(
    ActivityKind Kind,
    string Title,
    string? Target = null,
    bool UserInitiated = false,
    bool KeepInRecent = true,
    string? Key = null,
    Action? Cancel = null,
    Action? Pause = null,
    Action? Resume = null);

/// <summary>Ergebnis beim Fertigwerden: Zeile, Zustand, Aktionen, Details (Fehler/Übersprungene, mehrzeilig).</summary>
public sealed record ActivityCompletion(
    string Result,
    ActivityState State = ActivityState.Succeeded,
    IReadOnlyList<ActivityAction>? Actions = null,
    string? Details = null);

/// <summary>Unveränderliche Momentaufnahme eines Vorgangs für die Oberfläche.</summary>
/// <param name="Done">Erledigt; mit <paramref name="Total"/> 0 ist der Fortschritt unbestimmt.</param>
/// <param name="Remaining">Geschätzte Restzeit (erst nach einigen Sekunden und erledigten Einheiten), sonst null.</param>
/// <param name="Detail">Zweite Zeile (Ziel, Phase, „im Hintergrund · Grafikkarte“).</param>
/// <param name="WaitReason">Grund bei <see cref="ActivityState.Waiting"/>.</param>
/// <param name="Result">Ergebnis-Zeile nach dem Ende.</param>
public sealed record ActivitySnapshot(
    long Id,
    ActivityKind Kind,
    string Title,
    string? Target,
    string? Detail,
    ActivityState State,
    int Done,
    int Total,
    TimeSpan? Remaining,
    bool UserInitiated,
    bool CanCancel,
    bool CanPause,
    string? WaitReason,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    string? Result,
    string? Details,
    IReadOnlyList<ActivityAction> Actions)
{
    public bool IsActive => State is ActivityState.Running or ActivityState.Paused or ActivityState.Waiting;

    public bool IsIndeterminate => Total <= 0;

    /// <summary>Anteil 0…1 (0 bei unbestimmtem Fortschritt).</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);

    public bool HasAction(ActivityActionKind kind) => Actions.Any(a => a.Kind == kind);
}

/// <summary>Alle Vorgänge der Sitzung, nach Abschnitten: Läuft (wichtigster zuerst), Wartet, Zuletzt fertig (neuester zuerst).</summary>
public sealed record ActivityOverview(
    IReadOnlyList<ActivitySnapshot> Running,
    IReadOnlyList<ActivitySnapshot> Waiting,
    IReadOnlyList<ActivitySnapshot> Recent,
    DateTime? LastFinishedUtc)
{
    public static ActivityOverview Empty { get; } = new([], [], [], null);

    /// <summary>Wichtigster laufender (sonst wartender) Vorgang für die Statusleiste; null = nichts aktiv.</summary>
    public ActivitySnapshot? Primary => Running.Count > 0 ? Running[0] : Waiting.Count > 0 ? Waiting[0] : null;

    /// <summary>Weitere aktive Vorgänge neben <see cref="Primary"/> (Zähler „+n“).</summary>
    public int OthersCount => Math.Max(0, Running.Count + Waiting.Count - 1);
}
