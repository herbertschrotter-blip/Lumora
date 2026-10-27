using Lumora.Services.Faces;

namespace Lumora.Services.Enhance;

/// <summary>
/// „Automatisch“ (ADR-0071, Mockup 13): wählt je Foto die Werkzeuge nach einfachen, nachvollziehbaren Regeln — und nur mit
/// geladenen Modellen (fehlende werden als Hinweis genannt, nie still geladen):
/// <list type="bullet">
/// <item>ohne Farbe → Einfärben; zusätzlich Kratzer und Flecken (gilt als Scan),</item>
/// <item>Gesichter mit kleinem Augenabstand (&lt; 80 Pixel) → Gesichter restaurieren (Stärke 0,7),</item>
/// <item>längste Kante &lt; 2000 Pixel → Hochskalieren (&lt; 900 Pixel 4×, sonst 2×).</item>
/// </list>
/// </summary>
public static class EnhanceAutoPlanner
{
    public const int SmallImageEdge = 2000;
    public const int TinyImageEdge = 900;
    public const float SmallEyeDistance = 80f;

    /// <summary>Ergebnis: die Optionen für dieses Foto und Hinweise (fehlende Modelle, nichts zu tun).</summary>
    public sealed record Plan(EnhanceOptions Options, IReadOnlyList<string> Notes)
    {
        public bool HasWork => !Options.IsEmpty;
    }

    /// <param name="base">Gewählte Optionen (Licht, Drehen, Zuschnitt, Pinsel bleiben erhalten).</param>
    /// <param name="ready">Werkzeuge, deren Modell bereit ist.</param>
    /// <param name="faces">Gefundene Gesichter (null = Gesichtserkennung nicht verfügbar).</param>
    public static Plan Decide(EnhanceOptions @base, EnhanceImage image, IReadOnlySet<EnhanceTool> ready, IReadOnlyList<FacePoint[]>? faces)
    {
        var notes = new List<string>();
        var options = @base with { Auto = false };
        if (EnhanceImageOps.IsGrayscale(image))
        {
            if (ready.Contains(EnhanceTool.Colorize))
            {
                options = options with { Colorize = true };
            }
            else
            {
                notes.Add("Foto ohne Farbe — zum Einfärben das Modell laden.");
            }

            if (ready.Contains(EnhanceTool.Erase))
            {
                options = options with { Erase = true, Scratches = true };
            }
        }

        if (faces is { Count: > 0 } && faces.Any(f => EyeDistance(f) < SmallEyeDistance))
        {
            if (ready.Contains(EnhanceTool.Faces))
            {
                options = options with { Faces = true, FaceStrength = Math.Min(options.FaceStrength, 0.7) };
            }
            else
            {
                notes.Add("Kleine Gesichter erkannt — zum Restaurieren das Modell laden.");
            }
        }

        var edge = Math.Max(image.Width, image.Height);
        if (edge < SmallImageEdge)
        {
            if (EnhanceUpscaleModels.Engines.Any(ready.Contains))
            {
                options = options with { Upscale = true, UpscaleFactor = edge < TinyImageEdge ? 4 : 2 };
            }
            else
            {
                notes.Add("Kleines Bild — zum Hochskalieren das Modell laden.");
            }
        }

        if (options.IsEmpty)
        {
            notes.Add("Automatisch: nichts zu verbessern.");
        }

        return new Plan(options, notes);
    }

    /// <summary>Abstand der Augen (Landmarken 0 und 1) in Pixeln.</summary>
    public static float EyeDistance(FacePoint[] landmarks)
        => landmarks.Length < 2 ? 0 : MathF.Sqrt(MathF.Pow(landmarks[1].X - landmarks[0].X, 2) + MathF.Pow(landmarks[1].Y - landmarks[0].Y, 2));
}
