using Lumora.Domain.Enums;
using Lumora.Imaging.Protocol;

namespace Lumora.Imaging.Merge;

/// <summary>Zustand einer Quelle in der Auswahl (Filmstreifen-Marken „nicht eingebunden“, „nur OneDrive“, „fehlt“).</summary>
public enum MergeSourceState
{
    /// <summary>Lokal lesbar (JPEG, HEIC, PNG, TIFF).</summary>
    Usable = 0,

    Video = 1,

    /// <summary>Eintrag in einem Archiv (ZIP …).</summary>
    Archive = 2,

    /// <summary>Datei fehlt (Katalog „fehlt“ bzw. nicht mehr vorhanden).</summary>
    Missing = 3,

    /// <summary>OneDrive-Platzhalter: liegt nur online (ADR-0077) — Laden nur nach Rückfrage.</summary>
    OnlineOnly = 4,

    /// <summary>Laufwerk nicht erreichbar (PLAN Kap. 12) — nie als „fehlt“ gewertet.</summary>
    Offline = 5,

    /// <summary>RAW/DNG: kommt mit Stufe 3 (eigene 16-bit-Entwicklung); ohne JPEG-Geschwister nicht eingebunden.</summary>
    Raw = 6,

    /// <summary>Anderes Format (WebP, GIF …).</summary>
    Unsupported = 7,
}

/// <summary>Eine Quelle bzw. ein Zeit-Nachbar im Filmstreifen.</summary>
/// <param name="Position">Stelle nach Aufnahmezeit (0 = erstes Einzelbild).</param>
/// <param name="ReplacesRawFileId">Diese JPEG-Datei steht für das gewählte RAW-Geschwister (Stufe 1).</param>
public sealed record MergeSourceInfo(
    int FileId,
    int Position,
    string Name,
    DateTime? CapturedAt,
    string? Camera,
    int? Width,
    int? Height,
    long Size,
    MergeSourceState State,
    int? ReplacesRawFileId = null)
{
    public bool IsUsable => State == MergeSourceState.Usable;
}

/// <summary>Ein Verfahren in der Liste links: gebaut (Stufe 1: nur Panorama), passt/eher nicht, Grund.</summary>
public sealed record MergeMethodInfo(MergeMethod Method, string Name, bool Built, bool Suits, string Reason);

/// <summary>Hinweis mit Code (für Tests/Marken) und deutschem Text (für die Oberfläche).</summary>
public sealed record MergeNote(string Code, string Text);

/// <summary>
/// Analyse einer Auswahl (<see cref="IMergeService.AnalyzeAsync"/>, nur Katalog und Dateisystem, schnell): Quellen nach
/// Aufnahmezeit, Zeit-Nachbarn (bei einem Foto: gleicher Ordner, gleiche Kamera, ± 2 min), Bibliothek, Verfahren mit Eignung,
/// Vorschlag, Hinweise.
/// </summary>
public sealed record MergeAnalysis(
    IReadOnlyList<MergeSourceInfo> Sources,
    IReadOnlyList<MergeSourceInfo> Neighbors,
    int? LibraryId,
    bool MixedLibraries,
    IReadOnlyList<MergeMethodInfo> Methods,
    MergeMethod SuggestedMethod,
    PanoramaProjection SuggestedProjection,
    IReadOnlyList<MergeNote> Notes,
    int OnlineOnlyCount,
    long OnlineOnlyBytes)
{
    /// <summary>Lokal lesbare Quellen.</summary>
    public int UsableCount => Sources.Count(s => s.IsUsable);

    /// <summary>Vorschau möglich: eine Bibliothek, mindestens zwei lesbare Fotos (Platzhalter zählen nach Zustimmung mit).</summary>
    public bool CanPreview => !MixedLibraries && UsableCount + OnlineOnlyCount >= MergeLimits.MinSources;
}

/// <summary>Form der Kugel 360° (Abnahme 2026-11-18 Nr. 4: gleichrangig wählbar).</summary>
public enum MergeSphereForm
{
    /// <summary>Horizontstreifen (wie die bisherigen Photoshop-Ergebnisse).</summary>
    Band = 0,

    /// <summary>2 : 1-Rundum-Bild, Zenit/Nadir einfach gefüllt, GPano.</summary>
    Full = 1,
}

/// <summary>Zuschnitt (ADR-0078 Punkt 6).</summary>
public enum MergeCrop
{
    Straight = 0,
    Fill = 1,
    None = 2,
}

/// <summary>Optionen des Panoramas; <see cref="SphereForm"/> null = zuletzt gewählte Form (Einstellung <c>merge.panorama.sphereForm</c>).</summary>
/// <param name="ColorBoost">
/// „Farbe kräftiger“ in Prozent (<see cref="MergeLimits.ColorBoostOff"/> 0 = aus, treu zu den Quellen;
/// <see cref="MergeLimits.ColorBoostDefault"/> 100 = Standard „wie Photoshop“; bis <see cref="MergeLimits.ColorBoostMax"/> 150);
/// null = zuletzt gewählte Stärke (Einstellung <c>merge.colorBoost</c>, erstes Mal 100). Ändert sich in der Vorschau nur sie
/// (bzw. <see cref="ToneMatch"/>), wird nur neu getont — keine neue Ausrichtung, kein neues Zusammensetzen.
/// </param>
public sealed record MergeOptions(
    PanoramaProjection Projection = PanoramaProjection.Auto,
    MergeSphereForm? SphereForm = null,
    MergeCrop Crop = MergeCrop.Straight,
    bool Exposure = true,
    double ToneMatch = 0.75,
    int? ColorBoost = null)
{
    /// <summary>Gleich bis auf Tonwert-Angleich und Farbverstärkung (nur „neu tonen“).</summary>
    public bool SameGeometry(MergeOptions other)
        => other is not null && this with { ToneMatch = 0, ColorBoost = null } == other with { ToneMatch = 0, ColorBoost = null };
}

/// <summary>Auftrag für eine Vorschau-Sitzung.</summary>
/// <param name="FileIds">Angehakte Fotos (Reihenfolge egal — sortiert wird nach Aufnahmezeit).</param>
/// <param name="AllowOnlineOnlyDownload">Benutzer hat „Herunterladen und fortfahren“ bestätigt (ADR-0078 Punkt 14).</param>
public sealed record MergeRequest(
    IReadOnlyList<int> FileIds,
    MergeMethod Method = MergeMethod.Panorama,
    MergeOptions? Options = null,
    int? SeriesId = null,
    bool AllowOnlineOnlyDownload = false);

/// <summary>Ergebnis der Prüfung vor dem Speichern (Restfehler, Nahtgüte, Zuschnitthöhe) mit verständlichen Gründen.</summary>
public sealed record MergeQuality(
    bool Passed,
    IReadOnlyList<MergeNote> Problems,
    double ResidualMedianPx,
    double SeamNcc,
    double CropHeightFraction,
    int UsedImages,
    int TotalImages);

/// <summary>Eine fertige Vorschau (JPEG im Temp-Ordner des Katalogs, ≈ 4–6 MP).</summary>
public sealed record MergePreview(
    string ImagePath,
    int Width,
    int Height,
    int FullWidth,
    int FullHeight,
    PanoramaProjection Projection,
    PanoramaProjection SuggestedProjection,
    MergeSphereForm SphereForm,
    MergeQuality Quality,
    IReadOnlyList<MergeNote> Notes,
    IReadOnlyList<int> DroppedFileIds,
    TimeSpan Elapsed)
{
    /// <summary>Speichern nur, wenn die Prüfung bestanden ist (nie still speichern).</summary>
    public bool CanSave => Quality.Passed;
}

/// <summary>Fortschritt („Nähte suchen 4/6“) einer Vorschau bzw. des Speicherns.</summary>
/// <param name="Saving">true beim Speichern (volle Auflösung), false bei der Vorschau.</param>
/// <param name="Fraction">Anteil 0…1 des ganzen Vorgangs.</param>
public sealed record MergeProgress(bool Saving, WorkerStep Step, string Text, int Index, int Count, double Fraction);

/// <summary>
/// Schätzung für „Als neue Datei speichern“ (ADR-0078 Punkt 12, Abnahme Nr. 11): Größe (volle Auflösung, sonst die größte
/// passende Stufe mit Grund), Speicherspitze, frei verfügbarer Arbeitsspeicher, Zeit, Platz auf dem Datenträger.
/// </summary>
public sealed record MergeEstimate(
    int Width,
    int Height,
    double Scale,
    MergeNote? Reduction,
    long PeakBytes,
    long AvailableBytes,
    TimeSpan Duration,
    long DiskBytes);

/// <summary>Optionen beim Speichern; null = Wert aus der Sitzung bzw. den Einstellungen.</summary>
/// <param name="AlbumId">„auch ins Album …“ (ADR-0078 Punkt 7).</param>
public sealed record MergeSaveOptions(
    MergeOptions? Options = null,
    bool? CopyCommonTags = null,
    bool? CopyRating = null,
    int? AlbumId = null);

/// <summary>Ergebnis von <see cref="IMergePreviewSession.SaveAsync"/>.</summary>
/// <param name="Notes">Hinweise, u. a. „größte passende Größe“ (Code <c>size-reduced:memory</c> bzw. <c>size-reduced:jpeg-edge</c>).</param>
public sealed record MergeSaveResult(
    bool Success,
    int ResultFileId,
    int BatchId,
    string? Path,
    int Width,
    int Height,
    double Scale,
    IReadOnlyList<MergeNote> Notes,
    string? Error)
{
    public static MergeSaveResult Failed(string error, IReadOnlyList<MergeNote>? notes = null) => new(false, 0, 0, null, 0, 0, 0, notes ?? [], error);
}

/// <summary>Ist „Zusammenfügen“ verfügbar (Hilfsprozess vorhanden, OpenCV ladbar)?</summary>
public sealed record MergeAvailability(bool Available, string? Reason, string? OpenCvVersion);

/// <summary>Wo das Ergebnis liegt (ADR-0078 Punkt 10/17).</summary>
public enum MergeSaveLocation
{
    /// <summary>Neben dem ersten Einzelbild.</summary>
    NextToFirst = 0,

    /// <summary>Unterordner „zusammengefügt“ neben dem ersten Einzelbild.</summary>
    Subfolder = 1,
}

/// <summary>Einstellungen › Zusammenfügen (Teil Ergebnis; Settings-Tabelle des Katalogs, <c>merge.*</c>).</summary>
public sealed record MergeSettings(
    MergeSphereForm SphereForm = MergeSphereForm.Band,
    MergeSaveLocation Location = MergeSaveLocation.NextToFirst,
    string SubfolderName = MergeSettings.DefaultSubfolder,
    string PanoramaSuffix = MergeSettings.DefaultPanoramaSuffix,
    int JpegQuality = 92,
    bool CopyCommonTags = true,
    bool CopyRating = true,
    bool DroneFolderOneUp = false,
    int ColorBoost = MergeLimits.ColorBoostDefault)
{
    public const string DefaultSubfolder = "zusammengefügt";
    public const string DefaultPanoramaSuffix = "_panorama";

    public static MergeSettings Default { get; } = new();
}

/// <summary>Grenzen je Verfahren (ADR-0078 Punkt 5).</summary>
public static class MergeLimits
{
    public const int MinSources = 2;

    /// <summary>Farbverstärkung aus (treu zu den Quellen).</summary>
    public const int ColorBoostOff = 0;

    /// <summary>Farbverstärkung Standard „wie Photoshop“ (Herbert 2026-11-18).</summary>
    public const int ColorBoostDefault = 100;

    /// <summary>Höchste Farbverstärkung.</summary>
    public const int ColorBoostMax = 150;

    /// <summary>Panorama: höchstens 200 Quellen.</summary>
    public const int MaxPanoramaSources = 200;

    /// <summary>Lange Kante der Vorschau beim Lesen (ADR-0078 Punkt 12: ≈ 1 600 px).</summary>
    public const int PreviewDecodeEdge = 1600;

    /// <summary>Größe der Vorschau (ADR-0078 Punkt 12: ≈ 4–6 MP).</summary>
    public const long PreviewMaxPixels = 5_000_000;

    /// <summary>Lesbare Formate in Stufe 1.</summary>
    public static readonly IReadOnlySet<string> ReadableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".png", ".tif", ".tiff",
    };

    /// <summary>RAW-Formate (Stufe 3).</summary>
    public static readonly IReadOnlySet<string> RawExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw", ".raw", ".rwl", ".3fr", ".iiq", ".erf", ".kdc", ".mrw", ".x3f",
    };
}
