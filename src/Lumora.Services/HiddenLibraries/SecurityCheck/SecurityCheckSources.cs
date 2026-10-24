using System.Globalization;
using Microsoft.Win32;

namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>Registry-Zweig für <see cref="IRegistryReader"/> (nur diese beiden, nur lesend).</summary>
public enum RegistryRoot
{
    LocalMachine = 0,
    CurrentUser = 1,
}

/// <summary>
/// Liest die Registry — nur lesend (ADR-0064: Lumora ändert keine Windows-Einstellung). Tests ersetzen ihn.
/// Fehlt Schlüssel oder Wert, liefert er null; ein Lesefehler (kein Zugriff) wirft — die Prüfung meldet dann „Unbekannt“.
/// </summary>
public interface IRegistryReader
{
    object? ReadValue(RegistryRoot root, string subKey, string valueName);

    /// <summary>Namen der Unterschlüssel; null = Schlüssel fehlt.</summary>
    IReadOnlyList<string>? GetSubKeyNames(RegistryRoot root, string subKey);
}

/// <summary>Liest Shell-Eigenschaften eines Laufwerks (BitLocker-Zustand) ohne Administratorrechte; Tests ersetzen ihn.</summary>
public interface IVolumePropertyReader
{
    /// <summary>Wert von <c>System.Volume.BitLockerProtection</c>; null = Windows kennt keinen Wert. Fehler werfen.</summary>
    int? ReadBitLockerProtection(char driveLetter);
}

/// <summary>Liest kleine Textdateien im Benutzerprofil (VeraCrypt-Einstellungen); Tests ersetzen ihn.</summary>
public interface IUserFileReader
{
    /// <summary><c>%APPDATA%</c> (Roaming).</summary>
    string ApplicationDataFolder { get; }

    /// <summary>Inhalt; null = Datei fehlt. Fehler werfen.</summary>
    string? ReadText(string path);
}

/// <summary>Alle Leser des Sicherheits-Checks — produktiv die Windows-Leser, im Rauchtest und in Tests Fakes.</summary>
public sealed record SecurityCheckSources(
    IRegistryReader Registry,
    IVolumePropertyReader Volumes,
    IUserFileReader Files,
    IVeraCryptLocator VeraCrypt);

/// <summary>Hilfen zum Deuten von Registry-Werten (DWORD, auch als Text gespeichert).</summary>
public static class RegistryValues
{
    /// <summary>Ganzzahl aus DWORD/QWORD/Text; null = fehlt oder nicht deutbar.</summary>
    public static int? ToInt(object? value) => value switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    public static int? ReadInt(this IRegistryReader registry, RegistryRoot root, string subKey, string valueName)
        => ToInt(registry.ReadValue(root, subKey, valueName));

    /// <summary>Richtlinie: zuerst Computer (HKLM), dann Benutzer (HKCU) — wie Windows, Computer gewinnt.</summary>
    public static int? ReadPolicyInt(this IRegistryReader registry, string subKey, string valueName)
        => registry.ReadInt(RegistryRoot.LocalMachine, subKey, valueName) ?? registry.ReadInt(RegistryRoot.CurrentUser, subKey, valueName);
}

/// <summary>Registry über <c>Microsoft.Win32.Registry</c>, 64-Bit-Ansicht, Schlüssel nur lesend geöffnet.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public object? ReadValue(RegistryRoot root, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var baseKey = RegistryKey.OpenBaseKey(Hive(root), RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey); // ohne „writable“ = nur lesend
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public IReadOnlyList<string>? GetSubKeyNames(RegistryRoot root, string subKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var baseKey = RegistryKey.OpenBaseKey(Hive(root), RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey);
        return key?.GetSubKeyNames();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static RegistryHive Hive(RegistryRoot root) => root == RegistryRoot.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
}

/// <summary>Kleine Textdateien direkt lesen (höchstens 1 MB).</summary>
public sealed class LocalUserFileReader : IUserFileReader
{
    private const long MaxBytes = 1024 * 1024;

    public string ApplicationDataFolder => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string? ReadText(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        if (info.Length > MaxBytes)
        {
            throw new IOException("Datei zu groß.");
        }

        return File.ReadAllText(path);
    }
}
