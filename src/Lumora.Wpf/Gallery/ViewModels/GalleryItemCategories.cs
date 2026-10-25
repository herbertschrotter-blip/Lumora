using System;
using System.Collections.Generic;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Kategorie-Werte einer Datei (ADR-0027), unveränderlich — <see cref="GalleryItem.Categories"/> wird als Ganzes
/// ersetzt. Der Indexer liefert den Wertnamen je CategoryId (leer ohne Wert), damit die Listenspalten per Binding
/// <c>Categories[5]</c> anzeigen und innerhalb der Page sortieren können, ohne eine Ausnahme bei fehlendem Schlüssel.
/// </summary>
public sealed class GalleryItemCategories
{
    public static GalleryItemCategories Empty { get; } = new(new Dictionary<int, (int, string)>());

    private readonly IReadOnlyDictionary<int, (int ValueId, string Name)> _values;

    /// <param name="labels">Gesetzte Werte als „Kategorie: Wert“ in der Reihenfolge der Kategorien (Marke und Tooltip auf der
    /// Kachel, Mockup 16); null = keine Beschriftungen (dann zählt nur <see cref="Count"/>).</param>
    public GalleryItemCategories(IReadOnlyDictionary<int, (int ValueId, string Name)> values, IReadOnlyList<string>? labels = null)
    {
        _values = values ?? throw new ArgumentNullException(nameof(values));
        Labels = labels ?? Array.Empty<string>();
    }

    /// <summary>„Kategorie: Wert“ je gesetzter Kategorie in Anzeigereihenfolge (Tooltip der Kategorie-Marke).</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>Wertname der Kategorie oder leer.</summary>
    public string this[int categoryId] => _values.TryGetValue(categoryId, out var v) ? v.Name : string.Empty;

    /// <summary>ValueId der Kategorie oder null.</summary>
    public int? ValueIdOf(int categoryId) => _values.TryGetValue(categoryId, out var v) ? v.ValueId : null;

    public int Count => _values.Count;

    /// <summary>CategoryId → ValueId (für Neuabbildung nach Umbenennungen).</summary>
    public IEnumerable<KeyValuePair<int, int>> ValueIds
    {
        get
        {
            foreach (var (categoryId, value) in _values)
            {
                yield return new KeyValuePair<int, int>(categoryId, value.ValueId);
            }
        }
    }
}
