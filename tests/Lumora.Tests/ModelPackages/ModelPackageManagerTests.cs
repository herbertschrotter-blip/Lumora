using System.Net;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Activity;
using Lumora.Services.Assistant;
using Lumora.Services.Faces.Models;
using Lumora.Services.Infrastructure;
using Lumora.Services.ModelPackages;
using Lumora.Services.Motifs.Models;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.ModelPackages;

/// <summary>
/// Update-Manager „Modelle &amp; Daten“ (ADR-0070) mit Fake-Downloader und Fake-Metadaten — es gibt in Tests nie einen echten
/// Download: Bestätigung fehlt → kein Abruf; Laden mit SHA-256, Prüf-Vermerk und Aktivität; falsche Prüfsumme → verworfen
/// bzw. „defekt“; Abbruch räumt auf; Entfernen; „Update suchen“ nur Metadaten, ohne Quelle „keine automatische Prüfung“;
/// vorgegebener Entwicklungsordner nur prüfen; eingebaute Daten, Kartenkacheln und Ollama nur Status.
/// </summary>
public sealed class ModelPackageManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumora-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- feste Einträge -------------------------------------------------------------------------

    [Fact]
    public void Katalog_hat_Pruefsummen_Lizenzen_und_Update_Quellen()
    {
        var faces = ModelPackageCatalog.Faces;
        faces.LicenseRestricted.Should().BeTrue();
        faces.License.Should().Contain("nicht-kommerzielle");
        faces.UpdateSource.Should().BeNull("InsightFace bietet keine Versionsliste");
        faces.DownloadSize.Should().Be(FaceModelDownloadInfo.BuffaloL.Size);
        faces.FileList.Select(f => f.RelativePath).Should().Equal(IFaceModelProvider.DetectorFileName, IFaceModelProvider.RecognizerFileName);

        var motifs = ModelPackageCatalog.Motifs;
        motifs.FileList.Select(f => (f.RelativePath, f.Size, f.Sha256, f.Required))
            .Should().Equal(MotifModelDownloadInfo.Siglip2Base.Files.Select(f => (f.RelativePath, f.Size, f.Sha256, f.Required)));
        motifs.UpdateSource!.ManifestUri.Scheme.Should().Be(Uri.UriSchemeHttps);
        motifs.FolderName.Should().Be(IMotifModelProvider.FolderName);

        foreach (var d in new[] { faces, motifs })
        {
            d.FileList.Should().OnlyContain(f => f.Sha256.Length == 64 && f.Size > 0);
            d.Origin.Website!.Scheme.Should().Be(Uri.UriSchemeHttps);
        }

        ModelUpdateChecker.NoSourceText(faces).Should().Be("Version 0.7, keine automatische Prüfung.");
    }

    // ---- Laden ------------------------------------------------------------------------------------

    [Fact]
    public async Task Ohne_Bestaetigung_wird_nichts_geladen()
    {
        var (manager, downloader, _) = CreateFileSet(out _);
        var request = manager.DescribeDownload(TestDefinitionId);
        request.IsConfirmed.Should().BeFalse();

        var act = () => manager.InstallAsync(request);

        await act.Should().ThrowAsync<ModelDownloadNotConfirmedException>();
        downloader.Requests.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "models")).Should().BeFalse();
    }

    [Fact]
    public async Task Laden_nach_Bestaetigung_prueft_SHA256_schreibt_Vermerk_und_meldet_Aktivitaet()
    {
        var (manager, downloader, activities) = CreateFileSet(out var files);
        var source = (FileModelPackageSource)manager.Find(TestDefinitionId)!;
        (await manager.GetStatusAsync(TestDefinitionId)).State.Should().Be(ModelPackageState.NotInstalled);
        var request = manager.DescribeDownload(TestDefinitionId);
        request.DownloadUri!.Host.Should().Be("example.invalid");
        request.TargetFolder.Should().Be(source.Folder);
        var progress = new List<ModelPackageProgress>();

        var status = await manager.InstallAsync(request.Confirm(), new InlineProgress<ModelPackageProgress>(progress.Add));

        status.State.Should().Be(ModelPackageState.Installed);
        status.InstalledVersion.Should().Be("3");
        status.CheckedUtc.Should().NotBeNull();
        downloader.Requests.Should().HaveCount(files.Count);
        File.ReadAllBytes(source.PathOf(source.Definition.FileList[1])).Should().Equal(files["sub/b.bin"]);
        source.ReadManifest().Should().BeEquivalentTo(new { Id = TestDefinitionId, Version = "3", Ok = true });
        Directory.EnumerateFiles(source.Folder, "*.download", SearchOption.AllDirectories).Should().BeEmpty();
        progress.Should().Contain(p => p.Stage == "Wird heruntergeladen").And.Contain(p => p.Stage == "Prüfsumme wird geprüft");
        var done = activities.GetOverview().Recent.Should().ContainSingle().Subject;
        done.Kind.Should().Be(ActivityKind.ModelDownload);
        done.State.Should().Be(ActivityState.Succeeded);
        done.UserInitiated.Should().BeTrue();
        done.Result.Should().Be("Testmodell geladen und geprüft");
    }

    [Fact]
    public async Task Falsche_Pruefsumme_beim_Download_verwirft_die_Datei()
    {
        var (manager, downloader, activities) = CreateFileSet(out _);
        downloader.Corrupt = "sub/b.bin";

        var act = () => manager.InstallAsync(manager.DescribeDownload(TestDefinitionId).Confirm());

        (await act.Should().ThrowAsync<ModelPackageException>()).Which.Message.Should().Contain("Prüfsumme von sub/b.bin");
        var source = (FileModelPackageSource)manager.Find(TestDefinitionId)!;
        File.Exists(source.PathOf(source.Definition.FileList[1])).Should().BeFalse("die falsche Datei wird nicht an ihren Platz gelegt");
        Directory.EnumerateFiles(source.Folder, "*.download", SearchOption.AllDirectories).Should().BeEmpty();
        activities.GetOverview().Recent.Single().State.Should().Be(ActivityState.Failed);
    }

    [Fact]
    public async Task Falsche_Pruefsumme_nach_dem_Laden_markiert_defekt_und_naechstes_Laden_holt_alles_neu()
    {
        var folder = Path.Combine(_root, "models", "buffalo_l");
        var provider = new WritingFaceProvider(folder);
        var manager = new ModelPackageManager([new FaceModelPackageSource(provider, messenger: new WeakReferenceMessenger())], new FakeMetadata());

        var act = () => manager.InstallAsync(manager.DescribeDownload(ModelPackageCatalog.FacesId).Confirm());

        (await act.Should().ThrowAsync<ModelPackageException>()).Which.Message.Should().Contain("defekt");
        var status = await manager.GetStatusAsync(ModelPackageCatalog.FacesId);
        status.State.Should().Be(ModelPackageState.Broken, "Größe passt, Prüfsumme nicht");
        status.Text.Should().Be("defekt");

        // erneutes Laden: der Vermerk „defekt“ lässt alle eigenen Dateien vorher löschen
        provider.Downloads = 0;
        await act.Should().ThrowAsync<ModelPackageException>();
        provider.Downloads.Should().Be(1);
        provider.SawExistingFilesBeforeDownload.Should().BeFalse();
    }

    [Fact]
    public async Task Abbruch_raeumt_angefangene_Dateien_auf()
    {
        var (manager, downloader, activities) = CreateFileSet(out _);
        using var cts = new CancellationTokenSource();
        downloader.OnRequest = uri =>
        {
            if (uri.AbsoluteUri.EndsWith("sub/b.bin", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };

        var act = () => manager.InstallAsync(manager.DescribeDownload(TestDefinitionId).Confirm(), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var source = (FileModelPackageSource)manager.Find(TestDefinitionId)!;
        Directory.EnumerateFiles(source.Folder, "*.download", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.EnumerateFiles(source.Folder, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists(source.ManifestPath).Should().BeFalse();
        (await manager.GetStatusAsync(TestDefinitionId)).State.Should().Be(ModelPackageState.Broken, "a.onnx ist da, b.bin fehlt → unvollständig");
        activities.GetOverview().Recent.Single().State.Should().Be(ActivityState.Canceled);
        manager.IsBusy(TestDefinitionId).Should().BeFalse();
    }

    [Fact]
    public async Task Abbrechen_ueber_die_Aktivitaets_Anzeige()
    {
        var (manager, downloader, activities) = CreateFileSet(out _);
        downloader.OnRequest = _ =>
        {
            var running = activities.GetOverview().Running.Single();
            running.CanCancel.Should().BeTrue();
            activities.Cancel(running.Id);
        };

        var act = () => manager.InstallAsync(manager.DescribeDownload(TestDefinitionId).Confirm());

        await act.Should().ThrowAsync<OperationCanceledException>();
        activities.GetOverview().Recent.Single().State.Should().Be(ActivityState.Canceled);
    }

    // ---- Prüfen, Entfernen, Zustände ------------------------------------------------------------

    [Fact]
    public async Task Pruefen_findet_verfaelschte_Datei_und_Entfernen_raeumt_den_Ordner()
    {
        var (manager, _, _) = CreateFileSet(out _);
        await manager.InstallAsync(manager.DescribeDownload(TestDefinitionId).Confirm());
        var source = (FileModelPackageSource)manager.Find(TestDefinitionId)!;
        (await manager.VerifyAsync(TestDefinitionId)).Ok.Should().BeTrue();

        var path = source.PathOf(source.Definition.FileList[0]);
        var bytes = File.ReadAllBytes(path);
        bytes[3] ^= 0x55;
        File.WriteAllBytes(path, bytes);
        var result = await manager.VerifyAsync(TestDefinitionId);

        result.Ok.Should().BeFalse();
        result.BadFiles.Should().Equal("a.onnx");
        (await manager.GetStatusAsync(TestDefinitionId)).State.Should().Be(ModelPackageState.Broken);

        var changed = new List<string>();
        manager.PackageChanged += changed.Add;
        await manager.RemoveAsync(TestDefinitionId);
        Directory.Exists(source.Folder).Should().BeFalse();
        (await manager.GetStatusAsync(TestDefinitionId)).State.Should().Be(ModelPackageState.NotInstalled);
        changed.Should().Equal(TestDefinitionId);
    }

    [Fact]
    public async Task Aelterer_Vermerk_zeigt_Update_verfuegbar_ohne_Netz_und_Update_laden_holt_alle_Dateien_neu()
    {
        var (manager, downloader, _) = CreateFileSet(out var files);
        await manager.InstallAsync(manager.DescribeDownload(TestDefinitionId).Confirm());
        var source = (FileModelPackageSource)manager.Find(TestDefinitionId)!;
        File.WriteAllText(source.ManifestPath, File.ReadAllText(source.ManifestPath).Replace("\"Version\": \"3\"", "\"Version\": \"2\"", StringComparison.Ordinal));

        var status = await manager.GetStatusAsync(TestDefinitionId);
        status.State.Should().Be(ModelPackageState.UpdateAvailable);
        status.Detail.Should().Be("Geladen: Version 2, verfügbar: Version 3.");
        var update = await manager.CheckForUpdateAsync(TestDefinitionId);
        update.Outcome.Should().Be(ModelUpdateOutcome.UpdateReady);

        // „Update laden…“: Dateien gleicher Größe dürfen nicht übersprungen werden
        downloader.Requests.Clear();
        var after = await manager.InstallAsync(manager.DescribeDownload(TestDefinitionId).Confirm());
        downloader.Requests.Should().HaveCount(files.Count);
        after.State.Should().Be(ModelPackageState.Installed);
        after.InstalledVersion.Should().Be("3");
    }

    [Fact]
    public async Task Unvollstaendig_und_falsche_Groesse_sind_defekt_ohne_Pruefsumme()
    {
        var (manager, _, _) = CreateFileSet(out _);
        var source = (FileModelPackageSource)manager.Find(TestDefinitionId)!;
        Directory.CreateDirectory(source.Folder);
        File.WriteAllBytes(source.PathOf(source.Definition.FileList[0]), new byte[10]);

        var status = await manager.GetStatusAsync(TestDefinitionId);
        status.State.Should().Be(ModelPackageState.Broken);
        status.Text.Should().Be("unvollständig");
    }

    [Fact]
    public async Task Gleichzeitiger_zweiter_Vorgang_wird_abgelehnt()
    {
        var (manager, downloader, _) = CreateFileSet(out _);
        var gate = new TaskCompletionSource();
        downloader.Gate = gate.Task;
        var install = manager.InstallAsync(manager.DescribeDownload(TestDefinitionId).Confirm());
        await WaitUntilAsync(() => manager.IsBusy(TestDefinitionId));

        var second = () => manager.VerifyAsync(TestDefinitionId);

        (await second.Should().ThrowAsync<ModelPackageException>()).Which.Message.Should().Contain("schon ein Vorgang");
        gate.SetResult();
        (await install).State.Should().Be(ModelPackageState.Installed);
    }

    [Fact]
    public async Task Vorgegebener_Entwicklungsordner_wird_nur_geprueft()
    {
        var provider = new WritingFaceProvider(Path.Combine(_root, "dev"), overridden: true);
        var manager = new ModelPackageManager([new FaceModelPackageSource(provider)], new FakeMetadata());
        var source = manager.Packages.Single();

        source.Capabilities.Should().Be(ModelPackageCapabilities.Verify);
        var describe = () => manager.DescribeDownload(ModelPackageCatalog.FacesId);
        describe.Should().Throw<ModelPackageException>().Which.Message.Should().Contain("Umgebungsvariable");
        var remove = () => manager.RemoveAsync(ModelPackageCatalog.FacesId);
        await remove.Should().ThrowAsync<ModelPackageException>();
        provider.Downloads.Should().Be(0);
    }

    [Fact]
    public async Task Entfernen_der_Gesichter_meldet_das_geaenderte_Modell()
    {
        var folder = Path.Combine(_root, "models", "buffalo_l");
        Directory.CreateDirectory(folder);
        foreach (var file in ModelPackageCatalog.Faces.FileList)
        {
            File.WriteAllBytes(Path.Combine(folder, file.RelativePath), [1, 2, 3]);
        }

        File.WriteAllBytes(Path.Combine(folder, "fremd.txt"), [9]);
        var messenger = new WeakReferenceMessenger();
        var states = new List<FaceModelState>();
        messenger.Register<List<FaceModelState>, FaceModelChangedMessage>(states, (r, m) => r.Add(m.State));
        var manager = new ModelPackageManager([new FaceModelPackageSource(new WritingFaceProvider(folder), messenger: messenger)], new FakeMetadata());

        await manager.RemoveAsync(ModelPackageCatalog.FacesId);

        states.Should().Equal(FaceModelState.Missing);
        Directory.EnumerateFiles(folder).Select(Path.GetFileName).Should().Equal(["fremd.txt"], "nur die eigenen Dateien werden entfernt");
    }

    // ---- Update suchen --------------------------------------------------------------------------

    [Fact]
    public async Task Update_suchen_ohne_Quelle_fragt_nichts_ab()
    {
        var metadata = new FakeMetadata();
        var manager = new ModelPackageManager([new FaceModelPackageSource(new WritingFaceProvider(Path.Combine(_root, "f")))], metadata);

        var result = await manager.CheckForUpdateAsync(ModelPackageCatalog.FacesId);

        result.Outcome.Should().Be(ModelUpdateOutcome.NoSource);
        result.Message.Should().Be("Version 0.7, keine automatische Prüfung.");
        metadata.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_suchen_bei_Hugging_Face_vergleicht_nur_die_LFS_Pruefsummen()
    {
        var metadata = new FakeMetadata();
        var motifs = ModelPackageCatalog.Motifs;
        metadata.Responses["https://huggingface.co/api/models/onnx-community/siglip2-base-patch16-224-ONNX/tree/main"] =
            Tree(motifs.FileList.Where(f => !f.RelativePath.Contains('/', StringComparison.Ordinal)));
        metadata.Responses["https://huggingface.co/api/models/onnx-community/siglip2-base-patch16-224-ONNX/tree/main/onnx"] =
            Tree(motifs.FileList.Where(f => f.RelativePath.StartsWith("onnx/", StringComparison.Ordinal)));
        var manager = new ModelPackageManager([new MotifModelPackageSource(new FakeMotifProvider(Path.Combine(_root, "m")))], metadata);

        var current = await manager.CheckForUpdateAsync(ModelPackageCatalog.MotifsId);
        current.Outcome.Should().Be(ModelUpdateOutcome.UpToDate);
        metadata.Requests.Should().OnlyContain(u => u.Host == "huggingface.co" && string.IsNullOrEmpty(u.Query), "nur die Anfrage selbst, keine Parameter");

        metadata.Responses["https://huggingface.co/api/models/onnx-community/siglip2-base-patch16-224-ONNX/tree/main/onnx"] =
            Tree(motifs.FileList.Where(f => f.RelativePath.StartsWith("onnx/", StringComparison.Ordinal)), changeFirst: true);
        var newer = await manager.CheckForUpdateAsync(ModelPackageCatalog.MotifsId);
        newer.Outcome.Should().Be(ModelUpdateOutcome.NewerAtVendor);
        newer.Message.Should().Contain("onnx/vision_model.onnx").And.Contain("Lumora-Update");

        metadata.Fail = true;
        (await manager.CheckForUpdateAsync(ModelPackageCatalog.MotifsId)).Outcome.Should().Be(ModelUpdateOutcome.Failed);
    }

    // ---- Daten und Extern -----------------------------------------------------------------------

    [Fact]
    public async Task Ortsnamen_sind_eingebaut_und_pruefbar()
    {
        var source = new GeoNamesPackageSource();
        var status = await source.GetStatusAsync(CancellationToken.None);
        status.State.Should().Be(ModelPackageState.BuiltIn);
        status.DiskBytes.Should().BeGreaterThan(100_000);
        source.Capabilities.Should().Be(ModelPackageCapabilities.Verify);
        var verify = await source.VerifyAsync(null, CancellationToken.None);
        verify.Ok.Should().BeTrue();
        verify.Message.Should().Contain("Orte");

        var broken = new GeoNamesPackageSource(() => new MemoryStream([1, 2, 3]));
        (await broken.VerifyAsync(null, CancellationToken.None)).Ok.Should().BeFalse();
        (await new GeoNamesPackageSource(() => null).GetStatusAsync(CancellationToken.None)).State.Should().Be(ModelPackageState.Broken);
    }

    [Fact]
    public async Task Kartenkacheln_zeigen_die_Groesse_des_Zwischenspeichers()
    {
        var storage = new CachePaths(Path.Combine(_root, "cache"));
        var source = new MapTilesPackageSource(storage);
        (await source.GetStatusAsync(CancellationToken.None)).Detail.Should().Be("Zwischenspeicher leer.");
        var tile = Path.Combine(storage.CacheRoot, "maptiles", "osm", "3", "4", "5.png");
        Directory.CreateDirectory(Path.GetDirectoryName(tile)!);
        File.WriteAllBytes(tile, new byte[2048]);

        var status = await source.GetStatusAsync(CancellationToken.None);

        status.State.Should().Be(ModelPackageState.OnDemand);
        status.DiskBytes.Should().Be(2048);
        status.Detail.Should().Contain("1 Kacheln");
    }

    [Theory]
    [InlineData(AssistantRuntimeState.Ready, ModelPackageState.ExternalReady, "bereit")]
    [InlineData(AssistantRuntimeState.ModelMissing, ModelPackageState.ExternalMissing, "Modell fehlt")]
    [InlineData(AssistantRuntimeState.NotRunning, ModelPackageState.ExternalMissing, "läuft nicht")]
    [InlineData(AssistantRuntimeState.NotInstalled, ModelPackageState.ExternalMissing, "nicht installiert")]
    public async Task Ollama_nur_Status(AssistantRuntimeState runtime, ModelPackageState expected, string text)
    {
        var fake = new FakeRuntime(new AssistantRuntimeStatus(runtime, "qwen2.5:3b", runtime is AssistantRuntimeState.Ready or AssistantRuntimeState.ModelMissing ? "0.12.0" : null));
        var manager = new ModelPackageManager([new OllamaPackageSource(() => fake)], new FakeMetadata());

        var status = await manager.GetStatusAsync(ModelPackageCatalog.AssistantId);

        status.State.Should().Be(expected);
        status.Text.Should().Be(text);
        manager.Packages.Single().Capabilities.Should().Be(ModelPackageCapabilities.Verify);
        var describe = () => manager.DescribeDownload(ModelPackageCatalog.AssistantId);
        describe.Should().Throw<ModelPackageException>();
        fake.Pulls.Should().Be(0, "Lumora lädt das Ollama-Modell hier nie");
    }

    // ---- HTTP-Downloader ------------------------------------------------------------------------

    [Fact]
    public async Task Http_Downloader_bricht_bei_zu_grosser_Antwort_ab()
    {
        var handler = new BytesHandler(new byte[3 << 20]);
        var downloader = new HttpModelDownloader(new HttpClient(handler));
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "x.download");

        var act = () => downloader.DownloadAsync(new Uri("https://example.invalid/x"), target, 1 << 20, null, CancellationToken.None);

        await act.Should().ThrowAsync<ModelPackageException>();
        var ok = new List<long>();
        await downloader.DownloadAsync(new Uri("https://example.invalid/x"), target, 3 << 20, new InlineProgress<long>(ok.Add), CancellationToken.None);
        new FileInfo(target).Length.Should().Be(3 << 20);
        ok.Last().Should().Be(3 << 20);
        handler.UserAgents.Should().OnlyContain(a => a == "Lumora");
    }

    [Fact]
    public async Task Metadaten_nur_ueber_HTTPS()
    {
        var client = new HttpModelMetadataClient(new HttpClient(new BytesHandler("[]"u8.ToArray())));
        var act = () => client.GetStringAsync(new Uri("http://example.invalid/x"), CancellationToken.None);
        await act.Should().ThrowAsync<ModelPackageException>();
        (await client.GetStringAsync(new Uri("https://example.invalid/x"), CancellationToken.None)).Should().Be("[]");
    }

    // ---- Hilfen ---------------------------------------------------------------------------------

    private const string TestDefinitionId = "test.modell";

    private (ModelPackageManager Manager, FakeDownloader Downloader, ActivityCenter Activities) CreateFileSet(out Dictionary<string, byte[]> files)
    {
        files = new Dictionary<string, byte[]>
        {
            ["a.onnx"] = Encoding.UTF8.GetBytes(new string('a', 4096)),
            ["sub/b.bin"] = Encoding.UTF8.GetBytes(new string('b', 2048)),
        };
        var definition = new ModelPackageDefinition(
            TestDefinitionId, "Testmodell", "Test", "Zum Testen.", ModelPackageGroup.Model, "3",
            new ModelPackageOrigin("Test", new Uri("https://example.invalid/")), "MIT",
            DownloadSize: files.Values.Sum(v => (long)v.Length),
            FolderName: "test",
            Files: files.Select(kv => new ModelPackageFile(kv.Key, kv.Value.Length, Sha(kv.Value))).ToList());
        var downloader = new FakeDownloader(files);
        var source = new FileSetModelPackageSource(definition, new Uri("https://example.invalid/m"), new ModelStorage(Path.Combine(_root, "models")), downloader, messenger: new WeakReferenceMessenger());
        var activities = new ActivityCenter(TimeProvider.System);
        return (new ModelPackageManager([source], new FakeMetadata(), activities), downloader, activities);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Tree(IEnumerable<ModelPackageFile> files, bool changeFirst = false)
    {
        var entries = files.Select((f, i) => f.Size >= 1_000_000
            ? $"{{\"type\":\"file\",\"path\":\"{f.RelativePath}\",\"size\":{f.Size},\"lfs\":{{\"oid\":\"{(changeFirst && i == 0 ? new string('0', 64) : f.Sha256)}\",\"size\":{f.Size}}}}}"
            : $"{{\"type\":\"file\",\"path\":\"{f.RelativePath}\",\"oid\":\"abc\",\"size\":{f.Size}}}");
        return "[" + string.Join(",", entries) + "]";
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(until);
            await Task.Delay(10);
        }
    }

    private sealed class FakeDownloader(Dictionary<string, byte[]> files) : IModelDownloader
    {
        public List<Uri> Requests { get; } = [];

        public string? Corrupt { get; set; }

        public Action<Uri>? OnRequest { get; set; }

        public Task? Gate { get; set; }

        public async Task DownloadAsync(Uri source, string target, long expectedSize, IProgress<long>? progress, CancellationToken ct)
        {
            Requests.Add(source);
            if (Gate is not null)
            {
                await Gate;
            }

            var name = files.Keys.Single(k => source.AbsoluteUri.EndsWith("/m/" + k, StringComparison.Ordinal));
            var bytes = (byte[])files[name].Clone();
            if (name == Corrupt)
            {
                bytes[0] ^= 0xFF;
            }

            await File.WriteAllBytesAsync(target, bytes[..(bytes.Length / 2)], ct);
            OnRequest?.Invoke(source);
            ct.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(target, bytes, ct);
            progress?.Report(bytes.Length);
        }
    }

    private sealed class FakeMetadata : IModelMetadataClient
    {
        public Dictionary<string, string> Responses { get; } = [];

        public List<Uri> Requests { get; } = [];

        public bool Fail { get; set; }

        public Task<string> GetStringAsync(Uri uri, CancellationToken ct)
        {
            Requests.Add(uri);
            return Fail ? throw new HttpRequestException("offline") : Task.FromResult(Responses[uri.AbsoluteUri]);
        }
    }

    /// <summary>„Lädt“, indem es die beiden Dateien mit richtiger Größe, aber falschem Inhalt schreibt.</summary>
    private sealed class WritingFaceProvider(string folder, bool overridden = false) : IFaceModelProvider
    {
        public int Downloads { get; set; }

        public bool SawExistingFilesBeforeDownload { get; private set; }

        public string ModelFolder { get; } = folder;

        public bool IsOverridden { get; } = overridden;

        public string DetectorPath => Path.Combine(ModelFolder, IFaceModelProvider.DetectorFileName);

        public string RecognizerPath => Path.Combine(ModelFolder, IFaceModelProvider.RecognizerFileName);

        public FaceModelState State => File.Exists(DetectorPath) && File.Exists(RecognizerPath) ? FaceModelState.Ready : FaceModelState.Missing;

        public FaceModelDownloadInfo DownloadInfo => FaceModelDownloadInfo.BuffaloL;

        public Task DownloadAsync(IProgress<FaceModelDownloadProgress>? progress, CancellationToken ct)
        {
            Downloads++;
            SawExistingFilesBeforeDownload = File.Exists(DetectorPath);
            Directory.CreateDirectory(ModelFolder);
            foreach (var file in ModelPackageCatalog.Faces.FileList)
            {
                using var stream = File.Create(Path.Combine(ModelFolder, file.RelativePath));
                stream.SetLength(file.Size);
            }

            progress?.Report(new FaceModelDownloadProgress("Wird heruntergeladen", 1, 1));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMotifProvider(string folder) : IMotifModelProvider
    {
        public string ModelFolder { get; } = folder;

        public bool IsOverridden => false;

        public MotifModelState State => MotifModelState.Missing;

        public MotifModelDownloadInfo DownloadInfo => MotifModelDownloadInfo.Siglip2Base;

        public string PathOf(string relativePath) => Path.Combine(ModelFolder, relativePath);

        public Task DownloadAsync(IProgress<MotifModelDownloadProgress>? progress, CancellationToken ct) => throw new InvalidOperationException("kein Download im Test");
    }

    private sealed class FakeRuntime(AssistantRuntimeStatus status) : IAssistantRuntime
    {
        public int Pulls { get; private set; }

        public Task<AssistantRuntimeStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(status);

        public Task PullModelAsync(string model, IProgress<OllamaPullProgress>? progress, CancellationToken ct)
        {
            Pulls++;
            return Task.CompletedTask;
        }

        public bool TryStartOllama() => false;
    }

    private sealed class CachePaths(string cacheRoot) : ICatalogStoragePaths
    {
        public bool IsHidden => false;

        public string CacheRoot { get; } = cacheRoot;

        public string TempRoot => CacheRoot;
    }

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
