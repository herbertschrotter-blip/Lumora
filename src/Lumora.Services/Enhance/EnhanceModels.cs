using Lumora.Services.Faces;

namespace Lumora.Services.Enhance;

/// <summary>Werkzeuge von „Foto verbessern“ (ADR-0071, Mockup 13) in der Reihenfolge der Leiste.</summary>
public enum EnhanceTool
{
    /// <summary>Lumora wählt je Foto: Einfärben ohne Farbe, Kratzer bei Scans, Gesichter bei kleinen Gesichtern, Hochskalieren bei kleinen Bildern.</summary>
    Auto = 0,

    /// <summary>Hochskalieren und schärfen 2×/4× (Real-ESRGAN).</summary>
    Upscale = 1,

    /// <summary>Gesichter restaurieren mit Stärke (GFPGAN; braucht die Gesichtserkennung zum Finden).</summary>
    Faces = 2,

    /// <summary>Schwarz-Weiß einfärben (DeOldify).</summary>
    Colorize = 3,

    /// <summary>Entfernen mit Pinsel bzw. Kratzer automatisch (LaMa).</summary>
    Erase = 4,

    /// <summary>Zuschneiden, Drehen, Licht (Helligkeit/Kontrast) — ohne Modell.</summary>
    Adjust = 5,
}

/// <summary>Ein Bild als RGB24-Puffer (3 Bytes je Pixel, Zeile für Zeile ohne Auffüllung, EXIF-Ausrichtung angewandt).</summary>
public sealed class EnhanceImage
{
    public EnhanceImage(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length != (long)width * height * 3)
        {
            throw new ArgumentException($"RGB-Puffer passt nicht zu {width}×{height}.", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public long PixelCount => (long)Width * Height;

    public static EnhanceImage Blank(int width, int height) => new(new byte[(long)width * height * 3], width, height);

    public EnhanceImage Clone() => new((byte[])Pixels.Clone(), Width, Height);

    /// <summary>Dieselben Pixel als <see cref="FaceImage"/> (für Erkennung und Ausrichtung; kein Kopieren).</summary>
    public FaceImage AsFaceImage() => new(Pixels, Width, Height);
}

/// <summary>Ein Pinselstrich-Punkt für „Entfernen“: Mitte und Radius relativ zum Bild (0…1; Radius bezogen auf die Breite).</summary>
public readonly record struct EnhanceBrushDot(double X, double Y, double Radius);

/// <summary>Zuschnitt relativ zum (gedrehten) Bild: Links, Oben, Breite, Höhe (0…1).</summary>
public readonly record struct EnhanceCrop(double Left, double Top, double Width, double Height)
{
    public bool IsFull => Left <= 0.0005 && Top <= 0.0005 && Width >= 0.999 && Height >= 0.999;
}

/// <summary>
/// Was mit einem Foto geschehen soll. Werkzeuge mit Modell nur, wenn das Modell geladen ist (sonst <see cref="EnhanceModelMissingException"/>);
/// <see cref="Auto"/> wählt selbst und überspringt fehlende Modelle.
/// </summary>
public sealed record EnhanceOptions
{
    public bool Auto { get; init; }

    public bool Upscale { get; init; }

    /// <summary>2 oder 4.</summary>
    public int UpscaleFactor { get; init; } = 2;

    /// <summary>
    /// Natürlichkeit beim Hochskalieren 0…1 (0.5.6): Anteil des vergrößerten Originals im Ergebnis — gegen die glatte,
    /// künstliche Haut des Modells. null = Wert aus Einstellungen › Verbessern.
    /// </summary>
    public double? Naturalness { get; init; }

    /// <summary>Körnung beim Hochskalieren 0…1 (feines Helligkeitsrauschen, vor allem in Mitteltönen); null = Einstellung.</summary>
    public double? Grain { get; init; }

    public bool Faces { get; init; }

    /// <summary>Stärke 0…1 (Mischung restauriert ↔ Original).</summary>
    public double FaceStrength { get; init; } = 0.8;

    public bool Colorize { get; init; }

    /// <summary>Entfernen: Pinselstriche und/oder Kratzer automatisch.</summary>
    public bool Erase { get; init; }

    public IReadOnlyList<EnhanceBrushDot> BrushDots { get; init; } = [];

    /// <summary>Kratzer und Flecken automatisch finden (eingescannte Fotos).</summary>
    public bool Scratches { get; init; }

    /// <summary>Drehen im Uhrzeigersinn: 0, 90, 180, 270.</summary>
    public int Rotation { get; init; }

    public EnhanceCrop? Crop { get; init; }

    /// <summary>Zuschnitt mittig auf dieses Seitenverhältnis (Breite/Höhe, z. B. 1, 4/3, 16/9); gilt, wenn <see cref="Crop"/> fehlt.</summary>
    public double? CropAspect { get; init; }

    /// <summary>Helligkeit −1…1 (0 = unverändert).</summary>
    public double Brightness { get; init; }

    /// <summary>Kontrast −1…1 (0 = unverändert).</summary>
    public double Contrast { get; init; }

    public bool HasAdjust => Rotation % 360 != 0 || Crop is { IsFull: false } || CropAspect is > 0 || Math.Abs(Brightness) > 0.001 || Math.Abs(Contrast) > 0.001;

    public bool HasErase => Erase && (BrushDots.Count > 0 || Scratches);

    /// <summary>Mindestens ein Werkzeug gewählt.</summary>
    public bool IsEmpty => !Auto && !Upscale && !Faces && !Colorize && !HasErase && !HasAdjust;

    /// <summary>Werkzeuge mit Modell, die diese Optionen ausdrücklich verlangen (ohne Automatisch).</summary>
    public IEnumerable<EnhanceTool> RequiredModelTools()
    {
        if (HasErase)
        {
            yield return EnhanceTool.Erase;
        }

        if (Colorize)
        {
            yield return EnhanceTool.Colorize;
        }

        if (Faces)
        {
            yield return EnhanceTool.Faces;
        }

        if (Upscale)
        {
            yield return EnhanceTool.Upscale;
        }
    }
}

/// <summary>Zustand eines Werkzeugs in der Leiste („bereit“, „Modell fehlt“, „Quelle folgt“).</summary>
/// <param name="ModelId">Eintrag in Modelle &amp; Daten (null ohne Modell).</param>
/// <param name="Ready">Werkzeug kann laufen.</param>
/// <param name="CanDownload">Modell fehlt, ist aber ladbar („Laden…“ über den Manager).</param>
/// <param name="StateText">„bereit“, „Modell fehlt“, „Quelle folgt“, „defekt“, „braucht Gesichtserkennung“.</param>
/// <param name="ModelName">Modellname für die Unterzeile („Real-ESRGAN x4plus“).</param>
public sealed record EnhanceToolStatus(EnhanceTool Tool, string? ModelId, bool Ready, bool CanDownload, string StateText, string? ModelName);

/// <summary>Fortschritt beim Berechnen: Schritt in Worten und Anteil 0…1.</summary>
public sealed record EnhanceProgress(string Stage, double Fraction);

/// <summary>
/// Berechnetes Ergebnis (Vorschau = volles Ergebnis) als JPEG im Temp-Ordner des Katalogs. Gespeichert wird erst auf Klick
/// (<see cref="IPhotoEnhanceService.SaveAsync"/>); <see cref="IPhotoEnhanceService.Discard"/> löscht die Datei.
/// </summary>
public sealed record EnhancePreview(
    int OriginalFileId,
    string ResultPath,
    int Width,
    int Height,
    IReadOnlyList<EnhanceTool> AppliedTools,
    string ToolsKey,
    TimeSpan Duration,
    IReadOnlyList<string> Notes)
{
    /// <summary>„Hochskalieren · Licht“ für Status und Protokoll.</summary>
    public string ToolsText => EnhanceTexts.ToolList(AppliedTools);
}

/// <summary>Gespeicherte Fassung.</summary>
public sealed record EnhanceSaveResult(int BatchId, int NewFileId, string Path, string? Error)
{
    public bool Success => Error is null && NewFileId > 0;
}

/// <summary>Ergebnis eines Laufs über mehrere Fotos.</summary>
/// <param name="BatchId">Ein Protokoll-Batch für alle gespeicherten Fassungen (0 = keine).</param>
/// <param name="Saved">Gespeicherte Fassungen.</param>
/// <param name="Skipped">Übersprungen (Video, fehlt, nichts zu tun bei „Automatisch“).</param>
/// <param name="Failed">Fehlgeschlagen.</param>
/// <param name="Canceled">Abgebrochen (bereits fertige sind gespeichert und rückgängig machbar).</param>
public sealed record EnhanceBatchResult(int BatchId, int Saved, int Skipped, int Failed, bool Canceled, IReadOnlyList<string> Problems);

/// <summary>Zeitschätzung für mehrere Fotos.</summary>
public sealed record EnhanceEstimate(int Photos, TimeSpan Duration, bool OnGpu, IReadOnlyList<EnhanceTool> MissingModels)
{
    /// <summary>„etwa 2 Minuten auf der Grafikkarte“.</summary>
    public string Text => EnhanceTexts.Estimate(Duration, OnGpu);
}

/// <summary>Speicherort des Ergebnisses.</summary>
public enum EnhanceSaveLocation
{
    /// <summary>Im Ordner des Originals (Standard).</summary>
    NextToOriginal = 0,

    /// <summary>In einem Unterordner des Original-Ordners (Name in <see cref="EnhanceSettings.SubfolderName"/>).</summary>
    Subfolder = 1,
}

/// <summary>Einstellungen › Verbessern (Settings-Tabelle des Katalogs).</summary>
/// <param name="FileNamePattern">Muster mit <c>{name}</c> (Name des Originals ohne Endung), Standard <c>{name}_verbessert</c>.</param>
/// <param name="CopyMetadata">Aufnahmedatum und Ort (EXIF in der Datei und Katalog) übernehmen.</param>
/// <param name="CopyRatingAndTags">Favorit, Sterne und Tags übernehmen.</param>
/// <param name="ShowBestVariant">Galerie: nur die verbesserte Fassung zeigen, das Original über die Marke „2 Fassungen“.</param>
/// <param name="UseGpu">Grafikkarte (DirectML) mit Rückfall auf den Prozessor.</param>
public sealed record EnhanceSettings(
    EnhanceSaveLocation Location = EnhanceSaveLocation.NextToOriginal,
    string SubfolderName = EnhanceSettings.DefaultSubfolder,
    string FileNamePattern = EnhanceSettings.DefaultPattern,
    bool CopyMetadata = true,
    bool CopyRatingAndTags = true,
    bool ShowBestVariant = false,
    bool UseGpu = true,
    double Naturalness = EnhanceSettings.DefaultNaturalness,
    double Grain = EnhanceSettings.DefaultGrain)
{
    /// <summary>Natürlichkeit beim Hochskalieren, bis Herbert anders wählt (0.5.6: Haut wirkte „zu glatt und künstlich“).</summary>
    public const double DefaultNaturalness = 0.35;

    public const double DefaultGrain = 0.2;

    public const string DefaultPattern = "{name}_verbessert";
    public const string DefaultSubfolder = "verbessert";

    public static EnhanceSettings Default { get; } = new();
}

/// <summary>Zum Werkzeug fehlt das Modell (nicht geladen, defekt oder Quelle folgt) — es wird nichts berechnet.</summary>
public sealed class EnhanceModelMissingException(EnhanceTool tool, string message) : InvalidOperationException(message)
{
    public EnhanceTool Tool { get; } = tool;
}

/// <summary>Ein Foto ließ sich nicht verbessern (Text für den Benutzer, ohne Pfade).</summary>
public sealed class EnhanceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Fassungen einer Datei: das Original und seine verbesserten Fassungen (für Marke „2 Fassungen“ und Lightbox).</summary>
public sealed record PhotoVariantGroup(int OriginalFileId, IReadOnlyList<int> VariantFileIds)
{
    public int Count => VariantFileIds.Count + 1;

    /// <summary>Original zuerst, dann die Fassungen (älteste zuerst).</summary>
    public IReadOnlyList<int> AllFileIds => [OriginalFileId, .. VariantFileIds];
}

/// <summary>Kurze Texte (Werkzeugnamen, Schätzung).</summary>
public static class EnhanceTexts
{
    public static string Name(EnhanceTool tool) => tool switch
    {
        EnhanceTool.Auto => "Automatisch",
        EnhanceTool.Upscale => "Hochskalieren",
        EnhanceTool.Faces => "Gesichter",
        EnhanceTool.Colorize => "Einfärben",
        EnhanceTool.Erase => "Entfernen",
        EnhanceTool.Adjust => "Zuschneiden/Licht",
        _ => tool.ToString(),
    };

    /// <summary>Kurzkennung für <c>PhotoVariants.Tools</c>.</summary>
    public static string Key(EnhanceTool tool) => tool switch
    {
        EnhanceTool.Upscale => "upscale",
        EnhanceTool.Faces => "faces",
        EnhanceTool.Colorize => "colorize",
        EnhanceTool.Erase => "erase",
        EnhanceTool.Adjust => "adjust",
        _ => "auto",
    };

    public static string ToolList(IEnumerable<EnhanceTool> tools) => string.Join(" · ", tools.Select(Name));

    public static string Estimate(TimeSpan duration, bool onGpu)
    {
        var where = onGpu ? "auf der Grafikkarte" : "auf dem Prozessor";
        if (duration.TotalSeconds < 60)
        {
            return $"etwa {Math.Max(5, (int)Math.Ceiling(duration.TotalSeconds / 5) * 5)} Sekunden {where}";
        }

        if (duration.TotalMinutes < 90)
        {
            var minutes = (int)Math.Ceiling(duration.TotalMinutes);
            return minutes == 1 ? $"etwa 1 Minute {where}" : $"etwa {minutes} Minuten {where}";
        }

        return $"etwa {duration.TotalHours:N1} Stunden {where}";
    }
}
