using System.Text.RegularExpressions;

namespace Lumora.Services.AiEdit;

/// <summary>Ergebnis der Erkennung im Container.</summary>
public enum ComfyInstallState
{
    /// <summary>Kein Container offen (keine versteckte Sitzung bzw. Laufwerk weg).</summary>
    NoContainer,

    /// <summary><c>.lumora-ki\ComfyUI</c> fehlt oder ist unvollständig.</summary>
    NotInstalled,

    /// <summary>Gefunden, aber die Version steht nicht in <see cref="ComfyVersions.Verified"/> — Lumora startet sie nicht.</summary>
    UnverifiedVersion,

    /// <summary>Geprüfte Version gefunden; <c>ki-install.json</c> ist nicht lesbar.</summary>
    BrokenManifest,

    Ready,
}

/// <summary>
/// ComfyUI-Versionen, deren Startoptionen an der installierten Version geprüft sind (ADR-0076 Punkt 3). Eine neue Version
/// kommt erst nach Prüfung von <c>comfy/cli_args.py</c> und der Vorlagen hinzu.
/// </summary>
public static class ComfyVersions
{
    public static IReadOnlySet<string> Verified { get; } = new HashSet<string>(StringComparer.Ordinal) { "0.43.0" };
}

/// <summary>Was im Container gefunden wurde.</summary>
/// <param name="Problem">Kurzer Hinweis für die Ansicht (ohne Pfad), null bei <see cref="ComfyInstallState.Ready"/>.</param>
/// <param name="AllowedExtensions">Erweiterungen aus <c>ki-install.json</c>, die in <c>custom_nodes</c> auch vorhanden sind.</param>
public sealed partial record ComfyInstallation(
    ComfyInstallState State,
    AiEditLayout? Layout,
    string? Version,
    AiInstallManifest? Manifest,
    IReadOnlyList<string> AllowedExtensions,
    string? Problem)
{
    public bool CanStart => State == ComfyInstallState.Ready;

    public static ComfyInstallation NoContainer { get; } = new(ComfyInstallState.NoContainer, null, null, null, [], "Kein Container geöffnet.");

    /// <summary>Sucht ComfyUI unter <c>&lt;containerRoot&gt;\.lumora-ki</c>; liest nur, legt nichts an.</summary>
    public static ComfyInstallation Detect(string? containerRoot)
    {
        if (string.IsNullOrWhiteSpace(containerRoot) || !Directory.Exists(containerRoot))
        {
            return NoContainer;
        }

        var layout = new AiEditLayout(containerRoot);
        if (!File.Exists(layout.PythonExe) || !File.Exists(layout.MainPy))
        {
            return new(ComfyInstallState.NotInstalled, layout, null, null, [], "Im geöffneten Container ist keine KI-Bearbeitung eingerichtet.");
        }

        var version = ReadVersion(layout.VersionFile);
        if (version is null || !ComfyVersions.Verified.Contains(version))
        {
            return new(ComfyInstallState.UnverifiedVersion, layout, version, null, [],
                version is null
                    ? "Die ComfyUI-Version ist nicht erkennbar — Lumora startet nur geprüfte Versionen."
                    : $"Version {version} ist noch nicht geprüft — Lumora startet nur geprüfte Versionen.");
        }

        AiInstallManifest? manifest;
        try
        {
            manifest = AiInstallManifest.TryLoad(layout.InstallFile);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new(ComfyInstallState.BrokenManifest, layout, version, null, [], "Die Installationsliste (ki-install.json) ist nicht lesbar.");
        }

        var extensions = (manifest?.Extensions ?? [])
            .Select(e => e.Name)
            .Where(IsPlainFolderName)
            .Where(name => Directory.Exists(Path.Combine(layout.CustomNodesRoot, name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new(ComfyInstallState.Ready, layout, version, manifest, extensions, null);
    }

    /// <summary>Liest <c>__version__ = "x.y.z"</c> aus <c>comfyui_version.py</c>.</summary>
    internal static string? ReadVersion(string versionFile)
    {
        try
        {
            if (!File.Exists(versionFile))
            {
                return null;
            }

            var match = VersionPattern().Match(File.ReadAllText(versionFile));
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Nur ein einfacher Ordnername (kein Pfad, kein „..“) — sonst könnte die Liste aus <c>custom_nodes</c> hinauszeigen.</summary>
    internal static bool IsPlainFolderName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && name is not "." and not ".."
           && !name.Contains('/') && !name.Contains('\\');

    [GeneratedRegex(@"^\s*__version__\s*=\s*[""']([0-9]+(?:\.[0-9]+){1,3})[""']", RegexOptions.Multiline)]
    private static partial Regex VersionPattern();
}
