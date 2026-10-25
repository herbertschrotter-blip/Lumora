namespace Lumora.Services.ModelPackages;

/// <summary>Abschnitt in Einstellungen › Modelle &amp; Daten (ADR-0070).</summary>
public enum ModelPackageGroup
{
    /// <summary>KI-Modell, das Lumora nach Bestätigung lädt (Gesichter, Motive, später „Foto verbessern“).</summary>
    Model = 0,

    /// <summary>Daten, die mit Lumora kommen oder bei Bedarf entstehen (Ortsnamen, Kartenkacheln).</summary>
    Data = 1,

    /// <summary>Fremdes Programm, das Lumora nur abfragt (Ollama) — nichts installieren.</summary>
    External = 2,
}

/// <summary>Zustand eines Eintrags.</summary>
public enum ModelPackageState
{
    /// <summary>Keine Datei vorhanden.</summary>
    NotInstalled = 0,

    /// <summary>Alle benötigten Dateien da (Größen stimmen; Prüfsumme bei „Prüfen“ bzw. nach dem Laden).</summary>
    Installed = 1,

    /// <summary>Geladen, aber in einer älteren Version, als diese Lumora-Version mitbringt.</summary>
    UpdateAvailable = 2,

    /// <summary>Unvollständig, falsche Größe oder Prüfsumme — „Laden…“ ersetzt die Dateien.</summary>
    Broken = 3,

    /// <summary>Mit Lumora ausgeliefert (eingebettet), nichts zu laden.</summary>
    BuiltIn = 4,

    /// <summary>Wird bei Bedarf geladen und zwischengespeichert (Kartenkacheln).</summary>
    OnDemand = 5,

    /// <summary>Externes Programm läuft und hat das Modell.</summary>
    ExternalReady = 6,

    /// <summary>Externes Programm fehlt, läuft nicht oder hat das Modell nicht.</summary>
    ExternalMissing = 7,
}

/// <summary>Was ein Eintrag kann.</summary>
[Flags]
public enum ModelPackageCapabilities
{
    None = 0,
    Download = 1,
    Verify = 2,
    Remove = 4,
}

/// <summary>Eine Datei eines Modells: Pfad relativ zum Modellordner (mit „/“), Größe, SHA-256 (Kleinbuchstaben-Hex).</summary>
/// <param name="Required">false = Begleitdatei (Konfiguration); ohne sie gilt das Modell trotzdem als geladen.</param>
public sealed record ModelPackageFile(string RelativePath, long Size, string Sha256, bool Required = true)
{
    public string LocalRelativePath => RelativePath.Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>Hersteller bzw. Bezugsquelle mit Website (nur zum Anzeigen und Öffnen auf Klick).</summary>
public sealed record ModelPackageOrigin(string Name, Uri? Website);

/// <summary>Art der Update-Quelle.</summary>
public enum ModelUpdateSourceKind
{
    /// <summary>
    /// Hugging-Face-Dateiliste (<c>/api/models/&lt;repo&gt;/tree/main/&lt;ordner&gt;</c>): die LFS-Prüfsummen der großen Dateien
    /// werden mit den hinterlegten verglichen — nur Metadaten, kein Download.
    /// </summary>
    HuggingFaceFiles = 0,
}

/// <summary>Wo „Update suchen“ nachfragt (nur auf Klick).</summary>
public sealed record ModelUpdateSource(ModelUpdateSourceKind Kind, Uri ManifestUri);

/// <summary>
/// Beschreibung eines Eintrags (fest in Lumora hinterlegt, ADR-0070). Neue Modelle kommen als weitere Definition mit einer
/// Quelle (<see cref="IModelPackageSource"/>) dazu — siehe <see cref="ModelPackagesServiceCollectionExtensions.AddModelPackage"/>.
/// </summary>
/// <param name="Id">Stabiler Schlüssel („faces.buffalo_l“), auch im Protokoll — nie ein Pfad.</param>
/// <param name="Title">Zweck in einem Wort („Gesichtserkennung“).</param>
/// <param name="Name">Modellname („InsightFace buffalo_l“).</param>
/// <param name="Purpose">Was es in Lumora tut (ein Satz).</param>
/// <param name="Version">Hinterlegte Version (Release, Datum der geprüften Dateien).</param>
/// <param name="License">Lizenz in Kurzform.</param>
/// <param name="LicenseRestricted">true = Einschränkung, die hervorgehoben wird (z. B. nur nicht-kommerziell).</param>
/// <param name="DownloadSize">Bytes, die beim Laden übertragen werden (Bestätigung, Fortschritt); 0 ohne Download.</param>
/// <param name="FolderName">Unterordner im Modellordner; null ohne eigene Dateien.</param>
/// <param name="Files">Dateien mit Prüfsummen (Prüfen, Entfernen, Größe auf der Platte).</param>
/// <param name="UpdateSource">null = „Version x, keine automatische Prüfung“.</param>
/// <param name="Note">Zusatz im Bestätigungsdialog (z. B. was aus einem Archiv entpackt wird).</param>
public sealed record ModelPackageDefinition(
    string Id,
    string Title,
    string Name,
    string Purpose,
    ModelPackageGroup Group,
    string Version,
    ModelPackageOrigin Origin,
    string License,
    bool LicenseRestricted = false,
    long DownloadSize = 0,
    string? FolderName = null,
    IReadOnlyList<ModelPackageFile>? Files = null,
    ModelUpdateSource? UpdateSource = null,
    string? Note = null)
{
    public IReadOnlyList<ModelPackageFile> FileList => Files ?? [];

    /// <summary>Größe auf der Platte nach dem Laden.</summary>
    public long InstalledSize => FileList.Sum(f => f.Size);
}

/// <summary>Momentaufnahme für die Liste.</summary>
/// <param name="Text">Zustand in Worten („geladen“, „nicht geladen“, „unvollständig“ …).</param>
/// <param name="Detail">Zusatzzeile (Zwischenspeicher-Größe, Ollama-Version, Grund für „defekt“).</param>
/// <param name="InstalledVersion">Version laut Prüf-Vermerk; null = unbekannt (vor 0.4.42 geladen oder noch nie geprüft).</param>
/// <param name="CheckedUtc">Letzte erfolgreiche Prüfung.</param>
/// <param name="DiskBytes">Belegter Platz (Modell-Dateien bzw. Zwischenspeicher); null = unbekannt.</param>
public sealed record ModelPackageStatus(
    ModelPackageState State,
    string Text,
    string? Detail = null,
    string? InstalledVersion = null,
    DateTimeOffset? CheckedUtc = null,
    long? DiskBytes = null)
{
    public bool IsInstalled => State is ModelPackageState.Installed or ModelPackageState.UpdateAvailable;
}

/// <summary>Fortschritt beim Laden oder Prüfen (Bytes; <paramref name="Total"/> 0 = unbestimmt).</summary>
public sealed record ModelPackageProgress(string Stage, long Done, long Total);

/// <summary>Ergebnis von „Prüfen“.</summary>
/// <param name="BadFiles">Dateien (relativ, mit „/“), die fehlen oder deren Prüfsumme nicht stimmt.</param>
public sealed record ModelVerifyResult(bool Ok, string Message, IReadOnlyList<string> BadFiles)
{
    public static ModelVerifyResult Success(string message) => new(true, message, []);
}

/// <summary>Ergebnis von „Update suchen“.</summary>
public enum ModelUpdateOutcome
{
    /// <summary>Keine Update-Quelle hinterlegt: „Version x, keine automatische Prüfung“.</summary>
    NoSource = 0,
    UpToDate = 1,

    /// <summary>Der Hersteller hat andere Dateien veröffentlicht; Lumora übernimmt sie erst nach Prüfung mit einem Update.</summary>
    NewerAtVendor = 2,

    /// <summary>Im Paket steckt eine neuere Fassung als die geladene — „Laden…“ holt sie.</summary>
    UpdateReady = 3,
    Failed = 4,
}

public sealed record ModelUpdateCheckResult(ModelUpdateOutcome Outcome, string Message);

/// <summary>Laden, Prüfen oder Entfernen ist gescheitert (Text für den Benutzer, ohne Benutzerpfade).</summary>
public sealed class ModelPackageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Laden ohne Bestätigung des Benutzers — wird nie ausgeführt.</summary>
public sealed class ModelDownloadNotConfirmedException(string id)
    : InvalidOperationException($"Das Laden von „{id}“ wurde nicht bestätigt — es wird nichts heruntergeladen.");

/// <summary>
/// Angaben für den Bestätigungsdialog (Projektregel: Download nur nach Bestätigung mit Name, Quelle, Größe). Nur der
/// <see cref="IModelPackageManager"/> erzeugt sie; erst <see cref="Confirm"/> (nach „Laden“ im Dialog) gibt das Laden frei.
/// </summary>
public sealed class ModelDownloadRequest
{
    internal ModelDownloadRequest(ModelPackageDefinition definition, Uri? downloadUri, string? targetFolder)
    {
        Definition = definition;
        DownloadUri = downloadUri;
        TargetFolder = targetFolder;
    }

    public ModelPackageDefinition Definition { get; }

    public string Id => Definition.Id;

    /// <summary>Adresse, von der geladen wird (Host im Dialog).</summary>
    public Uri? DownloadUri { get; }

    /// <summary>Zielordner (nur für den Dialog, nie ins Protokoll).</summary>
    public string? TargetFolder { get; }

    public bool IsConfirmed { get; private set; }

    /// <summary>Nach „Laden“ im Dialog aufrufen.</summary>
    public ModelDownloadRequest Confirm()
    {
        IsConfirmed = true;
        return this;
    }
}
