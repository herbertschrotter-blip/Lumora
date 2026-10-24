namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>Anzeige eines Punkts: Titel, Etikett bei „empfohlen“, Anleitung, Ziel von „Einstellung öffnen“ (Texte wie Mockup 14).</summary>
/// <param name="RecommendedTag">Etikett, solange der Punkt zählt und nicht erfüllt ist („empfohlen aus“).</param>
/// <param name="Howto">Klick-Anleitung hinter ⓘ; null = keine.</param>
/// <param name="Target">Was „Einstellung öffnen“ startet; <see cref="SettingsTarget.None"/> = kein Knopf.</param>
public sealed record SecurityCheckDefinition(
    SecurityCheckId Id,
    string Title,
    string RecommendedTag,
    string? Howto,
    SettingsTarget Target,
    string OpenLabel = "Einstellung öffnen");

/// <summary>Texte des Sicherheits-Checks (ADR-0064). Lumora nennt Befehle nur zum Nachlesen — es führt selbst keinen aus.</summary>
public static class SecurityCheckCatalog
{
    public const string InfoTitle = "Nicht abschaltbar: Programm- und Laufwerksprotokolle";

    public const string InfoText =
        "Windows merkt sich, dass Lumora lief (Prefetch), dass ein Container-Laufwerk eingehängt war und welche Ordner Sie im Explorer angesehen haben. " +
        "Das verrät nicht, was darin war — und mit BitLocker auf C: ist es verschlüsselt. Gegen eine fachmännische Untersuchung des entsperrten PCs hilft nur ein verstecktes Betriebssystem (VeraCrypt).";

    public const string Lead =
        "Lumora selbst hinterlässt außerhalb des Containers nichts. Windows merkt sich aber an vielen Stellen, was auf dem PC passiert. " +
        "Hier sehen Sie, wo — und öffnen die passende Windows-Einstellung. Lumora ändert selbst keine Windows-Einstellungen.";

    public static IReadOnlyList<SecurityCheckDefinition> All { get; } =
    [
        new(SecurityCheckId.BitLocker, "BitLocker auf Laufwerk C:", "wichtigster Punkt",
            "Systemsteuerung › BitLocker-Laufwerkverschlüsselung › „BitLocker aktivieren“ für C:. Wiederherstellungsschlüssel ausdrucken oder im Microsoft-Konto sichern — nicht in OneDrive als Datei. " +
            "Windows Home: Einstellungen › Datenschutz und Sicherheit › Geräteverschlüsselung (falls vorhanden) oder VeraCrypt-Systemverschlüsselung.",
            SettingsTarget.BitLocker),
        new(SecurityCheckId.Hibernation, "Ruhezustand", "empfohlen aus",
            "Energieoptionen › „Auswählen, was beim Drücken des Netzschalters geschehen soll“ › Ruhezustand abwählen (dann bietet Windows ihn nicht mehr an). " +
            "Ganz abschalten (hiberfil.sys wird gelöscht) geht nur in einer Eingabeaufforderung als Administrator mit „powercfg /h off“ — Lumora führt diesen Befehl nie selbst aus.",
            SettingsTarget.PowerOptions),
        new(SecurityCheckId.FastStartup, "Schnellstart", "empfohlen aus",
            "Energieoptionen › „Auswählen, was beim Drücken des Netzschalters geschehen soll“ › „Einige Einstellungen sind momentan nicht verfügbar“ › „Schnellstart aktivieren“ abwählen.",
            SettingsTarget.PowerOptions),
        new(SecurityCheckId.PageFile, "Auslagerungsdatei beim Herunterfahren leeren", "empfohlen an",
            "Lokale Sicherheitsrichtlinie (secpol.msc) › Lokale Richtlinien › Sicherheitsoptionen › „Herunterfahren: Auslagerungsdatei des virtuellen Arbeitsspeichers löschen“ › Aktiviert. " +
            "Windows Home hat keine Lokale Sicherheitsrichtlinie: dort im Registrierungs-Editor als Administrator unter HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management den Wert „ClearPageFileAtShutdown“ auf 1 setzen.",
            SettingsTarget.SecurityPolicy),
        new(SecurityCheckId.RecentItems, "Zuletzt geöffnete Elemente und Sprunglisten", "empfohlen aus",
            "Einstellungen › Personalisierung › Start › „Zuletzt geöffnete Elemente in Start, Sprunglisten und im Explorer anzeigen“ › Aus.",
            SettingsTarget.StartSettings),
        new(SecurityCheckId.ActivityHistory, "Aktivitätsverlauf", "empfohlen aus",
            "Einstellungen › Datenschutz und Sicherheit › Aktivitätsverlauf › „Aktivitätsverlauf auf diesem Gerät speichern“ › Aus. Fehlt die Seite (neuere Windows-11-Versionen), speichert Windows keinen Verlauf mehr. " +
            "Für alle Konten: Gruppenrichtlinie „Veröffentlichen von Benutzeraktivitäten zulassen“ › Deaktiviert — dann zeigt Lumora den Punkt als erfüllt.",
            SettingsTarget.ActivityHistory),
        new(SecurityCheckId.Thumbnails, "Miniaturbilder des Explorers", "empfohlen aus",
            "Explorer › Optionen › Ansicht › „Immer Symbole statt Miniaturansichten anzeigen“. Besser: den Container nur über Lumora öffnen.",
            SettingsTarget.FolderOptions),
        new(SecurityCheckId.SearchIndex, "Windows-Suche auf dem Container-Laufwerk", "empfohlen aus",
            "Indizierungsoptionen › Ändern › das Container-Laufwerk abwählen. Zusätzlich im Explorer › Eigenschaften des Laufwerks › „Zulassen, dass für Dateien auf diesem Laufwerk Inhalte zusätzlich zu Dateieigenschaften indiziert werden“ abwählen.",
            SettingsTarget.IndexingOptions),
        new(SecurityCheckId.VeraCryptHistory, "VeraCrypt: Verlauf nie speichern", "empfohlen an",
            "VeraCrypt › im Hauptfenster „Verlauf nie speichern“ ankreuzen und „Verlauf löschen“ wählen.",
            SettingsTarget.VeraCrypt, "VeraCrypt öffnen"),
        new(SecurityCheckId.ExitAfterLock, "Lumora nach dem Zusperren ganz beenden", "Lumora-Einstellung",
            "Beim Zusperren von Hand und nach Untätigkeit beendet sich Lumora ganz, statt zur normalen Bibliothek zu wechseln — nichts aus der versteckten Bibliothek bleibt im Arbeitsspeicher.",
            SettingsTarget.None),
    ];

    public static SecurityCheckDefinition Get(SecurityCheckId id) => All.First(d => d.Id == id);

    /// <summary>Titel mit Laufwerksbuchstaben, wo er hilft (nur Anzeige).</summary>
    public static string TitleFor(SecurityCheckId id, SecurityCheckContext? context) => id switch
    {
        SecurityCheckId.BitLocker when context is not null => $"BitLocker auf Laufwerk {context.SystemDrive}:",
        SecurityCheckId.SearchIndex when context?.ContainerDrive is { } drive => $"Windows-Suche auf Laufwerk {drive}:",
        _ => Get(id).Title,
    };
}
