using System.Xml.Linq;
using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>Eine Prüfung des Sicherheits-Checks (ADR-0064) — liest nur, ändert nichts.</summary>
public interface ISecurityCheck
{
    SecurityCheckId Id { get; }

    /// <summary>Darf werfen; der Dienst macht daraus „Unbekannt“.</summary>
    SecurityCheckFinding Run(SecurityCheckContext context);
}

/// <summary>Registry-Orte der Prüfungen (Quellen in ADR-0064).</summary>
public static class SecurityCheckKeys
{
    public const string Power = @"SYSTEM\CurrentControlSet\Control\Power";
    public const string SessionPower = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    public const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    public const string SystemPolicies = @"SOFTWARE\Policies\Microsoft\Windows\System";
    public const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string ExplorerPolicies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    public const string SearchService = @"SYSTEM\CurrentControlSet\Services\WSearch";
    public const string SearchRules = @"SOFTWARE\Microsoft\Windows Search\CrawlScopeManager\Windows\SystemIndex\WorkingSetRules";
    public const string WindowsVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
}

/// <summary>BitLocker auf dem Windows-Laufwerk über die Shell-Eigenschaft <c>System.Volume.BitLockerProtection</c>.</summary>
public sealed class BitLockerCheck(SecurityCheckSources sources) : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.BitLocker;

    public SecurityCheckFinding Run(SecurityCheckContext context)
    {
        var drive = $"{context.SystemDrive}:";
        var value = sources.Volumes.ReadBitLockerProtection(context.SystemDrive);
        switch (value)
        {
            case (int)BitLockerProtection.On or (int)BitLockerProtection.Locked:
                return SecurityCheckFinding.Ok(Id, $"An — {drive} ist mit BitLocker verschlüsselt.");
            case (int)BitLockerProtection.Off:
                var hint = sources.VeraCrypt.FindExecutable() is not null
                    ? " (Eine VeraCrypt-Systemverschlüsselung kann Lumora nicht erkennen.)"
                    : string.Empty;
                return SecurityCheckFinding.Bad(Id, $"{drive} ist nicht verschlüsselt. Alles, was Windows sich merkt (Auslagerungsdatei, Ruhezustand, Miniaturbilder, Verlauf, Protokolle), liegt dort lesbar.{hint}");
            case (int)BitLockerProtection.Encrypting:
                return SecurityCheckFinding.Bad(Id, $"Die Verschlüsselung von {drive} läuft noch — geschützt ist das Laufwerk erst danach.");
            case (int)BitLockerProtection.Decrypting:
                return SecurityCheckFinding.Bad(Id, $"BitLocker entschlüsselt {drive} gerade.");
            case (int)BitLockerProtection.Suspended:
                return SecurityCheckFinding.Bad(Id, $"BitLocker ist auf {drive} angehalten — der Schlüssel liegt solange offen auf der Platte.");
            case (int)BitLockerProtection.WaitingForActivation:
                return SecurityCheckFinding.Bad(Id, $"{drive} ist verschlüsselt, aber noch ohne Schutz (wartet auf Aktivierung) — der Schlüssel liegt offen auf der Platte.");
        }

        var edition = sources.Registry.ReadValue(RegistryRoot.LocalMachine, SecurityCheckKeys.WindowsVersion, "EditionID") as string;
        return SecurityCheckFinding.Unknown(Id, BitLockerPlatform.IsEditionSupported(edition)
            ? $"Windows meldet keinen BitLocker-Zustand für {drive}."
            : "Windows Home enthält kein BitLocker. Bitte „Geräteverschlüsselung“ in den Einstellungen bzw. eine VeraCrypt-Systemverschlüsselung prüfen.");
    }
}

/// <summary>Ruhezustand: <c>HKLM\…\Control\Power\HibernateEnabled</c> (0 = aus; das Abschalten durch den Benutzer als Administrator setzt 0).</summary>
public sealed class HibernationCheck(SecurityCheckSources sources) : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.Hibernation;

    /// <summary>1 = an, 0 = aus, null = unbekannt (auch für den Schnellstart).</summary>
    internal static int? ReadState(IRegistryReader registry)
        => registry.ReadInt(RegistryRoot.LocalMachine, SecurityCheckKeys.Power, "HibernateEnabled")
           ?? registry.ReadInt(RegistryRoot.LocalMachine, SecurityCheckKeys.Power, "HibernateEnabledDefault");

    public SecurityCheckFinding Run(SecurityCheckContext context) => ReadState(sources.Registry) switch
    {
        0 => SecurityCheckFinding.Ok(Id, "Aus — Windows schreibt den Arbeitsspeicher nicht in hiberfil.sys."),
        1 => SecurityCheckFinding.Bad(Id, "Beim Ruhezustand schreibt Windows den gesamten Arbeitsspeicher in hiberfil.sys — also auch entschlüsselte Vorschaubilder aus Lumora."),
        _ => SecurityCheckFinding.Unknown(Id, "Windows nennt keinen Zustand des Ruhezustands."),
    };
}

/// <summary>
/// Schnellstart: <c>HKLM\…\Session Manager\Power\HiberbootEnabled</c> (1 = an); die Richtlinie „Schnellstart erzwingen“
/// (<c>HKLM\SOFTWARE\Policies\Microsoft\Windows\System\HiberbootEnabled</c> = 1) überschreibt. Ohne Ruhezustand kein Schnellstart.
/// </summary>
public sealed class FastStartupCheck(SecurityCheckSources sources) : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.FastStartup;

    public SecurityCheckFinding Run(SecurityCheckContext context)
    {
        if (HibernationCheck.ReadState(sources.Registry) == 0)
        {
            return SecurityCheckFinding.Ok(Id, "Aus — ohne Ruhezustand gibt es keinen Schnellstart.");
        }

        if (sources.Registry.ReadInt(RegistryRoot.LocalMachine, SecurityCheckKeys.SystemPolicies, "HiberbootEnabled") == 1)
        {
            return SecurityCheckFinding.Bad(Id, "Eine Richtlinie erzwingt den Schnellstart: Teile des Arbeitsspeichers bleiben beim Herunterfahren auf der Platte.");
        }

        return sources.Registry.ReadInt(RegistryRoot.LocalMachine, SecurityCheckKeys.SessionPower, "HiberbootEnabled") switch
        {
            0 => SecurityCheckFinding.Ok(Id, "Aus — beim Herunterfahren bleibt kein Arbeitsspeicher auf der Platte."),
            1 => SecurityCheckFinding.Bad(Id, "Schnellstart ist ein halber Ruhezustand: Teile des Arbeitsspeichers bleiben beim Herunterfahren auf der Platte."),
            _ => SecurityCheckFinding.Unknown(Id, "Windows nennt keinen Zustand des Schnellstarts."),
        };
    }
}

/// <summary>
/// Auslagerungsdatei leeren: <c>HKLM\…\Memory Management\ClearPageFileAtShutdown</c> (1 = an). Die Sicherheitsrichtlinie
/// „Herunterfahren: Auslagerungsdatei des virtuellen Arbeitsspeichers löschen“ schreibt genau diesen Wert; fehlt er, gilt
/// der dokumentierte Standard „deaktiviert“. Ohne Auslagerungsdatei (<c>PagingFiles</c> leer) ist nichts zu leeren.
/// </summary>
public sealed class PageFileCheck(SecurityCheckSources sources) : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.PageFile;

    public SecurityCheckFinding Run(SecurityCheckContext context)
    {
        if (sources.Registry.ReadInt(RegistryRoot.LocalMachine, SecurityCheckKeys.MemoryManagement, "ClearPageFileAtShutdown") == 1)
        {
            return SecurityCheckFinding.Ok(Id, "An — Windows überschreibt pagefile.sys beim Herunterfahren.");
        }

        if (sources.Registry.ReadValue(RegistryRoot.LocalMachine, SecurityCheckKeys.MemoryManagement, "PagingFiles") is string[] files
            && files.All(string.IsNullOrWhiteSpace))
        {
            return SecurityCheckFinding.Ok(Id, "Keine Auslagerungsdatei eingerichtet — es bleibt nichts liegen.");
        }

        return SecurityCheckFinding.Bad(Id, "Windows lagert Arbeitsspeicher nach pagefile.sys aus; ohne Leeren bleibt der Inhalt bis zum Überschreiben liegen. Herunterfahren dauert mit Leeren etwas länger.");
    }
}

/// <summary>
/// Zuletzt geöffnete Elemente/Sprunglisten: <c>HKCU\…\Explorer\Advanced\Start_TrackDocs</c> (0 = aus, fehlt = Windows-Standard
/// an); Richtlinie <c>…\Policies\Explorer\NoRecentDocsHistory</c> = 1 (Computer oder Benutzer) schaltet es ab.
/// </summary>
public sealed class RecentItemsCheck(SecurityCheckSources sources) : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.RecentItems;

    public SecurityCheckFinding Run(SecurityCheckContext context)
    {
        if (sources.Registry.ReadPolicyInt(SecurityCheckKeys.ExplorerPolicies, "NoRecentDocsHistory") == 1)
        {
            return SecurityCheckFinding.Ok(Id, "Aus (per Richtlinie) — Windows führt keine Liste zuletzt geöffneter Dateien.");
        }

        return sources.Registry.ReadInt(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "Start_TrackDocs") == 0
            ? SecurityCheckFinding.Ok(Id, "Aus — Windows führt keine Liste zuletzt geöffneter Dateien.")
            : SecurityCheckFinding.Bad(Id, "Lumora trägt nichts ein — andere Programme (Bildbearbeitung, Explorer) aber schon, wenn Sie dort Dateien aus dem Container öffnen.");
    }
}

/// <summary>
/// Aktivitätsverlauf: nur die Richtlinien sind dokumentiert — <c>HKLM\SOFTWARE\Policies\Microsoft\Windows\System</c>
/// <c>PublishUserActivities</c> = 0 bzw. <c>EnableActivityFeed</c> = 0 schalten ihn ab. Wo der Schalter der Einstellungen
/// gespeichert wird, ist nicht dokumentiert → ohne Richtlinie „Unbekannt“ (nicht raten).
/// </summary>
public sealed class ActivityHistoryCheck(SecurityCheckSources sources) : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.ActivityHistory;

    public SecurityCheckFinding Run(SecurityCheckContext context)
    {
        var publish = sources.Registry.ReadInt(RegistryRoot.LocalMachine, SecurityCheckKeys.SystemPolicies, "PublishUserActivities");
        var feed = sources.Registry.ReadInt(RegistryRoot.LocalMachine, SecurityCheckKeys.SystemPolicies, "EnableActivityFeed");
        if (publish == 0 || feed == 0)
        {
            return SecurityCheckFinding.Ok(Id, "Aus (per Richtlinie) — Windows speichert keinen Aktivitätsverlauf.");
        }

        return SecurityCheckFinding.Unknown(Id, "Den Schalter „Aktivitätsverlauf auf diesem Gerät speichern“ legt Windows an keiner dokumentierten Stelle ab — bitte in den Einstellungen nachsehen.");
    }
}

/// <summary>
/// Explorer-Miniaturbilder: <c>HKCU\…\Explorer\Advanced\IconsOnly</c> = 1 (immer Symbole); Richtlinien
/// <c>…\Policies\Explorer\DisableThumbnails</c> = 1 (nur Symbole) bzw. <c>NoThumbnailCache</c> = 1 (Miniaturbilder nicht
/// zwischenspeichern). <c>DisableThumbnailCache</c> betrifft nur <c>Thumbs.db</c> im Ordner selbst — im Container also
/// verschlüsselt — und zählt deshalb nicht.
/// </summary>
public sealed class ThumbnailCheck(SecurityCheckSources sources) : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.Thumbnails;

    public SecurityCheckFinding Run(SecurityCheckContext context)
    {
        if (sources.Registry.ReadPolicyInt(SecurityCheckKeys.ExplorerPolicies, "DisableThumbnails") == 1)
        {
            return SecurityCheckFinding.Ok(Id, "Aus (per Richtlinie) — der Explorer zeigt nur Symbole.");
        }

        if (sources.Registry.ReadPolicyInt(SecurityCheckKeys.ExplorerPolicies, "NoThumbnailCache") == 1)
        {
            return SecurityCheckFinding.Ok(Id, "Aus (per Richtlinie) — Windows speichert keine Miniaturbilder zwischen.");
        }

        var drive = context.ContainerDrive is { } letter ? $"Laufwerk {letter}:" : "das Container-Laufwerk";
        return sources.Registry.ReadInt(RegistryRoot.CurrentUser, SecurityCheckKeys.ExplorerAdvanced, "IconsOnly") == 1
            ? SecurityCheckFinding.Ok(Id, "Aus — der Explorer zeigt immer Symbole statt Miniaturansichten.")
            : SecurityCheckFinding.Bad(Id, $"Öffnen Sie {drive} im Explorer, legt Windows verkleinerte Bilder in thumbcache auf C: ab — auch nach dem Aushängen.");
    }
}

/// <summary>
/// Windows-Suche auf dem Container-Laufwerk: Dienst <c>WSearch</c> abgeschaltet (<c>Start</c> = 4) oder kein
/// Einschluss-Regel des Suchindex auf diesem Laufwerk. Die Regeln liest Lumora aus dem Speicher des Crawl-Scope-Managers
/// (<c>…\CrawlScopeManager\Windows\SystemIndex\WorkingSetRules\*</c>: <c>URL</c> = <c>file:///X:\[Volume-Kennung]\…</c>,
/// <c>Include</c> = 1/0) — nur lesend. Das Attribut „nicht für Inhaltsindizierung“ am Laufwerk genügt nicht (Dateinamen
/// landen trotzdem im Index) und zählt deshalb nicht.
/// </summary>
public sealed class SearchIndexCheck(SecurityCheckSources sources) : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.SearchIndex;

    public SecurityCheckFinding Run(SecurityCheckContext context)
    {
        if (sources.Registry.ReadInt(RegistryRoot.LocalMachine, SecurityCheckKeys.SearchService, "Start") == 4)
        {
            return SecurityCheckFinding.Ok(Id, "Aus — der Dienst „Windows Search“ ist abgeschaltet.");
        }

        if (context.ContainerDrive is not { } drive)
        {
            return SecurityCheckFinding.Unknown(Id, "Das Laufwerk der versteckten Bibliothek ist nicht bekannt.");
        }

        var rules = sources.Registry.GetSubKeyNames(RegistryRoot.LocalMachine, SecurityCheckKeys.SearchRules);
        if (rules is null)
        {
            return SecurityCheckFinding.Unknown(Id, "Die Regeln des Suchindex sind nicht lesbar.");
        }

        var parsed = rules
            .Select(rule => SecurityCheckKeys.SearchRules + "\\" + rule)
            .Select(key => (
                Path: NormalizeRule(sources.Registry.ReadValue(RegistryRoot.LocalMachine, key, "URL") as string),
                Include: RegistryValues.ToInt(sources.Registry.ReadValue(RegistryRoot.LocalMachine, key, "Include")) == 1))
            .Where(r => r.Path is not null)
            .ToList();
        return IsDriveIndexed(parsed!, drive)
            ? SecurityCheckFinding.Bad(Id, "Dateinamen aus dem Container landen im Suchindex auf dem Windows-Laufwerk (das Laufwerk ist in der Indizierung eingeschlossen).")
            : SecurityCheckFinding.Ok(Id, "Aus — Dateinamen aus dem Container landen nicht im Suchindex.");
    }

    /// <summary>
    /// Die spezifischste Regel gewinnt (Crawl-Scope-Manager): Eine Einschluss-Regel auf dem Laufwerk (Wurzel oder Ordner
    /// darauf) bedeutet „indiziert“. Eine Regel für alles (<c>file:///*</c>) zählt nur, wenn die Laufwerkswurzel nicht
    /// ausdrücklich ausgeschlossen ist.
    /// </summary>
    internal static bool IsDriveIndexed(IReadOnlyList<(string Path, bool Include)> rules, char drive)
    {
        var root = char.ToLowerInvariant(drive) + @":\";
        if (rules.Any(r => r.Include && r.Path.StartsWith(root, StringComparison.Ordinal)))
        {
            return true;
        }

        var rootExcluded = rules.Any(r => !r.Include && r.Path == root);
        return !rootExcluded && rules.Any(r => r.Include && r.Path.Length == 0);
    }

    /// <summary><c>file:///X:\[Volume-Kennung]\Ordner\*</c> → <c>x:\ordner\</c>; <c>file:///*</c> → leer (alles); andere Schemas → null.</summary>
    internal static string? NormalizeRule(string? url)
    {
        const string prefix = "file:///";
        if (string.IsNullOrEmpty(url) || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = url[prefix.Length..].TrimEnd('*').ToLowerInvariant();
        if (rest.Length == 0)
        {
            return string.Empty;
        }

        if (rest.Length < 2 || rest[1] != ':' || !char.IsAsciiLetter(rest[0]))
        {
            return null;
        }

        var path = rest.Length >= 3 ? rest[3..] : string.Empty;
        if (path.StartsWith('['))
        {
            var close = path.IndexOf(']');
            path = close < 0 ? string.Empty : path[(close + 1)..].TrimStart('\\');
        }

        return rest[..2] + "\\" + path;
    }
}

/// <summary>
/// VeraCrypt „Verlauf nie speichern“: <c>%APPDATA%\VeraCrypt\Configuration.xml</c>, <c>&lt;config key="SaveVolumeHistory"&gt;</c>
/// (1 = Verlauf wird gespeichert; fehlt der Wert, gilt VeraCrypts Standard 0). Liegt in <c>History.xml</c> noch eine
/// Liste, ist der Verlauf trotzdem da. Ohne VeraCrypt „nicht zutreffend“.
/// </summary>
public sealed class VeraCryptHistoryCheck(SecurityCheckSources sources) : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.VeraCryptHistory;

    public SecurityCheckFinding Run(SecurityCheckContext context)
    {
        if (sources.VeraCrypt.FindExecutable() is null)
        {
            return new SecurityCheckFinding(Id, SecurityCheckStatus.NotApplicable, "VeraCrypt ist nicht installiert.");
        }

        var folder = Path.Combine(sources.Files.ApplicationDataFolder, "VeraCrypt");
        var saveHistory = ReadSaveVolumeHistory(sources.Files.ReadText(Path.Combine(folder, "Configuration.xml")));
        if (saveHistory is null)
        {
            return SecurityCheckFinding.Unknown(Id, "Die Einstellungen von VeraCrypt sind nicht lesbar.");
        }

        if (saveHistory == 1)
        {
            return SecurityCheckFinding.Bad(Id, "VeraCrypt merkt sich die zuletzt eingehängten Containerdateien samt Pfad.");
        }

        var history = sources.Files.ReadText(Path.Combine(folder, "History.xml"));
        return history is not null && history.Contains("<volume", StringComparison.OrdinalIgnoreCase)
            ? SecurityCheckFinding.Bad(Id, "„Verlauf nie speichern“ ist an, aber eine alte Liste liegt noch vor — in VeraCrypt „Verlauf löschen“ wählen.")
            : SecurityCheckFinding.Ok(Id, "An — VeraCrypt merkt sich keine Containerdateien.");
    }

    /// <summary>0/1 aus <c>Configuration.xml</c>; fehlende Datei bzw. fehlender Wert = VeraCrypt-Standard 0; null = nicht deutbar.</summary>
    public static int? ReadSaveVolumeHistory(string? configurationXml)
    {
        if (configurationXml is null)
        {
            return 0;
        }

        try
        {
            var document = XDocument.Parse(configurationXml);
            var entry = document.Descendants("config")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("key"), "SaveVolumeHistory", StringComparison.Ordinal));
            if (entry is null)
            {
                return 0;
            }

            return RegistryValues.ToInt(entry.Value) is { } value and (0 or 1) ? value : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}

/// <summary>Lumora-Option „nach dem Zusperren ganz beenden“ (Einstellung im versteckten Katalog).</summary>
public sealed class ExitAfterLockCheck : ISecurityCheck
{
    public SecurityCheckId Id => SecurityCheckId.ExitAfterLock;

    public SecurityCheckFinding Run(SecurityCheckContext context) => context.ExitAfterLock
        ? SecurityCheckFinding.Ok(Id, "An — beim Zusperren schließt Lumora sich, damit nichts im Arbeitsspeicher bleibt.")
        : SecurityCheckFinding.Bad(Id, "Aus — nach dem Zusperren läuft Lumora mit der normalen Bibliothek weiter.");
}
