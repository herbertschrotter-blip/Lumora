using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Faces.Models;
using Lumora.Services.Motifs.Models;

namespace Lumora.Services.ModelPackages;

/// <summary>Ein Modell aus Dateien wurde geladen oder entfernt (Nutzer laden ihr Modell neu).</summary>
public sealed record ModelPackageChangedMessage(string Id, ModelPackageState State);

/// <summary>
/// Allgemeines Modell aus einzelnen Dateien unter einer Basisadresse (ADR-0070) — der Weg für neue Modelle wie „Foto
/// verbessern“: jede Datei wird von <c>BaseUri + RelativePath</c> in <c>*.download</c> geladen, Größe und SHA-256 geprüft
/// und erst dann an ihren Platz verschoben; vorhandene Dateien mit passender Größe bleiben (Fortsetzen). Abbruch oder
/// Fehler lassen keine halbe Datei liegen. Danach <see cref="ModelPackageChangedMessage"/>.
/// </summary>
public sealed class FileSetModelPackageSource : FileModelPackageSource
{
    private readonly Uri _baseUri;
    private readonly IModelDownloader _downloader;
    private readonly IMessenger _messenger;

    public FileSetModelPackageSource(
        ModelPackageDefinition definition,
        Uri baseUri,
        IModelStorage storage,
        IModelDownloader downloader,
        ILogger<FileSetModelPackageSource>? logger = null,
        IMessenger? messenger = null)
        : base(definition, logger)
    {
        _baseUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        _downloader = downloader;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        Folder = Path.Combine(storage.ModelsRoot, definition.FolderName!);
    }

    public override string Folder { get; }

    public override Uri? DownloadUri => _baseUri;

    public override async Task DownloadAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct)
    {
        var pending = Definition.FileList.Where(f => SafeLengthOrMissing(PathOf(f)) != f.Size).ToList();
        var total = pending.Sum(f => f.Size);
        long done = 0;
        foreach (var file in pending)
        {
            var final = PathOf(file);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            var temp = final + ".download";
            try
            {
                var before = done;
                await _downloader.DownloadAsync(
                    new Uri(_baseUri, file.RelativePath), temp, file.Size,
                    new ActionProgress<long>(d => progress?.Report(new ModelPackageProgress("Wird heruntergeladen", before + d, total))), ct).ConfigureAwait(false);
                done = before + file.Size;
                if (SafeLength(temp) != file.Size)
                {
                    throw new ModelPackageException($"{file.RelativePath} hat nicht die erwartete Größe — die Datei wurde verworfen.");
                }

                progress?.Report(new ModelPackageProgress("Prüfsumme wird geprüft", done, total));
                var hash = await ComputeSha256Async(temp, null, ct).ConfigureAwait(false);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ModelPackageException($"Die Prüfsumme von {file.RelativePath} stimmt nicht — die Datei wurde verworfen.");
                }

                File.Move(temp, final, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        OnChanged();
    }

    protected override void OnChanged() => _messenger.Send(new ModelPackageChangedMessage(Definition.Id, ComputeStatus().State));

    private static long SafeLengthOrMissing(string path) => File.Exists(path) ? SafeLength(path) : -1;
}

/// <summary>Fortschritt ohne Umweg über den SynchronizationContext (Reihenfolge bleibt, der Empfänger stellt selbst um).</summary>
internal sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Gesichtserkennung (ADR-0035) im Manager: lädt über <see cref="IFaceModelProvider"/> (Archiv, SHA-256, nur zwei Dateien).</summary>
public sealed class FaceModelPackageSource(IFaceModelProvider provider, ILogger<FaceModelPackageSource>? logger = null, IMessenger? messenger = null)
    : FileModelPackageSource(ModelPackageCatalog.Faces, logger)
{
    private readonly IMessenger _messenger = messenger ?? WeakReferenceMessenger.Default;

    public override string Folder => provider.ModelFolder;

    public override bool IsOverridden => provider.IsOverridden;

    public override Uri? DownloadUri => provider.DownloadInfo.Source;

    public override Task DownloadAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct)
        => provider.DownloadAsync(progress is null ? null : new ActionProgress<FaceModelDownloadProgress>(p => progress.Report(new ModelPackageProgress(p.Stage, p.Done, p.Total))), ct);

    protected override void OnChanged() => _messenger.Send(new FaceModelChangedMessage(provider.State));
}

/// <summary>Motiv-Erkennung (ADR-0052) im Manager: lädt über <see cref="IMotifModelProvider"/> (je Datei SHA-256).</summary>
public sealed class MotifModelPackageSource(IMotifModelProvider provider, ILogger<MotifModelPackageSource>? logger = null, IMessenger? messenger = null)
    : FileModelPackageSource(ModelPackageCatalog.Motifs, logger)
{
    private readonly IMessenger _messenger = messenger ?? WeakReferenceMessenger.Default;

    public override string Folder => provider.ModelFolder;

    public override bool IsOverridden => provider.IsOverridden;

    public override Uri? DownloadUri => provider.DownloadInfo.BaseUri;

    public override Task DownloadAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct)
        => provider.DownloadAsync(progress is null ? null : new ActionProgress<MotifModelDownloadProgress>(p => progress.Report(new ModelPackageProgress(p.Stage, p.Done, p.Total))), ct);

    protected override void OnChanged() => _messenger.Send(new MotifModelChangedMessage(provider.State));
}
