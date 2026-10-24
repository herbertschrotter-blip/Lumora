using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumora.Services.Albums;

/// <summary>Kategorie-Teil eines gespeicherten Filters: Werte über ihren Namen, „ohne Wert“.</summary>
public sealed record SmartAlbumCategory(string Name, IReadOnlyList<string> Values, bool IncludeNone);

/// <summary>Ort-Teil eines gespeicherten Filters (GeoNames-Ids sind katalogübergreifend stabil).</summary>
public sealed record SmartAlbumPlace(IReadOnlyList<int> PlaceIds, bool IncludeWithoutLocation, bool IncludeWithoutPlaceName, string? Label);

/// <summary>
/// Gespeicherter Galerie-Filter eines intelligenten Albums (ADR-0054), als JSON in <c>Albums.FilterJson</c>. Alle Teile
/// von <c>GalleryFilterState</c> (Dateiart, Favoriten, Ablage, Bewertung, Ort) plus Suche, Tags, Personen, Beziehung,
/// Kategorien, Motive, Ordner und (eigenes) Album. Begriffe stehen über ihren NAMEN, nicht über Ids: Verknüpfen führt
/// gleichnamige Begriffe zusammen, Lösen kopiert sie mit neuen Ids, „Neu aufbauen“ vergibt neue Ordner-Ids — ein
/// Namensbezug bleibt in allen Fällen gültig. Ein Name, den es in der Gruppe nicht (mehr) gibt, trifft keine Datei.
/// Modi sind Texte („Any“, „All“, …), damit die Services-Schicht die WPF-Enums nicht kennt. Nicht gespeichert werden der
/// Kartenausschnitt und die Bildinhalt-Suche (vorübergehende Auswahl, braucht das Modell).
/// </summary>
public sealed record SmartAlbumFilter
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public int Version { get; init; } = 1;

    /// <summary>„Images“, „Videos“, „Archives“ oder null (alle).</summary>
    public string? Media { get; init; }

    public bool FavoritesOnly { get; init; }

    /// <summary>„Inbox“, „Library“ oder null (überall).</summary>
    public string? Location { get; init; }

    /// <summary>Mindest-Bewertung 0–5.</summary>
    public int MinRating { get; init; }

    public string? SearchTerm { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>„Any“, „All“ oder „None“ (ohne Tag).</summary>
    public string? TagMode { get; init; }

    public IReadOnlyList<string> Persons { get; init; } = [];

    /// <summary>„Any“, „All“, „NoPerson“, „UnnamedFaces“, „AnyFace“.</summary>
    public string? PersonMode { get; init; }

    public bool PersonIncludeSuggestions { get; init; }

    public IReadOnlyList<string> Relations { get; init; } = [];

    public IReadOnlyList<SmartAlbumCategory> Categories { get; init; } = [];

    public IReadOnlyList<string> Motifs { get; init; } = [];

    /// <summary>„Any“ oder „All“.</summary>
    public string? MotifMode { get; init; }

    public SmartAlbumPlace? Place { get; init; }

    /// <summary>Ordner über seinen vollen Pfad (Folders.Path); null = ganze Bibliothek.</summary>
    public string? FolderPath { get; init; }

    /// <summary>Mit Unterordnern (nur mit <see cref="FolderPath"/>).</summary>
    public bool? IncludeSubfolders { get; init; }

    /// <summary>Eigenes Album über seinen Namen (Filter „Album“).</summary>
    public string? AlbumName { get; init; }

    /// <summary>true, wenn kein Teil wirkt (ein solches Album zeigt alles — beim Speichern abgelehnt).</summary>
    [JsonIgnore]
    public bool IsEmpty
        => Media is null && !FavoritesOnly && Location is null && MinRating <= 0 && string.IsNullOrWhiteSpace(SearchTerm)
           && Tags.Count == 0 && TagMode != "None"
           && Persons.Count == 0 && PersonMode is null or "Any" or "All"
           && Relations.Count == 0 && Categories.Count == 0 && Motifs.Count == 0
           && Place is null && FolderPath is null && AlbumName is null;

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    /// <summary>Liest einen gespeicherten Filter; null bei leerem oder unlesbarem Text.</summary>
    public static SmartAlbumFilter? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SmartAlbumFilter>(json, s_json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Die Bedingungen in Worten (Seite „Alben“, Tooltip) — „Bilder“, „Tag „Urlaub““ …</summary>
    public IReadOnlyList<string> Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(SearchTerm))
        {
            parts.Add($"Suche „{SearchTerm.Trim()}“");
        }

        switch (Media)
        {
            case "Images": parts.Add("Bilder"); break;
            case "Videos": parts.Add("Videos"); break;
            case "Archives": parts.Add("Archive"); break;
        }

        if (FavoritesOnly)
        {
            parts.Add("Favoriten");
        }

        if (MinRating > 0)
        {
            parts.Add(MinRating >= 5 ? "nur 5 Sterne" : $"ab {MinRating} Stern{(MinRating == 1 ? string.Empty : "en")}");
        }

        switch (Location)
        {
            case "Inbox": parts.Add("noch im Eingang"); break;
            case "Library": parts.Add("in der Bibliothek"); break;
            case "Missing": parts.Add("nur fehlende"); break; // ADR-0065
        }

        if (Place is not null)
        {
            parts.Add(Place.Label is { Length: > 0 } label ? $"Ort {label}" : "Ort");
        }

        if (TagMode == "None")
        {
            parts.Add("ohne Tag");
        }
        else if (Tags.Count > 0)
        {
            parts.Add($"Tag {Join(Tags, TagMode == "All")}");
        }

        switch (PersonMode)
        {
            case "NoPerson": parts.Add("ohne benannte Person"); break;
            case "UnnamedFaces": parts.Add("unbenannte Gesichter"); break;
            case "AnyFace": parts.Add("irgendein Gesicht"); break;
            default:
                if (Persons.Count > 0)
                {
                    parts.Add($"Person {Join(Persons, PersonMode == "All")}");
                }

                break;
        }

        if (Relations.Count > 0)
        {
            parts.Add($"Beziehung {Join(Relations, all: false)}");
        }

        if (Motifs.Count > 0)
        {
            parts.Add($"Motiv {Join(Motifs, MotifMode == "All")}");
        }

        foreach (var category in Categories)
        {
            var values = category.Values.Select(v => $"„{v}“").ToList();
            if (category.IncludeNone)
            {
                values.Add("ohne Wert");
            }

            parts.Add($"{category.Name}: {string.Join(" oder ", values)}");
        }

        if (FolderPath is { } folder)
        {
            parts.Add($"Ordner „{Path.GetFileName(folder.TrimEnd('\\', '/'))}“{(IncludeSubfolders == false ? string.Empty : " mit Unterordnern")}");
        }

        if (AlbumName is { } album)
        {
            parts.Add($"Album „{album}“");
        }

        return parts;

        static string Join(IReadOnlyList<string> names, bool all)
            => string.Join(all ? " und " : " oder ", names.Select(n => $"„{n}“"));
    }
}
