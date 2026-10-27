using Microsoft.Data.Sqlite;
using static Lumora.Services.Library.LibraryGroupStore;

namespace Lumora.Services.Places.Tracks;

/// <summary>
/// GPS-Spuren beim Verknüpfen, Lösen und Entfernen von Bibliotheks-Gruppen (ADR-0062, Muster der Alben ADR-0054):
/// <list type="bullet">
/// <item>Verknüpfen: die Spuren der hinzukommenden Gruppe wandern in die Zielgruppe; gleiche Spuren (Prüfsumme) gibt es
/// danach nur einmal.</item>
/// <item>Lösen: die gelöste Bibliothek bekommt eine Kopie aller Spuren der Gruppe (Spuren hängen an keiner Datei).</item>
/// <item>Gruppe ohne Bibliothek: ihre Spuren werden gelöscht.</item>
/// </list>
/// Läuft in der Transaktion des Aufrufers (<c>LibraryLinkStore</c>, <c>LibraryGroupStore.DeleteGroupIfEmpty</c>).
/// </summary>
internal static class GpsTrackGroupStore
{
    public static int Link(SqliteConnection connection, SqliteTransaction tx, int targetGroup, int sourceGroup)
    {
        var p = new (string, object)[] { ("$t", targetGroup), ("$s", sourceGroup) };
        var duplicates = Execute(connection, tx,
            "DELETE FROM GpsTracks WHERE GroupId = $s AND Checksum IN (SELECT Checksum FROM GpsTracks WHERE GroupId = $t);", p);
        Execute(connection, tx, "UPDATE GpsTracks SET GroupId = $t WHERE GroupId = $s;", p);
        return duplicates;
    }

    public static int CopyForLibrary(SqliteConnection connection, SqliteTransaction tx, int fromGroup, int toGroup)
        => Execute(connection, tx, """
            INSERT OR IGNORE INTO GpsTracks (GroupId, StartUtc, EndUtc, MinLatitude, MinLongitude, MaxLatitude, MaxLongitude, PointCount,
                                             DistanceMeters, Sport, Device, Name, Source, Checksum, Points, ImportedAtUtc, AppTourId)
            SELECT $to, StartUtc, EndUtc, MinLatitude, MinLongitude, MaxLatitude, MaxLongitude, PointCount,
                   DistanceMeters, Sport, Device, Name, Source, Checksum, Points, ImportedAtUtc, AppTourId
            FROM GpsTracks WHERE GroupId = $from;
            """, ("$from", fromGroup), ("$to", toGroup));

    public static int DeleteGroup(SqliteConnection connection, SqliteTransaction tx, int groupId)
        => Execute(connection, tx, "DELETE FROM GpsTracks WHERE GroupId = $g;", ("$g", groupId));
}
