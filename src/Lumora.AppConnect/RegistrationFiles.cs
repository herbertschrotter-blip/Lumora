using System.Diagnostics;
using System.Text.Json;

namespace Lumora.AppConnect;

/// <summary>
/// Anmeldedateien im gemeinsamen Ordner (Abschnitt 4): eigene atomar schreiben (temporäre Datei, dann Umbenennen), andere
/// lesen (Stufe 3: Lumora als Abfrager). Kein Geheimnis darin — die Datei ist nur der Wegweiser.
/// </summary>
public sealed class RegistrationFiles(string folder)
{
    /// <summary>Unterordner unter der Wurzel (<c>%LocalAppData%</c> bzw. die Testwurzel).</summary>
    public const string RelativeFolder = @"AppVerbindung\apps";

    public string Folder { get; } = folder;

    /// <summary>
    /// <c>%LocalAppData%\AppVerbindung\apps</c> (Abschnitt 4). Mit umgelenkter Lumora-Wurzel (<c>LUMORA_APPDATA</c>,
    /// Tests und Rauchtests) stattdessen <c>&lt;Wurzel&gt;\AppVerbindung\apps</c> — wie <c>--data</c> beim Gipfelbuch, damit
    /// ein Testlauf die echten Apps nicht stört.
    /// </summary>
    public static string ResolveFolder(string? testRoot)
        => Path.Combine(testRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), RelativeFolder);

    public string PathOf(string appId) => Path.Combine(Folder, appId + ".json");

    public void Write(AppRegistration registration)
    {
        Directory.CreateDirectory(Folder);
        var target = PathOf(registration.AppId);
        var temp = $"{target}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(registration, AppConnectContract.RegistrationJson));
        File.Move(temp, target, overwrite: true);
    }

    public AppRegistration? Read(string appId) => ReadFile(PathOf(appId));

    /// <summary>Alle Anmeldedateien außer der eigenen, nach Anzeigename (Abschnitt 4).</summary>
    public IReadOnlyList<AppRegistration> Others(string ownAppId)
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(Folder, "*.json")
            .Select(ReadFile)
            .OfType<AppRegistration>()
            .Where(r => !string.Equals(r.AppId, ownAppId, StringComparison.Ordinal))
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// Läuft die App wirklich und nimmt Anfragen an? <c>running</c> allein reicht nicht — nach einem Absturz bleibt es stehen
    /// (Abschnitt 4: der Leser prüft <c>pid</c> gegen laufende Prozesse).
    /// </summary>
    public static bool IsAlive(AppRegistration registration)
    {
        if (!registration.Running || registration.Pid is not int pid || registration.Port is not > 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static AppRegistration? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppRegistration>(File.ReadAllText(path), AppConnectContract.RegistrationJson) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
