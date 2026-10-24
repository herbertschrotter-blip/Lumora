using System.Collections.Generic;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.SecurityCheck;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest (ADR-0064): feste Leser für den Sicherheits-Check — kein Zugriff auf die echte Registry, die Shell oder das
/// Benutzerprofil — und ein Starter, der nichts öffnet. Erwartung bei „Mitbenutzer“: 3 von 9 erfüllt (Aktivitätsverlauf
/// per Richtlinie aus, Suche schließt das Container-Laufwerk nicht ein, VeraCrypt ohne Verlauf).
/// </summary>
internal static class SmokeSecurityCheckFakes
{
    public const int ExpectedFulfilledSharedUser = 3;
    public const int ExpectedTotalSharedUser = 9;

    public static SecurityCheckSources Sources() => new(new Registry(), new Volumes(), new Files(), new VeraCrypt());

    private sealed class Registry : IRegistryReader
    {
        private const string Rule = SecurityCheckKeys.SearchRules + @"\1";

        private static readonly Dictionary<(RegistryRoot, string, string), object> Values = new()
        {
            [(RegistryRoot.LocalMachine, SecurityCheckKeys.Power, "HibernateEnabled")] = 1,
            [(RegistryRoot.LocalMachine, SecurityCheckKeys.SessionPower, "HiberbootEnabled")] = 1,
            [(RegistryRoot.LocalMachine, SecurityCheckKeys.MemoryManagement, "ClearPageFileAtShutdown")] = 0,
            [(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "Start_TrackDocs")] = 1,
            [(RegistryRoot.LocalMachine, SecurityCheckKeys.SystemPolicies, "PublishUserActivities")] = 0,
            [(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "IconsOnly")] = 0,
            [(RegistryRoot.LocalMachine, SecurityCheckKeys.SearchService, "Start")] = 2,
            [(RegistryRoot.LocalMachine, Rule, "URL")] = @"file:///Q:\[00000000-0000-0000-0000-000000000000]\Users\",
            [(RegistryRoot.LocalMachine, Rule, "Include")] = 1,
            [(RegistryRoot.LocalMachine, SecurityCheckKeys.WindowsVersion, "EditionID")] = "Professional",
        };

        public object? ReadValue(RegistryRoot root, string subKey, string valueName)
            => Values.TryGetValue((root, subKey, valueName), out var value) ? value : null;

        public IReadOnlyList<string>? GetSubKeyNames(RegistryRoot root, string subKey)
            => subKey == SecurityCheckKeys.SearchRules ? ["1"] : null;
    }

    private sealed class Volumes : IVolumePropertyReader
    {
        public int? ReadBitLockerProtection(char driveLetter) => 2; // nicht verschlüsselt
    }

    private sealed class Files : IUserFileReader
    {
        public string ApplicationDataFolder => @"Q:\Rauchtest\AppData";

        public string? ReadText(string path)
            => path.EndsWith("Configuration.xml", System.StringComparison.OrdinalIgnoreCase)
                ? "<VeraCrypt><configuration><config key=\"SaveVolumeHistory\">0</config></configuration></VeraCrypt>"
                : null;
    }

    private sealed class VeraCrypt : IVeraCryptLocator
    {
        public string? FindExecutable() => @"Q:\Rauchtest\VeraCrypt\VeraCrypt.exe";
    }

    /// <summary>Öffnet nichts; zählt nur. Die Lokale Sicherheitsrichtlinie „fehlt“ (Knopf aus, Anleitung gilt).</summary>
    public sealed class Launcher : ISecuritySettingsLauncher
    {
        public List<SettingsTarget> Opened { get; } = new();

        public bool IsAvailable(SettingsTarget target) => target is not (SettingsTarget.None or SettingsTarget.SecurityPolicy);

        public bool Open(SettingsTarget target)
        {
            Opened.Add(target);
            return IsAvailable(target);
        }
    }
}
