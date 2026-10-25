namespace Lumora.Domain.Entities;

/// <summary>
/// Fassung eines Fotos (ADR-0071, „Foto verbessern“, Tabelle "PhotoVariants", Schlüssel FileId): die Datei
/// <see cref="FileId"/> ist eine verbesserte Fassung des Originals <see cref="OriginalFileId"/> — eine eigene, neue Datei
/// daneben; das Original bleibt immer unverändert. Beide Fremdschlüssel löschen mit (ON DELETE CASCADE): verschwindet eine
/// der beiden Files-Zeilen, ist die Verknüpfung weg und die andere Datei steht wieder allein (Datei-Operationen sichern die
/// Verknüpfung vorher in <see cref="FileLinkUndo"/>, ADR-0054).
/// </summary>
public sealed class PhotoVariant
{
    /// <summary>Die verbesserte Fassung (neue Datei).</summary>
    public int FileId { get; set; }

    /// <summary>Das unveränderte Original.</summary>
    public int OriginalFileId { get; set; }

    /// <summary>Angewandte Werkzeuge als kurze Kennungen mit „+“ („upscale+light“), nur zur Anzeige.</summary>
    public required string Tools { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Protokoll-Batch, der die Fassung angelegt hat (ohne Fremdschlüssel; 0 = unbekannt).</summary>
    public int BatchId { get; set; }
}
