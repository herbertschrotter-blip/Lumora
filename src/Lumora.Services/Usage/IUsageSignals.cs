namespace Lumora.Services.Usage;

/// <summary>
/// Melde-Weg der Nutzungs-Signale (ADR-0072): Ansichten (über <see cref="UsageViewTracker"/>), Verwendung und Weitergabe.
/// Alles bleibt im aktuellen Katalog (versteckte Bibliothek: nur in deren Container) und wird nur als Tagessumme je Datei
/// gespeichert; ist „Nutzung lernen“ aus, wird nichts geschrieben. Gesammelt im Speicher und kurz danach in einem Rutsch
/// geschrieben (<see cref="FlushAsync"/>). Nie Pfade oder Dateinamen ins Protokoll.
/// <para>
/// Für Aktionen ohne DI-Zugang gibt es dieselbe Meldung als <see cref="Messaging.UsageSignalMessage"/> über den Messenger.
/// </para>
/// </summary>
public interface IUsageSignals
{
    /// <summary>Uhr der Messung (Tagesgrenze, Ansehdauer) — Tests und Rauchtest setzen eine eigene.</summary>
    TimeProvider Time { get; }

    /// <summary>Zuletzt gelesener Stand von „Nutzung lernen“ (vor dem ersten Lesen: an).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Eine beendete Ansicht: <paramref name="activeTime"/> = Zeit, in der das Fenster vorne war. Unter
    /// <see cref="UsageScoring.MinView"/> zählt sie nicht; mehr als <see cref="UsageScoring.MaxView"/> wird gekappt.
    /// </summary>
    void RecordView(int fileId, TimeSpan activeTime, bool zoomed, UsageViewContext context);

    /// <summary>Verwendung bzw. Weitergabe von Dateien (je Datei und Tag einmal je Aufruf gezählt).</summary>
    void Record(IReadOnlyCollection<int> fileIds, UsageKind kind);

    /// <summary>Gesammelte Meldungen jetzt schreiben (Tests, Rauchtest, Beenden).</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Neue Messung für ein Fenster (Lightbox, Diashow) mit <see cref="Time"/>.</summary>
    UsageViewTracker CreateTracker(UsageViewContext context) => new(this, context, Time);
}

/// <summary>
/// Lesen, Einstellungen und Löschen der Nutzungs-Signale (ADR-0072) — für Highlights, „Vergessene Schätze“, Galerie-Filter
/// „Lieblinge“, die Zeile „Nutzung“ der Lightbox und Einstellungen › Storys.
/// </summary>
public interface IUsageService
{
    Task<UsageSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Speichert und sendet <see cref="Messaging.UsageChangedMessage"/>.</summary>
    Task SaveSettingsAsync(UsageSettings settings, CancellationToken ct = default);

    /// <summary>Summen je Datei (nur Dateien mit Nutzung); leer, wenn „Nutzung lernen“ aus ist.</summary>
    Task<IReadOnlyDictionary<int, UsageTotals>> GetTotalsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);

    /// <summary>Summen einer Datei (Lightbox) oder null ohne Nutzung bzw. wenn „Nutzung lernen“ aus ist.</summary>
    Task<UsageTotals?> GetTotalsAsync(int fileId, CancellationToken ct = default);

    /// <summary>Übersicht für die Einstellungen (auch bei ausgeschaltetem Lernen — gespeicherte Daten bleiben sichtbar).</summary>
    Task<UsageSummary> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>„Gelernte Nutzung löschen“: alle Zeilen dieses Katalogs; liefert die Zahl der betroffenen Dateien.</summary>
    Task<int> EraseAsync(CancellationToken ct = default);

    /// <summary>
    /// Kandidaten für „Vergessene Schätze“: gute Fotos des Bereichs (Favorit, ab 4 Sternen, scharf und gut belichtet oder
    /// früher oft angesehen), vor über einem Jahr aufgenommen und seit einem Jahr nicht geöffnet — gleichmäßige, je Monat
    /// andere Stichprobe von höchstens <paramref name="max"/>. Leer, wenn Lernen oder „Vergessene Schätze“ aus sind.
    /// </summary>
    Task<IReadOnlyList<int>> FindForgottenAsync(IReadOnlyCollection<int> libraryIds, DateOnly today, int max, CancellationToken ct = default);
}
