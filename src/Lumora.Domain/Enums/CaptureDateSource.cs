namespace Lumora.Domain.Enums;

/// <summary>
/// Herkunft des Aufnahmedatums einer Datei (<c>Files.CapturedAtSource</c>, ADR-0046). <c>NULL</c> in der Spalte heißt
/// „noch nicht gelesen“ — das Nachtragen im Hintergrund liest diese Dateien. Rangfolge beim Lesen: EXIF bzw.
/// Video-Container vor Dateiname; manuell gesetzte Daten werden nie überschrieben (auch nicht von „Neu aufbauen“).
/// </summary>
public enum CaptureDateSource
{
    /// <summary>Gelesen, aber weder Metadaten noch Dateiname ergaben ein Datum (Sortierung fällt auf das Änderungsdatum zurück).</summary>
    Unknown = 0,

    /// <summary>EXIF <c>DateTimeOriginal</c> (JPEG, TIFF/RAW, HEIC/AVIF, PNG <c>eXIf</c>, WebP <c>EXIF</c>).</summary>
    Exif = 1,

    /// <summary>Erstellzeit aus dem Video-Container (MP4/MOV: <c>mvhd</c> bzw. Apple <c>creationdate</c>).</summary>
    Video = 2,

    /// <summary>Datum aus dem Dateinamen (IMG_20240712_153012, PXL_…, Screenshot …, IMG-20240712-WA0001 …).</summary>
    FileName = 3,

    /// <summary>Vom Benutzer gesetzt („Datum setzen…“) — genau oder ungefähr (siehe <see cref="CaptureDatePrecision"/>).</summary>
    Manual = 4,

    /// <summary>
    /// Vorläufig aus dem Eigenschaftsspeicher von Windows, solange die Datei nur online liegt (OneDrive-Platzhalter,
    /// ADR-0077): Windows liefert UTC ohne den Zeitversatz der Aufnahme (<c>OffsetTimeOriginal</c>), Lumora rechnet mit der
    /// Zeitzone dieses PCs. Sobald die Datei lokal ist, liest das Nachtragen das Datum aus der Datei selbst.
    /// </summary>
    Cloud = 5,
}
