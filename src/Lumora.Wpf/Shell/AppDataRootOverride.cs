using System;
using Lumora.Services.Infrastructure;

namespace Lumora.Shell;

/// <summary>
/// App-Wurzel der Lumora-Instanz (ADR-0069): Vorrang Umgebungsvariable LUMORA_APPDATA (z. B. für Rauchtests mit frischer
/// DB, ohne die Katalog-DB des Benutzers anzufassen) &gt; portabler Modus (<c>lumora.portable</c> oder Ordner <c>Daten</c>
/// neben der EXE → <c>&lt;EXE-Ordner&gt;\Daten</c>) &gt; Standard %LocalAppData%\Lumora. In allen Fällen bestimmt
/// <c>database-location.json</c> in der Wurzel, wo DB und Thumbnails liegen (ADR-0026). Der Umzug aus PhotoViewerV3
/// (ADR-0056) läuft nur im Standardmodus (<see cref="AppDataLocation.Mode"/>, entschieden in <c>App</c>).
/// </summary>
public static class AppDataRootOverride
{
    public const string EnvironmentVariable = AppDataLocationResolver.EnvironmentVariable;

    private static AppDataLocation? _active;

    /// <summary>
    /// Die beim Start bestimmte App-Wurzel (Info-Seite, Protokoll). Setzt <c>App</c> einmal im Konstruktor; ohne das
    /// (Tests) wird sie hier aus Variable, EXE-Ordner und Standard bestimmt.
    /// </summary>
    public static AppDataLocation ActiveLocation
    {
        get => _active ??= ResolveLocation();
        internal set => _active = value;
    }

    /// <summary>Herkunft und Ordner der App-Wurzel (ohne <c>database-location.json</c> und ohne Umzug).</summary>
    /// <param name="exeDirectory">Ordner der EXE; null = <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="defaultRoot">Standard-Wurzel; null = <see cref="AppDataPaths.DefaultAppDataRoot"/>.</param>
    public static AppDataLocation ResolveLocation(string? exeDirectory = null, string? defaultRoot = null)
        => AppDataLocationResolver.Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            exeDirectory ?? AppContext.BaseDirectory,
            defaultRoot ?? AppDataPaths.DefaultAppDataRoot);
}
