namespace Lumora.Services.Faces.Assignment;

/// <summary>
/// Zuordnung von Gesichtern zu Personen (ADR-0037): lädt die Gesichter der aktiven Bibliothek, lernt NUR aus bestätigten
/// (ADR-0036), hält das Modell im Speicher, lernt nach jeder Bestätigung/Korrektur entprellt neu und schreibt Vorschläge
/// für unbestätigte Gesichter (Zustand 1 bzw. zurück auf 0, wenn niemand passt) — nach dem Training und nach jedem
/// Schreib-Stapel des Index (<c>FacesChangedMessage</c>). Alle Methoden arbeiten auf dem Thread-Pool; der Aufrufer
/// (UI-Thread) wartet nie auf Katalog oder Modell. Nach jeder Änderung kommt <see cref="PersonsChangedMessage"/>.
/// </summary>
public interface IFaceRecognitionService
{
    // ---- Vereinbarte Schnittstelle für Galerie/Lightbox (P2) ----------------------------------------------------

    /// <summary>Gesichter der Dateien mit Zuordnung (alle Zustände); Dateien ohne Gesichter fehlen im Ergebnis.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<FacePersonInfo>>> GetFacesForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct);

    /// <summary>Alle Personen (auch ausgeblendete, siehe <see cref="PersonSummary.IsHidden"/>) mit Zählern der aktiven Bibliothek, nach Name.</summary>
    Task<IReadOnlyList<PersonSummary>> GetPersonsAsync(CancellationToken ct);

    /// <summary>Bestätigt das Gesicht als Person (ohne Rückfrage — die Ausreißer-Prüfung macht vorher <see cref="CheckOutliersAsync"/>).</summary>
    Task ConfirmAsync(long faceId, int personId);

    /// <summary>Legt eine Person an (gibt es den Namen schon: diese Person) und bestätigt das Gesicht als sie.</summary>
    Task ConfirmNewPersonAsync(long faceId, string name);

    /// <summary>Lehnt den aktuellen Vorschlag ab (FaceRejections) und schlägt sofort den nächstbesten Kandidaten vor.</summary>
    Task RejectSuggestionAsync(long faceId);

    /// <summary>„Kein Gesicht / ignorieren“ (Zustand 3): keine Vorschläge, keine Gruppen, nie Lernbeispiel.</summary>
    Task IgnoreFaceAsync(long faceId);

    // ---- Personen-Seite --------------------------------------------------------------------------------------------

    /// <summary>Wie <see cref="GetPersonsAsync"/>, zusätzlich mit der Zahl offener Vorschläge je Person.</summary>
    Task<IReadOnlyList<PersonOverview>> GetPersonOverviewsAsync(CancellationToken ct = default);

    /// <summary>Bestätigte Gesichter und Vorschläge einer Person in der aktiven Bibliothek; null, wenn es die Person nicht gibt.</summary>
    Task<PersonFaces?> GetPersonFacesAsync(int personId, CancellationToken ct = default);

    /// <summary>
    /// Gruppen-Vorschläge über die unbestätigten Gesichter ohne Vorschlag (abschnittsweise bei sehr vielen). Das Ergebnis
    /// bleibt zwischengespeichert, bis neue Gesichter kommen; eigene Zuordnungen nehmen Gesichter sofort heraus.
    /// </summary>
    Task<FaceGroupsResult> GetGroupsAsync(bool refresh = false, CancellationToken ct = default);

    /// <summary>
    /// Ausreißer-Prüfung vor dem Bestätigen als <paramref name="personId"/> (null = neue Person: geprüft wird, ob ein
    /// Gesicht deutlich einer bekannten Person ähnelt). Liefert je Gesicht das Ergebnis von <c>CheckOutlier</c>.
    /// </summary>
    Task<IReadOnlyList<FaceOutlierResult>> CheckOutliersAsync(IReadOnlyCollection<long> faceIds, int? personId, CancellationToken ct = default);

    /// <summary>Bestätigt mehrere Gesichter als Person (eine Transaktion), lernt danach entprellt neu.</summary>
    Task ConfirmManyAsync(IReadOnlyCollection<long> faceIds, int personId, CancellationToken ct = default);

    /// <summary>Legt eine Person an; wirft <see cref="PersonNameTakenException"/>, wenn der Name vergeben ist.</summary>
    Task<int> CreatePersonAsync(string name, CancellationToken ct = default);

    /// <summary>Person mit diesem Namen (ohne Groß-/Kleinschreibung) oder null.</summary>
    Task<int?> FindPersonByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Benennt um; wirft <see cref="PersonNameTakenException"/>, wenn eine ANDERE Person so heißt.</summary>
    Task RenamePersonAsync(int personId, string name, CancellationToken ct = default);

    /// <summary>Führt <paramref name="sourceId"/> in <paramref name="targetId"/> zusammen (Gesichter, Vorschläge, Ablehnungen) und löscht die Quelle.</summary>
    Task MergePersonsAsync(int sourceId, int targetId, CancellationToken ct = default);

    Task SetPersonHiddenAsync(int personId, bool hidden, CancellationToken ct = default);

    /// <summary>Löscht die Person; ihre Gesichter werden wieder unbestätigt (Ablehnungen der Person entfallen).</summary>
    Task DeletePersonAsync(int personId, CancellationToken ct = default);

    /// <summary>Lehnt die Vorschläge mehrerer Gesichter ab (je nächstbester Kandidat sofort).</summary>
    Task RejectSuggestionsAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default);

    /// <summary>Nimmt bestätigte Gesichter aus ihrer Person heraus (wieder unbestätigt + Ablehnung dieser Person), lernt neu.</summary>
    Task RemoveFromPersonAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default);

    /// <summary>„Kein Gesicht / ignorieren“ für mehrere Gesichter.</summary>
    Task IgnoreFacesAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default);

    /// <summary>Titelbild der Person (muss ein bestätigtes Gesicht der Person sein).</summary>
    Task SetCoverAsync(int personId, long faceId, CancellationToken ct = default);

    /// <summary>Titelbild wieder automatisch wählen lassen (ADR-0068: bestes Gesicht nach Gesichts- und Foto-Güte).</summary>
    Task ClearCoverAsync(int personId, CancellationToken ct = default)
        => throw new NotSupportedException("Titelbild zurücksetzen wird nicht unterstützt.");

    /// <summary>Dateien der Gesichter für die Lightbox (in Eingabe-Reihenfolge, je Datei einmal, fehlende weggelassen).</summary>
    Task<IReadOnlyList<FaceFileInfo>> GetFilesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>Aktuelle Bibliothek (für den Gesichter-Ausschnitt-Cache); null ohne Bibliothek.</summary>
    Task<int?> GetActiveLibraryIdAsync(CancellationToken ct = default);

    // ---- Lebenszyklus ----------------------------------------------------------------------------------------------

    FaceRecognitionState State { get; }

    /// <summary>Nachrichten abonnieren, ersten Durchgang (Training + Vorschläge) verzögert anstoßen. Einmal beim App-Start.</summary>
    void Start();

    /// <summary>Stößt Training + Vorschläge für alle unbestätigten Gesichter an (entprellt).</summary>
    void RequestRetrain();

    /// <summary>
    /// Modell und zwischengespeicherte Gruppen verwerfen und neu lernen — nach Änderungen an den Gesichtsdaten an diesem
    /// Dienst vorbei (z. B. „alle Gesichtsdaten löschen“).
    /// </summary>
    void InvalidateAll();

    /// <summary>Wartet, bis keine Arbeit mehr ansteht (Tests, Rauchtest).</summary>
    Task WaitForIdleAsync(CancellationToken ct = default);
}
