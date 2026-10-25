namespace Lumora.Services.Scanning;

/// <summary>
/// Alben und Ordner-Verknüpfungen über „Neu aufbauen“ (ADR-0054). <c>AlbumItems</c> werden wie die Motive GEPARKT
/// (negative FileId, Fremdschlüssel bis zum Commit aufgeschoben — <see cref="ParkFaces"/> setzt
/// <c>PRAGMA defer_foreign_keys</c>) und über die Zuordnung alt → neu umgesetzt. <c>FolderLinks</c> hängen zusätzlich an
/// den Ordnern, die der Neuscan ebenfalls neu anlegt: sie werden mit dem Ordnerpfad gesichert (die Kaskade löscht sie mit
/// den alten Zeilen) und über Pfad (Ordner) + Zuordnung (Datei) neu geschrieben. Titelbilder (<c>Albums.CoverFileId</c>,
/// ON DELETE SET NULL) und Ordner-Titelbilder (<c>Folders.CoverFileId</c>, ADR-0068, über den Ordnerpfad) werden ebenso gesichert. Was keine neue Datei bzw. keinen Ordner mehr hat, fällt weg.
/// </summary>
public sealed partial class UserDataCarryOver
{
    private const string ParkAlbumsSql = """
        DROP TABLE IF EXISTS temp.pv_carry_folderlinks;
        DROP TABLE IF EXISTS temp.pv_carry_covers;
        CREATE TEMP TABLE pv_carry_folderlinks(
            OldFileId  INTEGER NOT NULL,
            FolderKey  TEXT NOT NULL,
            AddedAtUtc INTEGER NOT NULL);
        CREATE TEMP TABLE pv_carry_covers(
            AlbumId   INTEGER NOT NULL PRIMARY KEY,
            OldFileId INTEGER NOT NULL);
        INSERT INTO temp.pv_carry_folderlinks(OldFileId, FolderKey, AddedAtUtc)
            SELECT l.FileId, pv_casefold(d.Path), l.AddedAtUtc
            FROM FolderLinks l JOIN Folders d ON d.Id = l.FolderId
            WHERE l.FileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        INSERT INTO temp.pv_carry_covers(AlbumId, OldFileId)
            SELECT Id, CoverFileId FROM Albums WHERE CoverFileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        DROP TABLE IF EXISTS temp.pv_carry_foldercovers;
        CREATE TEMP TABLE pv_carry_foldercovers(
            FolderKey TEXT NOT NULL PRIMARY KEY,
            OldFileId INTEGER NOT NULL);
        INSERT OR IGNORE INTO temp.pv_carry_foldercovers(FolderKey, OldFileId)
            SELECT pv_casefold(d.Path), d.CoverFileId FROM Folders d
            WHERE d.LibraryId = $lib AND d.CoverFileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        UPDATE AlbumItems SET FileId = -FileId WHERE FileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        DROP TABLE IF EXISTS temp.pv_carry_undo;
        CREATE TEMP TABLE pv_carry_undo(
            UndoId    INTEGER NOT NULL PRIMARY KEY,
            FolderKey TEXT NOT NULL);
        INSERT INTO temp.pv_carry_undo(UndoId, FolderKey)
            SELECT u.Id, pv_casefold(d.Path) FROM FileLinkUndo u JOIN Folders d ON d.Id = u.TargetId
            WHERE u.Kind = 1 AND d.LibraryId = $lib;
        """;

    // Gesicherte Ordner-Verknüpfungen entfernter Dateien (FileLinkUndo, Art Ordner) zeigen auf Ordner-Ids, die der Neuscan
    // neu vergibt: über den Pfad auf die neue Id setzen; ohne Ordner mehr verwerfen. Läuft auch ohne gesicherte Dateien.
    private const string RemapUndoSql = """
        UPDATE FileLinkUndo SET TargetId = COALESCE(
            (SELECT f.Id FROM Folders f WHERE f.LibraryId = $lib AND pv_casefold(f.Path) = c.FolderKey ORDER BY f.Id LIMIT 1), -1)
        FROM temp.pv_carry_undo c WHERE FileLinkUndo.Id = c.UndoId;
        DELETE FROM FileLinkUndo WHERE Kind = 1 AND TargetId = -1;
        """;

    private const string RestoreAlbumsSql = """
        UPDATE OR IGNORE AlbumItems SET FileId = m.NewFileId FROM temp.pv_carry_map m WHERE AlbumItems.FileId = -m.OldFileId;
        DELETE FROM AlbumItems WHERE FileId < 0;
        INSERT OR IGNORE INTO FolderLinks(FolderId, FileId, AddedAtUtc)
            SELECT nf.FolderId, m.NewFileId, l.AddedAtUtc
            FROM temp.pv_carry_folderlinks l
            CROSS JOIN temp.pv_carry_map m ON m.OldFileId = l.OldFileId
            CROSS JOIN temp.pv_carry_folders nf ON nf.FolderKey = l.FolderKey
            WHERE NOT EXISTS (SELECT 1 FROM Files f WHERE f.Id = m.NewFileId AND f.FolderId = nf.FolderId);
        UPDATE Albums SET CoverFileId = m.NewFileId
            FROM temp.pv_carry_covers c CROSS JOIN temp.pv_carry_map m ON m.OldFileId = c.OldFileId
            WHERE Albums.Id = c.AlbumId;
        UPDATE Folders SET CoverFileId = m.NewFileId
            FROM temp.pv_carry_foldercovers c
            CROSS JOIN temp.pv_carry_map m ON m.OldFileId = c.OldFileId
            CROSS JOIN temp.pv_carry_folders nf ON nf.FolderKey = c.FolderKey
            WHERE Folders.Id = nf.FolderId;
        """;

    private void ParkAlbums() => Execute(_connection, _transaction, ParkAlbumsSql, _libraryId);

    private void RestoreAlbums() => Execute(_connection, _transaction, RestoreAlbumsSql, _libraryId);

    private void RemapFileLinkUndo() => Execute(_connection, _transaction, RemapUndoSql, _libraryId);
}
