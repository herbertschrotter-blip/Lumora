namespace Lumora.Services.Scanning;

/// <summary>
/// Offline-vs-Missing im Kleinen (PLAN Kap. 12, ADR-0077): Ordner bzw. Dateien, die beim Abgleich einen IO-Fehler lieferten,
/// obwohl die Wurzel erreichbar ist — etwa ein OneDrive-Ordner, dessen Inhalt Windows ohne Verbindung nicht auflisten kann.
/// Was darin liegt, wurde nicht gesehen, ist aber nicht weg: es gilt in diesem Durchgang nie als fehlend. Für große
/// Kataloge wird je Ordner einmal entschieden (<see cref="CoveredFolderIds"/>), danach je Datei nur nachgeschlagen.
/// </summary>
internal sealed class UnreadablePaths
{
    private readonly List<string> _paths = new();
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _paths.Count;

    public void Add(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            _paths.Add(trimmed);
            _exact.Add(trimmed);
        }
    }

    /// <summary>Liegt der Ordner in einem nicht lesbaren Ordner (in jeder Tiefe) oder ist er selbst einer?</summary>
    public bool CoversFolder(string folderPath)
    {
        foreach (var path in _paths)
        {
            if (IncrementalScanRunner.IsUnder(folderPath, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ids aller Ordner, deren Dateien in diesem Durchgang nicht als fehlend gelten.</summary>
    public HashSet<int> CoveredFolderIds(IReadOnlyDictionary<string, int> folderIdsByPath)
        => folderIdsByPath.Where(p => CoversFolder(p.Key)).Select(p => p.Value).ToHashSet();

    /// <summary>War genau diese Datei nicht lesbar?</summary>
    public bool IsUnreadableFile(string folderPath, string fileName) => _exact.Contains(Path.Combine(folderPath, fileName));
}
