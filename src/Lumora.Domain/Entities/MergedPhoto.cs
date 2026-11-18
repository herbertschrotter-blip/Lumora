using Lumora.Domain.Enums;

namespace Lumora.Domain.Entities;

/// <summary>
/// Ergebnis von „Zusammenfügen“ (ADR-0078 Punkt 10/11, Tabelle "MergedPhotos", Schlüssel FileId): die Datei <see cref="FileId"/>
/// ist aus mehreren Einzelbildern entstanden (<see cref="MergedPhotoSource"/>). Eine eigene, neue Datei neben dem ersten
/// Einzelbild; die Einzelbilder bleiben unverändert. Verschwindet die Files-Zeile des Ergebnisses, gehen Zeile und Quellen per
/// Kaskade mit (Datei-Operationen sichern sie vorher in <see cref="FileLinkUndo"/>, ADR-0054).
/// </summary>
public sealed class MergedPhoto
{
    /// <summary>Das Ergebnis (neue Datei).</summary>
    public int FileId { get; set; }

    public MergeMethod Method { get; set; }

    /// <summary>Aufnahmeart bei Panoramen, sonst null.</summary>
    public PanoramaProjection? Projection { get; set; }

    /// <summary>Zahl der Quellen beim Anlegen — bleibt stehen, wenn eine Quelle später verschwindet („2 nicht mehr vorhanden“).</summary>
    public int SourceCount { get; set; }

    /// <summary>Erkannte Serie (ADR-0078 Punkt 4, Stufe 2) ohne Fremdschlüssel; null = freie Auswahl.</summary>
    public int? SeriesId { get; set; }

    /// <summary>Kurzkennung der Werkzeuge („merge:panorama“), nur zur Anzeige.</summary>
    public required string Tools { get; set; }

    /// <summary>Rezept (JSON: Verfahren, Optionen, Ausrichtung) für „Neu berechnen…“ und volle Auflösung.</summary>
    public required string RecipeJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Protokoll-Batch, der das Ergebnis angelegt hat (ohne Fremdschlüssel; 0 = unbekannt).</summary>
    public int BatchId { get; set; }
}

/// <summary>
/// Gesicherte <see cref="MergedPhoto"/>-Zeile eines entfernten Ergebnisses (Tabelle "MergedPhotoUndo", Schlüssel
/// <see cref="FileOperationId"/>, ADR-0078 Nachtrag 0.5.46): die Quellen sichert <see cref="FileLinkUndo"/> (Art 6), Verfahren,
/// Aufnahmeart und Rezept diese Zeile — „Rückgängig“ bzw. die Rückkehr der Datei stellt beides wieder her.
/// </summary>
public sealed class MergedPhotoUndo
{
    public int FileOperationId { get; set; }

    public MergeMethod Method { get; set; }

    public PanoramaProjection? Projection { get; set; }

    public int SourceCount { get; set; }

    public int? SeriesId { get; set; }

    public required string Tools { get; set; }

    public required string RecipeJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int BatchId { get; set; }
}

/// <summary>Eine Quelle eines Ergebnisses (Tabelle "MergedPhotoSources"); <see cref="Position"/> 0 = erstes Einzelbild.</summary>
public sealed class MergedPhotoSource
{
    public int ResultFileId { get; set; }

    public int SourceFileId { get; set; }

    public int Position { get; set; }
}
