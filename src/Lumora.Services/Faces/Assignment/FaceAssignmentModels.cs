using System.Drawing;
using Lumora.Domain.Enums;
using Lumora.Services.Faces.Recognition;

namespace Lumora.Services.Faces.Assignment;

// Ein- und Ausgabe-Typen des Zuordnungsdienstes (ADR-0037). FacePersonInfo, PersonSummary und PersonsChangedMessage sind
// die mit der Galerie/Lightbox (Agent P2) vereinbarte Schnittstelle — Namen und Felder nicht ändern.

/// <summary>
/// Ein Gesicht einer Datei mit Zuordnung (für Galerie-Liste, Filter und Lightbox-Rahmen).
/// </summary>
/// <param name="FaceId">Kennung des Gesichts (Faces.Id).</param>
/// <param name="BoxRelative">Box relativ (0..1) zum orientierten Bild.</param>
/// <param name="PersonId">Bestätigte Person (nur bei <see cref="FaceState.Confirmed"/>).</param>
/// <param name="PersonName">Name der bestätigten Person.</param>
/// <param name="State">Zustand (0 unbestätigt, 1 Vorschlag, 2 bestätigt, 3 ignoriert).</param>
/// <param name="SuggestedPersonId">Vorgeschlagene Person (nur bei <see cref="FaceState.Suggested"/>).</param>
/// <param name="SuggestedPersonName">Name der vorgeschlagenen Person.</param>
/// <param name="SuggestionScore">Score des Vorschlags 0..1.</param>
public sealed record FacePersonInfo(
    long FaceId,
    RectangleF BoxRelative,
    int? PersonId,
    string? PersonName,
    FaceState State,
    int? SuggestedPersonId,
    string? SuggestedPersonName,
    float? SuggestionScore);

/// <summary>
/// Eine Person mit Zählern der AKTIVEN Bibliothek (Personen sind global, Gesichter je Bibliothek).
/// </summary>
/// <param name="FaceCount">Bestätigte Gesichter der Person in der aktiven Bibliothek.</param>
/// <param name="FileCount">Fotos mit mindestens einem bestätigten Gesicht der Person.</param>
/// <param name="CoverFaceId">Titelbild: gesetztes Gesicht, sonst das beste bestätigte Gesicht (Gesichts- und Foto-Güte, ADR-0068); null ohne Gesichter.</param>
/// <param name="IsHidden">In Listen ausgeblendet.</param>
public sealed record PersonSummary(int Id, string Name, int FaceCount, int FileCount, long? CoverFaceId, bool IsHidden)
{
    /// <summary>
    /// Vor-/Nachname, Rufname, Beziehung, Geburtsdatum, „Das bin ich“ (0.2.47, ADR-0045). <see cref="Name"/> ist der
    /// Anzeigename daraus. Null nur bei Testdaten ohne Details.
    /// </summary>
    public PersonDetails? Details { get; init; }

    /// <summary>Titelbild selbst gewählt (<c>Persons.CoverFaceId</c> gültig); false = automatisch gewählt (ADR-0068).</summary>
    public bool CoverIsManual { get; init; }

    /// <summary>Suchbegriffe (Anzeigename, Vor-/Nachname, Rufname, Beziehung).</summary>
    public string SearchText => Details?.SearchText ?? Name;
}

/// <summary>
/// Personen oder Zuordnungen der Bibliothek haben sich geändert (Bestätigen, Ablehnen, Umbenennen, neue Vorschläge …).
/// Kommt vom Hintergrund-Thread; Empfänger dispatchen selbst.
/// </summary>
public sealed record PersonsChangedMessage(int LibraryId);

/// <summary>Person mit Zusatzzählern für die Personen-Seite.</summary>
public sealed record PersonOverview(PersonSummary Person, int SuggestionCount);

/// <summary>Ein Gesicht in der Detailansicht einer Person (bestätigt oder Vorschlag).</summary>
public sealed record PersonFaceInfo(
    long FaceId,
    int FileId,
    RectangleF BoxRelative,
    FaceState State,
    float Quality,
    float? SuggestionScore);

/// <summary>Bestätigte Gesichter und Vorschläge einer Person in der aktiven Bibliothek.</summary>
/// <param name="Confirmed">Bestätigt, neueste Fotos zuerst.</param>
/// <param name="Suggested">Vorschläge, sicherste zuerst.</param>
public sealed record PersonFaces(PersonSummary Person, IReadOnlyList<PersonFaceInfo> Confirmed, IReadOnlyList<PersonFaceInfo> Suggested);

/// <summary>Mitglied einer Gruppe unbenannter Gesichter.</summary>
/// <param name="Similarity">Kosinus-Ähnlichkeit zum Gruppenzentrum (1 = identisch).</param>
/// <param name="IsOutlier">Zweifelhaftes Mitglied — die Oberfläche hebt es hervor und wählt es standardmäßig ab.</param>
public sealed record FaceGroupMemberInfo(long FaceId, int FileId, float Similarity, bool IsOutlier);

/// <summary>
/// Gruppen-Vorschlag „vermutlich dieselbe unbekannte Person“ (ADR-0036/0037).
/// </summary>
/// <param name="GroupId">Kennung innerhalb des zwischengespeicherten Ergebnisses (bleibt beim Herausnehmen von Gesichtern).</param>
/// <param name="Members">Nach Nähe zum Zentrum sortiert, Ausreißer am Ende.</param>
/// <param name="Cohesion">Mittlere Ähnlichkeit aller Paare.</param>
/// <param name="SuggestedPersonId">Bekannte Person, der das Gruppenzentrum ähnelt (Vorschlag für „Zu Person hinzufügen…“).</param>
public sealed record FaceGroupInfo(
    long GroupId,
    IReadOnlyList<FaceGroupMemberInfo> Members,
    float Cohesion,
    int? SuggestedPersonId,
    string? SuggestedPersonName,
    float? SuggestedScore)
{
    public int Count => Members.Count;

    public int OutlierCount => Members.Count(m => m.IsOutlier);
}

/// <summary>Ergebnis der Gruppierung der aktiven Bibliothek.</summary>
/// <param name="Groups">Größte zuerst.</param>
/// <param name="UnknownFaces">Unbestätigte Gesichter ohne Vorschlag (Grundmenge der Gruppierung).</param>
/// <param name="Sections">Anzahl der Abschnitte (mehr als 1 bei sehr vielen unbekannten Gesichtern).</param>
public sealed record FaceGroupsResult(IReadOnlyList<FaceGroupInfo> Groups, int UnknownFaces, int Sections)
{
    public static FaceGroupsResult Empty { get; } = new([], 0, 0);
}

/// <summary>Ausreißer-Prüfung eines Gesichts vor dem Bestätigen (<see cref="IFaceRecognizer.CheckOutlier"/>).</summary>
public sealed record FaceOutlierResult(long FaceId, OutlierCheck Check)
{
    public bool IsOutlier => Check.IsOutlier;
}

/// <summary>Datei eines Gesichts (für die Lightbox).</summary>
public sealed record FaceFileInfo(
    int FileId,
    string Path,
    string Name,
    MediaType MediaType,
    bool IsFavorite,
    byte? Rating,
    long Size,
    DateTime ModifiedAtUtc);

/// <summary>Zustand des Zuordnungsdienstes (Kopfzeile, Tests).</summary>
/// <param name="LibraryId">Bibliothek des Modells.</param>
/// <param name="PersonsLearned">Personen mit mindestens einem Lernbeispiel.</param>
/// <param name="Examples">Lernbeispiele insgesamt.</param>
/// <param name="Generation">Zählt jedes Training hoch.</param>
public sealed record FaceRecognitionState(int? LibraryId, int PersonsLearned, int Examples, int Generation, bool IsBusy);

/// <summary>Ein Name ist schon vergeben (Personen-Namen sind ohne Groß-/Kleinschreibung eindeutig).</summary>
public sealed class PersonNameTakenException(string name, int existingPersonId)
    : InvalidOperationException($"Es gibt schon eine Person „{name}“.")
{
    public int ExistingPersonId { get; } = existingPersonId;
}

/// <summary>Stellschrauben des Zuordnungsdienstes (Tests verkürzen die Zeiten).</summary>
public sealed record FaceRecognitionOptions
{
    public static FaceRecognitionOptions Default { get; } = new();

    /// <summary>Entprellung: so lange nach der letzten Bestätigung/Korrektur bzw. Index-Meldung wird gewartet.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Erstes Training nach dem Start erst nach dieser Zeit (Galerie und Vorschaubilder haben Vorrang).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>So viele Gesichter je Vorschlags-Stapel (Lesen, SuggestMany, Schreiben in einer Transaktion).</summary>
    public int SuggestBatchSize { get; init; } = 2000;

    /// <summary>Gruppierung höchstens über so viele Gesichter auf einmal (O(n²), ADR-0036) — darüber abschnittsweise.</summary>
    public int MaxClusterSection { get; init; } = 50_000;

    public FaceRecognizerOptions Recognizer { get; init; } = FaceRecognizerOptions.Default;

    public FaceClusterOptions Cluster { get; init; } = FaceClusterOptions.Default;

    public Database.SqliteBusyRetryOptions? BusyRetry { get; init; }
}
