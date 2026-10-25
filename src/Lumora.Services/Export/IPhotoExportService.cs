namespace Lumora.Services.Export;

/// <summary>
/// Liest ein Bild, dreht es nach EXIF, verkleinert es auf die längste Kante und schreibt ein JPEG (ADR-0068). WIC-basiert,
/// deshalb im Wpf-Projekt registriert. Wirft bei unlesbarem Bild oder Schreibfehler.
/// </summary>
public interface IPhotoExportEncoder
{
    void EncodeJpeg(string sourcePath, string targetPath, int maxLongEdge, int jpegQuality);
}

/// <summary>
/// Wege nach außen (ADR-0068): <see cref="GetExternalFilesAsync"/> liefert die Dateien für Ziehen/Strg+C (nur vorhandene, keine
/// Archive, gesperrt in der versteckten Bibliothek), <see cref="ExportAsync"/> schreibt verkleinerte JPEG-Kopien in einen Ordner
/// außerhalb — ohne Katalog, als Vorgang der Aktivitäts-Anzeige (ADR-0067) mit Abbrechen.
/// </summary>
public interface IPhotoExportService
{
    /// <summary>true in der versteckten Bibliothek: kein Weg nach außen.</summary>
    bool IsBlocked { get; }

    Task<ExternalFileSet> GetExternalFilesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);

    Task<PhotoExportResult> ExportAsync(IReadOnlyList<int> fileIds, string targetDirectory, PhotoExportOptions options, CancellationToken ct = default);
}
