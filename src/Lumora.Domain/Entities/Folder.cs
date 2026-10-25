namespace Lumora.Domain.Entities;

/// <summary>
/// Ein Ordner innerhalb einer Library. Hierarchie via ParentId (NULL = Top-Level, d. h. ein Wurzelordner).
/// <see cref="RootId"/> nennt die Wurzel (<see cref="LibraryRoot"/>), unter der der Ordner liegt (ADR-0020);
/// logischer Fremdschlüssel ohne DB-Constraint wie alle Verweise des Schemas (PLAN Kap. 4).
/// UNIQUE(LibraryId, Path) wird über Index erzwungen.
/// </summary>
public sealed class Folder
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public int? RootId { get; set; }
    public required string Path { get; set; }
    public int? ParentId { get; set; }
    public required string Name { get; set; }
    public DateTime ScannedAt { get; set; }

    /// <summary>
    /// Selbst gewähltes Titelbild des Ordners (ADR-0068, Migration AddFolderCover); null = Lumora wählt automatisch (beste
    /// Foto-Güte). Bewusst OHNE Fremdschlüssel (ADD COLUMN ohne Tabellen-Neuaufbau, wie <c>Persons.CoverFaceId</c>): Leser
    /// zählen den Wert nur, solange die Datei noch in diesem Ordner liegt — sonst gilt wieder „automatisch“.
    /// </summary>
    public int? CoverFileId { get; set; }
}
