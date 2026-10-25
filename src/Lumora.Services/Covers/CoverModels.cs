namespace Lumora.Services.Covers;

/// <summary>Wofür ein Foto Titelbild sein kann (ADR-0068).</summary>
public enum CoverTargetKind
{
    Person = 0,
    Album = 1,
    Story = 2,
    Folder = 3,
}

/// <summary>
/// Ein Ziel im Menü „Als Titelbild für ▸“: eine bestätigte Person im Foto (mit DEM Gesicht der Person in diesem Foto), ein
/// Album bzw. eine Story, in der das Foto liegt, oder sein Ordner.
/// </summary>
/// <param name="Name">Anzeigename (Person, Album, Story-Titel, Ordnername).</param>
/// <param name="IsCurrent">Dieses Foto ist schon das Titelbild (selbst gewählt oder automatisch) — Häkchen im Menü.</param>
/// <param name="IsManual">Das Ziel hat ein selbst gewähltes Titelbild (dann gibt es „Wieder automatisch wählen lassen“).</param>
/// <param name="FaceId">Nur Personen: Gesicht der Person in diesem Foto.</param>
public sealed record CoverTarget(CoverTargetKind Kind, int Id, string Name, bool IsCurrent, bool IsManual, long? FaceId = null);

/// <summary>Titelbild des Ordners: wirksame Datei und ob sie selbst gewählt ist.</summary>
public sealed record FolderCoverInfo(int FolderId, int? FileId, bool IsManual);

/// <summary>
/// Ein Titelbild wurde gesetzt oder auf „automatisch“ zurückgestellt (Ordnerleiste, Menüs). Personen, Alben und Storys senden
/// zusätzlich ihre eigenen Nachrichten.
/// </summary>
public sealed record CoverChangedMessage(CoverTargetKind Kind, int Id);
