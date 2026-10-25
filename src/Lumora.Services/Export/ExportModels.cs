namespace Lumora.Services.Export;

/// <summary>
/// Dateien für einen Weg nach außen (Ziehen in andere Programme, Strg+C, Exportieren; ADR-0068): vorhandene Pfade in
/// Eingabe-Reihenfolge und was ausgelassen wurde. <see cref="Blocked"/> = versteckte Bibliothek, nichts darf hinaus (ADR-0049).
/// </summary>
/// <param name="Paths">Volle Pfade der vorhandenen Dateien.</param>
/// <param name="FileIds">FileIds zu <paramref name="Paths"/> (gleiche Reihenfolge).</param>
/// <param name="SkippedMissing">Fehlt (im Katalog als „fehlt“ markiert oder auf der Platte nicht da).</param>
/// <param name="SkippedArchives">Archive (Fotos nur im Archiv).</param>
public sealed record ExternalFileSet(
    IReadOnlyList<string> Paths,
    IReadOnlyList<int> FileIds,
    int SkippedMissing,
    int SkippedArchives,
    bool Blocked)
{
    /// <summary>Hinweis in der versteckten Bibliothek.</summary>
    public const string BlockedText = "In der versteckten Bibliothek gesperrt — Fotos verlassen den Container nicht.";

    public static ExternalFileSet BlockedSet { get; } = new([], [], 0, 0, true);

    public static ExternalFileSet Empty { get; } = new([], [], 0, 0, false);

    public int Skipped => SkippedMissing + SkippedArchives;

    /// <summary>Kurzer Hinweis „2 fehlende Dateien und 1 Archiv ausgelassen“ bzw. null.</summary>
    public string? SkippedText
    {
        get
        {
            if (Blocked)
            {
                return BlockedText;
            }

            var parts = new List<string>();
            if (SkippedMissing > 0)
            {
                parts.Add(SkippedMissing == 1 ? "1 fehlende Datei" : $"{SkippedMissing:N0} fehlende Dateien");
            }

            if (SkippedArchives > 0)
            {
                parts.Add(SkippedArchives == 1 ? "1 Archiv" : $"{SkippedArchives:N0} Archive");
            }

            return parts.Count == 0 ? null : string.Join(" und ", parts) + " ausgelassen";
        }
    }
}

/// <summary>Größe und Qualität für „Exportieren (verkleinert)…“.</summary>
/// <param name="MaxLongEdge">Längste Kante in Pixeln (nie hochskaliert).</param>
/// <param name="JpegQuality">JPEG-Qualität 1–100.</param>
public sealed record PhotoExportOptions(int MaxLongEdge = 2048, int JpegQuality = 88)
{
    /// <summary>Angebotene Größen (längste Kante).</summary>
    public static IReadOnlyList<int> Sizes { get; } = [1024, 1600, 2048, 3072, 3840];
}

/// <summary>Ergebnis von „Exportieren (verkleinert)…“.</summary>
public sealed record PhotoExportResult(
    int Exported,
    int SkippedMissing,
    int SkippedArchives,
    int SkippedVideos,
    int Failed,
    IReadOnlyList<string> Errors,
    bool Canceled = false,
    bool IsBlocked = false)
{
    public static PhotoExportResult Blocked { get; } = new(0, 0, 0, 0, 0, [ExternalFileSet.BlockedText], IsBlocked: true);

    /// <summary>„12 Fotos exportiert · 1 Video ausgelassen“.</summary>
    public string Summary
    {
        get
        {
            if (IsBlocked)
            {
                return ExternalFileSet.BlockedText;
            }

            var text = Exported == 1 ? "1 Foto exportiert" : $"{Exported:N0} Fotos exportiert";
            var skipped = new List<string>();
            if (SkippedVideos > 0)
            {
                skipped.Add(SkippedVideos == 1 ? "1 Video" : $"{SkippedVideos:N0} Videos");
            }

            if (SkippedMissing > 0)
            {
                skipped.Add(SkippedMissing == 1 ? "1 fehlende" : $"{SkippedMissing:N0} fehlende");
            }

            if (SkippedArchives > 0)
            {
                skipped.Add(SkippedArchives == 1 ? "1 Archiv" : $"{SkippedArchives:N0} Archive");
            }

            if (skipped.Count > 0)
            {
                text += " · " + string.Join(", ", skipped) + " ausgelassen";
            }

            if (Failed > 0)
            {
                text += $" · {Failed:N0} fehlgeschlagen";
            }

            return Canceled ? text + " · abgebrochen" : text;
        }
    }
}
