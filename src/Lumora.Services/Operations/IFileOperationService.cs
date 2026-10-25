using Lumora.Domain.Enums;

namespace Lumora.Services.Operations;

public sealed record FileOperationItemResult(
    int FileId,
    string SourcePath,
    string? DestPath,
    bool Success,
    string? Error);

public sealed record FileOperationBatchResult(
    int BatchId,
    OperationType Type,
    int Succeeded,
    int Failed,
    IReadOnlyList<FileOperationItemResult> Items);

public sealed record OperationBatchInfo(
    int Id,
    OperationType Type,
    BatchStatus Status,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    int ItemCount,
    string? Description,
    bool CanUndo);

/// <summary>Ergebnis einer Ordner-Operation (anlegen/umbenennen): Folders-Zeile und Pfad des Ordners nach der Operation.</summary>
public sealed record FolderOperationResult(
    int BatchId,
    bool Success,
    int? FolderId,
    string Path,
    string? Error);

/// <summary>
/// Datei-Operationen mit Protokoll in OperationBatches/FileOperations (ADR-0012).
/// Jede Operation hält Dateisystem und Katalog (Files, Folders, Thumbnails, FTS) konsistent
/// und sendet danach eine <c>FilesChangedMessage</c>. Fehler einzelner Dateien brechen den
/// Batch nicht ab (Teilerfolg wird pro Item gemeldet).
/// </summary>
public interface IFileOperationService
{
    /// <summary>Dateien in den Windows-Papierkorb verschieben; Katalog-Einträge werden entfernt.</summary>
    Task<FileOperationBatchResult> RecycleAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>
    /// Dateien in einen Zielordner verschieben. Liegt das Ziel unterhalb einer Wurzel der Library (ADR-0020), wird
    /// die Folders-Zeile (RootId der Wurzel) bei Bedarf angelegt und Files.FolderId aktualisiert; liegt es außerhalb
    /// aller Wurzeln, wird der Katalog-Eintrag entfernt. Existiert der Zielname bereits, schlägt dieses Item fehl
    /// (kein Überschreiben).
    /// </summary>
    Task<FileOperationBatchResult> MoveAsync(IReadOnlyList<int> fileIds, string targetFolderPath, CancellationToken ct = default);

    Task<FileOperationBatchResult> RenameAsync(int fileId, string newName, CancellationToken ct = default);

    /// <summary>
    /// Legt einen Unterordner an (Dateisystem + Folders-Kette unterhalb der Wurzel, die <paramref name="parentFolderPath"/>
    /// enthält), protokolliert als MkDir-Batch (nicht rückgängig machbar). Existiert der Ordner auf der Platte bereits,
    /// wird nur die Katalogzeile ergänzt.
    /// </summary>
    Task<FolderOperationResult> CreateFolderAsync(int libraryId, string parentFolderPath, string name, CancellationToken ct = default);

    /// <summary>
    /// Benennt einen Katalog-Ordner um: Dateisystem (<c>Directory.Move</c>, ein Schritt) und die Folders-Zeilen des
    /// Ordners und aller Unterordner; Dateien behalten ihre Id. Protokoll: RenameFolder-Batch mit der Ordner-Operation
    /// plus einer Operation je enthaltener Datei; rückgängig machbar (Ordner wird zurückbenannt). Wurzelordner
    /// können nicht umbenannt werden.
    /// </summary>
    Task<FolderOperationResult> RenameFolderAsync(int folderId, string newName, CancellationToken ct = default);

    /// <summary>
    /// „Ordner in die Bibliothek übernehmen“ (ADR-0034): führt einen <see cref="FolderAdoptionPlan"/> in EINEM
    /// AdoptFolder-Batch aus — Zielordner anlegen (Folders.RootId = Wurzel des Ziels), jede Datei verschieben (FileId bleibt,
    /// bei Namenskonflikt mit neuem Namen inkl. FTS; nicht katalogisierte Medien werden aufgenommen, Nicht-Medien nur
    /// verschoben). Liegen danach keine Dateien mehr im Quellordner, kommt er bei <paramref name="recycleSource"/> in den
    /// Papierkorb — bei <paramref name="keepSourceFolder"/> (Eingangs-Wurzel bleibt eingetragen) nur sein Inhalt.
    /// Rückgängig machbar (Dateien zurück, Ordner neu angelegt, leere Zielordner entfernt, ausgetragener Eingang wieder eingetragen).
    /// </summary>
    Task<FolderAdoptionExecution> AdoptFolderAsync(
        int libraryId,
        FolderAdoptionPlan plan,
        bool recycleSource,
        bool keepSourceFolder = false,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    /// <summary>Macht den jüngsten rückgängig machbaren Batch (Move/Rename/RenameFolder/AdoptFolder) rückgängig; null, wenn keiner existiert.</summary>
    Task<FileOperationBatchResult?> UndoLastAsync(CancellationToken ct = default);

    Task<FileOperationBatchResult?> UndoBatchAsync(int batchId, CancellationToken ct = default);

    Task<IReadOnlyList<OperationBatchInfo>> GetRecentBatchesAsync(int take = 50, CancellationToken ct = default);

    /// <summary>
    /// Holt die Dateien eines Papierkorb-Batches aus dem Windows-Papierkorb an ihren Ursprungsort zurück und bucht sie wieder
    /// in den Katalog (ADR-0053, Rückgängig der Seite „Doppelte“; <c>FileOperationService.Restore.cs</c>).
    /// <paramref name="restoreCatalog"/> darf die Files-Zeile selbst zurückschreiben (alte FileId samt Angaben) und liefert
    /// dann deren Id; sonst entsteht eine neue Zeile. null, wenn der Batch nicht existiert. Standard: nicht unterstützt.
    /// </summary>
    Task<FileOperationBatchResult?> RestoreRecycledAsync(int batchId, RecycleRestoreCatalog? restoreCatalog = null, CancellationToken ct = default)
        => throw new NotSupportedException("Zurückholen aus dem Papierkorb wird nicht unterstützt.");

    // ---- ADR-0065: Ordner in den Papierkorb, Fehlende aus der Bibliothek entfernen (FileOperationService.RecycleFolder.cs / .RemoveMissing.cs) ----

    /// <summary>
    /// Ordner samt Inhalt in den Windows-Papierkorb (RecycleFolder-Batch): Katalogzeilen der Dateien darin (mit Abzug für
    /// „Rückgängig“), Thumbnails, FTS und Folders-Zeilen werden entfernt. Abgelehnt (ohne Änderung): Wurzel selbst, Ordner mit
    /// einer Wurzel darin, Laufwerk nicht erreichbar, Ordner nicht vorhanden. Hat das Laufwerk keinen Papierkorb, passiert ohne
    /// <paramref name="allowPermanentDelete"/> nichts (<see cref="FolderRecycleResult.NeedsPermanentConsent"/>); mit
    /// Zustimmung wird endgültig gelöscht (DeleteFolder-Batch, nicht rückgängig zu machen). Standard: nicht unterstützt.
    /// </summary>
    Task<FolderRecycleResult> RecycleFolderAsync(int folderId, bool allowPermanentDelete = false, CancellationToken ct = default)
        => throw new NotSupportedException("Ordner in den Papierkorb wird nicht unterstützt.");

    /// <summary>
    /// Fehlende Dateien aus dem Katalog entfernen (RemoveFromCatalog-Batch) — keine Datei wird angefasst. Je Datei: nur wenn
    /// als fehlend markiert, die Wurzel erreichbar ist (Offline-vs-Missing-Guard, PLAN Kap. 12) und die Datei wirklich nicht
    /// da ist (sonst „wieder da“: Missing zurücksetzen). Abzug je Zeile für „Rückgängig“, danach Thumbnails, Cache-Datei,
    /// FTS (alte Werte), Verknüpfungen gesichert (ADR-0054). Anschließend leere, nicht mehr vorhandene Ordner (unterhalb von
    /// <paramref name="pruneUnderFolderId"/> bzw. auf dem Weg der entfernten Dateien) aus dem Baum. Standard: nicht unterstützt.
    /// </summary>
    Task<MissingRemovalBatchResult> RemoveMissingAsync(IReadOnlyList<int> fileIds, int? pruneUnderFolderId = null, CancellationToken ct = default)
        => throw new NotSupportedException("Fehlende entfernen wird nicht unterstützt.");

    // ---- ADR-0066: Ausschlüsse beim Einlesen, Ordner verschieben (FileOperationService.RemoveExcluded.cs / .MoveFolder.cs) ----

    /// <summary>
    /// Vorhandene Dateien in ausgeschlossenen Ordnern aus dem Katalog entfernen (ExcludeFromCatalog-Batch) — keine Datei wird
    /// angefasst. Nur Dateien, deren Ordner nach den aktuell gespeicherten Ausschlüssen ihrer Bibliothek ausgeschlossen ist
    /// (der Ausschluss wird also vorher gespeichert). Abzug je Zeile für „Rückgängig“ wie bei „Fehlende entfernen“, danach leere
    /// ausgeschlossene Ordnerzeilen aus dem Baum. Standard: nicht unterstützt.
    /// </summary>
    Task<ExclusionRemovalResult> RemoveExcludedAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => throw new NotSupportedException("Ausgeschlossene entfernen wird nicht unterstützt.");

    /// <summary>
    /// Ordner samt Inhalt in einen anderen Ordner derselben Bibliothek verschieben (MoveFolder-Batch, <c>Directory.Move</c>):
    /// FileIds bleiben, Pfade, Elternordner und Wurzel der Folders-Zeilen werden nachgezogen; rückgängig machbar. Abgelehnt (ohne
    /// Änderung, <see cref="FolderOperationResult.Success"/> false und BatchId 0): Wurzel selbst bzw. Wurzel darin, Ziel = Ordner
    /// selbst oder darin, Ziel schon Elternordner, gleichnamiger Ordner im Ziel, Ziel außerhalb der Bibliothek bzw. ausgeschlossen,
    /// anderes Laufwerk, Laufwerk nicht erreichbar, Ordner oder Ziel nicht vorhanden. Standard: nicht unterstützt.
    /// </summary>
    Task<FolderOperationResult> MoveFolderAsync(int folderId, string targetFolderPath, CancellationToken ct = default)
        => throw new NotSupportedException("Ordner verschieben wird nicht unterstützt.");
}

/// <summary>Ergebnis von <see cref="IFileOperationService.RemoveExcludedAsync"/> (ADR-0066).</summary>
/// <param name="BatchId">Protokoll-Batch (0, wenn nichts entfernt wurde).</param>
/// <param name="Removed">Aus dem Katalog entfernt.</param>
/// <param name="FoldersRemoved">Leere ausgeschlossene Ordnerzeilen aus dem Baum entfernt.</param>
/// <param name="SkippedNotExcluded">Ordner der Datei ist (nicht mehr) ausgeschlossen: nicht entfernt.</param>
/// <param name="Failed">Katalog-Buchung gescheitert bzw. Datei unbekannt.</param>
public sealed record ExclusionRemovalResult(int BatchId, int Removed, int FoldersRemoved, int SkippedNotExcluded, int Failed)
{
    public static ExclusionRemovalResult Empty { get; } = new(0, 0, 0, 0, 0);

    public bool CanUndo => BatchId > 0 && Removed > 0;
}

/// <summary>Ergebnis von <see cref="IFileOperationService.RecycleFolderAsync"/> (ADR-0065).</summary>
/// <param name="BatchId">Protokoll-Batch (0, wenn nichts geschah).</param>
/// <param name="Success">Ordner ist weg und der Katalog gebucht.</param>
/// <param name="Permanent">Endgültig gelöscht statt Papierkorb (kein Papierkorb, Zustimmung gegeben).</param>
/// <param name="FilesRemoved">Katalogzeilen entfernt.</param>
/// <param name="FilesFailed">Dateien, die noch da sind (z. B. gesperrt).</param>
/// <param name="FoldersRemoved">Folders-Zeilen entfernt.</param>
/// <param name="Error">Grund bei Ablehnung oder Fehler.</param>
/// <param name="NeedsPermanentConsent">Laufwerk ohne Papierkorb: nichts geschehen, Rückfrage „endgültig löschen?“ nötig.</param>
/// <param name="NoRecycleBinReason">Warum es keinen Papierkorb gibt (für die Rückfrage).</param>
/// <param name="FolderGone">Der Ordner ist von der Platte weg (auch wenn die Katalog-Buchung danach scheiterte).</param>
public sealed record FolderRecycleResult(
    int BatchId,
    bool Success,
    bool Permanent,
    int FilesRemoved,
    int FilesFailed,
    int FoldersRemoved,
    string? Error,
    bool NeedsPermanentConsent = false,
    string? NoRecycleBinReason = null,
    bool FolderGone = false)
{
    public bool CanUndo => Success && !Permanent && BatchId > 0;

    /// <summary>Etwas liegt im Papierkorb, das „Rückgängig“ zurückholen kann (auch bei Teilerfolg oder gescheiterter Buchung).</summary>
    public bool CanUndoPartially => !Permanent && BatchId > 0 && (FilesRemoved > 0 || FolderGone);

    public static FolderRecycleResult Rejected(string error) => new(0, false, false, 0, 0, 0, error);
}

/// <summary>Ergebnis von <see cref="IFileOperationService.RemoveMissingAsync"/> (ADR-0065).</summary>
/// <param name="BatchId">Protokoll-Batch (0, wenn nichts entfernt wurde).</param>
/// <param name="Removed">Aus dem Katalog entfernt.</param>
/// <param name="Reappeared">Datei war wieder da: nicht entfernt, „fehlt“ zurückgesetzt.</param>
/// <param name="SkippedOffline">Wurzel nicht erreichbar: nicht entfernt (gelten als offline, nicht als fehlend).</param>
/// <param name="SkippedNotMissing">Nicht als fehlend markiert: nicht entfernt.</param>
/// <param name="FoldersRemoved">Leere, nicht mehr vorhandene Ordner aus dem Baum entfernt.</param>
/// <param name="Failed">Katalog-Buchung gescheitert.</param>
/// <param name="OfflineRoots">Nicht erreichbare Wurzeln (Pfade, nur für die Anzeige).</param>
public sealed record MissingRemovalBatchResult(
    int BatchId,
    int Removed,
    int Reappeared,
    int SkippedOffline,
    int SkippedNotMissing,
    int FoldersRemoved,
    int Failed,
    IReadOnlyList<string> OfflineRoots)
{
    public static MissingRemovalBatchResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, []);

    public bool CanUndo => BatchId > 0 && Removed > 0;
}

/// <summary>Eine aus dem Papierkorb zurückgeholte Datei, die noch in den Katalog muss.</summary>
/// <param name="OldFileId">FileId vor dem Löschen (aus dem Protokoll).</param>
/// <param name="Path">Ursprungspfad (Datei liegt wieder dort).</param>
/// <param name="LibraryId">Bibliothek der Wurzel, unter der der Pfad liegt.</param>
/// <param name="FolderId">Folders-Zeile des Ursprungsordners (bei Bedarf neu angelegt).</param>
public sealed record RecycledFile(int? OldFileId, string Path, int LibraryId, int FolderId);

/// <summary>
/// Schreibt die Files-Zeile einer zurückgeholten Datei innerhalb der laufenden Item-Transaktion (Verbindung und Transaktion
/// des Katalogs); liefert die FileId der geschriebenen Zeile oder null, wenn der Standardweg (neue Zeile) gelten soll.
/// </summary>
public delegate int? RecycleRestoreCatalog(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, RecycledFile file);
