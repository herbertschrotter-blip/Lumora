using Lumora.Domain.Enums;

namespace Lumora.Services.Duplicates;

/// <summary>Art der Doppelten (Umschalter der Seite „Doppelte“, ADR-0053).</summary>
public enum DuplicateMode
{
    /// <summary>Byte für Byte gleich (gleiche SHA-256-Prüfsumme und Größe).</summary>
    Exact = 0,

    /// <summary>Ähnliches Bild (pHash-Abstand ≤ Schwelle): verkleinert, neu gespeichert, Serienaufnahme, WhatsApp-Kopie.</summary>
    Similar = 1,
}

/// <summary>Abfrage der Seite „Doppelte“.</summary>
/// <param name="LibraryIds">Bereich (ADR-0048): aktive Bibliothek bzw. alle der Gruppe in „Alle verknüpften“.</param>
/// <param name="Mode">Exakt gleich oder ähnlich.</param>
/// <param name="MaxDistance">Höchster pHash-Abstand (nur <see cref="DuplicateMode.Similar"/>).</param>
/// <param name="InboxOnly">Nur Gruppen mit einer Datei im Eingang UND einer in der Bibliothek („schon in der Bibliothek“).</param>
public sealed record DuplicateQuery(
    IReadOnlyList<int> LibraryIds,
    DuplicateMode Mode,
    int MaxDistance = DuplicateSettings.DefaultSimilarThreshold,
    bool InboxOnly = false);

/// <summary>Eine Datei einer Gruppe mit allem, was die Seite zeigt und die Behalten-Regel braucht.</summary>
public sealed record DuplicateMember(
    int FileId,
    int LibraryId,
    string LibraryName,
    string Name,
    string FolderPath,
    MediaType MediaType,
    long Size,
    int? Width,
    int? Height,
    DateTime? CapturedAtUtc,
    CaptureDateSource? CapturedAtSource,
    int? CapturedAtOffsetMinutes,
    byte? Rating,
    bool IsFavorite,
    int TagCount,
    int CategoryCount,
    int PersonCount,
    bool IsInInbox,
    DateTime ModifiedAtUtc,
    string? ThumbnailPath)
{
    public string FullPath => Path.Combine(FolderPath, Name);

    /// <summary>Pixelzahl (0 = unbekannt).</summary>
    public long Pixels => Width is > 0 && Height is > 0 ? (long)Width.Value * Height.Value : 0;

    /// <summary>
    /// Aufnahmedatum aus der Datei (EXIF/Video, bei OneDrive-Platzhaltern vorläufig über Windows) oder von Hand gesetzt — nicht
    /// aus dem Dateinamen geraten.
    /// </summary>
    public bool HasCaptureDate => CapturedAtUtc is not null
                                  && CapturedAtSource is CaptureDateSource.Exif or CaptureDateSource.Video or CaptureDateSource.Manual or CaptureDateSource.Cloud;

    /// <summary>Bewertung, Favorit, Tags, Kategorien, Personen als eine Zahl (für die Behalten-Regel).</summary>
    public int UserDataScore => (Rating ?? 0) * 2 + (IsFavorite ? 3 : 0) + TagCount + CategoryCount + PersonCount;

    /// <summary>pHash-Abstand zum ersten Bild der Gruppe (nur „ähnlich“; 0 bei „exakt gleich“).</summary>
    public int Distance { get; init; }

    /// <summary>Byte-gleich mit dem ersten Bild der Gruppe (in „ähnlich“ als Hinweis).</summary>
    public bool IsExactCopy { get; init; }
}

/// <summary>Eine Gruppe doppelter Dateien; <see cref="Members"/> stehen in der Reihenfolge der Behalten-Regel (Vorschlag zuerst).</summary>
public sealed record DuplicateGroup(
    string Key,
    DuplicateMode Mode,
    IReadOnlyList<DuplicateMember> Members,
    int KeeperFileId,
    string KeeperReason)
{
    /// <summary>Größter Abstand innerhalb der Gruppe zum ersten Bild (nur „ähnlich“).</summary>
    public int MaxDistance => Members.Count == 0 ? 0 : Members.Max(m => m.Distance);

    /// <summary>Bytes, die frei würden, wenn nur der Vorschlag bleibt.</summary>
    public long RedundantBytes => Members.Where(m => m.FileId != KeeperFileId).Sum(m => m.Size);
}

/// <summary>Ergebnis der Abfrage samt Stand des Hintergrund-Index (für den Hinweis „noch nicht vollständig“).</summary>
public sealed record DuplicateResult(IReadOnlyList<DuplicateGroup> Groups, int PendingContent, int PendingImages)
{
    public int FileCount => Groups.Sum(g => g.Members.Count);

    public int RedundantCount => Groups.Sum(g => g.Members.Count - 1);

    public long RedundantBytes => Groups.Sum(g => g.RedundantBytes);
}

/// <summary>Eine Gruppe zum Aufräumen: <see cref="KeepFileId"/> bleibt, <see cref="DeleteFileIds"/> gehen in den Papierkorb.</summary>
public sealed record DuplicateCleanupGroup(int KeepFileId, IReadOnlyList<int> DeleteFileIds, bool IsExact);

/// <summary>Ergebnis von „Übrige in den Papierkorb“.</summary>
/// <param name="BatchId">Recycle-Batch (ADR-0012); 0, wenn nichts gelöscht wurde.</param>
/// <param name="Recycled">Dateien im Papierkorb.</param>
/// <param name="Failed">Dateien, die bleiben mussten (Grund in <paramref name="Errors"/>).</param>
/// <param name="Transferred">Übertragene Einzelheiten (Favorit, Bewertung, Tags, Kategorien, Personen).</param>
/// <param name="PersonsNotTransferred">Personen-Zuordnungen ohne passendes Gesicht in der behaltenen Datei.</param>
public sealed record DuplicateCleanupResult(
    int BatchId,
    int Recycled,
    int Failed,
    int Transferred,
    int PersonsNotTransferred,
    IReadOnlyList<string> Errors);

/// <summary>Letzte rückgängig machbare Aufräumaktion.</summary>
public sealed record DuplicateUndoInfo(int BatchId, int FileCount, DateTime CreatedAtUtc, string? Description);

/// <summary>Ergebnis von „Rückgängig“: zurückgeholte und nicht zurückholbare Dateien.</summary>
public sealed record DuplicateUndoResult(int BatchId, int Restored, int Failed, IReadOnlyList<string> Errors);

/// <summary>Zustand des Hintergrund-Index (Statusleiste).</summary>
/// <param name="IsRunning">Ein Durchgang läuft.</param>
/// <param name="Phase">„Prüfsummen“ bzw. „Bild-Fingerabdrücke“.</param>
public sealed record DuplicateIndexStatus(bool IsRunning, string Phase, int Done, int Total, string? Message = null)
{
    public static DuplicateIndexStatus Idle { get; } = new(false, string.Empty, 0, 0);
}

/// <summary>Ergebnis eines Durchgangs (Tests, Log).</summary>
public sealed record DuplicateIndexReport(int Hashed, int Fingerprinted, int Skipped, int Failed);

/// <summary>Einstellungen der Seite „Doppelte“ (Settings-Tabelle).</summary>
public sealed record DuplicateSettings(int SimilarThreshold, bool TransferMetadata)
{
    /// <summary>
    /// Standard-Schwelle für „ähnlich“: pHash-Abstand ≤ 10 von 64 Bit (Spike 15: verkleinerte/neu gespeicherte Kopien 0–4,
    /// leicht beschnittene und Serienaufnahmen bis ≈ 10, fremde Fotos fast nie darunter).
    /// </summary>
    public const int DefaultSimilarThreshold = 10;

    public const int MinThreshold = 1;

    /// <summary>Höchste einstellbare Schwelle (darüber zählen zu viele fremde Fotos als „ähnlich“, und die Suche wird langsam).</summary>
    public const int MaxThreshold = 12;

    public const string ThresholdKey = "duplicates.similarThreshold";

    public const string TransferKey = "duplicates.transferMetadata";

    public static DuplicateSettings Default { get; } = new(DefaultSimilarThreshold, true);
}

/// <summary>Fortschritt des Hintergrund-Index (vom Worker-Thread, gedrosselt; Empfänger dispatchen selbst).</summary>
public sealed record DuplicateIndexStatusMessage(DuplicateIndexStatus Status);

/// <summary>Neue Fingerabdrücke bzw. aufgeräumte Doppelte — die Seite „Doppelte“ und der Galerie-Filter lesen neu.</summary>
public sealed record DuplicatesChangedMessage(IReadOnlyList<int> LibraryIds);

/// <summary>
/// Nach einem Durchgang mit neuen Dateien im Eingang: <paramref name="Count"/> Dateien im Eingang der Bibliothek liegen
/// byte-gleich schon im Bibliotheksordner (Hinweis in der Statusleiste, „anzeigen“ öffnet die Seite gefiltert).
/// </summary>
public sealed record InboxDuplicatesFoundMessage(int LibraryId, int Count);
