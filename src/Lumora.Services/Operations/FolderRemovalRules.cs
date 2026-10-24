using Microsoft.EntityFrameworkCore;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Services.Scanning;

namespace Lumora.Services.Operations;

/// <summary>
/// Regeln für „Ordner in den Papierkorb“ (ADR-0065), gemeinsam für die Vorschau (Bestätigungsdialog) und die Ausführung im
/// <see cref="FileOperationService"/>: nicht die Wurzel selbst und kein Ordner mit einer Wurzel darin (Hinweis auf die
/// Einstellungen), nur unter einer Wurzel der Bibliothek, nur bei erreichbarem Laufwerk und vorhandenem Ordner.
/// </summary>
public static class FolderRemovalRules
{
    /// <summary>Grund gegen das Löschen oder null (erlaubt).</summary>
    public static async Task<string?> CheckAsync(LibraryContext ctx, int libraryId, string folderPath, CancellationToken ct = default)
    {
        var path = Normalize(folderPath);
        var allRoots = await ctx.LibraryRoots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var structural = CheckStructure(allRoots, libraryId, path, out var root);
        if (structural is not null)
        {
            return structural;
        }

        if (!await RootAvailabilityProbe.IsAvailableAsync(root!.Path, ct).ConfigureAwait(false))
        {
            return "Laufwerk nicht erreichbar — es wurde nichts gelöscht.";
        }

        if (!Directory.Exists(path))
        {
            return $"„{NameOf(path)}“ ist auf der Platte nicht (mehr) vorhanden — „Fehlende entfernen“ räumt den Katalog auf.";
        }

        return null;
    }

    /// <summary>Nur die Lage-Regeln (ohne Platte): Wurzel, Wurzel darin, keine Wurzel. <paramref name="root"/> = Wurzel des Ordners.</summary>
    public static string? CheckStructure(IReadOnlyList<LibraryRoot> allRoots, int libraryId, string folderPath, out LibraryRoot? root)
    {
        var path = Normalize(folderPath);
        root = null;
        foreach (var r in allRoots)
        {
            var rootPath = Normalize(r.Path);
            if (string.Equals(rootPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return $"„{NameOf(path)}“ ist eine Wurzel der Bibliothek — Wurzeln trägst du unter Einstellungen › Bibliotheken & Eingänge aus.";
            }

            if (IsInside(rootPath, path))
            {
                return $"In „{NameOf(path)}“ liegt die Wurzel „{NameOf(rootPath)}“ — erst unter Einstellungen › Bibliotheken & Eingänge austragen.";
            }
        }

        root = allRoots
            .Where(r => r.LibraryId == libraryId && IsInside(path, Normalize(r.Path)))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();
        return root is null ? $"„{NameOf(path)}“ liegt unter keiner Wurzel der Bibliothek." : null;
    }

    /// <summary>Voller Pfad ohne abschließendes Trennzeichen (Laufwerkswurzeln behalten ihres).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var driveRoot = Path.GetPathRoot(full);
        return driveRoot is not null && string.Equals(full, driveRoot, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Liegt <paramref name="path"/> unter (oder gleich) <paramref name="parent"/>? Beide normalisiert.</summary>
    public static bool IsInside(string path, string parent)
    {
        if (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NameOf(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
