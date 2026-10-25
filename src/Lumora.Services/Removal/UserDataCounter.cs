using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lumora.Services.Removal;

/// <summary>
/// Zählt Benutzerdaten an Dateien für Bestätigungen „Diese Angaben gehen verloren“ (ADR-0065, auch ADR-0066): Sterne/Favorit,
/// benannte Personen, Alben, Tags/Kategorien — je Merkmal Anzahl Dateien.
/// </summary>
public static class UserDataCounter
{
    private const int Chunk = 500;

    public static UserDataCounts Count(SqliteConnection connection, IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        var total = UserDataCounts.None;
        foreach (var chunk in fileIds.Chunk(Chunk))
        {
            ct.ThrowIfCancellationRequested();
            var list = string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            total = total.Add(new UserDataCounts(
                chunk.Length,
                Scalar(connection, $"SELECT COUNT(*) FROM Files WHERE Id IN ({list}) AND (IsFavorite = 1 OR Rating > 0);"),
                Scalar(connection, $"SELECT COUNT(DISTINCT f.FileId) FROM Faces f JOIN Persons p ON p.Id = f.PersonId WHERE f.FileId IN ({list});"),
                Scalar(connection, $"SELECT COUNT(DISTINCT FileId) FROM AlbumItems WHERE FileId IN ({list});"),
                Scalar(connection, $"SELECT COUNT(*) FROM (SELECT FileId FROM FileTags WHERE FileId IN ({list}) UNION SELECT FileId FROM FileCategoryValues WHERE FileId IN ({list}));")));
        }

        return total;
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
