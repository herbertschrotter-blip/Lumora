using System.Text.Json.Serialization;

namespace Lumora.Imaging.Protocol;

// Nachrichten zwischen Lumora und dem Hilfsprozess (ADR-0078, Nachtrag „Bau Stufe 1 Teil A“). Eine Nachricht je Zeile,
// JSON mit Kennung „type“, über stdin (Lumora → Hilfsprozess) und stdout (Hilfsprozess → Lumora), siehe WorkerProtocol.
// Texte für den Benutzer entstehen erst in Lumora: der Hilfsprozess meldet Codes (WorkerStep, WorkerErrorCode, Hinweise).

/// <summary>Basis aller Nachrichten; <see cref="Id"/> ordnet Antworten der Anfrage zu (0 = ohne Anfrage).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EstimateRequest), "estimate")]
[JsonDerivedType(typeof(ComposeRequest), "compose")]
[JsonDerivedType(typeof(RenderRequest), "render")]
[JsonDerivedType(typeof(CancelRequest), "cancel")]
[JsonDerivedType(typeof(ThreadsRequest), "threads")]
[JsonDerivedType(typeof(QuitRequest), "quit")]
[JsonDerivedType(typeof(DebugRequest), "debug")]
[JsonDerivedType(typeof(ReadyMessage), "ready")]
[JsonDerivedType(typeof(ProgressMessage), "progress")]
[JsonDerivedType(typeof(EstimateResult), "estimated")]
[JsonDerivedType(typeof(ComposeResult), "composed")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record WorkerMessage
{
    public int Id { get; init; }
}

// ------------------------------------------------------------------ Anfragen (Lumora → Hilfsprozess)

/// <summary>
/// Phase 1 (IPanoramaStitcher.EstimateAsync): Quellen lesen (verkleinert auf <see cref="DecodeEdge"/>, 0 = volle Größe),
/// Merkmale, Paarabgleich, Startwerte, Bündelausgleich, Horizont. Der Hilfsprozess behält die gelesenen Bilder für folgende
/// <see cref="ComposeRequest"/> (Vorschau-Sitzung).
/// </summary>
public sealed record EstimateRequest : WorkerMessage
{
    public required IReadOnlyList<SourceSpec> Sources { get; init; }

    /// <summary>Längste Kante beim Lesen (Vorschau ≈ 1 600 px); 0 = volle Auflösung.</summary>
    public int DecodeEdge { get; init; } = 1600;

    /// <summary>Arbeitsgröße für Merkmale und Abgleich in Megapixeln (Spike 20: 2 MP; 1 MP ließ ein Bild fallen).</summary>
    public double WorkMegapixels { get; init; } = 2.0;

    /// <summary>Fester Startwert für RANSAC (je Paar abgeleitet) — gleiche Eingabe, gleiches Ergebnis.</summary>
    public int Seed { get; init; } = 20_781;
}

/// <summary>
/// Phase 2 (IPanoramaStitcher.ComposeAsync) in der laufenden Sitzung: aus der letzten Ausrichtung und den gelesenen Bildern
/// zusammensetzen und als JPEG nach <see cref="OutputPath"/> schreiben (Temp-Ordner des Katalogs).
/// </summary>
public sealed record ComposeRequest : WorkerMessage
{
    public required ComposeSpec Settings { get; init; }

    public required string OutputPath { get; init; }

    public int JpegQuality { get; init; } = 85;
}

/// <summary>
/// Volle Auflösung aus dem Rezept in einem frischen Lauf: Quellen in voller Größe lesen, mit der gespeicherten Ausrichtung
/// (keine zweite Ausrichtung) zusammensetzen, prüfen, als JPEG schreiben. Reicht <see cref="MaxBytes"/> nicht oder wäre eine
/// Kante länger als <see cref="MaxEdge"/>, wird die größte passende Stufe gewählt und im Ergebnis gemeldet.
/// </summary>
public sealed record RenderRequest : WorkerMessage
{
    public required IReadOnlyList<SourceSpec> Sources { get; init; }

    public required AlignmentSpec Alignment { get; init; }

    public required ComposeSpec Settings { get; init; }

    public required string OutputPath { get; init; }

    public int JpegQuality { get; init; } = 92;

    /// <summary>Arbeitsspeicher, der für den Lauf frei ist (Bytes, 0 = unbegrenzt).</summary>
    public long MaxBytes { get; init; }

    /// <summary>Längste erlaubte Kante (JPEG-Grenze).</summary>
    public int MaxEdge { get; init; } = WorkerLimits.MaxJpegEdge;
}

/// <summary>Bricht die laufende Arbeit <see cref="TargetId"/> zwischen Stufen, Bildern und Bändern ab.</summary>
public sealed record CancelRequest : WorkerMessage
{
    public int TargetId { get; init; }
}

/// <summary>
/// Rechen-Threads ändern, wirksam ab dem nächsten parallelen Abschnitt („Im Hintergrund weiterrechnen“: halbe Kerne, ADR-0078
/// Punkt 12).
/// </summary>
public sealed record ThreadsRequest : WorkerMessage
{
    public int Threads { get; init; }
}

/// <summary>Beendet den Hilfsprozess (Sitzung geschlossen).</summary>
public sealed record QuitRequest : WorkerMessage;

/// <summary>Nur für Tests (Umgebungsvariable <see cref="WorkerLimits.TestHooksVariable"/>): Absturz, Hängen, Ende.</summary>
public sealed record DebugRequest : WorkerMessage
{
    /// <summary>„crash“ (harter Abbruch), „hang“ (antwortet nie, auch nicht auf Abbrechen), „exit“ (Ende mit Code 3), „echo“.</summary>
    public required string Action { get; init; }
}

// ------------------------------------------------------------------ Antworten (Hilfsprozess → Lumora)

/// <summary>Erste Zeile nach dem Start: Protokoll- und OpenCV-Version.</summary>
public sealed record ReadyMessage : WorkerMessage
{
    public int ProtocolVersion { get; init; }

    public string OpenCvVersion { get; init; } = string.Empty;

    public int Threads { get; init; }
}

/// <summary>Fortschritt einer Arbeit: Schritt (Code), Teil <see cref="Index"/> von <see cref="Count"/>, Anteil 0…1.</summary>
public sealed record ProgressMessage : WorkerMessage
{
    public WorkerStep Step { get; init; }

    public int Index { get; init; }

    public int Count { get; init; }

    public double Fraction { get; init; }
}

/// <summary>Ergebnis von <see cref="EstimateRequest"/>.</summary>
public sealed record EstimateResult : WorkerMessage
{
    public required AlignmentSpec Alignment { get; init; }

    /// <summary>Vorschlag der Aufnahmeart aus Abdeckung, Gimbal und Bildwinkel.</summary>
    public WireProjection SuggestedProjection { get; init; }

    /// <summary>Hinweise als Codes („dropped:3“, „gimbal“, „focal-estimated“ …).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public double Seconds { get; init; }

    public long PeakBytes { get; init; }
}

/// <summary>Ergebnis von <see cref="ComposeRequest"/> bzw. <see cref="RenderRequest"/>.</summary>
public sealed record ComposeResult : WorkerMessage
{
    public required string OutputPath { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Maße bei voller Auflösung (Maßstab 1) mit denselben Einstellungen.</summary>
    public int FullWidth { get; init; }

    public int FullHeight { get; init; }

    /// <summary>Maßstab gegenüber voller Auflösung (1 = voll).</summary>
    public double Scale { get; init; }

    /// <summary>Geschätzte Speicherspitze bei voller Auflösung (Bytes).</summary>
    public long FullPeakBytes { get; init; }

    public required QualityCheck Check { get; init; }

    /// <summary>Hinweise als Codes („size-reduced:memory“, „size-reduced:jpeg-edge“, „tone:…“ …).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Tatsächlich verwendete Aufnahmeart (bei Automatisch der Vorschlag).</summary>
    public WireProjection Projection { get; init; }

    public double Seconds { get; init; }

    public long PeakBytes { get; init; }
}

/// <summary>Fehler einer Arbeit (der Hilfsprozess läuft weiter).</summary>
public sealed record ErrorMessage : WorkerMessage
{
    public WorkerErrorCode Code { get; init; }

    /// <summary>Technische Meldung (ohne Pfade persönlicher Dateien; nur für das Protokoll auf Debug-Ebene).</summary>
    public string Detail { get; init; } = string.Empty;
}

// ------------------------------------------------------------------ Bausteine

/// <summary>Eine Quelle: Pfad, Stelle in der Auswahl, Hinweise aus dem Katalog (fehlen sie, liest der Hilfsprozess die Datei).</summary>
public sealed record SourceSpec
{
    public required string Path { get; init; }

    /// <summary>Position in der Auswahl (0 = erstes Einzelbild).</summary>
    public int Index { get; init; }

    /// <summary>Brennweite KB (35 mm), null = aus der Datei lesen.</summary>
    public double? Focal35 { get; init; }

    /// <summary>Gimbal-Gier/-Neigung/-Rollen (DJI-XMP) in Grad, null = aus der Datei lesen.</summary>
    public double? GimbalYaw { get; init; }

    public double? GimbalPitch { get; init; }

    public double? GimbalRoll { get; init; }
}

/// <summary>Ausrichtung aus Phase 1 — steht im Rezept, damit volle Auflösung und „Neu berechnen“ nicht neu ausrichten.</summary>
public sealed record AlignmentSpec
{
    public int Version { get; init; } = 1;

    /// <summary>Kameras der verwendeten Quellen (volle, gedrehte Auflösung).</summary>
    public required IReadOnlyList<CameraSpec> Cameras { get; init; }

    /// <summary>Quellen (Index aus <see cref="SourceSpec.Index"/>), die nicht eingebunden werden konnten.</summary>
    public IReadOnlyList<int> Dropped { get; init; } = [];

    /// <summary>Die Bilder decken 360° rundum ab.</summary>
    public bool Is360 { get; init; }

    /// <summary>Gimbal-Winkel dienten als Startwerte und zur Ausrichtung am Horizont.</summary>
    public bool UsedGimbal { get; init; }

    /// <summary>Restfehler des Bündelausgleichs in Pixeln bei voller Auflösung (Median / 90 %).</summary>
    public double ResidualMedianPx { get; init; }

    public double ResidualP90Px { get; init; }

    /// <summary>Belichtung, Weißabgleich und Vignettierung je Bild (nur angewandt, wenn das Modell die Überlappungen erklärt).</summary>
    public PhotometricSpec? Photometric { get; init; }

    /// <summary>„Nur verschieben“ (Scans): Ähnlichkeits-Abbildung je Kamera in Pixeln der vollen Auflösung.</summary>
    public IReadOnlyList<SimilaritySpec>? Similarity { get; init; }

    /// <summary>Startwert, mit dem diese Ausrichtung entstand.</summary>
    public int Seed { get; init; }
}

/// <summary>Kamera: Rotation (Kamera → Welt, zeilenweise), Brennweite und Verzeichnung bei voller Auflösung.</summary>
public sealed record CameraSpec
{
    public int Source { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public double F { get; init; }

    public double K1 { get; init; }

    public double K2 { get; init; }

    public double Ppx { get; init; }

    public double Ppy { get; init; }

    public required double[] R { get; init; }
}

/// <summary>Photometrisches Modell (Hugin-Art, Spike 20 Runde 2), Werte je Kamera in derselben Reihenfolge wie die Kameras.</summary>
public sealed record PhotometricSpec
{
    public required double[] E { get; init; }

    public required double[] Wr { get; init; }

    public required double[] Wb { get; init; }

    public double B1 { get; init; }

    public double B2 { get; init; }

    public double B3 { get; init; }

    public bool Applied { get; init; }

    public double RmsBefore { get; init; }

    public double RmsAfter { get; init; }
}

/// <summary>Ähnlichkeits-Abbildung Bild → Leinwand: x' = A·x + B·y + Tx, y' = −B·x + A·y + Ty (volle Auflösung).</summary>
public sealed record SimilaritySpec
{
    public int Source { get; init; }

    public double A { get; init; }

    public double B { get; init; }

    public double Tx { get; init; }

    public double Ty { get; init; }
}

/// <summary>Einstellungen der Phase 2 (ComposeSettings aus ADR-0078 Punkt 3).</summary>
public sealed record ComposeSpec
{
    public WireProjection Projection { get; init; } = WireProjection.Auto;

    /// <summary>Kugel 360°: Horizontstreifen oder 2 : 1-Rundum-Bild (Zenit/Nadir einfach gefüllt).</summary>
    public WireSphereForm SphereForm { get; init; } = WireSphereForm.Band;

    public WireCrop Crop { get; init; } = WireCrop.Straight;

    /// <summary>Belichtung ausgleichen (photometrisches Modell + Blockverstärkung).</summary>
    public bool Exposure { get; init; } = true;

    /// <summary>Nähte suchen (Graph-Cut); aus = nur Mehrband-Überblendung über die ganze Überlappung.</summary>
    public bool Seams { get; init; } = true;

    /// <summary>Entgeisterung im Nahtband (optischer Fluss) — teuer, Standard nur bei voller Auflösung.</summary>
    public bool Deghost { get; init; } = true;

    /// <summary>Stärke des Tonwert-Angleichs an die Quellen, 0…1 (Helligkeitsverteilung und Buntheit; 0 = aus).</summary>
    public double ToneMatch { get; init; } = 0.75;

    /// <summary>
    /// Farbverstärkung nach dem Tonwert-Angleich in Prozent (Dynamik + leichte Sättigung, Hauttöne gedämpft, Grau bleibt grau,
    /// kein Abschneiden): 0 = aus (treu zu den Quellen), 100 = Standard „wie Photoshop“, höchstens 150. Ändert sich in der
    /// Vorschau-Sitzung nur sie bzw. <see cref="ToneMatch"/>, tont der Hilfsprozess das letzte Bild nur neu.
    /// </summary>
    public int ColorBoost { get; init; } = 100;

    /// <summary>Maßstab gegenüber voller Auflösung (≤ 0 = so groß wie die gelesenen Bilder erlauben).</summary>
    public double Scale { get; init; }

    /// <summary>Obergrenze der Ausgabe in Pixeln (0 = keine), z. B. Vorschau ≈ 5 MP.</summary>
    public long MaxOutputPixels { get; init; }

    /// <summary>Nahtsuche in Megapixeln (Spike 20: 0,5 MP; Vorschau 0,1 MP).</summary>
    public double SeamMegapixels { get; init; } = 0.5;
}

/// <summary>Prüfung vor dem Speichern (Nachtrag 0.5.44, Punkt 6): Restfehler, Nahtgüte, Zuschnitthöhe.</summary>
public sealed record QualityCheck
{
    public bool Passed { get; init; }

    /// <summary>Gründe als Codes („residual“, „seams“, „crop-height“, „crop-size“, „images-dropped“, „tilted“).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public double ResidualMedianPx { get; init; }

    public double ResidualP90Px { get; init; }

    /// <summary>Median der Nahtgüte (NCC im texturierten Nahtband, 1 = deckungsgleich), NaN ohne Messung.</summary>
    public double SeamNcc { get; init; } = double.NaN;

    /// <summary>Höhe des Zuschnitts quer zum Schwenk gegenüber der mittleren Bildhöhe (1 = eine Bildhöhe).</summary>
    public double CropHeightFraction { get; init; }

    /// <summary>Fläche des Zuschnitts gegenüber dem Rahmen der gedeckten Fläche (0…1).</summary>
    public double CropAreaFraction { get; init; } = 1;

    public int UsedImages { get; init; }

    public int TotalImages { get; init; }
}

/// <summary>Aufnahmeart auf der Leitung (gleiche Reihenfolge wie Domain.PanoramaProjection).</summary>
public enum WireProjection
{
    Auto = 0,
    Spherical360 = 1,
    Cylindrical = 2,
    Perspective = 3,
    Vertical = 4,
    ShiftOnly = 5,
}

/// <summary>Form der Kugel 360°.</summary>
public enum WireSphereForm
{
    /// <summary>Horizontstreifen (wie die bisherigen Photoshop-Ergebnisse, ≈ 5 : 1).</summary>
    Band = 0,

    /// <summary>2 : 1-Rundum-Bild mit gefülltem Zenit und Nadir (GPano).</summary>
    Full = 1,
}

/// <summary>Zuschnitt.</summary>
public enum WireCrop
{
    /// <summary>Gerade Kanten: größtes Rechteck ohne Lücken (schmale Ränder werden gefüllt).</summary>
    Straight = 0,

    /// <summary>Lücken füllen: ganze Leinwand, Lücken weich gefüllt.</summary>
    Fill = 1,

    /// <summary>Ungeschnitten: ganze Leinwand, Lücken schwarz.</summary>
    None = 2,
}

/// <summary>Schritte der Rechenkette (Texte in Lumora).</summary>
public enum WorkerStep
{
    Load = 0,
    Features = 1,
    Match = 2,
    Bundle = 3,
    Exposure = 4,
    Seams = 5,
    Deghost = 6,
    Warp = 7,
    Blend = 8,
    Finish = 9,
    Write = 10,
}

/// <summary>Fehlerarten (Texte in Lumora).</summary>
public enum WorkerErrorCode
{
    Unknown = 0,

    /// <summary>Eine Quelle ließ sich nicht lesen (fehlt, gesperrt, Format).</summary>
    SourceUnreadable = 1,

    /// <summary>Zu wenige Quellen überlappen (weniger als 2 eingebunden).</summary>
    NotEnoughOverlap = 2,

    /// <summary>Die Prüfung vor dem Speichern hat das Ergebnis auch nach Wiederholung verworfen.</summary>
    CheckFailed = 3,

    /// <summary>Kein Arbeitsspeicher für die kleinste Stufe.</summary>
    OutOfMemory = 4,

    /// <summary>Abgebrochen.</summary>
    Canceled = 5,

    /// <summary>Anfrage passt nicht (z. B. Zusammensetzen ohne Ausrichtung).</summary>
    BadRequest = 6,

    /// <summary>OpenCV hat einen Fehler gemeldet.</summary>
    OpenCv = 7,

    /// <summary>Ergebnis ließ sich nicht schreiben.</summary>
    WriteFailed = 8,
}

/// <summary>Grenzen und feste Werte des Protokolls.</summary>
public static class WorkerLimits
{
    /// <summary>
    /// Längste Kante eines JPEG: das Format erlaubt 65 535 px, libjpeg(-turbo) in OpenCV schreibt höchstens 65 500
    /// (<c>JPEG_MAX_DIMENSION</c>) — deshalb diese Grenze.
    /// </summary>
    public const int MaxJpegEdge = 65_500;

    /// <summary>Umgebungsvariable, die <see cref="DebugRequest"/> im Hilfsprozess zulässt (nur Tests).</summary>
    public const string TestHooksVariable = "LUMORA_IMAGING_WORKER_TEST";
}
