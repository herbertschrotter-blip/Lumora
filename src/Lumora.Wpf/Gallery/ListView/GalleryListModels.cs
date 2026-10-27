using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Gallery.Data;

namespace Lumora.Gallery.ListView;

/// <summary>
/// Darstellung der Galerie: Kachel-Grid, Listenansicht (DataGrid über dieselbe Page) oder Collage (echte
/// Seitenverhältnisse, randvolle Reihen, ADR-0044). Gespeichert wird der Name (Setting <c>ui.gallery.viewMode</c>).
/// </summary>
public enum GalleryViewMode
{
    Grid = 0,
    List = 1,
    Collage = 2,

    /// <summary>Unterordner als Zeilen (Mockup 21): je Set erstes, letztes und zufällige Fotos.</summary>
    Folders = 3,
}

/// <summary>Spalten der Listenansicht (Schlüssel für Einstellungen und Kopfzeilen-Kontextmenü).</summary>
public enum GalleryListColumnKey
{
    Name,
    Folder,
    Modified,
    Size,
    Type,
    Rating,
    Favorite,
    Tags,
    Status,

    /// <summary>Bestätigte Personen (und Vorschläge kursiv mit „?“), 0.2.38 — nur innerhalb der Page sortierbar.</summary>
    Persons,

    /// <summary>Aufnahmedatum (ADR-0046) — sortiert die ganze Trefferliste.</summary>
    Captured,

    /// <summary>Ort („Wien, Österreich“, ADR-0050) — nur innerhalb der Page sortierbar.</summary>
    Place,
    /// <summary>Erkannte Motive (ADR-0052), wahrscheinlichste zuerst — nur innerhalb der Page sortierbar.</summary>
    Motifs,
}

/// <summary>Stammdaten einer Spalte: Überschrift, Startbreite, serverseitige Sortierung (auf/ab) oder null = nur innerhalb der Page.</summary>
public sealed record GalleryListColumnDefinition(
    GalleryListColumnKey Key,
    string Header,
    double Width,
    GallerySort? SortAscending,
    GallerySort? SortDescending)
{
    /// <summary>True, wenn ein Klick auf die Kopfzeile die Sortierung der gesamten Trefferliste (DB) ändert.</summary>
    public bool SortsServerSide => SortAscending is not null;
}

/// <summary>Alle Spalten in Standardreihenfolge; „Status“ = fehlt / Eingang / Archiv (ADR-0020).</summary>
public static class GalleryListColumns
{
    public static IReadOnlyList<GalleryListColumnDefinition> All { get; } =
    [
        new(GalleryListColumnKey.Name, "Name", 280, GallerySort.NameAsc, GallerySort.NameDesc),
        new(GalleryListColumnKey.Folder, "Ordner", 260, null, null),
        new(GalleryListColumnKey.Captured, "Aufgenommen", 130, GallerySort.CapturedAsc, GallerySort.CapturedDesc), // ADR-0046
        new(GalleryListColumnKey.Modified, "Geändert", 130, GallerySort.ModifiedAsc, GallerySort.ModifiedDesc),
        new(GalleryListColumnKey.Size, "Größe", 90, GallerySort.SizeAsc, GallerySort.SizeDesc),
        new(GalleryListColumnKey.Type, "Typ", 70, null, null),
        new(GalleryListColumnKey.Rating, "Bewertung", 100, null, null),
        new(GalleryListColumnKey.Favorite, "Favorit", 70, null, null),
        new(GalleryListColumnKey.Tags, "Tags", 180, null, null),
        new(GalleryListColumnKey.Persons, "Personen", 180, null, null),
        new(GalleryListColumnKey.Place, "Ort", 170, null, null), // ADR-0050
        new(GalleryListColumnKey.Motifs, "Motive", 180, null, null), // ADR-0052
        new(GalleryListColumnKey.Status, "Status", 80, null, null),
    ];

    public static GalleryListColumnDefinition Get(GalleryListColumnKey key) => All.First(c => c.Key == key);

    /// <summary>Spalte + Richtung zu einer Galerie-Sortierung (für den Sortierpfeil in der Kopfzeile); null bei Page-Sortierung.</summary>
    public static (GalleryListColumnKey Key, bool Descending)? ForSort(GallerySort sort)
    {
        foreach (var column in All)
        {
            if (column.SortAscending == sort)
            {
                return (column.Key, false);
            }

            if (column.SortDescending == sort)
            {
                return (column.Key, true);
            }
        }

        return null;
    }

    /// <summary>
    /// Nächste Sortierung nach Klick auf eine Spalte mit serverseitiger Sortierung: erst aufsteigend, bei erneutem
    /// Klick auf dieselbe Spalte die Richtung wechseln. Null für Spalten, die nur innerhalb der Page sortieren.
    /// </summary>
    public static GallerySort? NextSort(GalleryListColumnKey key, GallerySort current)
    {
        var column = Get(key);
        if (!column.SortsServerSide)
        {
            return null;
        }

        return current == column.SortAscending ? column.SortDescending : column.SortAscending;
    }
}

/// <summary>
/// Sichtbare Spalten der Listenansicht in Reihenfolge (Setting <c>ui.gallery.listColumns</c>, Token durch Komma).
/// Token: Name einer festen Spalte (<see cref="GalleryListColumnKey"/>) oder „Kategorie:{Id}“ (ADR-0027);
/// „!Kategorie:{Id}“ = ausgeblendete Kategorie-Spalte. Feste Spalten ohne Token sind ausgeblendet, Kategorie-Spalten
/// ohne Token sichtbar (neue Kategorien erscheinen am Ende). Unbekannte Token werden beim Parsen übergangen,
/// „Name“ bleibt immer sichtbar.
/// </summary>
public sealed class GalleryListColumnSettings
{
    public const string CategoryPrefix = "Kategorie:";

    private readonly List<string> _tokens;
    private readonly HashSet<int> _hiddenCategories;

    private GalleryListColumnSettings(List<string> tokens, HashSet<int> hiddenCategories)
    {
        _tokens = tokens;
        _hiddenCategories = hiddenCategories;
    }

    public static GalleryListColumnSettings Default
        => new(GalleryListColumns.All.Select(c => c.Key.ToString()).ToList(), new HashSet<int>());

    /// <summary>Sichtbare feste Spalten in Anzeigereihenfolge.</summary>
    public IReadOnlyList<GalleryListColumnKey> Visible
        => _tokens
            .Select(t => System.Enum.TryParse<GalleryListColumnKey>(t, out var key) ? key : (GalleryListColumnKey?)null)
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .ToList();

    /// <summary>Alle ausdrücklich sichtbaren Spalten (feste und Kategorie-Spalten) in Anzeigereihenfolge.</summary>
    public IReadOnlyList<string> VisibleTokens => _tokens;

    public bool IsVisible(GalleryListColumnKey key) => _tokens.Contains(key.ToString());

    /// <summary>Kategorie-Spalten sind sichtbar, solange sie nicht ausdrücklich ausgeblendet wurden.</summary>
    public bool IsCategoryVisible(int categoryId) => !_hiddenCategories.Contains(categoryId);

    public static string CategoryToken(int categoryId) => CategoryPrefix + categoryId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParseCategoryToken(string token, out int categoryId)
    {
        categoryId = 0;
        return token.StartsWith(CategoryPrefix, System.StringComparison.OrdinalIgnoreCase)
               && int.TryParse(token.AsSpan(CategoryPrefix.Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out categoryId)
               && categoryId > 0;
    }

    public static GalleryListColumnSettings Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        var tokens = new List<string>();
        var hidden = new HashSet<int>();
        foreach (var token in value.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith('!') && TryParseCategoryToken(token[1..], out var hiddenId))
            {
                hidden.Add(hiddenId);
            }
            else if (TryParseCategoryToken(token, out var categoryId))
            {
                var canonical = CategoryToken(categoryId);
                if (!tokens.Contains(canonical))
                {
                    tokens.Add(canonical);
                }
            }
            else if (System.Enum.TryParse<GalleryListColumnKey>(token, ignoreCase: true, out var key) && !tokens.Contains(key.ToString()))
            {
                tokens.Add(key.ToString());
            }
        }

        if (!tokens.Contains(nameof(GalleryListColumnKey.Name)))
        {
            tokens.Insert(0, nameof(GalleryListColumnKey.Name));
        }

        tokens.RemoveAll(t => TryParseCategoryToken(t, out var id) && hidden.Contains(id));
        return new GalleryListColumnSettings(tokens, hidden);
    }

    public string Serialize()
        => string.Join(",", _tokens.Concat(_hiddenCategories.Order().Select(id => "!" + CategoryToken(id))));

    /// <summary>Blendet eine feste Spalte ein (ans Ende) oder aus; „Name“ lässt sich nicht ausblenden.</summary>
    public GalleryListColumnSettings WithVisibility(GalleryListColumnKey key, bool visible)
    {
        var tokens = new List<string>(_tokens);
        var token = key.ToString();
        if (visible)
        {
            if (!tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }
        else if (key != GalleryListColumnKey.Name)
        {
            tokens.Remove(token);
        }

        return new GalleryListColumnSettings(tokens, new HashSet<int>(_hiddenCategories));
    }

    /// <summary>Blendet die Spalte einer Kategorie ein (ans Ende) oder aus.</summary>
    public GalleryListColumnSettings WithCategoryVisibility(int categoryId, bool visible)
    {
        var tokens = new List<string>(_tokens);
        var hidden = new HashSet<int>(_hiddenCategories);
        var token = CategoryToken(categoryId);
        if (visible)
        {
            hidden.Remove(categoryId);
            if (!tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }
        else
        {
            hidden.Add(categoryId);
            tokens.Remove(token);
        }

        return new GalleryListColumnSettings(tokens, hidden);
    }

    /// <summary>Neue Reihenfolge der festen Spalten, wie das DataGrid sie nach dem Ziehen anzeigt.</summary>
    public GalleryListColumnSettings WithOrder(IEnumerable<GalleryListColumnKey> orderedVisible)
        => WithTokenOrder(orderedVisible.Select(k => k.ToString()));

    /// <summary>
    /// Neue Reihenfolge aus allen sichtbaren Spalten (feste und Kategorie-Spalten); nicht genannte sichtbare Spalten
    /// folgen dahinter. Sichtbare Kategorie-Spalten ohne Token werden dabei festgeschrieben.
    /// </summary>
    public GalleryListColumnSettings WithTokenOrder(IEnumerable<string> orderedVisible)
    {
        var tokens = new List<string>();
        foreach (var token in orderedVisible)
        {
            var known = _tokens.Contains(token)
                        || (TryParseCategoryToken(token, out var id) && !_hiddenCategories.Contains(id));
            if (known && !tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }

        foreach (var token in _tokens)
        {
            if (!tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return new GalleryListColumnSettings(tokens, new HashSet<int>(_hiddenCategories));
    }

    public override string ToString() => Serialize();
}
