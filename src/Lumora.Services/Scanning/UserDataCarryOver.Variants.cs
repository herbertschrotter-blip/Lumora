namespace Lumora.Services.Scanning;

/// <summary>
/// Fassungen über „Neu aufbauen“ (ADR-0071). <c>PhotoVariants</c> hängt mit zwei Fremdschlüsseln (Fassung, Original) an
/// <c>Files</c>; die Zeilen werden in eine TEMP-Tabelle kopiert (die Kaskade löscht sie mit den alten Files-Zeilen) und über
/// die Zuordnung alt → neu beider Dateien neu geschrieben. Fehlt eine der beiden neuen Dateien, fällt die Verknüpfung weg.
/// </summary>
public sealed partial class UserDataCarryOver
{
    private const string ParkVariantsSql = """
        DROP TABLE IF EXISTS temp.pv_carry_variants;
        CREATE TEMP TABLE pv_carry_variants(
            OldFileId     INTEGER NOT NULL,
            OldOriginalId INTEGER NOT NULL,
            Tools         TEXT NOT NULL,
            CreatedAtUtc  INTEGER NOT NULL,
            BatchId       INTEGER NOT NULL);
        INSERT INTO temp.pv_carry_variants(OldFileId, OldOriginalId, Tools, CreatedAtUtc, BatchId)
            SELECT FileId, OriginalFileId, Tools, CreatedAtUtc, BatchId FROM PhotoVariants
            WHERE FileId IN (SELECT OldFileId FROM temp.pv_carry_files) OR OriginalFileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        """;

    private const string RestoreVariantsSql = """
        INSERT OR IGNORE INTO PhotoVariants(FileId, OriginalFileId, Tools, CreatedAtUtc, BatchId)
            SELECT mv.NewFileId, mo.NewFileId, v.Tools, v.CreatedAtUtc, v.BatchId
            FROM temp.pv_carry_variants v
            CROSS JOIN temp.pv_carry_map mv ON mv.OldFileId = v.OldFileId
            CROSS JOIN temp.pv_carry_map mo ON mo.OldFileId = v.OldOriginalId
            WHERE mv.NewFileId <> mo.NewFileId;
        DROP TABLE IF EXISTS temp.pv_carry_variants;
        """;

    private void ParkVariants() => Execute(_connection, _transaction, ParkVariantsSql, _libraryId);

    private void RestoreVariants() => Execute(_connection, _transaction, RestoreVariantsSql, _libraryId);
}
