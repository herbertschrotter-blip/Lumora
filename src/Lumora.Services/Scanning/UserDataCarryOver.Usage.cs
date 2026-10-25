namespace Lumora.Services.Scanning;

/// <summary>
/// Nutzungs-Signale über „Neu aufbauen“ (ADR-0072): <c>FileUsageDays</c> hängt per ON DELETE CASCADE an <c>Files</c>. Wie die
/// Motive (<see cref="ParkMotifs"/>) werden die Tageszeilen in der Transaktion GEPARKT (negative FileId, Fremdschlüssel bis
/// zum Commit aufgeschoben — <c>PRAGMA defer_foreign_keys</c>) und nach dem Bulk-Insert über dieselbe Zuordnung alt → neu
/// auf die neuen FileIds gesetzt; was keine neue Datei hat, wird gelöscht.
/// </summary>
public sealed partial class UserDataCarryOver
{
    private const string ParkUsageSql = """
        PRAGMA defer_foreign_keys = ON;
        UPDATE FileUsageDays SET FileId = -FileId WHERE FileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        """;

    private const string RestoreUsageSql = """
        UPDATE OR IGNORE FileUsageDays SET FileId = m.NewFileId FROM temp.pv_carry_map m WHERE FileUsageDays.FileId = -m.OldFileId;
        DELETE FROM FileUsageDays WHERE FileId < 0;
        """;

    private void ParkUsage() => Execute(_connection, _transaction, ParkUsageSql, _libraryId);

    private void RestoreUsage() => Execute(_connection, _transaction, RestoreUsageSql, _libraryId);
}
