using System.Globalization;

namespace Lumora.Services.Usage;

/// <summary>Verwendung bzw. Weitergabe einer Datei über Lumora (ADR-0072) — von Aktionen gemeldet, nie vom Ansehen.</summary>
public enum UsageKind
{
    /// <summary>Vom Benutzer verwendet: in ein Album, in eine Story, als Titelbild, in eine Collage aus einer Auswahl.</summary>
    Used = 0,

    /// <summary>Weitergegeben: Drucken, Teilen, Exportieren, Kopieren nach außen (Ziehen/Strg+C), später Handy-App.</summary>
    Shared = 1,
}

/// <summary>Woher eine Ansicht kommt — bestimmt, ob und wie stark sie zählt (ADR-0072).</summary>
public enum UsageViewContext
{
    /// <summary>Lightbox aus Galerie, Ordnern, Personen, Orten, Alben: zählt voll.</summary>
    Normal = 0,

    /// <summary>Lightbox aus einer Story: gedämpft (Highlights sollen sich nicht selbst verstärken).</summary>
    Story = 1,

    /// <summary>Diashow: nur die Zeit zählt, gedämpft; keine „Öffnung“.</summary>
    Slideshow = 2,

    /// <summary>Zählt nicht (Seite „Doppelte“, Sortieren, Rauchtest-Vorschauen).</summary>
    None = 3,
}

/// <summary>Einstellungen der Nutzungs-Signale (Settings-Tabelle des Katalogs, je Katalog).</summary>
/// <param name="Enabled">„Nutzung lernen“ (Standard an). Aus: nichts messen, nichts verwenden — Daten bleiben bis „löschen“.</param>
/// <param name="ForgottenEnabled">Rückblick „Vergessene Schätze“ anbieten (Standard an; wirkt nur mit <paramref name="Enabled"/>).</param>
public sealed record UsageSettings(bool Enabled = true, bool ForgottenEnabled = true)
{
    public const string EnabledKey = "usage.enabled";
    public const string ForgottenKey = "usage.forgotten";

    /// <summary>Erster Tag mit gemerkter Nutzung (Tageszahl seit 1970-01-01) — nur Anzeige „seit …“.</summary>
    public const string SinceKey = "usage.since";

    public static UsageSettings Default { get; } = new();
}

/// <summary>Summen einer Datei über alle Tage (aus <c>FileUsageDays</c>).</summary>
/// <param name="Days">Tage mit gezielter Nutzung (Öffnen, Verwendung, Weitergabe) — „Wiederkehr an anderen Tagen“.</param>
/// <param name="LastDay">Letzter Tag mit irgendeiner Nutzung (Tageszahl seit 1970-01-01).</param>
public sealed record UsageTotals(
    int Opens,
    int Seconds,
    int Zooms,
    int SoftOpens,
    int SoftSeconds,
    int Uses,
    int Shares,
    int Days,
    int LastDay)
{
    public static UsageTotals None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Gelernter Rohwert (≥ 0) — siehe <see cref="UsageScoring.Raw"/>.</summary>
    public double Raw => UsageScoring.Raw(this);

    /// <summary>Vorliebe 0..1 (<see cref="UsageScoring.Normalize"/>).</summary>
    public double Preference => UsageScoring.Normalize(Raw);
}

/// <summary>Übersicht für Einstellungen › Storys („Gelernt: 1 284 Fotos an 57 Tagen“).</summary>
public sealed record UsageSummary(int Files, int Days, int Loved, DateOnly? Since)
{
    public static UsageSummary Empty { get; } = new(0, 0, 0, null);

    public bool IsEmpty => Files == 0;
}

/// <summary>
/// Rechenkern der Nutzungs-Signale (ADR-0072), ohne Datenbank: Grenzen je Ansicht, Rohwert, Vorliebe 0..1, gedämpfter
/// Zuschlag für die Highlight-Auswahl und der Grund-Text. Der Rohwert wird in C# (<see cref="Raw"/>) und in SQL
/// (<see cref="RawSql"/>) gleich gerechnet — Tests prüfen beides gegeneinander.
/// </summary>
public static class UsageScoring
{
    /// <summary>Ansichten unter einer Sekunde (Durchblättern im Schnelllauf) zählen nicht.</summary>
    public static readonly TimeSpan MinView = TimeSpan.FromSeconds(1);

    /// <summary>Je Ansicht zählen höchstens zwei Minuten (Foto offen liegen gelassen).</summary>
    public static readonly TimeSpan MaxView = TimeSpan.FromMinutes(2);

    /// <summary>Höchster Zuschlag in der Highlight-Auswahl — kleiner als ein Favorit (0,15, ADR-0057).</summary>
    public const double MaxBonus = 0.12;

    /// <summary>Ab diesem Rohwert gilt ein Foto als „Liebling“ (z. B. an drei Tagen geöffnet).</summary>
    public const double LovedRaw = 6;

    /// <summary>Ab dieser Vorliebe nennt die Highlight-Auswahl die Nutzung als Grund.</summary>
    public const double ReasonPreference = 0.25;

    /// <summary>Skala der Sättigung: Vorliebe = 1 − e^(−Roh / Skala).</summary>
    public const double Scale = 8;

    /// <summary>
    /// Rohwert: Öffnen (bis 20) × 1, gedämpftes Öffnen (bis 20) × 0,5, Minuten (bis 30) × 0,5, gedämpfte Minuten × 0,125,
    /// Zoom (bis 10) × 0,5, jeder weitere Tag (bis 10) × 2, Verwendung (bis 5) × 2, Weitergabe (bis 5) × 3.
    /// </summary>
    public static double Raw(UsageTotals t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return Math.Min(t.Opens, 20)
               + (0.5 * Math.Min(t.SoftOpens, 20))
               + (Math.Min(t.Seconds, 1800) / 120.0)
               + (Math.Min(t.SoftSeconds, 1800) / 480.0)
               + (0.5 * Math.Min(t.Zooms, 10))
               + (2.0 * Math.Min(Math.Max(t.Days - 1, 0), 10))
               + (2.0 * Math.Min(t.Uses, 5))
               + (3.0 * Math.Min(t.Shares, 5));
    }

    /// <summary>Derselbe Rohwert als SQL über die Tageszeilen einer Datei (<c>GROUP BY FileId</c>, Alias <paramref name="u"/>).</summary>
    public static string RawSql(string u = "u")
        => $"""
            (MIN(SUM({u}.Opens), 20) * 1.0 + MIN(SUM({u}.SoftOpens), 20) * 0.5 + MIN(SUM({u}.Seconds), 1800) / 120.0
             + MIN(SUM({u}.SoftSeconds), 1800) / 480.0 + MIN(SUM({u}.Zooms), 10) * 0.5
             + MIN(MAX(SUM(CASE WHEN {u}.Opens > 0 OR {u}.Uses > 0 OR {u}.Shares > 0 THEN 1 ELSE 0 END) - 1, 0), 10) * 2.0
             + MIN(SUM({u}.Uses), 5) * 2.0 + MIN(SUM({u}.Shares), 5) * 3.0)
            """;

    /// <summary>
    /// Unterabfrage: FileIds mit Rohwert ≥ <see cref="LovedRaw"/> („Lieblinge“). Mit <paramref name="respectSetting"/> leer,
    /// solange „Nutzung lernen“ aus ist (Galerie-Filter: ausgeschaltet heißt auch „nicht verwenden“).
    /// </summary>
    public static string LovedFileIdsSql(bool respectSetting = false)
        => "SELECT u.FileId FROM FileUsageDays u"
           + (respectSetting ? $" WHERE NOT EXISTS (SELECT 1 FROM Settings s WHERE s.Key = '{UsageSettings.EnabledKey}' AND s.Value = 'false')" : string.Empty)
           + $" GROUP BY u.FileId HAVING {RawSql()} >= {LovedRaw.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Vorliebe 0..1 aus dem Rohwert (sättigt: viele Ansichten heben nicht beliebig).</summary>
    public static double Normalize(double raw) => raw <= 0 || double.IsNaN(raw) ? 0 : 1 - Math.Exp(-raw / Scale);

    /// <summary>Zuschlag für die Highlight-Auswahl: 0 ohne Nutzung (nie ein Abzug), höchstens <see cref="MaxBonus"/>.</summary>
    public static double Bonus(UsageTotals? totals) => totals is null ? 0 : MaxBonus * Math.Clamp(totals.Preference, 0, 1);

    public static bool IsLoved(UsageTotals? totals) => totals is not null && totals.Raw >= LovedRaw;

    /// <summary>Kurzer Grund für „warum ausgewählt“ — null, wenn die Nutzung zu klein ist, um sie zu nennen.</summary>
    public static string? Reason(UsageTotals? t)
    {
        if (t is null || t.Preference < ReasonPreference)
        {
            return null;
        }

        if (t.Shares >= 2)
        {
            return $"{t.Shares.ToString(CultureInfo.InvariantCulture)}× geteilt";
        }

        if (t.Days >= 2 || t.Opens >= 3)
        {
            return "oft angesehen";
        }

        if (t.Shares == 1)
        {
            return "weitergegeben";
        }

        return t.Uses > 0 ? "verwendet" : "gern angesehen";
    }

    /// <summary>Zeile „Nutzung“ der Lightbox: „9× angesehen an 6 Tagen · 4 min · 1× verwendet“; null ohne Nutzung.</summary>
    public static string? Describe(UsageTotals? t)
    {
        if (t is null)
        {
            return null;
        }

        var parts = new List<string>(4);
        if (t.Opens > 0)
        {
            parts.Add(t.Days > 1
                ? $"{t.Opens.ToString(CultureInfo.InvariantCulture)}× angesehen an {t.Days.ToString(CultureInfo.InvariantCulture)} Tagen"
                : $"{t.Opens.ToString(CultureInfo.InvariantCulture)}× angesehen");
        }

        if (t.SoftOpens > 0)
        {
            parts.Add($"{t.SoftOpens.ToString(CultureInfo.InvariantCulture)}× in Storys");
        }

        var seconds = t.Seconds + t.SoftSeconds;
        if (seconds >= 60)
        {
            parts.Add($"{(seconds / 60).ToString(CultureInfo.InvariantCulture)} min");
        }
        else if (seconds > 0 && t.Opens + t.SoftOpens == 0)
        {
            parts.Add("in der Diashow");
        }

        if (t.Zooms > 0)
        {
            parts.Add($"{t.Zooms.ToString(CultureInfo.InvariantCulture)}× gezoomt");
        }

        if (t.Uses > 0)
        {
            parts.Add($"{t.Uses.ToString(CultureInfo.InvariantCulture)}× verwendet");
        }

        if (t.Shares > 0)
        {
            parts.Add($"{t.Shares.ToString(CultureInfo.InvariantCulture)}× weitergegeben");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Tageszahl seit 1970-01-01 eines Kalendertags (wie die Storys).</summary>
    public static int DayNumber(DateOnly date) => date.DayNumber - Epoch;

    public static DateOnly DateOf(int day) => DateOnly.FromDayNumber(day + Epoch);

    private static readonly int Epoch = new DateOnly(1970, 1, 1).DayNumber;
}
