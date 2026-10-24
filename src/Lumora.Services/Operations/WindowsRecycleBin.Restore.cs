using System.Security.Principal;
using System.Text;

namespace Lumora.Services.Operations;

/// <summary>
/// Zurückholen aus dem Papierkorb (ADR-0053) ohne Shell-Oberfläche: Windows legt je Laufwerk unter
/// <c>&lt;Laufwerk&gt;\$Recycle.Bin\&lt;SID&gt;</c> zu jeder gelöschten Datei ein Paar <c>$Ixxxxxx.ext</c> (Kopf: Version,
/// Größe, Löschzeit als FILETIME, Ursprungspfad UTF-16) und <c>$Rxxxxxx.ext</c> (die Datei selbst) ab. Gesucht wird das
/// jüngste Paar mit diesem Ursprungspfad, gelöscht nach dem angegebenen Zeitpunkt (minus 2 min Spielraum); die
/// <c>$R</c>-Datei wird an den Ursprungsort verschoben (gleiches Laufwerk, Zeitstempel bleiben), die <c>$I</c>-Datei
/// gelöscht — genau das tut auch „Wiederherstellen“ im Explorer.
/// </summary>
public sealed partial class WindowsRecycleBin
{
    private static readonly TimeSpan DeletionTolerance = TimeSpan.FromMinutes(2);

    public void RestoreFromRecycleBin(string originalPath, DateTime deletedAfterUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        var fullPath = Path.GetFullPath(originalPath);
        if (File.Exists(fullPath))
        {
            throw new IOException($"Am Ursprungsort liegt schon eine Datei: {fullPath}");
        }

        var root = Path.GetPathRoot(fullPath);
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(root) || sid is null)
        {
            throw new IOException($"Papierkorb für {fullPath} nicht bestimmbar.");
        }

        var bin = Path.Combine(root, "$Recycle.Bin", sid);
        var match = Lookup(bin, fullPath, DateTime.SpecifyKind(deletedAfterUtc, DateTimeKind.Utc) - DeletionTolerance)
                    ?? throw new FileNotFoundException("Die Datei liegt nicht (mehr) im Papierkorb.", fullPath);

        if (!File.Exists(match.DataPath))
        {
            throw new FileNotFoundException("Unter diesem Pfad liegt ein Ordner im Papierkorb, keine Datei.", fullPath);
        }

        File.Move(match.DataPath, fullPath);
        DeleteInfo(match.InfoPath);
    }

    /// <summary>
    /// Ordner samt Inhalt zurückholen (ADR-0065, Rückgängig von „Ordner in den Papierkorb“): wie
    /// <see cref="RestoreFromRecycleBin"/>, nur ist die <c>$R</c>-Seite des Paars ein Ordner, der in einem Schritt an den
    /// Ursprungsort verschoben wird (gleiches Laufwerk).
    /// </summary>
    public void RestoreDirectoryFromRecycleBin(string originalPath, DateTime deletedAfterUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalPath));
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new IOException($"Am Ursprungsort liegt schon ein Ordner oder eine Datei: {fullPath}");
        }

        var root = Path.GetPathRoot(fullPath);
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(root) || sid is null)
        {
            throw new IOException($"Papierkorb für {fullPath} nicht bestimmbar.");
        }

        var bin = Path.Combine(root, "$Recycle.Bin", sid);
        var match = Lookup(bin, fullPath, DateTime.SpecifyKind(deletedAfterUtc, DateTimeKind.Utc) - DeletionTolerance);
        if (match is null || !Directory.Exists(match.DataPath))
        {
            throw new DirectoryNotFoundException($"Der Ordner liegt nicht (mehr) im Papierkorb: {fullPath}");
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Directory.Move(match.DataPath, fullPath);
        DeleteInfo(match.InfoPath);
    }

    /// <inheritdoc />
    public bool ContainsDirectory(string originalPath, DateTime deletedAfterUtc)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalPath));
        var root = Path.GetPathRoot(fullPath);
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(root) || sid is null)
        {
            return false;
        }

        var entry = FindEntry(Path.Combine(root, "$Recycle.Bin", sid), fullPath, DateTime.SpecifyKind(deletedAfterUtc, DateTimeKind.Utc) - DeletionTolerance);
        return entry is not null && Directory.Exists(entry.DataPath);
    }

    private static void DeleteInfo(string infoPath)
    {
        try
        {
            File.Delete(infoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nur der Verwaltungseintrag bleibt liegen; der Papierkorb zeigt ihn dann nicht mehr an.
        }
    }

    public sealed record RecycleEntry(string InfoPath, string DataPath, string OriginalPath, DateTime DeletedUtc);

    /// <summary>Verzeichnis des Papierkorbs, einmal je Rückgängig-Lauf gelesen (sonst je Datei alle $I-Dateien).</summary>
    private readonly object _indexLock = new();
    private (string Bin, DateTime NotBefore, DateTime BuiltUtc, Dictionary<string, RecycleEntry> Entries)? _index;

    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(1);

    /// <summary>Eintrag über das zwischengespeicherte Verzeichnis; fehlt er, wird einmal neu gelesen. Benutzte Einträge fallen heraus.</summary>
    private RecycleEntry? Lookup(string bin, string originalPath, DateTime notBeforeUtc)
    {
        lock (_indexLock)
        {
            var now = DateTime.UtcNow;
            var fresh = _index is { } cached && cached.Bin == bin && cached.NotBefore == notBeforeUtc && now - cached.BuiltUtc < IndexLifetime;
            if (!fresh)
            {
                _index = (bin, notBeforeUtc, now, BuildIndex(bin, notBeforeUtc));
            }

            if (!_index!.Value.Entries.Remove(originalPath, out var entry) && fresh)
            {
                _index = (bin, notBeforeUtc, now, BuildIndex(bin, notBeforeUtc));
                _index.Value.Entries.Remove(originalPath, out entry);
            }

            return entry is not null && (File.Exists(entry.DataPath) || Directory.Exists(entry.DataPath)) ? entry : null; // Ordner: ADR-0065
        }
    }

    /// <summary>Jüngster Eintrag mit diesem Ursprungspfad, dessen <c>$R</c>-Datei (bzw. -Ordner) noch da ist.</summary>
    public static RecycleEntry? FindEntry(string binDirectory, string originalPath, DateTime notBeforeUtc)
        => BuildIndex(binDirectory, notBeforeUtc).GetValueOrDefault(originalPath);

    /// <summary>Ursprungspfad → jüngster Eintrag (gelöscht nach <paramref name="notBeforeUtc"/>, $R-Datei vorhanden).</summary>
    public static Dictionary<string, RecycleEntry> BuildIndex(string binDirectory, DateTime notBeforeUtc)
    {
        var index = new Dictionary<string, RecycleEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(binDirectory))
        {
            return index;
        }

        foreach (var info in Directory.EnumerateFiles(binDirectory, "$I*"))
        {
            if (TryReadInfo(info) is not { } entry || entry.DeletedUtc < notBeforeUtc)
            {
                continue;
            }

            var data = Path.Combine(binDirectory, "$R" + Path.GetFileName(info)[2..]);
            if (!File.Exists(data) && !Directory.Exists(data)) // $R-Seite eines gelöschten Ordners ist ein Ordner (ADR-0065)
            {
                continue;
            }

            if (!index.TryGetValue(entry.OriginalPath, out var best) || entry.DeletedUtc > best.DeletedUtc)
            {
                index[entry.OriginalPath] = new RecycleEntry(info, data, entry.OriginalPath, entry.DeletedUtc);
            }
        }

        return index;
    }

    /// <summary>
    /// Liest den Kopf einer <c>$I</c>-Datei: Version 1 (bis Windows 8.1: 520 Bytes Pfad fester Länge) oder Version 2
    /// (ab Windows 10: Pfadlänge in Zeichen, dann der Pfad); null bei unbekanntem Aufbau.
    /// </summary>
    public static (string OriginalPath, DateTime DeletedUtc)? TryReadInfo(string infoPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(infoPath);
            if (bytes.Length < 24)
            {
                return null;
            }

            var version = BitConverter.ToInt64(bytes, 0);
            var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16));
            string path;
            if (version == 2 && bytes.Length >= 28)
            {
                var chars = BitConverter.ToInt32(bytes, 24);
                var length = Math.Min(chars * 2, bytes.Length - 28);
                path = Encoding.Unicode.GetString(bytes, 28, Math.Max(0, length));
            }
            else if (version == 1)
            {
                path = Encoding.Unicode.GetString(bytes, 24, Math.Min(520, bytes.Length - 24));
            }
            else
            {
                return null;
            }

            var end = path.IndexOf('\0', StringComparison.Ordinal);
            return (end >= 0 ? path[..end] : path, deleted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
