using Lumora.Domain.Enums;

namespace Lumora.Services.Metadata;

/// <summary>Liest das Aufnahmedatum einer Datei (Metadaten, sonst Dateiname) — austauschbar für Tests.</summary>
public interface ICaptureDateReader
{
    /// <summary>
    /// Datum aus den Metadaten (EXIF bzw. Video-Container), sonst aus dem Dateinamen; null = nichts gefunden
    /// (Herkunft <see cref="CaptureDateSource.Unknown"/>). Wirft nicht; die Datei muss lokal vorliegen (der Aufrufer
    /// prüft auf Cloud-Platzhalter, deren Lesen einen Download auslöste).
    /// </summary>
    CaptureDateValue? Read(string path);
}

/// <summary>
/// Rangfolge (ADR-0046): EXIF <c>DateTimeOriginal</c> bzw. Video-Container (<see cref="MediaDateReader"/>) vor dem
/// Dateinamen (<see cref="FileNameDateParser"/>). Archive und andere Formate liefern nur den Dateinamen. Mit
/// <see cref="IFileMetadataProbe"/> teilt es sich den Lesedurchgang mit Orten und Genauigkeit (ADR-0074).
/// </summary>
public sealed class CaptureDateReader(IFileMetadataProbe? probe = null) : ICaptureDateReader
{
    public CaptureDateValue? Read(string path)
        => (probe is null ? MediaDateReader.Read(path) : probe.Read(path).MediaDate) ?? FileNameDateParser.Parse(Path.GetFileName(path));
}
