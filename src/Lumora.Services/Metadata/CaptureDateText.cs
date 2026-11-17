using System.Globalization;
using Lumora.Domain.Enums;

namespace Lumora.Services.Metadata;

/// <summary>
/// Anzeigetexte des Aufnahmedatums (Lightbox, Listenspalte, Kachel-Tooltip; ADR-0046): „12.07.2024 15:30 (EXIF)“,
/// „12.07.2024 (Dateiname)“, „ca. Juli 1982 (manuell)“, „ca. 1982 (manuell)“. Deutsch, unabhängig von der
/// Spracheinstellung von Windows (die App ist deutschsprachig).
/// </summary>
public static class CaptureDateText
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Nur das Datum („12.07.2024 15:30“, „ca. 1982“) — ohne Herkunft.</summary>
    public static string FormatDate(CaptureDateValue value)
    {
        var local = value.Local;
        return value.Precision switch
        {
            CaptureDatePrecision.Year => "ca. " + local.Year.ToString(CultureInfo.InvariantCulture),
            CaptureDatePrecision.Month => "ca. " + local.ToString("MMMM yyyy", German),
            CaptureDatePrecision.Day => local.ToString("dd.MM.yyyy", German),
            _ => local.ToString("dd.MM.yyyy HH:mm", German),
        };
    }

    /// <summary>Datum mit Herkunft in Klammern („12.07.2024 15:30 (EXIF)“).</summary>
    public static string Format(CaptureDateValue value) => $"{FormatDate(value)} ({SourceLabel(value.Source)})";

    /// <summary>Kurzname der Herkunft.</summary>
    public static string SourceLabel(CaptureDateSource? source) => source switch
    {
        CaptureDateSource.Exif => "EXIF",
        CaptureDateSource.Video => "Video",
        CaptureDateSource.FileName => "Dateiname",
        CaptureDateSource.Manual => "manuell",
        CaptureDateSource.Cloud => "OneDrive, vorläufig",
        CaptureDateSource.Unknown => "unbekannt",
        _ => "noch nicht gelesen",
    };

    /// <summary>
    /// Text für eine Datei aus den Katalogspalten: mit Datum wie <see cref="Format"/>, ohne Datum „unbekannt“ bzw. „noch
    /// nicht gelesen“ — jeweils mit dem Hinweis, dass die Sortierung dann das Änderungsdatum nimmt.
    /// </summary>
    public static string Describe(CaptureDateValue? value, CaptureDateSource? source)
        => value is { } v
            ? Format(v)
            : source is null
                ? "noch nicht gelesen (sortiert nach Änderungsdatum)"
                : "unbekannt (sortiert nach Änderungsdatum)";
}
