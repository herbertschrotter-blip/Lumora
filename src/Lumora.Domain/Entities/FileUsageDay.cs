namespace Lumora.Domain.Entities;

/// <summary>
/// Nutzungs-Signale einer Datei an einem Kalendertag (ADR-0072, Tabelle "FileUsageDays", Schlüssel FileId + Day): nur
/// Tagessummen, keine Zeitpunkte einzelner Ansichten. <see cref="Day"/> = Tage seit 1970-01-01 in Ortszeit des Rechners.
/// <see cref="Opens"/>/<see cref="Seconds"/> = gezählte Ansichten in der Lightbox (aktiv, je Ansicht 1 s bis 2 min);
/// <see cref="SoftOpens"/>/<see cref="SoftSeconds"/> = gedämpft gezählte Ansichten (Lightbox aus einer Story, Diashow);
/// <see cref="Zooms"/> = Ansichten mit Zoom; <see cref="Uses"/> = Verwendung (Album, Story, Titelbild);
/// <see cref="Shares"/> = Weitergabe (Drucken, Teilen, Exportieren, Kopieren nach außen). Files-Zeile weg → Zeile weg
/// (ON DELETE CASCADE); „Neu aufbauen“ trägt die Zeilen über <c>UserDataCarryOver.Usage.cs</c> mit.
/// </summary>
public sealed class FileUsageDay
{
    public int FileId { get; set; }

    public int Day { get; set; }

    public int Opens { get; set; }

    public int Seconds { get; set; }

    public int Zooms { get; set; }

    public int SoftOpens { get; set; }

    public int SoftSeconds { get; set; }

    public int Uses { get; set; }

    public int Shares { get; set; }
}
