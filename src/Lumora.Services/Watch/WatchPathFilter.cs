using Lumora.Services.Media;

namespace Lumora.Services.Watch;

/// <summary>
/// Entscheidet, ob ein Dateisystem-Ereignis einen Scan rechtfertigt: nur Medienformate aus dem Formatregister;
/// Thumbnail-, Temp- und Teildateien (<c>.tmp</c>, <c>.part</c>, <c>~…</c>, <c>Thumbs.db</c>, Ordner <c>.thumbnails</c>,
/// <c>@eaDir</c>, Papierkorb, der eigene Thumbnail-Cache, der Katalogordner <c>.lumora</c> in einer
/// versteckten Bibliothek — dort schreibt die App selbst laufend Medien-Endungen) werden ignoriert. Ordner zählen bei Anlegen/Löschen/Umbenennen
/// (eine verschobene Ordnerstruktur liefert nicht für jede Datei ein Ereignis), nicht bei „Changed“ (das feuert für jede
/// beliebige Datei darin, auch für Nicht-Medien).
/// <para>
/// Ordnernamen mit Punkt (<c>2024.06 Urlaub</c>, <c>v1.2</c>) haben für <see cref="Path.GetExtension(string)"/> eine
/// „Endung“. Angelegt/umbenannt erkennt sie <see cref="Directory.Exists(string)"/>. Gelöscht lässt sich nicht mehr
/// prüfen, ob der Pfad ein Ordner war: dann gilt er als Ordner, wenn die „Endung“ nicht wie eine Dateiendung aussieht
/// (siehe <see cref="LooksLikeFileExtension"/>). Restlücke: ein gelöschter Ordner wie <c>Fotos.alt</c> (Endung sieht
/// echt aus) löst keinen Scan aus — das nächste „Aktualisieren“ oder Ereignis holt es nach.
/// </para>
/// </summary>
public sealed class WatchPathFilter
{
    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".download", ".bak", ".lnk",
    };

    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".thumbnails", "@eaDir", "$RECYCLE.BIN", "System Volume Information", ".git",
        Infrastructure.CatalogFolders.HiddenCatalogFolderName,
    };

    private readonly IMediaFormatRegistry _formats;
    private readonly string? _thumbnailsRoot;

    /// <param name="thumbnailsRoot">Eigener Thumbnail-Cache (wird ignoriert, falls er unterhalb einer Wurzel liegt); optional.</param>
    public WatchPathFilter(IMediaFormatRegistry formats, string? thumbnailsRoot = null)
    {
        _formats = formats;
        _thumbnailsRoot = string.IsNullOrWhiteSpace(thumbnailsRoot) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(thumbnailsRoot));
    }

    /// <summary>true, wenn das Ereignis für den Katalog relevant ist. <paramref name="oldPath"/> nur bei Renamed.</summary>
    public bool IsRelevant(WatcherChangeTypes change, string fullPath, string? oldPath = null)
    {
        if (change == WatcherChangeTypes.Renamed && oldPath is not null && IsRelevantPath(change, oldPath))
        {
            return true; // Medium wurde weg-umbenannt (z. B. zu .tmp) → fehlend
        }

        return IsRelevantPath(change, fullPath);
    }

    private bool IsRelevantPath(WatcherChangeTypes change, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        if (_thumbnailsRoot is not null && IsUnder(fullPath, _thumbnailsRoot))
        {
            return false;
        }

        var name = Path.GetFileName(fullPath);
        if (name.Length == 0 || name[0] == '~' || name[0] == '.' || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null && HasIgnoredSegment(directory))
        {
            return false;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length > 0)
        {
            if (IgnoredExtensions.Contains(extension))
            {
                return false;
            }

            if (_formats.IsSupported(extension))
            {
                return true;
            }
        }

        // Ordner-Ereignisse (angelegt, gelöscht, umbenannt) — "Changed" eines Ordners feuert bei jeder Datei darin.
        if (change == WatcherChangeTypes.Changed)
        {
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            return true; // auch Ordner mit Punkt im Namen („2024.06 Urlaub“)
        }

        // Gelöscht: ohne Endung vermutlich ein Ordner; mit „Endung“ nur, wenn sie nicht wie eine Dateiendung aussieht.
        return change == WatcherChangeTypes.Deleted && (extension.Length == 0 || !LooksLikeFileExtension(extension));
    }

    /// <summary>
    /// Typische Dateiendung: 1–8 Zeichen nach dem Punkt, nur Buchstaben/Ziffern, mindestens ein Buchstabe
    /// (<c>.txt</c>, <c>.xmp</c>, <c>.mp4</c>) — nicht <c>.06 Urlaub</c>, <c>.06</c> oder <c>. Teil 2</c>.
    /// </summary>
    internal static bool LooksLikeFileExtension(string extension)
    {
        if (extension.Length < 2 || extension.Length > 9 || extension[0] != '.')
        {
            return false;
        }

        var hasLetter = false;
        for (var i = 1; i < extension.Length; i++)
        {
            var c = extension[i];
            if (!char.IsLetterOrDigit(c))
            {
                return false;
            }

            hasLetter |= char.IsLetter(c);
        }

        return hasLetter;
    }

    private static bool HasIgnoredSegment(string directory)
    {
        foreach (var segment in directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IgnoredSegments.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
