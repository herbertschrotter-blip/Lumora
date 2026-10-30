namespace Lumora.Services.Edit;

/// <summary>Eine Kleidung aus der festen Liste: Anzeige und die englische Beschreibung für die Vorlage.</summary>
public sealed record OutfitChoice(string Id, string Label, string Prompt);

/// <summary>
/// Feste Liste Alltagskleidung für die Vorlage „Outfit“ (ADR-0076 Punkt 8.1) — die einzige Eingabe dieser Vorlage, kein
/// Freitext. Bademode, Unterwäsche, Nachtwäsche, Lingerie, transparente oder freizügige Kleidung gibt es hier nicht; jede
/// Beschreibung sagt ausdrücklich „vollständig bekleidet“.
/// </summary>
public static class OutfitCatalog
{
    public static IReadOnlyList<OutfitChoice> All { get; } =
    [
        new("jeans-pullover", "Jeans und Pullover", "blue jeans and a knitted wool sweater, fully clothed"),
        new("anzug", "Anzug", "a dark business suit with shirt and tie, fully clothed"),
        new("kostuem", "Hosenanzug", "a tailored business pantsuit with a blouse, fully clothed"),
        new("wanderkleidung", "Wanderkleidung", "hiking trousers, a long-sleeved outdoor shirt and a hiking jacket, fully clothed"),
        new("winterjacke", "Winterjacke", "a warm winter parka, scarf and long trousers, fully clothed"),
        new("regenjacke", "Regenjacke", "a rain jacket and long trousers, fully clothed"),
        new("tracht", "Tracht", "traditional Alpine costume, dirndl with blouse and apron or lederhosen with shirt, fully clothed"),
        new("sommerkleid", "Sommerkleid (knielang)", "a knee-length summer dress with sleeves, fully clothed"),
        new("hemd-chino", "Hemd und Chino", "a button-up shirt and chino trousers, fully clothed"),
        new("sportlich", "Sportkleidung (lang)", "a long-sleeved sports top and full-length track pants, fully clothed"),
    ];

    public static OutfitChoice? Find(string? id)
        => id is null ? null : All.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
}
