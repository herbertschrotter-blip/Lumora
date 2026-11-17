using System.Runtime.Versioning;

namespace Lumora.Services.Cloud;

/// <summary>Bekannte Wurzeln von Cloud-Ordnern („Dateien bei Bedarf“) — austauschbar für Tests.</summary>
public interface ICloudSyncRoots
{
    /// <summary>Wurzeln mit abschließendem Trennzeichen.</summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>Liegt der Pfad in (oder ist er) einer der Wurzeln?</summary>
    bool Contains(string path);
}

/// <summary>
/// Cloud-Wurzeln dieses Benutzers (ADR-0077): OneDrive aus den Umgebungsvariablen (privat und geschäftlich) und alle bei
/// Windows angemeldeten Sync-Wurzeln (<c>SyncRootManager</c> in der Registry — OneDrive, auch weitere Konten und andere
/// Anbieter mit Platzhaltern). Einmal je Sitzung gelesen; nur zum schnellen Vorfiltern (Galerie, Menü) — ob eine Datei nur
/// online liegt, sagen allein ihre Attribute (<see cref="CloudFiles"/>).
/// </summary>
public sealed class CloudSyncRoots : ICloudSyncRoots
{
    private const string SyncRootManagerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    public CloudSyncRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        Roots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> Roots { get; }

    /// <summary>Wurzeln des angemeldeten Benutzers aus Umgebung und Registry (Fehler beim Lesen: nur die Umgebung).</summary>
    public static CloudSyncRoots FromSystem()
    {
        var roots = new List<string>();
        foreach (var variable in OneDriveVariables)
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                roots.Add(value);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            roots.AddRange(ReadRegisteredRoots());
        }

        return new CloudSyncRoots(roots);
    }

    public bool Contains(string path)
    {
        if (Roots.Count == 0 || string.IsNullOrEmpty(path))
        {
            return false;
        }

        var withSeparator = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        foreach (var root in Roots)
        {
            if (withSeparator.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ReadRegisteredRoots()
    {
        var roots = new List<string>();
        try
        {
            using var manager = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SyncRootManagerKey);
            if (manager is null)
            {
                return roots;
            }

            foreach (var name in manager.GetSubKeyNames())
            {
                using var userRoots = manager.OpenSubKey(name + @"\UserSyncRoots");
                if (userRoots is null)
                {
                    continue;
                }

                foreach (var valueName in userRoots.GetValueNames())
                {
                    if (userRoots.GetValue(valueName) is string path && path.Length > 0)
                    {
                        roots.Add(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Registry nicht lesbar: nur die Umgebungsvariablen
        }

        return roots;
    }
}
