using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumora.Gallery.Data;

namespace Lumora.Gallery.FolderRows;

/// <summary>Eine geplante Zeile: Ordner, angezeigter Name, Einrückung (0 oder 1).</summary>
public sealed record FolderRowEntry(int FolderId, string Name, string Path, int Level);

/// <summary>
/// Reihenfolge und Einrückung der Zeilen der Ansicht „Ordner“: jeder Ordner mit eigenen Fotos wird eine Zeile, in
/// Baum-Reihenfolge. Liegt er unter einem Ordner, der selbst eine Zeile hat, steht er eingerückt darunter (Name relativ zu
/// diesem); sonst ungerückt mit dem Pfad relativ zum geöffneten Ordner (z. B. „Urlaub\Tag 1“, wenn „Urlaub“ selbst keine
/// Fotos hat).
/// </summary>
internal static class FolderRowPlan
{
    private static readonly char Separator = System.IO.Path.DirectorySeparatorChar;

    /// <param name="folders">Ordner mit eigenen Fotos, Name = Pfad relativ zum geöffneten Ordner.</param>
    public static IReadOnlyList<FolderRowEntry> Build(IEnumerable<GallerySubfolderDto> folders)
    {
        var result = new List<FolderRowEntry>();
        string? top = null;
        foreach (var folder in folders.OrderBy(f => f.Name, SegmentComparer.Instance))
        {
            if (top is not null && folder.Name.StartsWith(top + Separator, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new FolderRowEntry(folder.Id, folder.Name[(top.Length + 1)..], folder.Path, 1));
            }
            else
            {
                top = folder.Name;
                result.Add(new FolderRowEntry(folder.Id, folder.Name, folder.Path, 0));
            }
        }

        return result;
    }

    /// <summary>Letzter Teil eines relativen Pfads („Urlaub\Tag 1“ → „Tag 1“).</summary>
    public static string LastSegment(string relativePath)
    {
        var index = relativePath.LastIndexOf(Separator);
        return index < 0 ? relativePath : relativePath[(index + 1)..];
    }

    /// <summary>Vergleicht Teil für Teil, damit „Set\Raw“ direkt unter „Set“ steht und nicht hinter „Set 2“.</summary>
    private sealed class SegmentComparer : IComparer<string>
    {
        public static readonly SegmentComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = (x ?? string.Empty).Split(Separator);
            var b = (y ?? string.Empty).Split(Separator);
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
