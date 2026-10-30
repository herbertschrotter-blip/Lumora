using System.Globalization;
using System.Text;

namespace Lumora.Services.Edit;

/// <summary>
/// Begriffsliste für Freitext (ADR-0076 Punkt 8.2): deutsch und englisch, als Wortanfänge. Ein Treffer heißt „nicht
/// senden“ mit sachlichem Hinweis. Groß/klein, Umlaute (ä = ae) und Akzente sind egal; Wörter werden nur am Anfang
/// verglichen, damit „Bikini“ in „Bikinimode“ trifft, „sex“ aber nicht in „Essex“. Mehrdeutige kurze Wörter stehen mit
/// Leerzeichen da („slip “, „butt “, „strip “), damit Slipper, Butter oder Streifen nicht anschlagen.
/// </summary>
public static class EditTerms
{
    /// <summary>Wortanfänge: Nacktheit, Unterwäsche/Bademode, sexuelle Inhalte, Ausziehen, Körperteile im sexuellen Sinn.</summary>
    private static readonly string[] Stems =
    [
        // deutsch
        "nackt", "nacke", "entbloe", "entkleid", "auszieh", "ausgezog", "oben ohne", "unbekleid", "freizueg", "aufreiz",
        "erotik", "erotisch", "sexy", "sexuell", "porno", "unterwaesch", "unterhose", "dessous", "reizwaesch", "slip ", "slips",
        "hoeschen", "tanga", "strapse", "korsett", "negligee", "bh ", "busen", "brueste", "brustwarz", "nippel", "intim",
        "genital", "scham", "po frei", "hintern", "badeanzug", "bademode", "badehose", "bikini", "durchsichtig",
        "transparent", "fetisch", "lack und leder",
        // englisch
        "nude", "nudity", "naked", "undress", "strip ", "stripp", "striptease", "topless", "bottomless", "lingerie", "underwear", "undies",
        "panties", "panty", "thong", "g-string", "bra ", "bralette", "boob", "breast", "nipple", "cleavage", "butt ", "butts",
        "genitals", "crotch", "pussy", "penis", "vagina", "sex", "porn", "nsfw", "erotic", "seductive", "sensual",
        "provocative", "revealing", "skimpy", "see-through", "see through", "sheer", "swimsuit", "swimwear", "bikini",
        "fetish", "bondage", "bdsm", "latex", "hentai", "onlyfans", "playboy",
    ];

    /// <summary>Getroffene Wortanfänge (normalisiert), leer = in Ordnung.</summary>
    public static IReadOnlyList<string> FindBlocked(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = " " + Normalize(text) + " ";
        var hits = new List<string>();
        foreach (var stem in Stems)
        {
            var needle = Normalize(stem);
            // am Wortanfang: davor ein Nicht-Buchstabe
            var index = normalized.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                if (index == 0 || !char.IsLetterOrDigit(normalized[index - 1]))
                {
                    hits.Add(stem.Trim());
                    break;
                }

                index = normalized.IndexOf(needle, index + 1, StringComparison.Ordinal);
            }
        }

        return hits.Distinct().ToList();
    }

    /// <summary>Kleinbuchstaben, ä/ö/ü/ß ausgeschrieben, Akzente weg, Satzzeichen zu Leerzeichen (Bindestrich bleibt).</summary>
    public static string Normalize(string text)
    {
        var lower = text.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var builder = new StringBuilder(lower.Length);
        foreach (var c in lower.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) || c == '-' ? c : ' ');
        }

        return builder.ToString();
    }
}
