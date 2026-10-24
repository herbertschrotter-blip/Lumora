using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>Ziele von „Einstellung öffnen“ — die feste Positivliste (ADR-0064).</summary>
public enum SettingsTarget
{
    None = 0,
    BitLocker = 1,
    PowerOptions = 2,
    SecurityPolicy = 3,
    StartSettings = 4,
    ActivityHistory = 5,
    FolderOptions = 6,
    IndexingOptions = 7,
    VeraCrypt = 8,
}

/// <summary>Was genau gestartet würde (für Tests und Anzeige).</summary>
/// <param name="FileName">Voller Pfad eines Windows-Programms bzw. eine <c>ms-settings:</c>-Adresse.</param>
/// <param name="Arguments">Feste Argumente aus der Positivliste; nie aus Benutzereingaben.</param>
public sealed record SettingsLaunch(string FileName, string? Arguments);

/// <summary>Öffnet die Windows-Oberfläche zu einem Punkt — sonst nichts (keine freien Befehlszeilen).</summary>
public interface ISecuritySettingsLauncher
{
    /// <summary>false, wenn es das Ziel auf diesem PC nicht gibt (z. B. secpol.msc bei Windows Home) — dann gilt die Anleitung.</summary>
    bool IsAvailable(SettingsTarget target);

    bool Open(SettingsTarget target);
}

/// <summary>
/// Starter mit fester Positivliste (ADR-0064): <c>control.exe</c> aus System32 mit festen kanonischen Namen,
/// <c>secpol.msc</c> aus System32 (nur wenn vorhanden), drei <c>ms-settings:</c>-Seiten und <c>VeraCrypt.exe</c> vom
/// <see cref="IVeraCryptLocator"/> (nur eine vorhandene Datei dieses Namens, ohne Argumente). Lumora startet damit nur die
/// Oberfläche; ändern muss der Benutzer selbst.
/// </summary>
public sealed class SecuritySettingsLauncher : ISecuritySettingsLauncher
{
    /// <summary>Die einzigen erlaubten <c>ms-settings:</c>-Seiten.</summary>
    public static IReadOnlyList<string> AllowedSettingsPages { get; } =
        ["ms-settings:personalization-start", "ms-settings:privacy-activityhistory", "ms-settings:deviceencryption"];

    private readonly IShellLauncher _shell;
    private readonly IVeraCryptLocator _veraCrypt;
    private readonly Func<string, bool> _fileExists;
    private readonly string _system32;

    public SecuritySettingsLauncher(IShellLauncher shell, IVeraCryptLocator veraCrypt, Func<string, bool>? fileExists = null, string? system32 = null)
    {
        _shell = shell;
        _veraCrypt = veraCrypt;
        _fileExists = fileExists ?? File.Exists;
        _system32 = system32 ?? Environment.SystemDirectory;
    }

    /// <summary>Löst ein Ziel in Programm + feste Argumente auf; null = auf diesem PC nicht vorhanden bzw. kein Ziel.</summary>
    public SettingsLaunch? Resolve(SettingsTarget target)
    {
        var control = Path.Combine(_system32, "control.exe");
        switch (target)
        {
            case SettingsTarget.BitLocker:
                // Windows Home hat keine BitLocker-Systemsteuerung → Seite „Geräteverschlüsselung“
                return _fileExists(Path.Combine(_system32, "BitLockerWizardElev.exe"))
                    ? new SettingsLaunch(control, "/name Microsoft.BitLockerDriveEncryption")
                    : new SettingsLaunch("ms-settings:deviceencryption", null);
            case SettingsTarget.PowerOptions:
                return new SettingsLaunch(control, "/name Microsoft.PowerOptions /page pageGlobalSettings");
            case SettingsTarget.SecurityPolicy:
                var secpol = Path.Combine(_system32, "secpol.msc");
                return _fileExists(secpol) ? new SettingsLaunch(secpol, null) : null;
            case SettingsTarget.StartSettings:
                return new SettingsLaunch("ms-settings:personalization-start", null);
            case SettingsTarget.ActivityHistory:
                return new SettingsLaunch("ms-settings:privacy-activityhistory", null);
            case SettingsTarget.FolderOptions:
                return new SettingsLaunch(control, "folders");
            case SettingsTarget.IndexingOptions:
                return new SettingsLaunch(control, "/name Microsoft.IndexingOptions");
            case SettingsTarget.VeraCrypt:
                var exe = _veraCrypt.FindExecutable();
                return IsPlainVeraCryptPath(exe) && _fileExists(exe!) ? new SettingsLaunch(exe!, null) : null;
            default:
                return null;
        }
    }

    public bool IsAvailable(SettingsTarget target) => Resolve(target) is not null;

    public bool Open(SettingsTarget target)
    {
        if (Resolve(target) is not { } launch || !IsAllowed(launch))
        {
            return false;
        }

        return _shell.Start(launch.FileName, launch.Arguments) == ShellLaunchResult.Started;
    }

    /// <summary>Zweite Sicherung: nur Ziele aus der Positivliste, nichts mit Anführungszeichen, Umleitungen oder Verkettungen.</summary>
    internal bool IsAllowed(SettingsLaunch launch)
    {
        if (launch.FileName.StartsWith("ms-settings:", StringComparison.Ordinal))
        {
            return launch.Arguments is null && AllowedSettingsPages.Contains(launch.FileName, StringComparer.Ordinal);
        }

        var control = Path.Combine(_system32, "control.exe");
        if (string.Equals(launch.FileName, control, StringComparison.OrdinalIgnoreCase))
        {
            return launch.Arguments is "/name Microsoft.BitLockerDriveEncryption"
                or "/name Microsoft.PowerOptions /page pageGlobalSettings"
                or "folders"
                or "/name Microsoft.IndexingOptions";
        }

        if (string.Equals(launch.FileName, Path.Combine(_system32, "secpol.msc"), StringComparison.OrdinalIgnoreCase))
        {
            return launch.Arguments is null;
        }

        return launch.Arguments is null && IsPlainVeraCryptPath(launch.FileName);
    }

    /// <summary>Voller lokaler Pfad, Dateiname genau <c>VeraCrypt.exe</c>, keine Zeichen einer Befehlszeile.</summary>
    internal static bool IsPlainVeraCryptPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['"', '\'', '&', '|', '<', '>', '^', '%', '\r', '\n', '\t']) >= 0)
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(Path.GetFileName(path), "VeraCrypt.exe", StringComparison.OrdinalIgnoreCase)
               && !path.Contains(" /", StringComparison.Ordinal)
               && !path.Contains(" -", StringComparison.Ordinal);
    }
}
