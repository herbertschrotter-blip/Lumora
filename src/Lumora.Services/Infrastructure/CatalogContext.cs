namespace Lumora.Services.Infrastructure;

/// <summary>
/// Speicherorte außerhalb der Katalog-DB, die zum aktiven Katalog gehören (Caches, Temp-Dateien). Normal unter
/// <c>%LocalAppData%\Lumora\cache|temp</c>, in einer versteckten Bibliothek im Container unter
/// <c>&lt;Bibliotheksordner&gt;\.lumora\cache|temp</c> (ADR-0049). Wer Dateien außerhalb des Katalogs ablegt (Kartenkacheln,
/// Modell-Ausgaben, Entpack-Zwischenstände), holt den Ordner hier — nie fest aus <c>%LocalAppData%</c>.
/// </summary>
public interface ICatalogStoragePaths
{
    /// <summary>true in einer versteckten Bibliothek: nichts darf außerhalb dieser Ordner landen.</summary>
    bool IsHidden { get; }

    /// <summary>Wurzel für Caches dieses Katalogs (wird bei Bedarf angelegt).</summary>
    string CacheRoot { get; }

    /// <summary>Wurzel für Temp-Dateien dieses Katalogs (wird bei Bedarf angelegt).</summary>
    string TempRoot { get; }
}

/// <summary>
/// Der aktive Katalog einer Lumora-Sitzung (ADR-0049): wo DB, Vorschaubilder, Protokolle, Caches und Temp-Dateien liegen
/// und ob es eine versteckte Bibliothek ist. Jede Katalog-Sitzung hat ihren eigenen DI-Container; dieser Wert ist darin
/// fest. Andere Module richten sich danach — z. B. blendet die Verknüpfung von Bibliotheken bei <see cref="IsHidden"/>
/// „Verknüpfen“ aus (versteckte Bibliotheken werden nie gemeinsam mit anderen angezeigt).
/// </summary>
public interface ICatalogContext : ICatalogStoragePaths
{
    /// <summary>Ordner mit <c>library.db</c> (normal der Datenordner aus ADR-0026, versteckt <c>.lumora</c>).</summary>
    string CatalogRoot { get; }

    string DatabasePath { get; }

    string ThumbnailsRoot { get; }

    string LogsRoot { get; }
}

/// <summary>Standard-Implementierung aus den <see cref="IAppDataPaths"/> der Sitzung.</summary>
public sealed class CatalogContext : ICatalogContext
{
    public const string CacheFolderName = "cache";
    public const string TempFolderName = "temp";

    public CatalogContext(IAppDataPaths paths, bool isHidden)
    {
        IsHidden = isHidden;
        DatabasePath = paths.LibraryDatabasePath;
        CatalogRoot = Path.GetDirectoryName(paths.LibraryDatabasePath) ?? paths.AppDataRoot;
        ThumbnailsRoot = paths.ThumbnailsRoot;
        LogsRoot = paths.LogsRoot;
        CacheRoot = Path.Combine(paths.AppDataRoot, CacheFolderName);
        TempRoot = Path.Combine(paths.AppDataRoot, TempFolderName);
    }

    public bool IsHidden { get; }

    public string CatalogRoot { get; }

    public string DatabasePath { get; }

    public string ThumbnailsRoot { get; }

    public string LogsRoot { get; }

    public string CacheRoot { get; }

    public string TempRoot { get; }
}

/// <summary>
/// Der Katalogordner einer versteckten Bibliothek (<c>&lt;Bibliotheksordner&gt;\.lumora</c>, ADR-0049). Er liegt IN der
/// Bibliothek und darf deshalb von keinem Scan, Sortierer, Flatten oder Zielbaum als Fotoordner behandelt werden. Dasselbe
/// gilt für den Ordner der KI-Bearbeitung <c>.lumora-ki</c> in der Container-Wurzel (ADR-0076: ComfyUI, Modelle, Arbeitsordner).
/// </summary>
public static class CatalogFolders
{
    public const string HiddenCatalogFolderName = ".lumora";

    /// <summary>Ordner der KI-Bearbeitung im Container (ADR-0076 Punkt 2).</summary>
    public const string AiEditFolderName = ".lumora-ki";

    /// <summary>true, wenn der letzte Pfadteil <c>.lumora</c> oder <c>.lumora-ki</c> heißt (Groß/Klein egal).</summary>
    public static bool IsCatalogFolder(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(name, HiddenCatalogFolderName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, AiEditFolderName, StringComparison.OrdinalIgnoreCase);
    }
}
