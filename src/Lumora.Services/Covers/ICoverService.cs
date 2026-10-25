namespace Lumora.Services.Covers;

/// <summary>
/// „Als Titelbild für ▸“ (ADR-0068, Mockup 17): welche Ziele zu einem Foto passen, Titelbild setzen, wieder automatisch
/// wählen lassen, und das Titelbild eines Ordners (neu, <c>Folders.CoverFileId</c>). Die eigene Wahl gewinnt immer und wird nie
/// automatisch ersetzt; ohne eigene Wahl wählt Lumora: Person = bestes Gesicht (<see cref="FaceCoverScoring"/>), Album/Story/
/// Ordner = höchste Foto-Güte, bevorzugt mit Personen (<c>IHighlightSelector.PickCoverAsync</c>, ADR-0057). Alles nur im
/// aktuellen Bereich (ADR-0048); die versteckte Sitzung hat ihren eigenen Katalog (ADR-0049).
/// </summary>
public interface ICoverService
{
    /// <summary>Ziele für das Foto: bestätigte Personen darin, Alben und Storys, in denen es liegt, sein Ordner.</summary>
    Task<IReadOnlyList<CoverTarget>> GetTargetsAsync(int fileId, CancellationToken ct = default);

    /// <summary>Setzt das Foto als Titelbild des Ziels (Person: das Gesicht <see cref="CoverTarget.FaceId"/>).</summary>
    Task SetAsync(CoverTarget target, int fileId, CancellationToken ct = default);

    /// <summary>Selbst gewähltes Titelbild lösen — Lumora wählt wieder automatisch.</summary>
    Task ResetAsync(CoverTargetKind kind, int id, CancellationToken ct = default);

    /// <summary>Wirksames Titelbild eines Ordners (selbst gewählt, solange die Datei noch darin liegt, sonst automatisch).</summary>
    Task<FolderCoverInfo> GetFolderCoverAsync(int folderId, CancellationToken ct = default);
}
