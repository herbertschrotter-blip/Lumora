using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Domain.Enums;
using Lumora.Services.Database;

namespace Lumora.Lightbox;

/// <summary>
/// Lightbox-Einträge zu Dateien in der gegebenen Reihenfolge, auch außerhalb der Galerie-Seite (Fassungen aus „Foto verbessern“,
/// Ergebnis und Quellen aus „Zusammenfügen“). Abfrage in Stücken zu 400 Ids; unbekannte Ids fallen weg.
/// </summary>
public static class LightboxEntryLoader
{
    public static Task<IReadOnlyList<LightboxEntry>> LoadAsync(ILibraryContextFactory factory, IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<LightboxEntry>>(() =>
        {
            if (fileIds.Count == 0)
            {
                return [];
            }

            using var connection = factory.OpenSqliteConnection();
            var found = new Dictionary<int, LightboxEntry>();
            foreach (var chunk in fileIds.Distinct().Chunk(400))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT f.Id, d.Path, f.Name, f.MediaType, f.IsFavorite, f.Rating, f.Size, f.ModifiedAtUtc
                    FROM Files f JOIN Folders d ON d.Id = f.FolderId
                    WHERE f.Id IN ({string.Join(',', chunk)});
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    found[id] = new LightboxEntry(
                        id,
                        Path.Combine(reader.GetString(1), reader.GetString(2)),
                        reader.GetString(2),
                        (MediaType)reader.GetInt32(3),
                        reader.GetInt64(4) != 0,
                        reader.IsDBNull(5) ? null : (byte)reader.GetInt32(5),
                        reader.GetInt64(6),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)).UtcDateTime);
                }
            }

            return fileIds.Distinct().Where(found.ContainsKey).Select(id => found[id]).ToList();
        }, ct);
}
