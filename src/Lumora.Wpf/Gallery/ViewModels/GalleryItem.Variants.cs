using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Services.Enhance;

namespace Lumora.Gallery.ViewModels;

/// <summary>Fassungen aus „Foto verbessern“ (ADR-0071): Marke „2 Fassungen“ auf der Kachel; Klick öffnet alle Fassungen in der Lightbox.</summary>
public sealed partial class GalleryItem
{
    /// <summary>Gruppe aus Original und Fassungen (null = keine Fassungen).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVariants), nameof(VariantText), nameof(IsVariant))]
    private PhotoVariantGroup? _variants;

    public bool HasVariants => Variants is { Count: > 1 };

    /// <summary>Diese Datei ist eine verbesserte Fassung (nicht das Original).</summary>
    public bool IsVariant => Variants is { } g && g.OriginalFileId != Id;

    public string VariantText => Variants is { } g ? $"{g.Count} Fassungen" : string.Empty;
}
