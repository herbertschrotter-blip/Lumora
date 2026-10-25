using Lumora.Domain.Enums;

namespace Lumora.Services.Scanning;

/// <summary>
/// Ergebnis eines Filesystem-Walks (vor DB-Persistenz). Path ist absoluter Pfad.
/// </summary>
public sealed record DiscoveredFile(
    string FullPath,
    string FolderPath,
    string Name,
    string Extension,
    long Size,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    MediaType MediaType);

/// <summary>
/// Aggregierte Folder-Information aus dem Discover-Pass.
/// </summary>
public sealed record DiscoveredFolder(
    string Path,
    string? ParentPath,
    string Name);

/// <summary>
/// Phase eines laufenden Scans (für Statusleiste und Scan-Panel).
/// </summary>
public enum ScanPhase
{
    /// <summary>Filesystem wird durchsucht (CurrentPath = aktueller Ordner).</summary>
    Enumerating = 0,

    /// <summary>ClearAndRescan: alte Katalogdaten werden entfernt.</summary>
    Clearing = 1,

    /// <summary>Folders/Files werden in die DB geschrieben (FilesDiscovered = bereits geschriebene Zeilen).</summary>
    Persisting = 2,

    /// <summary>FTS5-Suchindex wird befüllt.</summary>
    Indexing = 3,
}

/// <summary>
/// Progress-Event für UI-Feedback während eines Scans.
/// </summary>
public sealed record ScanProgress(
    int FoldersDiscovered,
    int FilesDiscovered,
    long TotalBytesDiscovered,
    string? CurrentPath)
{
    /// <summary>Aktuelle Phase; Default Enumerating, damit bestehende Aufrufer unverändert bleiben.</summary>
    public ScanPhase Phase { get; init; } = ScanPhase.Enumerating;
}

/// <summary>
/// Wie ein Scan geendet hat (PLAN Kap. 12: Offline-vs-Missing-Guard).
/// </summary>
public enum ScanOutcome
{
    /// <summary>Scan vollständig durchgelaufen, Katalog ist aktuell.</summary>
    Completed = 0,

    /// <summary>
    /// Der Bibliotheksordner war nicht erreichbar (vor dem Scan oder währenddessen). Es wurden keine
    /// Dateien als fehlend markiert; bereits geschriebene neue/geänderte Dateien bleiben im Katalog.
    /// </summary>
    LibraryOffline = 1,

    /// <summary>
    /// Incremental: ungewöhnlich viele Dateien fehlen (&gt; 10.000 oder &gt; 20 % der Bibliothek).
    /// Neue und geänderte Dateien wurden übernommen, fehlende NICHT markiert — der Aufrufer fragt nach
    /// und wiederholt den Scan mit <see cref="ScanOptions.ConfirmMassMissing"/>.
    /// </summary>
    MassMissingBlocked = 2,

    /// <summary>
    /// Reserviert für einen abgebrochenen Scan. Aktuell signalisiert <c>ScanAsync</c> den Abbruch wie bisher
    /// durch <see cref="OperationCanceledException"/>; dieser Wert wird nicht geliefert.
    /// </summary>
    Canceled = 3,
}

/// <summary>
/// Optionen für <see cref="IScanService.ScanAsync(int, ScanMode, ScanOptions, IProgress{ScanProgress}?, CancellationToken)"/>.
/// </summary>
public sealed record ScanOptions
{
    public static ScanOptions Default { get; } = new();

    /// <summary>
    /// Incremental: fehlende Dateien auch dann als Missing markieren, wenn der Mass-Missing-Schwellwert
    /// überschritten ist (Benutzer hat „Als fehlend markieren“ bestätigt, PLAN Kap. 12).
    /// </summary>
    public bool ConfirmMassMissing { get; init; }

    /// <summary>
    /// Kennung des Aufrufers für die Scan-Sperre (0.2.29): landet in <see cref="ScanGateWait.CallerId"/>, falls der Scan
    /// auf die Bibliothek warten muss. Die Bibliotheksseite vergibt je Scan-Lauf eine eigene und zeigt nur dieses Warten.
    /// </summary>
    public Guid? GateCallerId { get; init; }

    /// <summary>
    /// Nur <see cref="ScanMode.ClearAndRescan"/> (#22, Nachtrag ADR-0066): Dateien, die schon in ausgeschlossenen Ordnern
    /// katalogisiert sind („Nur künftig ausschließen“), bleiben im neuen Katalog — der Ausschluss gilt für genau diese Einträge
    /// nicht, neue Dateien dort bleiben draußen. Ihre Benutzerdaten trägt <see cref="UserDataCarryOver"/> wie bei allen anderen mit.
    /// </summary>
    public bool KeepCatalogedInExcludedFolders { get; init; }

    /// <summary>
    /// Incremental: nur dieser Ordner samt Unterordnern (ADR-0068, Ordnerleiste › „Neu einlesen“). Dateien außerhalb bleiben
    /// unberührt (weder neu noch „fehlt“); null = ganze Bibliothek.
    /// </summary>
    public string? SubtreePath { get; init; }
}

/// <summary>
/// Ergebnis eines abgeschlossenen Scans, inkl. Zähler und Timing. Die positionalen Felder stammen aus dem
/// Fresh-Scan und bleiben erhalten; die Diff-Zähler (Added, Modified, MarkedMissing, Reappeared, FoldersAdded)
/// und <see cref="Outcome"/> kamen mit dem Incremental-Scan dazu. Bei Fresh/ClearAndRescan gilt
/// Added = FilesInserted und FoldersAdded = FoldersInserted. <paramref name="RootPath"/> ist bei
/// <see cref="ScanOutcome.LibraryOffline"/> die nicht erreichbare Wurzel, sonst die Archiv-Wurzel (ADR-0020);
/// die Zähler summieren über alle Wurzeln.
/// </summary>
public sealed record ScanResult(
    int LibraryId,
    string RootPath,
    int FoldersInserted,
    int FilesInserted,
    int FtsRowsInserted,
    long TotalBytes,
    TimeSpan TotalDuration,
    TimeSpan EnumerationDuration,
    TimeSpan PersistenceDuration,
    TimeSpan FtsDuration)
{
    /// <summary>Modus, mit dem gescannt wurde.</summary>
    public ScanMode Mode { get; init; } = ScanMode.Fresh;

    /// <summary>Wie der Scan geendet hat; bei Fresh/ClearAndRescan immer Completed oder LibraryOffline.</summary>
    public ScanOutcome Outcome { get; init; } = ScanOutcome.Completed;

    /// <summary>Neu in den Katalog aufgenommene Dateien.</summary>
    public int Added { get; init; } = FilesInserted;

    /// <summary>Neu angelegte Ordner (inkl. Zwischenordner der Kette).</summary>
    public int FoldersAdded { get; init; } = FoldersInserted;

    /// <summary>Dateien, deren Größe oder Änderungszeit sich geändert hat (Zeile aktualisiert, Id bleibt).</summary>
    public int Modified { get; init; }

    /// <summary>Dateien, die in diesem Scan als fehlend markiert wurden (Zeile, Favorit und Bewertung bleiben).</summary>
    public int MarkedMissing { get; init; }

    /// <summary>Zuvor fehlende Dateien, die wieder auf der Platte liegen (Missing zurückgesetzt).</summary>
    public int Reappeared { get; init; }

    /// <summary>
    /// Beim Abgleich erkannte, bisher vorhandene Dateien, die nicht mehr auf der Platte liegen — unabhängig
    /// davon, ob sie markiert wurden. Bei <see cref="ScanOutcome.MassMissingBlocked"/> ist MarkedMissing 0
    /// und dieser Zähler nennt die Zahl für den Rückfrage-Dialog.
    /// </summary>
    public int MissingDetected { get; init; }

    /// <summary>Zahl der tatsächlich durchlaufenen Wurzeln (ADR-0020); bei LibraryOffline vor dem Start 0.</summary>
    public int RootsScanned { get; init; }

    /// <summary>ClearAndRescan mit <see cref="ScanOptions.KeepCatalogedInExcludedFolders"/>: behaltene Einträge in ausgeschlossenen Ordnern (#22).</summary>
    public int KeptExcluded { get; init; }

    /// <summary>true, wenn der Scan irgendetwas am Katalog verändert hat.</summary>
    public bool HasChanges => Added > 0 || Modified > 0 || MarkedMissing > 0 || Reappeared > 0 || FoldersAdded > 0;
}

/// <summary>
/// Modus für ScanService.ScanAsync.
/// </summary>
public enum ScanMode
{
    /// <summary>Erst-Scan: erwartet leere Folders/Files in der DB für diese Library, sonst Fehler.</summary>
    Fresh = 0,

    /// <summary>
    /// Bestehende Scan-Daten der Library (Files, Folders, Thumbnails, FTS) löschen und danach wie Fresh scannen.
    /// FileIds werden dabei neu vergeben.
    /// </summary>
    ClearAndRescan = 1,

    /// <summary>
    /// Diff-Scan: neue Dateien anlegen, nicht mehr vorhandene als Missing markieren, geänderte
    /// (Size/ModifiedAt) aktualisieren. FileIds bleiben stabil. Mit Offline-vs-Missing-Guard (PLAN Kap. 12).
    /// </summary>
    Incremental = 2,
}
