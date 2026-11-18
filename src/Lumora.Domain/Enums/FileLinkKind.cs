namespace Lumora.Domain.Enums;

/// <summary>Art einer Verknüpfung einer Datei (ADR-0054): in ein Album oder in einen Ordner; seit ADR-0058 auch manuelle Story-Einträge.</summary>
public enum FileLinkKind
{
    Album = 0,
    Folder = 1,

    /// <summary>Manuell zu einer Story hinzugefügt (ADR-0058, <c>StoryItems.Origin</c> = 1).</summary>
    StoryAdded = 2,

    /// <summary>Manuell aus einer Story entfernt (Ausschluss, <c>StoryItems.Origin</c> = −1).</summary>
    StoryExcluded = 3,

    /// <summary>Die Datei war eine verbesserte Fassung (ADR-0071); TargetId = FileId des Originals.</summary>
    VariantOf = 4,

    /// <summary>Die Datei war das Original einer Fassung (ADR-0071); TargetId = FileId der Fassung.</summary>
    HasVariant = 5,

    /// <summary>
    /// Die Datei war ein zusammengefügtes Ergebnis (ADR-0078); TargetId = FileId einer Quelle. Je Quelle eine Zeile; die Zeile
    /// mit der kleinsten Id trägt in <c>AddedAtUtc</c> den Zeitpunkt des Ergebnisses. Verfahren und Rezept werden über die
    /// Quelle mit Position 0 nicht gesichert — wiederhergestellt wird die Verknüpfung als „restored“.
    /// </summary>
    MergedFrom = 6,

    /// <summary>Die Datei war Quelle eines zusammengefügten Ergebnisses (ADR-0078); TargetId = FileId des Ergebnisses.</summary>
    MergeSourceOf = 7,
}
