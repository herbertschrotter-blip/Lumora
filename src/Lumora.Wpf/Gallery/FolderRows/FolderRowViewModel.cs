using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;

namespace Lumora.Gallery.FolderRows;

/// <summary>Ein Foto in einer Ordner-Zeile: Breite bei der Zeilenhöhe, Schild „erstes“/„letztes“.</summary>
public sealed class FolderRowTile(FolderRowViewModel row, GalleryItem item, double width, FolderRowEdge edge)
{
    public FolderRowViewModel Row { get; } = row;

    public GalleryItem Item { get; } = item;

    public double Width { get; } = width;

    public bool IsFirst => edge == FolderRowEdge.First;

    public bool IsLast => edge == FolderRowEdge.Last;
}

/// <summary>
/// Eine Zeile der Ansicht „Ordner“ (Mockup 21): ein Ordner mit eigenen Fotos — nur die direkt darin, nie die seiner
/// Unterordner — mit Kopf (Name, Zeitraum, Anzahl) und den Fotos, die in die Breite passen. Die Kandidaten (erstes, letztes, zufällige) bleiben bis „Andere Fotos zeigen“ bzw.
/// „Neu mischen“; bei geänderter Breite oder Zeilenhöhe wird nur neu verteilt.
/// </summary>
public sealed partial class FolderRowViewModel : ObservableObject
{
    public FolderRowViewModel(int folderId, string name, string? path, bool isOpenedFolder, int level, GalleryQuery query)
    {
        FolderId = folderId;
        Name = name;
        Path = path;
        IsOpenedFolder = isOpenedFolder;
        Level = level;
        Query = query;
    }

    public int FolderId { get; }

    public string Name { get; }

    public string? Path { get; }

    /// <summary>true nur für die Zeile „Fotos in …“ (Dateien direkt im geöffneten Ordner).</summary>
    public bool IsOpenedFolder { get; }

    /// <summary>0 = Ordner, 1 = eingerückter Unterordner eines Ordners mit eigener Zeile (<see cref="FolderRowPlan"/>).</summary>
    public int Level { get; }

    public bool IsNested => Level > 0;

    /// <summary>Abfrage dieser Zeile (gemeinsamer Galerie-Filter + Ordner).</summary>
    public GalleryQuery Query { get; }

    /// <summary>Fotos des Ordners (mit Filter) in Aufnahme-Reihenfolge.</summary>
    internal IReadOnlyList<int> IdsAscending { get; set; } = [];

    internal Dictionary<int, int> Positions { get; } = new();

    internal GalleryItem? First { get; set; }

    internal GalleryItem? Last { get; set; }

    internal List<GalleryItem> MiddleRandom { get; } = new();

    /// <summary>„Andere Fotos zeigen“ dieser Zeile: zählt hoch, ergibt einen anderen Startwert.</summary>
    internal int Salt { get; set; }

    public ObservableCollection<FolderRowTile> Tiles { get; } = new();

    public int TotalCount => IdsAscending.Count;

    [ObservableProperty] private string _dateText = string.Empty;

    [ObservableProperty] private string _infoText = string.Empty;

    /// <summary>Alle geladenen Kandidaten (für Vorschaubilder und Seitenverhältnisse).</summary>
    internal IEnumerable<GalleryItem> Candidates
    {
        get
        {
            if (First is not null)
            {
                yield return First;
            }

            foreach (var item in MiddleRandom)
            {
                yield return item;
            }

            if (Last is not null)
            {
                yield return Last;
            }
        }
    }
}
