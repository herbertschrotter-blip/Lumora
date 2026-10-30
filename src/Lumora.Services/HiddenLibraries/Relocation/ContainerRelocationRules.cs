namespace Lumora.Services.HiddenLibraries.Relocation;

/// <summary>Was mit der alten Container-Datei nach erfolgreicher Prüfung geschieht (Mockup 26).</summary>
public enum OldContainerFile
{
    /// <summary>Liegen lassen — der Benutzer löscht sie selbst (Standard).</summary>
    Keep,

    /// <summary>In den Papierkorb (bei sehr großen Dateien löscht Windows unter Umständen sofort endgültig und fragt nach).</summary>
    RecycleBin,
}

/// <summary>
/// Plan zum Verschieben der Container-Datei der versteckten Bibliothek (Mockup 26). Lebt nur im Speicher: Lumora merkt
/// sich die Container-Datei weiterhin nicht (ADR-0049).
/// </summary>
/// <param name="SourceFile">Container-Datei, die gerade als <paramref name="DriveLetter"/> geöffnet ist.</param>
/// <param name="TargetFile">Neuer Ort samt Dateiname.</param>
/// <param name="DriveLetter">Laufwerksbuchstabe, den der Katalog kennt — bleibt nach dem Verschieben gleich.</param>
/// <param name="IsVirtualDisk">BitLocker-VHDX (Windows hängt ein) statt VeraCrypt.</param>
/// <param name="IsRename">Gleiches Laufwerk: nur umbenennen statt kopieren.</param>
public sealed record ContainerRelocationPlan(string SourceFile, string TargetFile, char DriveLetter, bool IsVirtualDisk, OldContainerFile OldFile, bool IsRename);

/// <summary>Ergebnis der Ziel-Prüfung: Probleme sperren den Start.</summary>
public sealed record ContainerTargetCheck(IReadOnlyList<string> Problems, bool IsRename)
{
    public bool CanStart => Problems.Count == 0;
}

/// <summary>Regeln für das Ziel beim Verschieben einer Container-Datei (rein, getestet).</summary>
public static class ContainerRelocationRules
{
    /// <summary>So viel Platz bleibt mindestens frei, über die Größe der Datei hinaus.</summary>
    public const long SpareBytes = 1L << 30;

    /// <summary>Ordnernamen von Cloud-Abgleich-Diensten — ein Container gehört nie in einen solchen Ordner.</summary>
    private static readonly string[] SyncFolderNames = ["OneDrive", "Dropbox", "Google Drive", "iCloudDrive", "Nextcloud", "MEGA"];

    /// <param name="sourceFile">Bisherige Datei.</param>
    /// <param name="sourceBytes">Ihre Größe auf dem Datenträger.</param>
    /// <param name="targetFile">Neuer Pfad samt Dateiname.</param>
    /// <param name="containerLetter">Laufwerk des geöffneten Containers (darf nicht Ziel sein).</param>
    /// <param name="freeBytes">Freier Platz des Laufwerks zu einem Wurzelpfad („E:\“), null = nicht erreichbar.</param>
    /// <param name="exists">Gibt es die Datei schon?</param>
    /// <param name="syncFolders">Ordner von Cloud-Diensten (z. B. %OneDrive%), zusätzlich zu den bekannten Namen.</param>
    public static ContainerTargetCheck Check(
        string sourceFile,
        long sourceBytes,
        string targetFile,
        char containerLetter,
        Func<string, long?> freeBytes,
        Func<string, bool> exists,
        IReadOnlyList<string> syncFolders)
    {
        string target;
        try
        {
            target = Path.GetFullPath(targetFile);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ContainerTargetCheck(["Der Zielpfad ist ungültig."], false);
        }

        var problems = new List<string>();
        var source = Path.GetFullPath(sourceFile);
        var targetRoot = Path.GetPathRoot(target) ?? string.Empty;
        var sourceRoot = Path.GetPathRoot(source) ?? string.Empty;
        var isRename = string.Equals(targetRoot, sourceRoot, StringComparison.OrdinalIgnoreCase);

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Das Ziel ist die Datei selbst.");
        }
        else if (exists(target))
        {
            problems.Add("Am Ziel liegt schon eine Datei mit diesem Namen.");
        }

        if (targetRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            problems.Add("Netzwerkpfade gehen nicht — Windows hängt Container nur von lokalen Laufwerken ein.");
        }
        else if (targetRoot.Length >= 2 && targetRoot[1] == ':' && char.ToUpperInvariant(targetRoot[0]) == char.ToUpperInvariant(containerLetter))
        {
            problems.Add($"Der Container kann nicht in sich selbst ({char.ToUpperInvariant(containerLetter)}:) liegen.");
        }

        if (!string.Equals(Path.GetExtension(source), Path.GetExtension(target), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Die Dateiendung muss gleich bleiben ({Path.GetExtension(source)}).");
        }

        if (IsInSyncFolder(target, syncFolders))
        {
            problems.Add("Dieser Ordner wird mit einem Cloud-Dienst abgeglichen. Ein Container gehört nie in einen Cloud-Ordner.");
        }

        if (!isRename)
        {
            var free = freeBytes(targetRoot);
            if (free is null)
            {
                problems.Add($"Das Laufwerk {targetRoot.TrimEnd('\\')} ist nicht erreichbar.");
            }
            else if (free.Value < sourceBytes + SpareBytes)
            {
                problems.Add($"Zu wenig Platz auf {targetRoot.TrimEnd('\\')}: {FormatBytes(free.Value)} frei, gebraucht {FormatBytes(sourceBytes + SpareBytes)}.");
            }
        }

        return new ContainerTargetCheck(problems, isRename);
    }

    /// <summary>Liegt der Pfad in einem Cloud-Ordner (bekannter Name als Ordner im Pfad oder einer der übergebenen)?</summary>
    public static bool IsInSyncFolder(string path, IReadOnlyList<string> syncFolders)
    {
        foreach (var folder in syncFolders)
        {
            if (!string.IsNullOrWhiteSpace(folder) && IsSameOrInside(path, folder))
            {
                return true;
            }
        }

        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => SyncFolderNames.Any(n => p.Equals(n, StringComparison.OrdinalIgnoreCase) || p.StartsWith(n + " - ", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Cloud-Ordner aus den Umgebungsvariablen (OneDrive privat und geschäftlich).</summary>
    public static IReadOnlyList<string> KnownSyncFolders()
        => new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>„1,2 TB“, „418 GB“, „512 MB“.</summary>
    public static string FormatBytes(long bytes)
    {
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        const double Gb = 1L << 30;
        if (bytes >= 1L << 40)
        {
            return (bytes / (Gb * 1024)).ToString("0.0", de) + " TB";
        }

        return bytes >= 1L << 30 ? Math.Round(bytes / Gb).ToString("0", de) + " GB" : Math.Max(1, bytes >> 20).ToString(de) + " MB";
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
