namespace Lumora.Gallery.Data;

/// <summary>„Beste Fassung zeigen“ (ADR-0071); eigener Teil des Records.</summary>
public sealed partial record GalleryQuery
{
    /// <summary>Originale mit verbesserter Fassung ausblenden (Einstellungen › Verbessern › „beste Fassung zeigen“).</summary>
    public bool HideVariantOriginals { get; init; }
}

/// <summary>
/// „Beste Fassung zeigen“ in der Galerie-SQL (ADR-0071): ein Original verschwindet, wenn es eine vorhandene (nicht fehlende)
/// Fassung hat — im Ordnerfilter nur, wenn die Fassung im selben Ordner liegt (sonst wären beide unsichtbar). Das Original bleibt
/// über die Marke „2 Fassungen“ der Fassung erreichbar. Über den Index <c>IX_PhotoVariants_Original</c>.
/// </summary>
public sealed partial class GalleryPageSource
{
    private const string HideOriginalsClause =
        "AND NOT EXISTS (SELECT 1 FROM PhotoVariants pv JOIN Files vf ON vf.Id = pv.FileId WHERE pv.OriginalFileId = f.Id AND vf.Missing = 0)";

    private const string HideOriginalsInFolderClause =
        "AND NOT EXISTS (SELECT 1 FROM PhotoVariants pv JOIN Files vf ON vf.Id = pv.FileId WHERE pv.OriginalFileId = f.Id AND vf.Missing = 0 AND vf.FolderId = f.FolderId)";

    /// <summary>Zusatz zum Ordner-/Albumteil (leer, wenn die Einstellung aus ist).</summary>
    private static string VariantClause(GalleryQuery query)
        => !query.HideVariantOriginals ? string.Empty
            : "\n  " + (query.HasFolder ? HideOriginalsInFolderClause : HideOriginalsClause);
}
