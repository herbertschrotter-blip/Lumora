namespace Lumora.Services.Operations;

/// <summary>
/// Liest den Quellbaum einer Übernahme (ADR-0034) aus dem Dateisystem. Übernommen werden alle Dateien — Medien und
/// Nicht-Medien (.xmp, .txt …), damit der Quellordner wirklich leer wird. Nicht übernommen werden Systemordner wie beim
/// Flatten bzw. in der Ordnerüberwachung („.thumbs“, „.cache“, „.temp“, „.converted“, „.thumbnails“, „@eaDir“) und
/// Systemdateien („Thumbs.db“, „desktop.ini“, „.DS_Store“, Dateien mit System-Attribut). Sie zählen nicht als
/// verbliebene Dateien und gehen mit dem Quellordner in den Papierkorb. Verknüpfungen auf Ordner (Reparse-Points)
/// werden nicht verfolgt und bleiben liegen (zählen als Rest).
/// </summary>
public static class FolderAdoptionSourceReader
{
    private static readonly HashSet<string> s_ignoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".thumbs", ".cache", ".temp", ".converted", ".thumbnails", "@eaDir",
    };

    private static readonly HashSet<string> s_ignoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db", "desktop.ini", ".DS_Store",
    };

    public static bool IsIgnoredDirectoryName(string name) => s_ignoredDirectories.Contains(name);

    public static bool IsIgnoredFileName(string name) => s_ignoredFiles.Contains(name);

    /// <param name="exclusions">
    /// Ausschlüsse beim Einlesen der Bibliothek (ADR-0066): ausgeschlossene Ordner (außer den Systemordnern oben) werden nicht
    /// übernommen und bleiben liegen — sie zählen als Rest, der Quellordner kommt dann nicht in den Papierkorb.
    /// </param>
    public static AdoptionSourceSnapshot Read(string folder, CancellationToken ct = default, Scanning.Exclusions.ScanExclusionSet? exclusions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Der Ordner „{full}“ wurde nicht gefunden.");
        }

        var ignored = new List<string>();
        var warnings = new List<string>();
        var root = new AdoptionSourceFolder(full);
        var pending = new Stack<AdoptionSourceFolder>();
        pending.Push(root);
        var all = new List<AdoptionSourceFolder>();
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = pending.Pop();
            all.Add(node);
            try
            {
                foreach (var entry in new DirectoryInfo(node.Path).EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo dir)
                    {
                        if (Infrastructure.CatalogFolders.IsCatalogFolder(dir.FullName))
                        {
                            // Katalog einer versteckten Bibliothek (ADR-0049): nie verschieben, nie in den Papierkorb
                            throw new InvalidOperationException(
                                "Dieser Ordner enthält eine versteckte Bibliothek (.lumora) und kann nicht übernommen werden.");
                        }

                        if (s_ignoredDirectories.Contains(dir.Name))
                        {
                            ignored.Add(dir.FullName);
                        }
                        else if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            warnings.Add($"Verknüpfung wird nicht übernommen: {dir.FullName}");
                        }
                        else if (exclusions?.IsExcludedPath(dir.FullName) == true)
                        {
                            warnings.Add($"Vom Einlesen ausgeschlossen, bleibt liegen: {dir.FullName}"); // ADR-0066
                        }
                        else
                        {
                            var child = new AdoptionSourceFolder(dir.FullName);
                            node.Children.Add(child);
                            pending.Push(child);
                        }
                    }
                    else if (IsSystemFile((FileInfo)entry))
                    {
                        ignored.Add(entry.FullName);
                    }
                    else
                    {
                        node.Files.Add(entry.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Ordner nicht lesbar: {node.Path} ({ex.Message})");
            }
        }

        // Zähler erst nach dem vollständigen Aufbau berechnen (sie werden zwischengespeichert).
        foreach (var node in all)
        {
            node.ResetCount();
        }

        return new AdoptionSourceSnapshot(root, ignored, warnings);
    }

    /// <summary>
    /// Dateien, die nach der Übernahme noch im Quellordner liegen (ohne Systemordner und Systemdateien) — bestimmt, ob der
    /// Quellordner in den Papierkorb darf. Nicht lesbare Ordner zählen als Rest (lieber stehen lassen als verlieren).
    /// </summary>
    public static IReadOnlyList<string> FindRemainingFiles(string folder, int max = 200)
    {
        var result = new List<string>();
        if (!Directory.Exists(folder))
        {
            return result;
        }

        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0 && result.Count < max)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo sub)
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            result.Add(sub.FullName); // Verknüpfung: nicht blind mit wegwerfen
                        }
                        else if (!s_ignoredDirectories.Contains(sub.Name))
                        {
                            pending.Push(sub.FullName);
                        }
                    }
                    else if (!IsSystemFile((FileInfo)entry))
                    {
                        result.Add(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add($"{dir} (nicht lesbar: {ex.Message})");
            }
        }

        return result;
    }

    private static bool IsSystemFile(FileInfo file)
        => s_ignoredFiles.Contains(file.Name) || (file.Attributes & FileAttributes.System) != 0;
}
