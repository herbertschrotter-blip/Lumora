namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>
/// Welche Punkte bei welcher Gefahren-Stufe zählen (ADR-0064, Tabelle „Wertung“):
/// <list type="bullet">
/// <item><b>Ausgeschalteter PC:</b> nur BitLocker auf C: — mit verschlüsseltem C: liegt alles, was Windows sich merkt
/// (Auslagerungsdatei, Ruhezustand, Miniaturbilder, Verlauf, Protokolle), verschlüsselt auf der Platte.</item>
/// <item><b>Mitbenutzer (Standard):</b> alle Windows-Punkte; die Lumora-Option ist Zusatz (grau) — nach dem Zusperren
/// zeigt Lumora ohnehin nur noch die normale Bibliothek.</item>
/// <item><b>Fachmännische Untersuchung:</b> alle Punkte, auch „Lumora nach dem Zusperren ganz beenden“ (Arbeitsspeicher).</item>
/// </list>
/// „Nicht zutreffend“ zählt nie; „Unbekannt“ zählt als nicht erfüllt.
/// </summary>
public static class SecurityCheckRating
{
    public const ThreatLevel DefaultLevel = ThreatLevel.SharedUser;

    public static SecurityCheckWeight WeightOf(SecurityCheckId id, ThreatLevel level)
    {
        if (id == SecurityCheckId.BitLocker)
        {
            return SecurityCheckWeight.Critical;
        }

        return level switch
        {
            ThreatLevel.PoweredOff => SecurityCheckWeight.NotCounted,
            ThreatLevel.SharedUser => id == SecurityCheckId.ExitAfterLock ? SecurityCheckWeight.NotCounted : SecurityCheckWeight.Recommended,
            _ => SecurityCheckWeight.Recommended,
        };
    }

    public static bool Counts(SecurityCheckFinding finding, ThreatLevel level)
        => finding.Status != SecurityCheckStatus.NotApplicable && WeightOf(finding.Id, level) != SecurityCheckWeight.NotCounted;

    /// <summary>Erklärsatz unter der Auswahl der Gefahren-Stufe (Texte wie Mockup 14).</summary>
    public static string LevelNote(ThreatLevel level) => level switch
    {
        ThreatLevel.PoweredOff => "Für „Ausgeschalteter PC“ genügt BitLocker auf C: — die übrigen Punkte sind dann nur Zusatz (grau).",
        ThreatLevel.Forensic => "Für eine fachmännische Untersuchung reicht keine Einstellung: nötig ist zusätzlich ein verstecktes Betriebssystem (VeraCrypt) — Lumora zeigt die Anleitung, ändert aber nichts.",
        _ => "Für „Mitbenutzer“ zählen alle roten und gelben Punkte. Grau = gut zu wissen, ändert bei dieser Gefahr wenig.",
    };

    /// <summary>Gültige Stufe aus einem gespeicherten Wert; sonst der Standard.</summary>
    public static ThreatLevel ParseLevel(string? text)
        => int.TryParse(text, out var value) && Enum.IsDefined(typeof(ThreatLevel), value) ? (ThreatLevel)value : DefaultLevel;
}
