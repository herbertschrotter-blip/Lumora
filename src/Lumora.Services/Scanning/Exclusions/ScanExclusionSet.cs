namespace Lumora.Services.Scanning.Exclusions;

/// <summary>
/// Ausschlüsse beim Einlesen einer Bibliothek (ADR-0066), fertig zum Prüfen: Ordnernamen der allgemeinen Liste (gelten in jeder
/// Tiefe unterhalb einer Wurzel, Groß/Klein egal) und ausgeschlossene Ordner dieser Bibliothek (volle Pfade, samt Unterordnern).
/// Unveränderlich und thread-sicher; Scan, Incremental-Scan, Ordnerüberwachung, Übernehmen und Ordnerbaum nutzen dieselben Regeln.
/// Die Wurzeln selbst sind nie ausgeschlossen — auch dann nicht, wenn ein Teil ihres Pfads wie ein ausgeschlossener Name heißt.
/// </summary>
public sealed class ScanExclusionSet
{
    private readonly HashSet<string> _names;
    private readonly string[] _paths;
    private readonly string[] _roots;

    /// <summary>Keine Ausschlüsse (auch nicht die Standardnamen) — für Aufrufer ohne Katalog, z. B. Tests.</summary>
    public static ScanExclusionSet None { get; } = new([], [], []);

    /// <param name="folderNames">Allgemeine Liste (Ordnernamen, z. B. „.thumbs“).</param>
    /// <param name="excludedPaths">Ausgeschlossene Ordner dieser Bibliothek (volle Pfade).</param>
    /// <param name="roots">Wurzeln der Bibliothek (Namen gelten nur unterhalb davon).</param>
    public ScanExclusionSet(IEnumerable<string> folderNames, IEnumerable<string> excludedPaths, IEnumerable<string> roots)
    {
        _names = new HashSet<string>(folderNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        _paths = excludedPaths.Select(Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _roots = roots.Select(Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(r => r.Length).ToArray();
    }

    /// <summary>Ordnernamen der allgemeinen Liste.</summary>
    public IReadOnlyCollection<string> FolderNames => _names;

    /// <summary>Ausgeschlossene Ordner der Bibliothek (normalisierte volle Pfade).</summary>
    public IReadOnlyList<string> FolderPaths => _paths;

    public bool IsEmpty => _names.Count == 0 && _paths.Length == 0;

    /// <summary>
    /// Liegt der Pfad (Ordner oder Datei) in einem ausgeschlossenen Ordner oder ist selbst einer? Namen zählen nur für die Teile
    /// unterhalb der Wurzel, unter der der Pfad liegt; ohne passende Wurzel nur die Ordner der Bibliothek.
    /// </summary>
    public bool IsExcludedPath(string path)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var full = Normalize(path);
        if (full.Length == 0 || IsRoot(full))
        {
            return false;
        }

        foreach (var excluded in _paths)
        {
            if (IsInside(full, excluded))
            {
                return true;
            }
        }

        if (_names.Count == 0)
        {
            return false;
        }

        var root = _roots.FirstOrDefault(r => IsInside(full, r));
        if (root is null)
        {
            return false;
        }

        var relative = full[root.Length..];
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (_names.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Schnelle Prüfung beim Durchlaufen: ein Unterordner, dessen Elternordner schon geprüft (nicht ausgeschlossen) ist — Name
    /// in der Liste oder Pfad ausgeschlossen. Wurzeln nie.
    /// </summary>
    public bool IsExcludedChildDirectory(string directoryPath)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        var full = Normalize(directoryPath);
        if (full.Length == 0 || IsRoot(full))
        {
            return false;
        }

        return _names.Contains(Path.GetFileName(full)) || _paths.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsRoot(string full) => _roots.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase));

    /// <summary>Voller Pfad ohne abschließendes Trennzeichen (Laufwerkswurzeln behalten ihres); leer bei ungültigem Pfad.</summary>
    internal static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var driveRoot = Path.GetPathRoot(full);
            return driveRoot is not null && string.Equals(full, driveRoot, StringComparison.OrdinalIgnoreCase)
                ? full
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    /// <summary>Liegt <paramref name="path"/> unter (oder gleich) <paramref name="parent"/>? Beide normalisiert.</summary>
    internal static bool IsInside(string path, string parent)
    {
        if (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
