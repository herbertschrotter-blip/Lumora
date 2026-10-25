using Lumora.Services.Assistant;
using Lumora.Services.Infrastructure;
using Lumora.Services.Places;

namespace Lumora.Services.ModelPackages;

/// <summary>Grundlage der Einträge ohne eigene Dateien (eingebaut, Zwischenspeicher, extern): kein Laden, kein Entfernen.</summary>
public abstract class InfoModelPackageSource(ModelPackageDefinition definition) : IModelPackageSource
{
    public ModelPackageDefinition Definition { get; } = definition;

    public ModelPackageCapabilities Capabilities => ModelPackageCapabilities.Verify;

    public virtual string? Folder => null;

    public bool IsOverridden => false;

    public Uri? DownloadUri => null;

    public abstract Task<ModelPackageStatus> GetStatusAsync(CancellationToken ct);

    public Task DownloadAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct)
        => throw new ModelPackageException($"„{Definition.Title}“ wird nicht von Lumora geladen.");

    public abstract Task<ModelVerifyResult> VerifyAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct);

    public Task RemoveAsync(CancellationToken ct)
        => throw new ModelPackageException($"„{Definition.Title}“ kann hier nicht entfernt werden.");
}

/// <summary>Ortsnamen (ADR-0050): eingebettete GeoNames-Daten; „Prüfen“ liest sie einmal vollständig.</summary>
public sealed class GeoNamesPackageSource : InfoModelPackageSource
{
    private readonly Func<Stream?> _open;

    /// <param name="open">Tests: eigene Daten; sonst die eingebettete Ressource.</param>
    public GeoNamesPackageSource(Func<Stream?>? open = null)
        : base(ModelPackageCatalog.GeoNames)
    {
        _open = open ?? (() => typeof(GeoNamesData).Assembly.GetManifestResourceStream(GeoNamesData.ResourceName));
    }

    public override Task<ModelPackageStatus> GetStatusAsync(CancellationToken ct)
    {
        using var stream = _open();
        return Task.FromResult(stream is null
            ? new ModelPackageStatus(ModelPackageState.Broken, "fehlt", "Die eingebauten Ortsnamen fehlen in dieser Lumora-Version.")
            : new ModelPackageStatus(ModelPackageState.BuiltIn, "eingebaut", "Mit Lumora ausgeliefert — nichts zu laden.", DiskBytes: stream.Length));
    }

    public override Task<ModelVerifyResult> VerifyAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            using var stream = _open();
            if (stream is null)
            {
                return new ModelVerifyResult(false, "Die eingebauten Ortsnamen fehlen.", [GeoNamesData.ResourceName]);
            }

            try
            {
                var data = GeoNamesData.Load(stream);
                return data.Count > 0
                    ? ModelVerifyResult.Success($"In Ordnung — {data.Count:N0} Orte in {data.CountryCodes.Length:N0} Ländern lesbar.")
                    : new ModelVerifyResult(false, "Die eingebauten Ortsnamen sind leer.", [GeoNamesData.ResourceName]);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or IndexOutOfRangeException)
            {
                return new ModelVerifyResult(false, $"Die eingebauten Ortsnamen sind nicht lesbar ({ex.GetType().Name}).", [GeoNamesData.ResourceName]);
            }
        }, ct);
}

/// <summary>
/// Kartenkacheln (ADR-0051/0055): Zwischenspeicher <c>&lt;CacheRoot&gt;\maptiles</c> des Katalogs (in der versteckten Sitzung
/// im Container). Nur Größe anzeigen; aufgeräumt wird über die Grenze (500 MB) bzw. Speicher &amp; Wartung.
/// </summary>
public sealed class MapTilesPackageSource(ICatalogStoragePaths storage) : InfoModelPackageSource(ModelPackageCatalog.MapTiles)
{
    public const string FolderName = "maptiles";

    public override string? Folder => Path.Combine(storage.CacheRoot, FolderName);

    public override Task<ModelPackageStatus> GetStatusAsync(CancellationToken ct)
        => Task.Run(() =>
        {
            var (files, bytes) = Measure(Folder!, ct);
            return new ModelPackageStatus(ModelPackageState.OnDemand, "bei Bedarf",
                files == 0 ? "Zwischenspeicher leer." : $"Zwischenspeicher {bytes / 1048576.0:N0} MB ({files:N0} Kacheln).", DiskBytes: bytes);
        }, ct);

    public override Task<ModelVerifyResult> VerifyAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var (files, bytes) = Measure(Folder!, ct);
            return ModelVerifyResult.Success(files == 0
                ? "Zwischenspeicher leer — Kacheln werden beim Ansehen der Karte geladen."
                : $"In Ordnung — {files:N0} Kacheln, {bytes / 1048576.0:N0} MB (Grenze 500 MB).");
        }, ct);

    private static (int Files, long Bytes) Measure(string folder, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
        {
            return (0, 0);
        }

        var files = 0;
        long bytes = 0;
        foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            files++;
            bytes += info.Length;
        }

        return (files, bytes);
    }
}

/// <summary>
/// Sprachmodell des Story-Assistenten (ADR-0063): nur Status über den lokalen Ollama-Dienst (127.0.0.1). Installiert und
/// lädt nichts — das bleibt in Einstellungen › Assistent.
/// </summary>
public sealed class OllamaPackageSource(Func<IAssistantRuntime?> runtime) : InfoModelPackageSource(ModelPackageCatalog.Assistant)
{
    public override async Task<ModelPackageStatus> GetStatusAsync(CancellationToken ct)
    {
        if (runtime() is not { } r)
        {
            return new ModelPackageStatus(ModelPackageState.ExternalMissing, "nicht verfügbar", "Der Story-Assistent ist in dieser Sitzung nicht eingerichtet.");
        }

        var status = await r.GetStatusAsync(ct).ConfigureAwait(false);
        var model = AssistantModels.Describe(status.Model);
        return status.State switch
        {
            AssistantRuntimeState.Ready => new ModelPackageStatus(ModelPackageState.ExternalReady, "bereit",
                $"Ollama {status.Version} mit „{status.Model}“ ({model.ApproximateSize}).", status.Version),
            AssistantRuntimeState.ModelMissing => new ModelPackageStatus(ModelPackageState.ExternalMissing, "Modell fehlt",
                $"Ollama {status.Version} läuft, „{status.Model}“ ist noch nicht geladen — in Einstellungen › Assistent laden.", status.Version),
            AssistantRuntimeState.NotRunning => new ModelPackageStatus(ModelPackageState.ExternalMissing, "läuft nicht",
                "Ollama ist installiert, läuft aber nicht — in Einstellungen › Assistent starten."),
            _ => new ModelPackageStatus(ModelPackageState.ExternalMissing, "nicht installiert",
                "Ollama ist nicht installiert — Einrichtung in Einstellungen › Assistent."),
        };
    }

    public override async Task<ModelVerifyResult> VerifyAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        return status.State == ModelPackageState.ExternalReady
            ? ModelVerifyResult.Success($"In Ordnung — {status.Detail}")
            : new ModelVerifyResult(false, status.Detail ?? status.Text, []);
    }
}
