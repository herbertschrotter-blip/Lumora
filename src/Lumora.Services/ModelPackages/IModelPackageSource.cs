namespace Lumora.Services.ModelPackages;

/// <summary>
/// Ein Eintrag in Einstellungen › Modelle &amp; Daten (ADR-0070): kennt seine Definition, seinen Zustand und — je nach
/// <see cref="Capabilities"/> — wie er geladen, geprüft und entfernt wird. Aufgerufen wird nur über den
/// <see cref="IModelPackageManager"/> (Bestätigung, Aktivitäts-Anzeige, Prüf-Vermerk, ein Vorgang je Eintrag).
/// </summary>
public interface IModelPackageSource
{
    ModelPackageDefinition Definition { get; }

    ModelPackageCapabilities Capabilities { get; }

    /// <summary>Ordner der Dateien (Anzeige, Dialog); null ohne eigene Dateien. Nie ins Protokoll.</summary>
    string? Folder { get; }

    /// <summary>
    /// true, wenn eine Umgebungsvariable einen Entwicklungs-/Testordner vorgibt (<c>LUMORA_FACE_MODELS</c>,
    /// <c>LUMORA_MOTIF_MODEL</c>) — dann nur anzeigen und prüfen, nie laden, entfernen oder einen Vermerk schreiben.
    /// </summary>
    bool IsOverridden { get; }

    /// <summary>Adresse, von der geladen wird (für den Bestätigungsdialog); null ohne Download.</summary>
    Uri? DownloadUri { get; }

    /// <summary>Zustand ohne Prüfsummen (schnell; für die Liste).</summary>
    Task<ModelPackageStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Lädt und legt die Dateien ab (nur über den Manager nach Bestätigung). Abbruch/Fehler: keine halben Dateien.</summary>
    Task DownloadAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct);

    /// <summary>Prüft die Dateien (SHA-256) bzw. die Daten.</summary>
    Task<ModelVerifyResult> VerifyAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct);

    /// <summary>Entfernt die Dateien des Eintrags (nur die eigenen, nicht den ganzen Ordner).</summary>
    Task RemoveAsync(CancellationToken ct);
}

/// <summary>Wurzel der Modell-Ordner: <c>&lt;AppData&gt;\models</c> — auch in der versteckten Sitzung der normale Ordner.</summary>
public interface IModelStorage
{
    string ModelsRoot { get; }
}

/// <inheritdoc />
public sealed class ModelStorage(string modelsRoot) : IModelStorage
{
    public string ModelsRoot { get; } = Path.GetFullPath(modelsRoot);
}

/// <summary>Lädt eine Datei (HTTP bzw. in Tests ein Fake). Nur über den Manager nach Bestätigung.</summary>
public interface IModelDownloader
{
    /// <summary>
    /// Schreibt <paramref name="source"/> nach <paramref name="target"/>; bricht ab, wenn mehr als
    /// <paramref name="expectedSize"/> Bytes kommen (0 = ohne Grenze). Fortschritt in Bytes.
    /// </summary>
    Task DownloadAsync(Uri source, string target, long expectedSize, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>Holt Metadaten für „Update suchen“ (nur die Anfrage selbst, keine Daten über den Benutzer).</summary>
public interface IModelMetadataClient
{
    Task<string> GetStringAsync(Uri uri, CancellationToken ct);
}
