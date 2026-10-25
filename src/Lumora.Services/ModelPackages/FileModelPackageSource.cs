using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.Services.ModelPackages;

/// <summary>Prüf-Vermerk im Modellordner (<see cref="FileModelPackageSource.ManifestFileName"/>): Version und Ergebnis der letzten Prüfung.</summary>
public sealed record ModelPackageManifest(string Id, string Version, bool Ok, DateTimeOffset CheckedUtc);

/// <summary>
/// Grundlage für Modelle aus Dateien in einem eigenen Ordner (ADR-0070): Zustand aus Vorhandensein und Größe, „Prüfen“
/// per SHA-256 gegen die Definition, „Entfernen“ nur der eigenen Dateien, Prüf-Vermerk <c>lumora-package.json</c> (Version,
/// Ergebnis, Zeitpunkt — keine Benutzerdaten). Das Laden selbst liefert die Unterklasse.
/// </summary>
public abstract class FileModelPackageSource : IModelPackageSource
{
    public const string ManifestFileName = "lumora-package.json";
    private const int BufferSize = 1 << 16;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    protected FileModelPackageSource(ModelPackageDefinition definition, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(definition.FolderName) || definition.FileList.Count == 0)
        {
            throw new ArgumentException("Ein Modell aus Dateien braucht FolderName und Files.", nameof(definition));
        }

        Definition = definition;
        Logger = logger ?? NullLogger.Instance;
    }

    public ModelPackageDefinition Definition { get; }

    protected ILogger Logger { get; }

    public abstract string Folder { get; }

    public virtual bool IsOverridden => false;

    public abstract Uri? DownloadUri { get; }

    public ModelPackageCapabilities Capabilities
        => IsOverridden ? ModelPackageCapabilities.Verify : ModelPackageCapabilities.Download | ModelPackageCapabilities.Verify | ModelPackageCapabilities.Remove;

    public string ManifestPath => Path.Combine(Folder, ManifestFileName);

    public string PathOf(ModelPackageFile file) => Path.Combine(Folder, file.LocalRelativePath);

    public Task<ModelPackageStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(ComputeStatus());

    /// <summary>Zustand ohne Prüfsummen: vorhanden, Größe, Prüf-Vermerk.</summary>
    public ModelPackageStatus ComputeStatus()
    {
        var present = Definition.FileList.Where(f => File.Exists(PathOf(f))).ToList();
        var bytes = present.Sum(f => SafeLength(PathOf(f)));
        var manifest = ReadManifest();
        if (present.Count == 0)
        {
            return new ModelPackageStatus(ModelPackageState.NotInstalled, "nicht geladen");
        }

        var missing = Definition.FileList.Where(f => f.Required && !present.Contains(f)).ToList();
        if (missing.Count > 0)
        {
            return new ModelPackageStatus(ModelPackageState.Broken, "unvollständig",
                $"Es fehlt: {string.Join(", ", missing.Select(f => f.RelativePath))}.", manifest?.Version, DiskBytes: bytes);
        }

        var wrongSize = present.Where(f => SafeLength(PathOf(f)) != f.Size).ToList();
        if (wrongSize.Count > 0)
        {
            return new ModelPackageStatus(ModelPackageState.Broken, "defekt",
                $"Größe stimmt nicht: {string.Join(", ", wrongSize.Select(f => f.RelativePath))}.", manifest?.Version, DiskBytes: bytes);
        }

        if (manifest is { Ok: false } && string.Equals(manifest.Version, Definition.Version, StringComparison.Ordinal))
        {
            return new ModelPackageStatus(ModelPackageState.Broken, "defekt", "Die letzte Prüfung hat falsche Prüfsummen gefunden.",
                manifest.Version, DiskBytes: bytes);
        }

        if (manifest is not null && !string.Equals(manifest.Version, Definition.Version, StringComparison.Ordinal))
        {
            return new ModelPackageStatus(ModelPackageState.UpdateAvailable, "Update verfügbar",
                $"Geladen: Version {manifest.Version}, verfügbar: Version {Definition.Version}.", manifest.Version, DiskBytes: bytes);
        }

        return new ModelPackageStatus(ModelPackageState.Installed, "geladen",
            manifest is null ? "Noch nicht geprüft." : null, manifest?.Version, manifest?.Ok == true ? manifest.CheckedUtc : null, bytes);
    }

    public abstract Task DownloadAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct);

    public async Task<ModelVerifyResult> VerifyAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct)
    {
        var bad = new List<string>();
        var present = Definition.FileList.Where(f => File.Exists(PathOf(f))).ToList();
        if (present.Count == 0)
        {
            return new ModelVerifyResult(false, "Nicht geladen — nichts zu prüfen.", Definition.FileList.Where(f => f.Required).Select(f => f.RelativePath).ToList());
        }

        bad.AddRange(Definition.FileList.Where(f => f.Required && !present.Contains(f)).Select(f => f.RelativePath));
        var total = present.Sum(f => SafeLength(PathOf(f)));
        long done = 0;
        foreach (var file in present)
        {
            var before = done;
            var hash = await ComputeSha256Async(PathOf(file), d => progress?.Report(new ModelPackageProgress("Prüfsumme wird geprüft", before + d, total)), ct).ConfigureAwait(false);
            done = before + SafeLength(PathOf(file));
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                bad.Add(file.RelativePath);
            }
        }

        return bad.Count == 0
            ? ModelVerifyResult.Success($"Alle {present.Count} Dateien stimmen mit der Prüfsumme (SHA-256) überein.")
            : new ModelVerifyResult(false, $"Stimmt nicht: {string.Join(", ", bad)}. „Laden…“ ersetzt die Dateien.", bad);
    }

    public Task RemoveAsync(CancellationToken ct)
    {
        if (IsOverridden)
        {
            throw new ModelPackageException("Der Modellordner ist über eine Umgebungsvariable vorgegeben — Lumora entfernt dort nichts.");
        }

        try
        {
            foreach (var file in Definition.FileList)
            {
                ct.ThrowIfCancellationRequested();
                DeleteFile(PathOf(file));
            }

            DeleteFile(ManifestPath);
            CleanupPartial();
            DeleteEmptyFolders();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelPackageException("Die Modell-Dateien sind gerade in Benutzung oder gesperrt. Bitte später (oder nach einem Neustart von Lumora) erneut versuchen.", ex);
        }

        OnChanged();
        return Task.CompletedTask;
    }

    /// <summary>Nach Laden/Entfernen: Nutzer des Modells benachrichtigen (z. B. Analysator neu laden).</summary>
    protected virtual void OnChanged()
    {
    }

    /// <summary>
    /// Vor dem Laden: hat die letzte Prüfung falsche Dateien gefunden oder ist eine ältere Version geladen („Update laden…“),
    /// werden alle eigenen Dateien neu geholt — die Anbieter überspringen sonst Dateien mit passender Größe.
    /// </summary>
    internal void PrepareDownload()
    {
        if (ReadManifest() is { } manifest && (!manifest.Ok || !string.Equals(manifest.Version, Definition.Version, StringComparison.Ordinal)))
        {
            foreach (var file in Definition.FileList)
            {
                DeleteFile(PathOf(file));
            }

            DeleteFile(ManifestPath);
        }
    }

    /// <summary>Angefangene Dateien (<c>*.download</c>, <c>*.tmp</c>) im Ordner löschen (nach Abbruch oder Fehler).</summary>
    internal void CleanupPartial()
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        foreach (var pattern in new[] { "*.download", "*.tmp" })
        {
            foreach (var path in Directory.EnumerateFiles(Folder, pattern, SearchOption.AllDirectories))
            {
                TryDelete(path);
            }
        }
    }

    public ModelPackageManifest? ReadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<ModelPackageManifest>(File.ReadAllText(ManifestPath), JsonOptions);
            return manifest is not null && string.Equals(manifest.Id, Definition.Id, StringComparison.Ordinal) ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.LogDebug(ex, "Modelle: Prüf-Vermerk von {Id} nicht lesbar", Definition.Id);
            return null;
        }
    }

    /// <summary>Prüf-Vermerk schreiben (nicht im vorgegebenen Entwicklungsordner).</summary>
    internal void WriteManifest(bool ok, DateTimeOffset checkedUtc)
    {
        if (IsOverridden || !Directory.Exists(Folder))
        {
            return;
        }

        try
        {
            var temp = ManifestPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new ModelPackageManifest(Definition.Id, Definition.Version, ok, checkedUtc), JsonOptions));
            File.Move(temp, ManifestPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("Modelle: Prüf-Vermerk von {Id} konnte nicht geschrieben werden ({Error})", Definition.Id, ex.GetType().Name);
        }
    }

    /// <summary>SHA-256 als Kleinbuchstaben-Hex, Fortschritt in gelesenen Bytes.</summary>
    public static async Task<string> ComputeSha256Async(string path, Action<long>? progress, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long done = 0;
        long lastReport = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            if (done - lastReport >= 1 << 22)
            {
                lastReport = done;
                progress?.Invoke(done);
            }
        }

        progress?.Invoke(done);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    protected static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    protected void TryDelete(string path)
    {
        try
        {
            DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug("Modelle: angefangene Datei von {Id} konnte nicht gelöscht werden ({Error})", Definition.Id, ex.GetType().Name);
        }
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void DeleteEmptyFolders()
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(Folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(Folder).Any())
        {
            Directory.Delete(Folder);
        }
    }
}
