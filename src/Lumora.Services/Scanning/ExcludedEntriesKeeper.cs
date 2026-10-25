using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Scanning.Exclusions;

namespace Lumora.Services.Scanning;

/// <summary>
/// „Neu aufbauen“ mit „Einträge behalten“ (#22, Nachtrag ADR-0066): Nach „Nur künftig ausschließen“ liegen katalogisierte Dateien in
/// Ordnern, die der Scan nicht mehr betritt — ein Neuaufbau nähme sie heraus. Mit
/// <see cref="ScanOptions.KeepCatalogedInExcludedFolders"/> nimmt der volle Scan genau diese Einträge wieder auf (mit aktuellen
/// Werten von der Platte); neue Dateien in den ausgeschlossenen Ordnern bleiben draußen. Gleicher Ordnerpfad + Name → die
/// Benutzerdaten kommen über <see cref="UserDataCarryOver"/> mit wie bei jeder anderen Datei. Dateien, die es nicht mehr gibt,
/// fallen weg (wie sonst beim Neuaufbau).
/// </summary>
internal static class ExcludedEntriesKeeper
{
    private const int Chunk = 500;

    /// <summary>Ein katalogisierter Eintrag in einem ausgeschlossenen Ordner.</summary>
    internal sealed record Entry(int? RootId, string FolderPath, string Name, string Extension, MediaType MediaType);

    /// <summary>Katalogisierte Dateien der Bibliothek in ausgeschlossenen Ordnern (nach den gespeicherten Ausschlüssen).</summary>
    public static async Task<List<Entry>> LoadAsync(ILibraryContextFactory factory, int libraryId, ScanExclusionSet exclusions, CancellationToken ct)
    {
        if (exclusions.IsEmpty)
        {
            return [];
        }

        await using var ctx = factory.Create();
        var folders = await ctx.Folders.AsNoTracking()
            .Where(f => f.LibraryId == libraryId)
            .Select(f => new { f.Id, f.Path, f.RootId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var excluded = folders.Where(f => exclusions.IsExcludedPath(f.Path)).ToDictionary(f => f.Id);
        var entries = new List<Entry>();
        foreach (var chunk in excluded.Keys.Chunk(Chunk))
        {
            var files = await ctx.Files.AsNoTracking()
                .Where(f => chunk.Contains(f.FolderId))
                .Select(f => new { f.FolderId, f.Name, f.Extension, f.MediaType })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            entries.AddRange(files.Select(f =>
            {
                var folder = excluded[f.FolderId];
                return new Entry(folder.RootId, folder.Path, f.Name, f.Extension, f.MediaType);
            }));
        }

        return entries;
    }

    /// <summary>
    /// Einträge, die zu dieser Wurzel gehören (unterhalb von ihr und über die alte <c>RootId</c>, ersatzweise über den Pfad).
    /// <paramref name="rootPaths"/>/<paramref name="rootIds"/> = alle Wurzeln der Bibliothek, damit ein Eintrag ohne (gültige) RootId der
    /// tiefsten passenden Wurzel zufällt.
    /// </summary>
    public static IEnumerable<Entry> ForRoot(IReadOnlyList<Entry> entries, int rootId, string rootPath, IReadOnlyList<string> rootPaths, IReadOnlySet<int> rootIds)
    {
        var root = ScanExclusionSet.Normalize(rootPath);
        var normalizedRoots = rootPaths.Select(ScanExclusionSet.Normalize).OrderByDescending(r => r.Length).ToList();
        foreach (var e in entries)
        {
            // Nur unterhalb der Wurzel (sonst stimmte die Ordnerkette nicht, z. B. nach einem Wurzel-Umzug)
            var folder = ScanExclusionSet.Normalize(e.FolderPath);
            if (root.Length == 0 || folder.Length == 0 || !ScanExclusionSet.IsInside(folder, root))
            {
                continue;
            }

            // RootId einer nicht mehr vorhandenen Wurzel (z. B. entfernt und neu angelegt) → über den Pfad zuordnen
            var owner = e.RootId is { } id && rootIds.Contains(id)
                ? id == rootId
                : string.Equals(normalizedRoots.FirstOrDefault(r => ScanExclusionSet.IsInside(folder, r)), root, StringComparison.OrdinalIgnoreCase);
            if (owner)
            {
                yield return e;
            }
        }
    }
}
