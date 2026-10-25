namespace Lumora.Services.Infrastructure;

/// <summary>Woher die App-Wurzel (Datenordner) dieser Lumora-Instanz kommt (ADR-0069).</summary>
public enum AppDataLocationMode
{
    /// <summary><c>%LocalAppData%\Lumora</c> (ADR-0056).</summary>
    Standard,

    /// <summary>Portabler Modus: <c>&lt;Ordner der Lumora.exe&gt;\Daten</c>.</summary>
    Portable,

    /// <summary>Umgebungsvariable <c>LUMORA_APPDATA</c> (Rauchtests, Tests).</summary>
    EnvironmentVariable,
}

/// <summary>Aktive App-Wurzel samt Herkunft — für den Start, die Info-Seite und das Protokoll.</summary>
/// <param name="Mode">Herkunft.</param>
/// <param name="Root">Voll aufgelöster Ordner (Logs, Speicherort-Datei, Sicherungen; Standard-Datenordner).</param>
public sealed record AppDataLocation(AppDataLocationMode Mode, string Root)
{
    /// <summary>Anzeige ohne Pfad: „Standard (%LocalAppData%\Lumora)“, „Portabel (Ordner „Daten“ neben Lumora.exe)“ …</summary>
    public string DisplayText => Mode switch
    {
        AppDataLocationMode.Portable => "Portabel (Ordner „Daten“ neben Lumora.exe)",
        AppDataLocationMode.EnvironmentVariable => $"Umgebungsvariable {AppDataLocationResolver.EnvironmentVariable}",
        _ => "Standard (%LocalAppData%\\Lumora)",
    };
}

/// <summary>
/// Bestimmt die App-Wurzel (ADR-0069). Vorrang: <c>LUMORA_APPDATA</c> &gt; portabler Modus &gt; Standard. Portabel ist
/// Lumora, wenn neben der EXE die Markierungsdatei <see cref="PortableMarkerFileName"/> oder der Ordner
/// <see cref="PortableDataFolderName"/> liegt; dann gilt <c>&lt;EXE-Ordner&gt;\Daten</c> — getrennt von jeder anderen
/// Installation, ohne Umzug aus alten Ordnern (ADR-0056 greift nur im Standardmodus).
/// </summary>
public static class AppDataLocationResolver
{
    public const string EnvironmentVariable = "LUMORA_APPDATA";

    /// <summary>Markierungsdatei neben <c>Lumora.exe</c> (legt <c>build/publish.ps1 -Portable</c> ins Paket).</summary>
    public const string PortableMarkerFileName = "lumora.portable";

    /// <summary>Datenordner des portablen Modus neben <c>Lumora.exe</c>.</summary>
    public const string PortableDataFolderName = "Daten";

    /// <param name="environmentValue">Wert von <see cref="EnvironmentVariable"/> (leer = nicht gesetzt).</param>
    /// <param name="exeDirectory">Ordner der laufenden EXE.</param>
    /// <param name="defaultRoot">Standard-Wurzel (<see cref="AppDataPaths.DefaultAppDataRoot"/>).</param>
    public static AppDataLocation Resolve(string? environmentValue, string exeDirectory, string defaultRoot)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return new AppDataLocation(AppDataLocationMode.EnvironmentVariable, Path.GetFullPath(environmentValue));
        }

        if (IsPortable(exeDirectory))
        {
            return new AppDataLocation(AppDataLocationMode.Portable, Path.Combine(Path.GetFullPath(exeDirectory), PortableDataFolderName));
        }

        return new AppDataLocation(AppDataLocationMode.Standard, defaultRoot);
    }

    /// <summary>true, wenn neben der EXE <see cref="PortableMarkerFileName"/> oder der Ordner <see cref="PortableDataFolderName"/> liegt.</summary>
    public static bool IsPortable(string exeDirectory)
        => !string.IsNullOrWhiteSpace(exeDirectory)
           && (File.Exists(Path.Combine(exeDirectory, PortableMarkerFileName))
               || Directory.Exists(Path.Combine(exeDirectory, PortableDataFolderName)));
}
