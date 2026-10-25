using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.ModelPackages;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Ersatz für „Modelle &amp; Daten“ (ADR-0070): ein Testmodell mit zwei kleinen Dateien, deren Inhalt der
/// Fake-Downloader aus dem Namen erzeugt — nur für die Adresse <see cref="BaseUri"/>, jede andere Adresse wird abgelehnt (kein
/// echter Download). Update-Suche fragt nichts im Netz ab.
/// </summary>
internal static class SmokeModelPackages
{
    public const string Id = "smoke.testmodell";
    public static readonly Uri BaseUri = new("https://rauchtest.lumora.invalid/modell/");

    private static readonly (string Path, int Size)[] Files = [("testmodell.onnx", 300_000), ("daten/vokabular.bin", 40_000)];

    public static ModelPackageDefinition Definition { get; } = new(
        Id,
        "Testmodell",
        "Lumora-Rauchtest-Modell",
        "Nur im Rauchtest: Laden, Prüfen, Defekt, Entfernen ohne echten Download.",
        ModelPackageGroup.Model,
        "1",
        new ModelPackageOrigin("Lumora-Rauchtest", null),
        "nur Test",
        DownloadSize: Files.Sum(f => (long)f.Size),
        FolderName: "smoke-testmodell",
        Files: Files.Select(f => new ModelPackageFile(f.Path, f.Size, Convert.ToHexStringLower(SHA256.HashData(Content(f.Path, f.Size))))).ToList());

    /// <summary>Deterministischer Inhalt je Datei.</summary>
    public static byte[] Content(string path, int size)
    {
        var bytes = new byte[size];
        new Random(path.Sum(c => (int)c) ^ size).NextBytes(bytes); // fester Startwert (string.GetHashCode ist je Prozess zufällig)
        return bytes;
    }

    /// <summary>Im Rauchtest: Fake-Downloader/Metadaten statt HTTP, Testmodell als weiterer Eintrag.</summary>
    public static void Register(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IModelDownloader, SmokeModelDownloader>());
        services.Replace(ServiceDescriptor.Singleton<IModelMetadataClient, SmokeModelMetadataClient>());
        services.AddFileSetModelPackage(Definition, BaseUri);
    }
}

/// <summary>Liefert nur die Dateien des Testmodells; zählt Abrufe.</summary>
internal sealed class SmokeModelDownloader : IModelDownloader
{
    private int _requests;

    public int Requests => _requests;

    public async Task DownloadAsync(Uri source, string target, long expectedSize, IProgress<long>? progress, CancellationToken ct)
    {
        if (!string.Equals(source.Host, SmokeModelPackages.BaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelPackageException("Rauchtest: kein echter Download.");
        }

        Interlocked.Increment(ref _requests);
        var relative = SmokeModelPackages.BaseUri.MakeRelativeUri(source).ToString();
        var file = SmokeModelPackages.Definition.FileList.Single(f => f.RelativePath == relative);
        var bytes = SmokeModelPackages.Content(file.RelativePath, (int)file.Size);
        await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
        progress?.Report(bytes.Length);
    }
}

/// <summary>Keine Netzabfrage im Rauchtest.</summary>
internal sealed class SmokeModelMetadataClient : IModelMetadataClient
{
    public List<Uri> Requests { get; } = [];

    public Task<string> GetStringAsync(Uri uri, CancellationToken ct)
    {
        Requests.Add(uri);
        throw new ModelPackageException("Rauchtest: keine Netzabfrage.");
    }
}
