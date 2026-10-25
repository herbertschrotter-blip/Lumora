using Lumora.Services.Database;

namespace Lumora.Services.Enhance;

/// <summary>Liest die Fassungen (ADR-0071) für die Galerie-Marke „2 Fassungen“ und die Lightbox.</summary>
public interface IPhotoVariantStore
{
    /// <summary>
    /// Für jede der Dateien, die Original oder Fassung ist: die ganze Gruppe (Original + Fassungen). Dateien ohne Fassung fehlen
    /// im Ergebnis.
    /// </summary>
    Task<IReadOnlyDictionary<int, PhotoVariantGroup>> GetGroupsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class PhotoVariantStore(ILibraryContextFactory factory) : IPhotoVariantStore
{
    public Task<IReadOnlyDictionary<int, PhotoVariantGroup>> GetGroupsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
        => Task.Run<IReadOnlyDictionary<int, PhotoVariantGroup>>(() =>
        {
            var result = new Dictionary<int, PhotoVariantGroup>();
            if (fileIds.Count == 0)
            {
                return result;
            }

            using var connection = factory.OpenSqliteConnection();
            var originals = new HashSet<int>();
            foreach (var chunk in fileIds.Distinct().Chunk(400))
            {
                ct.ThrowIfCancellationRequested();
                var list = string.Join(',', chunk);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT OriginalFileId FROM PhotoVariants WHERE FileId IN ({list}) UNION SELECT OriginalFileId FROM PhotoVariants WHERE OriginalFileId IN ({list});";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    originals.Add(reader.GetInt32(0));
                }
            }

            var variants = new Dictionary<int, List<int>>();
            foreach (var chunk in originals.Chunk(400))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT OriginalFileId, FileId FROM PhotoVariants WHERE OriginalFileId IN ({string.Join(',', chunk)}) ORDER BY CreatedAtUtc, FileId;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var original = reader.GetInt32(0);
                    if (!variants.TryGetValue(original, out var list))
                    {
                        variants[original] = list = [];
                    }

                    list.Add(reader.GetInt32(1));
                }
            }

            var wanted = fileIds.ToHashSet();
            foreach (var (original, list) in variants)
            {
                var group = new PhotoVariantGroup(original, list);
                foreach (var id in group.AllFileIds.Where(wanted.Contains))
                {
                    result[id] = group;
                }
            }

            return result;
        }, ct);
}
