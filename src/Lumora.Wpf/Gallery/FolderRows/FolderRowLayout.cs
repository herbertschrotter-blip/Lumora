using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumora.Gallery.FolderRows;

/// <summary>Rand-Foto einer Ordner-Zeile (Schild „erstes“ / „letztes“).</summary>
public enum FolderRowEdge
{
    None,
    First,
    Last,
}

/// <summary>Kandidat einer Zeile: Datei, Stelle im Ordner (Aufnahme-Reihenfolge, 0 = erstes) und Seitenverhältnis.</summary>
public sealed record FolderRowCandidate(int Id, int Position, double Aspect);

/// <summary>Platz eines Fotos in der Zeile: Breite in DIP bei der Zeilenhöhe.</summary>
public sealed record FolderRowSlot(int Id, double Width, FolderRowEdge Edge);

/// <summary>Auswahl einer Zeile: erstes und letztes Foto, dazwischen Kandidaten in zufälliger Reihenfolge.</summary>
public sealed record FolderRowPick(int? First, int? Last, IReadOnlyList<int> Middle)
{
    public static FolderRowPick Empty { get; } = new(null, null, Array.Empty<int>());

    /// <summary>Alle Ids, die geladen werden müssen.</summary>
    public IEnumerable<int> All => new[] { First, Last }.OfType<int>().Concat(Middle);
}

/// <summary>
/// Ansicht „Ordner“ (Mockup 21): Je Unterordner eine Zeile mit so vielen Fotos, wie in die Breite passen — erstes und
/// letztes Foto des Ordners immer außen, dazwischen zufällige in Aufnahme-Reihenfolge. Reine Rechnung ohne WPF:
/// <see cref="Pick"/> wählt die Kandidaten (fester Startwert → gleiche Auswahl bis „Neu mischen“), <see cref="Fit"/>
/// nimmt in zufälliger Reihenfolge so viele, wie passen, und gleicht die Zeile auf volle Breite aus (wie die Collage).
/// Passen alle Fotos des Ordners hinein, bleiben sie natürlich breit.
/// </summary>
public static class FolderRowLayout
{
    /// <summary>Abstand zwischen zwei Fotos (DIP).</summary>
    public const double Spacing = 4;

    /// <summary>Grenzen des Seitenverhältnisses je Foto (Panoramen und schmale Streifen nicht übertreiben).</summary>
    public const double MinAspect = 0.4;
    public const double MaxAspect = 3.0;

    /// <param name="idsAscending">Fotos des Ordners in Aufnahme-Reihenfolge (ältestes zuerst).</param>
    /// <param name="maxMiddle">Höchstens so viele Kandidaten zwischen erstem und letztem.</param>
    public static FolderRowPick Pick(IReadOnlyList<int> idsAscending, int maxMiddle, int seed)
    {
        ArgumentNullException.ThrowIfNull(idsAscending);
        var count = idsAscending.Count;
        if (count == 0)
        {
            return FolderRowPick.Empty;
        }

        if (count == 1)
        {
            return new FolderRowPick(idsAscending[0], null, Array.Empty<int>());
        }

        // Teil-Fisher-Yates über die Stellen 1 … count-2: die ersten maxMiddle gemischten sind die Kandidaten
        var inner = Enumerable.Range(1, count - 2).ToArray();
        var take = Math.Clamp(maxMiddle, 0, inner.Length);
        var random = new Random(seed);
        for (var i = 0; i < take; i++)
        {
            var j = random.Next(i, inner.Length);
            (inner[i], inner[j]) = (inner[j], inner[i]);
        }

        return new FolderRowPick(idsAscending[0], idsAscending[^1], inner.Take(take).Select(p => idsAscending[p]).ToArray());
    }

    /// <summary>Plätze der Zeile in Anzeige-Reihenfolge (erstes, zufällige nach Stelle sortiert, letztes).</summary>
    /// <param name="middleRandom">Kandidaten in zufälliger Reihenfolge (wie aus <see cref="Pick"/>).</param>
    /// <param name="totalCount">Fotos im Ordner — passen alle hinein, wird nicht auf volle Breite gestreckt.</param>
    public static IReadOnlyList<FolderRowSlot> Fit(
        FolderRowCandidate? first,
        FolderRowCandidate? last,
        IReadOnlyList<FolderRowCandidate> middleRandom,
        int totalCount,
        double width,
        double height)
    {
        ArgumentNullException.ThrowIfNull(middleRandom);
        if (first is null || width <= 0 || height <= 0)
        {
            return Array.Empty<FolderRowSlot>();
        }

        double Natural(FolderRowCandidate c) => Math.Clamp(double.IsFinite(c.Aspect) && c.Aspect > 0 ? c.Aspect : 4.0 / 3, MinAspect, MaxAspect) * height;

        var used = Natural(first) + (last is null ? 0 : Spacing + Natural(last));
        var chosen = new List<FolderRowCandidate>();
        foreach (var candidate in middleRandom)
        {
            var w = Spacing + Natural(candidate);
            if (used + w <= width)
            {
                chosen.Add(candidate);
                used += w;
            }
        }

        var ordered = new List<(FolderRowCandidate C, FolderRowEdge Edge)> { (first, FolderRowEdge.First) };
        ordered.AddRange(chosen.OrderBy(c => c.Position).Select(c => (c, FolderRowEdge.None)));
        if (last is not null)
        {
            ordered.Add((last, FolderRowEdge.Last));
        }

        var gaps = Spacing * (ordered.Count - 1);
        var natural = ordered.Sum(o => Natural(o.C));
        var allShown = ordered.Count >= totalCount;
        // Ausgleichen: Rest der Breite gleichmäßig verteilen; alle gezeigt → nur verkleinern, falls es nicht passt
        var scale = allShown && natural + gaps <= width ? 1 : Math.Max(0, width - gaps) / natural;
        return ordered.Select(o => new FolderRowSlot(o.C.Id, Natural(o.C) * scale, o.Edge)).ToArray();
    }
}
