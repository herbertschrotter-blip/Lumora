namespace Lumora.Services.Scanning;

/// <summary>
/// Zusammengefügte Ergebnisse über „Neu aufbauen“ (ADR-0078 Punkt 11). <c>MergedPhotos</c> hängt am Ergebnis, die Quellen-Zeilen
/// an Ergebnis und Quelle — alle löscht die Kaskade mit den alten Files-Zeilen. Beides wird in TEMP-Tabellen geparkt und über die
/// Zuordnung alt → neu (Pfade) neu geschrieben. Fehlt das neue Ergebnis, fällt alles weg; fehlt eine Quelle, nur ihre Zeile
/// (<c>SourceCount</c> bleibt). Die Serie (Stufe 2) wird neu erkannt, deshalb ohne <c>SeriesId</c>.
/// <para>
/// Dazu gesicherte Datei-Verknüpfungen entfernter Dateien, deren Ziel eine FileId dieser Bibliothek ist (<c>FileLinkUndo</c>
/// Arten 4–7: Fassung/Original, Ergebnis/Quelle): das Ziel wird mitgetragen und auf die neue FileId umgeschrieben; ohne
/// Gegenstück verfällt die Sicherung — sonst zeigte sie nach dem Neuaufbau auf eine fremde Datei.
/// </para>
/// </summary>
public sealed partial class UserDataCarryOver
{
    private const string ParkMergesSql = """
        DROP TABLE IF EXISTS temp.pv_carry_merges;
        DROP TABLE IF EXISTS temp.pv_carry_merge_sources;
        DROP TABLE IF EXISTS temp.pv_carry_undo_files;
        CREATE TEMP TABLE pv_carry_undo_files(
            UndoId      INTEGER PRIMARY KEY,
            OldTargetId INTEGER NOT NULL);
        INSERT INTO temp.pv_carry_undo_files(UndoId, OldTargetId)
            SELECT u.Id, u.TargetId FROM FileLinkUndo u JOIN Files f ON f.Id = u.TargetId
            WHERE u.Kind BETWEEN 4 AND 7 AND f.LibraryId = $lib;
        CREATE TEMP TABLE pv_carry_merges(
            OldFileId    INTEGER PRIMARY KEY,
            Method       INTEGER NOT NULL,
            Projection   INTEGER,
            SourceCount  INTEGER NOT NULL,
            Tools        TEXT NOT NULL,
            RecipeJson   TEXT NOT NULL,
            CreatedAtUtc INTEGER NOT NULL,
            BatchId      INTEGER NOT NULL);
        CREATE TEMP TABLE pv_carry_merge_sources(
            OldResultId INTEGER NOT NULL,
            OldSourceId INTEGER NOT NULL,
            Position    INTEGER NOT NULL);
        INSERT INTO temp.pv_carry_merges(OldFileId, Method, Projection, SourceCount, Tools, RecipeJson, CreatedAtUtc, BatchId)
            SELECT FileId, Method, Projection, SourceCount, Tools, RecipeJson, CreatedAtUtc, BatchId FROM MergedPhotos
            WHERE FileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        INSERT INTO temp.pv_carry_merge_sources(OldResultId, OldSourceId, Position)
            SELECT ResultFileId, SourceFileId, Position FROM MergedPhotoSources
            WHERE ResultFileId IN (SELECT OldFileId FROM temp.pv_carry_merges);
        """;

    private const string RestoreMergesSql = """
        INSERT OR IGNORE INTO MergedPhotos(FileId, Method, Projection, SourceCount, SeriesId, Tools, RecipeJson, CreatedAtUtc, BatchId)
            SELECT m.NewFileId, c.Method, c.Projection, c.SourceCount, NULL, c.Tools, c.RecipeJson, c.CreatedAtUtc, c.BatchId
            FROM temp.pv_carry_merges c
            CROSS JOIN temp.pv_carry_map m ON m.OldFileId = c.OldFileId;
        INSERT OR IGNORE INTO MergedPhotoSources(ResultFileId, SourceFileId, Position)
            SELECT mr.NewFileId, ms.NewFileId, s.Position
            FROM temp.pv_carry_merge_sources s
            CROSS JOIN temp.pv_carry_map mr ON mr.OldFileId = s.OldResultId
            CROSS JOIN temp.pv_carry_map ms ON ms.OldFileId = s.OldSourceId
            WHERE mr.NewFileId <> ms.NewFileId AND EXISTS (SELECT 1 FROM MergedPhotos x WHERE x.FileId = mr.NewFileId);
        UPDATE FileLinkUndo SET TargetId = m.NewFileId
            FROM temp.pv_carry_undo_files c CROSS JOIN temp.pv_carry_map m ON m.OldFileId = c.OldTargetId
            WHERE FileLinkUndo.Id = c.UndoId;
        DELETE FROM FileLinkUndo WHERE Id IN (
            SELECT c.UndoId FROM temp.pv_carry_undo_files c WHERE c.OldTargetId NOT IN (SELECT OldFileId FROM temp.pv_carry_map));
        DROP TABLE IF EXISTS temp.pv_carry_merges;
        DROP TABLE IF EXISTS temp.pv_carry_merge_sources;
        DROP TABLE IF EXISTS temp.pv_carry_undo_files;
        """;

    private void ParkMerges() => Execute(_connection, _transaction, ParkMergesSql, _libraryId);

    private void RestoreMerges() => Execute(_connection, _transaction, RestoreMergesSql, _libraryId);
}
