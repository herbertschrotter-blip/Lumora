using Lumora.Domain.Entities;
using Lumora.Services.Scanning.Exclusions;

namespace Lumora.Services.Operations;

/// <summary>
/// Lage-Regeln für „Ordner verschieben“ (ADR-0066), ohne Platte: nur innerhalb derselben Bibliothek, nie die Wurzel oder einen
/// Ordner mit einer Wurzel darin, nie in sich selbst, nicht dorthin, wo er schon liegt, nicht in einen ausgeschlossenen Ordner und
/// nur auf demselben Laufwerk. Die Platte (erreichbar, vorhanden, gleichnamiger Ordner im Ziel) prüft
/// <see cref="FileOperationService.MoveFolderAsync"/> unter Schreib- und Scan-Sperre.
/// </summary>
public static class FolderMoveRules
{
    /// <summary>Grund gegen das Verschieben oder null (erlaubt). <paramref name="sourceRoot"/>/<paramref name="targetRoot"/> = Wurzeln von Quelle und Ziel.</summary>
    public static string? Check(
        IReadOnlyList<LibraryRoot> allRoots,
        int libraryId,
        string sourcePath,
        string targetPath,
        ScanExclusionSet exclusions,
        out LibraryRoot? sourceRoot,
        out LibraryRoot? targetRoot)
    {
        sourceRoot = null;
        targetRoot = null;
        var source = FolderRemovalRules.Normalize(sourcePath);
        var target = FolderRemovalRules.Normalize(targetPath);
        var name = NameOf(source);
        foreach (var r in allRoots)
        {
            var rootPath = FolderRemovalRules.Normalize(r.Path);
            if (string.Equals(rootPath, source, StringComparison.OrdinalIgnoreCase))
            {
                return $"„{name}“ ist eine Wurzel der Bibliothek und lässt sich nicht verschieben.";
            }

            if (FolderRemovalRules.IsInside(rootPath, source))
            {
                return $"In „{name}“ liegt die Wurzel „{NameOf(rootPath)}“ — der Ordner lässt sich nicht verschieben.";
            }
        }

        sourceRoot = RootOf(allRoots, libraryId, source);
        if (sourceRoot is null)
        {
            return $"„{name}“ liegt unter keiner Wurzel der Bibliothek.";
        }

        if (FolderRemovalRules.IsInside(target, source))
        {
            return $"„{name}“ lässt sich nicht in sich selbst oder einen eigenen Unterordner verschieben.";
        }

        if (Path.GetDirectoryName(source) is { } parent && string.Equals(FolderRemovalRules.Normalize(parent), target, StringComparison.OrdinalIgnoreCase))
        {
            return $"„{name}“ liegt bereits in „{NameOf(target)}“.";
        }

        targetRoot = RootOf(allRoots, libraryId, target);
        if (targetRoot is null)
        {
            return $"Das Ziel „{NameOf(target)}“ liegt außerhalb dieser Bibliothek — Ordner lassen sich nur innerhalb derselben Bibliothek verschieben.";
        }

        if (exclusions.IsExcludedPath(target))
        {
            return $"Das Ziel „{NameOf(target)}“ ist vom Einlesen ausgeschlossen.";
        }

        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
        {
            return "Das Ziel liegt auf einem anderen Laufwerk — Ordner lassen sich nur auf demselben Laufwerk verschieben "
                   + "(aus einem Eingang in die Bibliothek: „In die Bibliothek übernehmen…“).";
        }

        return null;
    }

    private static LibraryRoot? RootOf(IReadOnlyList<LibraryRoot> roots, int libraryId, string path)
        => roots.Where(r => r.LibraryId == libraryId && FolderRemovalRules.IsInside(path, FolderRemovalRules.Normalize(r.Path)))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();

    internal static string NameOf(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
