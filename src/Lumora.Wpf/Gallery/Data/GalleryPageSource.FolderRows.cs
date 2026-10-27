using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Gallery.Data;

/// <summary>Ansicht „Ordner“ (Mockup 21): gezielt gewählte Dateien einer Zeile laden — nie den ganzen Ordner.</summary>
public sealed partial class GalleryPageSource
{
    /// <summary>
    /// Alle Ordner unterhalb von <paramref name="folderId"/> (jede Tiefe), die selbst Dateien enthalten — Name = Pfad
    /// relativ zum Ordner, Anzahl = nur die Dateien direkt darin. Ordner ohne eigene Dateien fallen weg.
    /// </summary>
    public Task<IReadOnlyList<GallerySubfolderDto>> GetFoldersWithFilesBelowAsync(int folderId, CancellationToken ct)
        => RunAsync<IReadOnlyList<GallerySubfolderDto>>(conn =>
        {
            if (ReadDescendants(conn, folderId, ct) is not { } found)
            {
                return Array.Empty<GallerySubfolderDto>();
            }

            var (parentTrimmed, descendants, directCounts) = found;

            var prefixLength = parentTrimmed.Length + 1;
            return descendants
                .Where(d => directCounts.GetValueOrDefault(d.Id) > 0)
                .Select(d =>
                {
                    var trimmed = TrimSeparators(d.Path);
                    return new GallerySubfolderDto(d.Id, trimmed.Length > prefixLength ? trimmed[prefixLength..] : trimmed, d.Path,
                        directCounts[d.Id]);
                })
                .ToList();
        }, ct);

    /// <summary>Zeilen-DTOs zu den Ids, in der Reihenfolge von <paramref name="ids"/>; fehlende Ids fallen weg.</summary>
    public Task<IReadOnlyList<GalleryFileDto>> LoadFilesByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct)
        => RunAsync<IReadOnlyList<GalleryFileDto>>(conn =>
        {
            var byId = new Dictionary<int, GalleryFileDto>(ids.Count);
            foreach (var chunk in ids.Distinct().Chunk(InChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                var inList = string.Join(",", chunk.Select(id => id.ToString(CultureInfo.InvariantCulture)));
                cmd.CommandText = $"""
                    SELECT {FileColumns}
                    FROM Files f
                    JOIN Folders fo ON fo.Id = f.FolderId
                    {RootJoin}
                    WHERE f.Id IN ({inList});
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = ReadRow(reader);
                    byId[row.Id] = row;
                }
            }

            return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }, ct);
}
