using Lumora.Domain.Enums;

namespace Lumora.Services.Stories;

/// <summary>
/// Eine Story als Karte der Übersicht (ADR-0058). Überschriften sind sachlich gebaut (<see cref="StoryTitleBuilder"/>);
/// <see cref="Title"/> ist der eigene Titel, falls gesetzt. Zähler gelten für den aktuellen Bereich (ADR-0048).
/// </summary>
/// <param name="KindLabel">„Reise“, „Ereignis“, „Ausflug“, „Person“, „Ort“, „Rückblick“, „Eigene Story“.</param>
/// <param name="AutoTitle">Sachliche Überschrift (auch wenn ein eigener Titel gesetzt ist — für „Titel zurücksetzen“).</param>
/// <param name="Subtitle">Orte („Grado · Triest · Aquileia — Italien“), bei Personen „312 Fotos mit Anna“, bei Orten „6 Besuche · Italien“.</param>
/// <param name="CountsText">„421 Fotos · 9 Videos“.</param>
/// <param name="Year">Jahr des ersten Tages (Jahres-Chips der Übersicht).</param>
/// <param name="DayCount">Zahl der Kalendertage (Kopfzeile „Reise · 8 Tage“).</param>
public sealed record StoryCard(
    int Id,
    StoryKind Kind,
    string KindLabel,
    string Title,
    string AutoTitle,
    string? Subtitle,
    string CountsText,
    int FileCount,
    int VideoCount,
    DateTime StartAt,
    DateTime EndAt,
    DateOnly FirstDay,
    DateOnly LastDay,
    int Year,
    int DayCount,
    int? CoverFileId,
    bool CoverIsManual,
    bool IsPinned,
    bool IsHidden,
    bool IsSeen,
    bool IsAway,
    bool HasCustomTitle,
    int? PersonId,
    int? PlaceId,
    DateTime CreatedAtUtc);

/// <summary>Zähler je Art (Filter-Leiste der Übersicht: Alle, Ereignisse &amp; Reisen, Rückblicke, Personen, Orte).</summary>
public sealed record StoryKindCounts(int All, int EventsAndTrips, int Memories, int Persons, int Places, int Custom);

/// <summary>Jahr mit Zahl der Ereignisse und Reisen (Jahres-Chips).</summary>
public sealed record StoryYearCount(int Year, int Count);

/// <summary>
/// Übersicht der Story-Seite (ADR-0058, Mockup 09): Rückblicke von heute, Angeheftet, Neu (ungesehen), Ereignisse &amp;
/// Reisen (neueste zuerst, mit Jahren), Personen, Orte, eigene Storys, Zahl der ausgeblendeten. Ausgeblendete Storys und
/// Arten, die in den Einstellungen aus sind, fehlen in allen Listen.
/// </summary>
/// <param name="NewCount">Alle ungesehenen (die Liste <see cref="New"/> ist auf die neuesten begrenzt).</param>
/// <param name="TotalFiles">Fotos und Videos des Bereichs, die in mindestens einer Story stehen.</param>
public sealed record StoryOverview(
    IReadOnlyList<StoryMemory> Memories,
    IReadOnlyList<StoryCard> Pinned,
    IReadOnlyList<StoryCard> New,
    int NewCount,
    IReadOnlyList<StoryCard> EventsAndTrips,
    IReadOnlyList<StoryYearCount> Years,
    IReadOnlyList<StoryCard> Persons,
    IReadOnlyList<StoryCard> Places,
    IReadOnlyList<StoryCard> Custom,
    int HiddenCount,
    StoryKindCounts Counts,
    int TotalFiles)
{
    public static StoryOverview Empty { get; } = new([], [], [], 0, [], [], [], [], [], 0, new StoryKindCounts(0, 0, 0, 0, 0, 0), 0);
}

/// <summary>Person in einer Story (bestätigte Gesichter), meiste Fotos zuerst.</summary>
public sealed record StoryPerson(int PersonId, string Name, int FileCount);

/// <summary>Ein Ort einer Story mit Namen (GeoNames) — Reihenfolge des ersten Auftretens.</summary>
public sealed record StoryPlace(int PlaceId, string Name, string Country);

/// <summary>
/// Tagesabschnitt der Story-Ansicht: „Tag 1“, „Samstag, 12. Juli“, Orte des Tages, Datei-Ids in Aufnahme-Reihenfolge.
/// </summary>
/// <param name="Index">1-basiert („Tag 1“).</param>
/// <param name="ShortTitle">„Sa 12.“ (Tagesleiste).</param>
public sealed record StoryDay(
    int Index,
    DateOnly Date,
    string Title,
    string ShortTitle,
    string? Places,
    IReadOnlyList<int> FileIds,
    int PhotoCount,
    int VideoCount);

/// <summary>Details einer Story für die Story-Ansicht.</summary>
/// <param name="Kicker">Kopfzeile über dem Titel: „Reise · 8 Tage“, „Heute vor 3 Jahren“.</param>
/// <param name="FileIds">Alle zählenden Dateien in Aufnahme-Reihenfolge (aktueller Bereich).</param>
public sealed record StoryDetails(
    StoryCard Card,
    string Kicker,
    IReadOnlyList<StoryPlace> Places,
    IReadOnlyList<StoryPerson> Persons,
    IReadOnlyList<StoryDay> Days,
    IReadOnlyList<int> FileIds);

/// <summary>Art eines Rückblicks.</summary>
public enum StoryMemoryKind
{
    /// <summary>„Heute vor N Jahren“.</summary>
    OnThisDay = 0,

    /// <summary>„Oktober in früheren Jahren“.</summary>
    ThisMonth = 1,

    /// <summary>„Vergessene Schätze“: gute Fotos, die seit über einem Jahr niemand angesehen hat (ADR-0072, je Monat neu).</summary>
    ForgottenTreasures = 2,
}

/// <summary>
/// Ein Rückblick von heute (dynamisch je Tag berechnet, ADR-0058). <see cref="Key"/> ist die Signatur, mit der
/// <see cref="IStoryService.OpenMemoryAsync"/> ihn als Story speichert (öffnen, anheften, Titelbild …).
/// </summary>
/// <param name="Kicker">„Heute vor 3 Jahren“ bzw. „Oktober in früheren Jahren“.</param>
/// <param name="Title">„Freitag, 21. Oktober 2022“ bzw. „Oktober · 2015–2024“.</param>
/// <param name="StoryId">Schon gespeicherte Story zu diesem Rückblick oder null.</param>
public sealed record StoryMemory(
    string Key,
    StoryMemoryKind Kind,
    int YearsAgo,
    DateOnly Date,
    string Kicker,
    string Title,
    string? Subtitle,
    string CountsText,
    int FileCount,
    int VideoCount,
    int? CoverFileId,
    IReadOnlyList<int> PreviewFileIds,
    int? StoryId,
    bool IsPinned);

/// <summary>Zustand der Hintergrund-Erkennung (Statusleiste).</summary>
public sealed record StoryIndexStatus(bool IsRunning, string Text, int Stories = 0)
{
    public static StoryIndexStatus Idle { get; } = new(false, "Storys aktuell");
}

/// <summary>Ergebnis eines Erkennungs-Durchgangs (Log, Messung, Tests).</summary>
public sealed record StoryIndexReport(
    int Groups,
    int Files,
    int Detected,
    int Inserted,
    int Changed,
    int Deleted,
    TimeSpan Read,
    TimeSpan Detect,
    TimeSpan Write,
    TimeSpan Covers,
    TimeSpan Total)
{
    public static StoryIndexReport Empty { get; } = new(0, 0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    public StoryIndexReport Add(StoryIndexReport other) => new(
        Groups + other.Groups, Files + other.Files, Detected + other.Detected, Inserted + other.Inserted, Changed + other.Changed,
        Deleted + other.Deleted, Read + other.Read, Detect + other.Detect, Write + other.Write, Covers + other.Covers, Total + other.Total);
}

/// <summary>Story nicht gefunden (gelöscht oder aus einer anderen Gruppe).</summary>
public sealed class StoryNotFoundException(int storyId) : InvalidOperationException($"Story {storyId} gibt es nicht (mehr).")
{
    public int StoryId { get; } = storyId;
}

/// <summary>
/// Zeitzone und „heute“ der Storys: Kalendertage in Ortszeit der Aufnahme (Dateien ohne eigenen Zeitversatz in der
/// Zeitzone des Rechners). Eigene Klasse, damit Tests einen festen Tag und eine feste Zone setzen.
/// </summary>
public class StoryClock
{
    public virtual TimeZoneInfo Zone => TimeZoneInfo.Local;

    public virtual DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, Zone));
}

/// <summary>Feste Uhr (Tests, Rauchtest).</summary>
public sealed class FixedStoryClock(DateOnly today, TimeZoneInfo? zone = null) : StoryClock
{
    public override TimeZoneInfo Zone { get; } = zone ?? TimeZoneInfo.Utc;

    public override DateOnly Today { get; } = today;
}
