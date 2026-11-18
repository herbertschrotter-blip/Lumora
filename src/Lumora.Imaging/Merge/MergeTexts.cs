using System.Globalization;
using Lumora.Domain.Enums;
using Lumora.Imaging.Protocol;

namespace Lumora.Imaging.Merge;

/// <summary>Texte zu Codes des Hilfsprozesses (Schritte, Hinweise, Prüfung, Fehler) — der Hilfsprozess kennt keine Oberfläche.</summary>
public static class MergeTexts
{
    public static string Step(WorkerStep step) => step switch
    {
        WorkerStep.Load => "Fotos lesen",
        WorkerStep.Features => "Merkmale suchen",
        WorkerStep.Match => "Fotos abgleichen",
        WorkerStep.Bundle => "Ausrichten",
        WorkerStep.Exposure => "Belichtung ausgleichen",
        WorkerStep.Seams => "Nähte suchen",
        WorkerStep.Deghost => "Nähte angleichen",
        WorkerStep.Warp => "Verzerren",
        WorkerStep.Blend => "Überblenden",
        WorkerStep.Finish => "Zuschneiden",
        WorkerStep.Write => "Speichern",
        _ => "Rechnen",
    };

    public static string Method(MergeMethod method) => method switch
    {
        MergeMethod.Panorama => "Panorama",
        MergeMethod.ExposureFusion => "Belichtungsreihe (HDR)",
        MergeMethod.Denoise => "Entrauschen / mehr Details",
        MergeMethod.RemovePeople => "Leute entfernen",
        MergeMethod.LongExposure => "Langzeitbelichtung",
        MergeMethod.FocusStack => "Fokus-Stapel",
        MergeMethod.GroupShot => "Bestes Gruppenfoto",
        MergeMethod.HdrPanorama => "HDR-Panorama",
        MergeMethod.Timelapse => "Zeitraffer",
        _ => method.ToString(),
    };

    public static string Projection(PanoramaProjection projection) => projection switch
    {
        PanoramaProjection.Spherical360 => "Kugel 360°",
        PanoramaProjection.Cylindrical => "Zylinder",
        PanoramaProjection.Perspective => "Perspektive",
        PanoramaProjection.Vertical => "Vertikal",
        PanoramaProjection.ShiftOnly => "Nur verschieben",
        _ => "Automatisch",
    };

    /// <summary>Grund der Prüfung vor dem Speichern als verständlicher Satz.</summary>
    public static MergeNote Problem(string code) => code switch
    {
        "residual" => new(code, "Die Fotos passen nicht genau genug zusammen (großer Restfehler beim Ausrichten)."),
        "tilted" => new(code, "Die Ausrichtung ist gekippt — überlappende Fotos wurden an falscher Stelle eingesetzt."),
        "crop-height" => new(code, "Nach dem Zuschnitt bliebe zu wenig Bildhöhe — die Kette ist wellig oder gekippt."),
        "crop-size" => new(code, "Nach dem Zuschnitt bliebe nur ein kleiner Teil des Panoramas — die Kette ist gekippt."),
        "seams" => new(code, "Die Nähte passen nicht zusammen."),
        "images-dropped" => new(code, "Zu viele Fotos überlappen nicht und konnten nicht eingebunden werden."),
        _ => new(code, "Das Ergebnis hat die Prüfung nicht bestanden."),
    };

    /// <summary>Hinweis-Code des Hilfsprozesses als Text; null für rein technische Codes.</summary>
    public static MergeNote? Note(string code, int fullWidth = 0, int fullHeight = 0, double scale = 1, long fullPeakBytes = 0)
    {
        var name = code.Split(':')[0];
        var value = code.Contains(':', StringComparison.Ordinal) ? code[(code.IndexOf(':') + 1)..] : string.Empty;
        return name switch
        {
            "dropped" => new(code, $"{value} Foto(s) überlappen nicht und wurden nicht eingebunden."),
            "gimbal" => new(code, "Gimbal-Winkel der Drohne dienten als Startwerte."),
            "gimbal-rescued" => new(code, $"{value} Foto(s) ohne Treffer wurden über die Gimbal-Winkel eingesetzt."),
            "focal-estimated" => new(code, "Keine Brennweite im EXIF — aus den Überlappungen geschätzt."),
            "retried" => new(code, "Die Ausrichtung wurde mit einem anderen Startwert wiederholt."),
            "photometric" => new(code, "Belichtung, Weißabgleich und Vignettierung je Foto ausgeglichen."),
            "sphere-not-closed" => new(code, "Die Fotos decken keine ganze Kugel ab — 2 : 1 nicht möglich, Horizontstreifen gerechnet."),
            "shift-unavailable" => new(code, "„Nur verschieben“ ließ sich nicht bestimmen — als Perspektive gerechnet."),
            "size-reduced" when value == "memory" => new(code, ReducedText("der Arbeitsspeicher reicht dafür nicht", fullWidth, fullHeight, scale, fullPeakBytes)),
            "size-reduced" when value == "jpeg-edge" => new(code, ReducedText($"eine Kante wäre länger als {WorkerLimits.MaxJpegEdge:N0} px (JPEG-Grenze)", fullWidth, fullHeight, scale, 0)),
            _ => null,
        };
    }

    private static string ReducedText(string reason, int w, int h, double scale, long peak)
    {
        var full = w > 0 && h > 0 ? string.Create(CultureInfo.GetCultureInfo("de-DE"), $"{w:N0} × {h:N0} px") : "volle Auflösung";
        var mem = peak > 0 ? string.Create(CultureInfo.GetCultureInfo("de-DE"), $" (≈ {peak / 1e9:0.0} GB)") : string.Empty;
        return string.Create(CultureInfo.GetCultureInfo("de-DE"), $"Größte passende Größe: {Math.Round(scale * 100):0} % — volle Auflösung {full}{mem} geht nicht, weil {reason}.");
    }

    /// <summary>Fehler des Hilfsprozesses als verständlicher Satz.</summary>
    public static string Error(WorkerErrorCode? code, string? detail) => code switch
    {
        WorkerErrorCode.SourceUnreadable => "Ein Foto ließ sich nicht lesen (fehlt, gesperrt oder Format ohne Decoder).",
        WorkerErrorCode.NotEnoughOverlap => "Die Fotos überlappen nicht genug — mindestens zwei müssen gemeinsame Bildteile haben (15–70 %).",
        WorkerErrorCode.CheckFailed => "Das Ergebnis hat die Prüfung nicht bestanden.",
        WorkerErrorCode.OutOfMemory => "Zu wenig Arbeitsspeicher — andere Programme schließen oder eine kleinere Größe wählen.",
        WorkerErrorCode.Canceled => "Abgebrochen.",
        WorkerErrorCode.WriteFailed => "Das Ergebnis ließ sich nicht schreiben.",
        WorkerErrorCode.OpenCv => "Die Bildbearbeitung (OpenCV) hat einen Fehler gemeldet.",
        WorkerErrorCode.BadRequest => detail ?? "Die Berechnung passt nicht zur Auswahl.",
        _ => "Die Berechnung ist fehlgeschlagen.",
    };
}
