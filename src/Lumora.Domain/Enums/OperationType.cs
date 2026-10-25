namespace Lumora.Domain.Enums;

/// <summary>
/// Art einer Datei-Operation. V0.1 nur Recycle (Delete-to-Recycle-Bin).
/// Move/Rename ab V0.2, MkDir/Copy in V0.3+.
/// </summary>
public enum OperationType
{
    Recycle = 0,
    Move = 1,
    Copy = 2,
    Rename = 3,
    MkDir = 4,

    /// <summary>Ordner umbenannt (Dateisystem + Folders-Zeilen; eine Operation je enthaltener Datei, ADR-0020).</summary>
    RenameFolder = 5,

    /// <summary>
    /// Ordner in die Bibliothek übernommen (ADR-0034): eine Ordner-Operation (Quelle → Zielordner), MkDir je neu
    /// angelegtem Zielordner, Move je Datei (auch Nicht-Mediendateien), optional Recycle des Quellordners.
    /// </summary>
    AdoptFolder = 6,

    /// <summary>
    /// Aufnahmedatum im Katalog gesetzt, verschoben oder zurückgesetzt (ADR-0046) — ändert keine Datei. Je Datei eine
    /// Operation; die alten Werte stehen in <c>CaptureDateUndo</c>, „Rückgängig“ schreibt sie zurück.
    /// </summary>
    SetCaptureDate = 7,

    /// <summary>
    /// Ort gesetzt oder entfernt (ADR-0050): Katalog + EXIF (JPEG) bzw. XMP-Begleitdatei. Je Datei eine Operation; alte
    /// Werte und alte Bytes stehen in <c>GeoTagUndo</c>, „Rückgängig“ stellt sie wieder her.
    /// </summary>
    SetGeoLocation = 8,

    // ---- ADR-0065: Ordner in den Papierkorb, Fehlende aus der Bibliothek entfernen ----

    /// <summary>
    /// Ordner samt Inhalt in den Windows-Papierkorb (ADR-0065): eine Ordner-Operation (FileId null), dazu eine
    /// Recycle-Operation je katalogisierter Datei darin. Abzüge der Katalogzeilen liegen in <c>DuplicateCleanupUndo</c>
    /// (KeptFileId 0); „Rückgängig“ holt den Ordner zurück und schreibt die Zeilen mit denselben FileIds zurück.
    /// </summary>
    RecycleFolder = 9,

    /// <summary>Ordner endgültig gelöscht, weil das Laufwerk keinen Papierkorb hat und der Benutzer zugestimmt hat (ADR-0065); nicht rückgängig zu machen.</summary>
    DeleteFolder = 10,

    /// <summary>
    /// Fehlende Dateien aus der Bibliothek entfernt (ADR-0065) — nur Katalog, keine Datei wird angefasst. Je Datei eine
    /// Operation; Abzüge in <c>DuplicateCleanupUndo</c> (KeptFileId 0), „Rückgängig“ schreibt sie zurück.
    /// </summary>
    RemoveFromCatalog = 11,

    // ---- ADR-0066: Ordner verschieben, Ausschlüsse beim Einlesen ----

    /// <summary>
    /// Ordner samt Inhalt in einen anderen Ordner derselben Bibliothek verschoben (ADR-0066): eine Ordner-Operation (FileId
    /// null, Quelle → Ziel), dazu eine Move-Operation je katalogisierter Datei darin. FileIds bleiben; „Rückgängig“ verschiebt
    /// den Ordner zurück und stellt Pfade, Elternordner und Wurzel der Folders-Zeilen wieder her.
    /// </summary>
    MoveFolder = 12,

    /// <summary>
    /// Vorhandene Dateien in ausgeschlossenen Ordnern aus der Bibliothek entfernt (ADR-0066) — nur Katalog, keine Datei wird
    /// angefasst. Wie <see cref="RemoveFromCatalog"/>: je Datei eine Operation und ein Abzug in <c>DuplicateCleanupUndo</c>
    /// (KeptFileId 0); „Rückgängig“ schreibt die Zeilen mit denselben FileIds zurück.
    /// </summary>
    ExcludeFromCatalog = 13,

    // ---- ADR-0068: Drehen ----

    /// <summary>
    /// Foto um 90° gedreht (ADR-0068): EXIF-Ausrichtung im JPEG neu gesetzt (nur das APP1-Segment, wie beim GPS-Schreiben),
    /// Maße und Gesichtsrahmen im Katalog mitgedreht. Je Datei eine Operation; alte Bytes stehen in <c>GeoTagUndo</c> (gleicher
    /// Rückgängig-Speicher wie „Ort setzen“), „Rückgängig“ stellt Datei, Maße und Gesichtsrahmen wieder her.
    /// </summary>
    Rotate = 14,

    // ---- ADR-0071: Foto verbessern ----

    /// <summary>
    /// Verbesserte Fassung als neue Datei angelegt (ADR-0071): je Fassung eine Operation (SourcePath = Original, DestPath = neue
    /// Datei, FileId = neue Files-Zeile). Das Original wird nie angefasst. „Rückgängig“ legt die neue Datei in den Papierkorb und
    /// entfernt ihre Katalogzeile samt Verknüpfung.
    /// </summary>
    CreateVariant = 15,
}
