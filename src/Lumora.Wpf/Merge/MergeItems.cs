using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge;

namespace Lumora.Merge;

/// <summary>Womit das Fenster „Zusammenfügen“ startet (Rechtsklick, Aktionsleiste, Lightbox, „Neu berechnen…“).</summary>
/// <param name="FileIds">Gewählte Fotos (bei einem Foto zeigt das Fenster die Zeit-Nachbarn, noch nicht angehakt).</param>
/// <param name="AlbumId">Gestartet aus einem eigenen Album: „auch ins Album …“ (ADR-0078 Punkt 7).</param>
/// <param name="Options">„Neu berechnen…“: Optionen aus dem gespeicherten Rezept.</param>
/// <param name="RecomputeOf">„Neu berechnen…“: das Ergebnis, dessen Quellen und Rezept gelten (speichert als weitere Datei).</param>
public sealed record MergeStart(
    IReadOnlyList<int> FileIds,
    int? AlbumId = null,
    string? AlbumName = null,
    MergeOptions? Options = null,
    int? SeriesId = null,
    int? RecomputeOf = null);

/// <summary>Ein Verfahren in der Liste links (Stufe 1: nur Panorama aktiv, übrige „kommt später“).</summary>
public sealed partial class MergeMethodItem(MergeMethod method, string name) : ObservableObject
{
    public MergeMethod Method { get; } = method;

    public string Name { get; } = name;

    [ObservableProperty] private bool _isBuilt;
    [ObservableProperty] private bool _suits;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isSuggested;
    [ObservableProperty] private string _reason = string.Empty;

    /// <summary>„passt“, „eher nicht“ oder „kommt später“.</summary>
    [ObservableProperty] private string _fitText = string.Empty;
}

/// <summary>Eine Quelle bzw. ein Zeit-Nachbar im Filmstreifen.</summary>
public sealed partial class MergeSourceItem(int fileId, string name) : ObservableObject
{
    public int FileId { get; } = fileId;

    public string Name { get; } = name;

    /// <summary>Lesbar (lokal) bzw. nach Zustimmung aus OneDrive.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    private bool _isUsable;

    [ObservableProperty] private bool _isChecked;

    /// <summary>Zeit-Nachbar (gestartet mit einem Foto).</summary>
    [ObservableProperty] private bool _isNeighbor;

    /// <summary>Das angeklickte Foto (Bezug).</summary>
    [ObservableProperty] private bool _isReference;

    /// <summary>Unterzeile („+0:09“, „Bezug“).</summary>
    [ObservableProperty] private string _label = string.Empty;

    /// <summary>Marke („nicht eingebunden“, „nur OneDrive“, „fehlt“ …); leer = keine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMark))]
    private string _mark = string.Empty;

    [ObservableProperty] private BitmapSource? _thumbnail;

    public MergeSourceState State { get; set; }

    public DateTime? CapturedAt { get; init; }

    public bool CanCheck => IsUsable;

    public bool HasMark => Mark.Length > 0;

    /// <summary>Tooltip: Name und Marke.</summary>
    public string ToolTip => HasMark ? $"{Name} · {Mark}" : Name;

    partial void OnMarkChanged(string value) => OnPropertyChanged(nameof(ToolTip));
}

/// <summary>Aufnahmeart als Kachel mit Skizze (Mockup 28, Ansicht 5).</summary>
public sealed partial class MergeProjectionItem(PanoramaProjection projection) : ObservableObject
{
    public PanoramaProjection Projection { get; } = projection;

    public string Title => Projection == PanoramaProjection.Auto && Suggested is { } s && s != PanoramaProjection.Auto
        ? $"Automatisch → {MergeTexts.Projection(s)}"
        : MergeTexts.Projection(Projection);

    public string Hint => MergeTexts.ProjectionHint(Projection);

    /// <summary>Skizze der Form (Normalraum 0…1, gestreckt auf die Kachel).</summary>
    public Geometry Sketch { get; } = MergeSketches.For(projection);

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsSuggested))]
    private PanoramaProjection? _suggested;

    /// <summary>Diese Art ist der Vorschlag der Ausrichtung.</summary>
    public bool IsSuggested => Projection != PanoramaProjection.Auto && Suggested == Projection;
}

/// <summary>Größe: volle Auflösung bzw. die größte passende Stufe; ausgegraute Stufen mit Zahl (Abnahme Nr. 11).</summary>
public sealed record MergeSizeItem(string Title, string Detail, bool IsAvailable, bool IsSelected);

/// <summary>Hinweis- bzw. Warnstreifen über der Bühne.</summary>
public sealed record MergeNoteItem(string Text, MergeNoteKind Kind);

public enum MergeNoteKind
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Success = 3,
}

/// <summary>Schritt der Speichern-Liste (Mockup 28, Ansicht 11 „Rechnet“).</summary>
public sealed partial class MergeStepItem(string text) : ObservableObject
{
    public string Text { get; } = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsRunning))]
    private MergeStepState _state;

    [ObservableProperty] private string _detail = string.Empty;

    public bool IsDone => State == MergeStepState.Done;

    public bool IsRunning => State == MergeStepState.Running;
}

public enum MergeStepState
{
    Pending = 0,
    Running = 1,
    Done = 2,
}

/// <summary>Zeile der Fertig-Ansicht bzw. des Info-Abschnitts.</summary>
public sealed record MergeInfoRow(string Label, string Value);

/// <summary>Skizzen der Aufnahmearten (Kacheln rechts): Umriss der Leinwand je Art, wie die Mini-Skizzen in Mockup 28.</summary>
internal static class MergeSketches
{
    public static Geometry For(PanoramaProjection projection)
    {
        var g = projection switch
        {
            PanoramaProjection.Spherical360 => Geometry.Parse("M0,0.25 L1,0.25 L1,0.75 L0,0.75 Z M0,0 L1,0 M0,1 L1,1"),
            PanoramaProjection.Cylindrical => Geometry.Parse("M0,0.3 Q0.25,0.18 0.5,0.3 Q0.75,0.42 1,0.3 L1,0.7 Q0.75,0.82 0.5,0.7 Q0.25,0.58 0,0.7 Z"),
            PanoramaProjection.Perspective => Geometry.Parse("M0,0.1 L0.3,0.28 L0.7,0.28 L1,0.1 L1,0.9 L0.7,0.72 L0.3,0.72 L0,0.9 Z"),
            PanoramaProjection.Vertical => Geometry.Parse("M0.36,0 L0.64,0.02 L0.68,0.34 L0.64,0.66 L0.68,1 L0.34,0.98 L0.32,0.66 L0.36,0.33 Z"),
            PanoramaProjection.ShiftOnly => Geometry.Parse("M0,0.12 L0.4,0.12 L0.4,0.2 L0.7,0.2 L0.7,0.28 L1,0.28 L1,0.88 L0.6,0.88 L0.6,0.8 L0.3,0.8 L0.3,0.72 L0,0.72 Z"),
            _ => Geometry.Parse("M0.2,0.85 L0.5,0.15 L0.8,0.85 M0.32,0.58 L0.68,0.58"),
        };
        g.Freeze();
        return g;
    }
}
