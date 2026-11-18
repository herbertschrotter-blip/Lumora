using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Imaging.Merge;
using Lumora.Services.Merge;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Ergebnis von „Zusammenfügen“ (ADR-0078 Punkt 9/10): Marke „Panorama · aus 26 Fotos“ auf der Kachel; Klick öffnet Ergebnis und
/// Quellen in der Lightbox (Ergebnis zuerst). Gebündelt je Seite nachgetragen wie „2 Fassungen“; die Kachel bleibt ein
/// Automation-Blatt (Knopf in der Kachel-Vorlage).
/// </summary>
public sealed partial class GalleryItem
{
    /// <summary>Marke des Ergebnisses (null = kein zusammengefügtes Ergebnis).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMergeResult), nameof(MergeMarkText), nameof(MergeMarkToolTip))]
    private MergedPhotoMark? _mergeMark;

    public bool IsMergeResult => MergeMark is not null;

    public string MergeMarkText => MergeMark is { } m ? MergeTexts.ResultMark(m.Method, m.SourceCount) : string.Empty;

    public string MergeMarkToolTip => MergeMark is { } m
        ? MergeTexts.MadeFrom(m.SourceCount) + (m.PresentSources < m.SourceCount ? $" ({m.SourceCount - m.PresentSources} nicht mehr in der Bibliothek)" : string.Empty) + " — Ergebnis und Quellen öffnen"
        : string.Empty;
}
