namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>Punkte des Sicherheits-Checks (ADR-0064) in Anzeigereihenfolge.</summary>
public enum SecurityCheckId
{
    BitLocker = 1,
    Hibernation = 2,
    FastStartup = 3,
    PageFile = 4,
    RecentItems = 5,
    ActivityHistory = 6,
    Thumbnails = 7,
    SearchIndex = 8,
    VeraCryptHistory = 9,
    ExitAfterLock = 10,
}

/// <summary>Ergebnis einer Prüfung.</summary>
public enum SecurityCheckStatus
{
    /// <summary>Konnte nicht sicher gelesen werden (Leser-Fehler, Wert fehlt ohne festen Standard) — nie geraten.</summary>
    Unknown = 0,

    Fulfilled = 1,

    NotFulfilled = 2,

    /// <summary>Betrifft diesen PC nicht (z. B. VeraCrypt nicht installiert) — zählt nicht.</summary>
    NotApplicable = 3,
}

/// <summary>Wovor die versteckte Bibliothek schützen soll; bestimmt, welche Punkte zählen (Tabelle in ADR-0064).</summary>
public enum ThreatLevel
{
    /// <summary>Diebstahl, Reparatur, Verkauf der Festplatte — es zählt nur BitLocker auf C:.</summary>
    PoweredOff = 1,

    /// <summary>Familie oder jemand kurz am PC (Standard) — alle Windows-Punkte zählen.</summary>
    SharedUser = 2,

    /// <summary>Fachmännische Untersuchung — alle Punkte zählen, dazu die Lumora-Option.</summary>
    Forensic = 3,
}

/// <summary>Gewicht eines Punkts in der Anzeige (Etikett und Farbe).</summary>
public enum SecurityCheckWeight
{
    /// <summary>Zählt nicht bei dieser Gefahr (grau).</summary>
    NotCounted = 0,

    /// <summary>Zählt, „empfohlen“ (gelb, wenn nicht erfüllt).</summary>
    Recommended = 1,

    /// <summary>Zählt, „wichtigster Punkt“ (rot, wenn nicht erfüllt).</summary>
    Critical = 2,
}

/// <summary>Was eine Prüfung über den PC weiß. Laufwerksbuchstaben nur hier und in der Anzeige — nie im Protokoll.</summary>
/// <param name="SystemDrive">Laufwerk mit Windows (meist C).</param>
/// <param name="ContainerDrive">Laufwerk der geöffneten versteckten Bibliothek; null = unbekannt.</param>
/// <param name="ExitAfterLock">Lumora-Option „nach dem Zusperren ganz beenden“.</param>
public sealed record SecurityCheckContext(char SystemDrive, char? ContainerDrive, bool ExitAfterLock);

/// <summary>Ergebnis eines Punkts.</summary>
/// <param name="Detail">Ein Satz für die Anzeige (Begründung bzw. Zustand).</param>
public sealed record SecurityCheckFinding(SecurityCheckId Id, SecurityCheckStatus Status, string Detail)
{
    public static SecurityCheckFinding Ok(SecurityCheckId id, string detail) => new(id, SecurityCheckStatus.Fulfilled, detail);

    public static SecurityCheckFinding Bad(SecurityCheckId id, string detail) => new(id, SecurityCheckStatus.NotFulfilled, detail);

    public static SecurityCheckFinding Unknown(SecurityCheckId id, string detail) => new(id, SecurityCheckStatus.Unknown, detail);
}

/// <summary>Alle Punkte eines Durchlaufs samt Wertung für die gewählte Gefahren-Stufe.</summary>
public sealed record SecurityCheckReport(IReadOnlyList<SecurityCheckFinding> Findings, ThreatLevel Level)
{
    /// <summary>Erfüllte unter den zählenden Punkten.</summary>
    public int FulfilledCount => Findings.Count(f => SecurityCheckRating.Counts(f, Level) && f.Status == SecurityCheckStatus.Fulfilled);

    /// <summary>Zählende Punkte (ohne „nicht zutreffend“ und ohne graue Punkte dieser Stufe).</summary>
    public int CountedTotal => Findings.Count(f => SecurityCheckRating.Counts(f, Level));

    /// <summary>„Sicherheits-Check: x von y erfüllt“ für die Statusleiste.</summary>
    public string StatusText => $"Sicherheits-Check: {FulfilledCount} von {CountedTotal} erfüllt";

    public SecurityCheckReport WithLevel(ThreatLevel level) => this with { Level = level };
}

/// <summary>Neues Ergebnis für die Statusleiste (nur die versteckte Sitzung sendet es).</summary>
public sealed record SecurityCheckSummaryMessage(string Text);
