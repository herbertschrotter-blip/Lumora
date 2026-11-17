using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Cloud;
using Lumora.Services.Faces.Crops;
using Lumora.Services.Messaging;
using Lumora.Services.Motifs;
using Lumora.Tests.Faces;
using Lumora.Tests.Faces.Assignment;
using Lumora.Tests.Fixtures;
using Lumora.Tests.Metadata;
using Lumora.Tests.Motifs;

namespace Lumora.Tests.Cloud;

/// <summary>
/// Kriterium 4 und 8 (ADR-0077, Nachbesserung 0.5.41): Motive und Gesichts-Ausschnitte laden das Original eines
/// Platzhalters nie (das wäre ein Download), merken ihn nicht als gescheitert und holen ihn nach, sobald er lokal ist; in der
/// versteckten Sitzung fragt Lumora die Shell nie nach Eigenschaften. Platzhalter = Datei mit Offline-Attribut.
/// </summary>
public sealed class CloudAnalysisSkipTests
{
    [Fact]
    public async Task Motive_laden_das_Original_eines_Platzhalters_nie_und_holen_es_nach()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        var online = fx.WriteLibraryFile("p2_katze.jpg");
        await fx.ScanAsync(lib.Id);
        var onlineId = (await fx.FindFileAsync("p2_katze.jpg"))!.Id;
        File.SetAttributes(online, File.GetAttributes(online) | FileAttributes.Offline);
        try
        {
            var loader = new RecordingLoader();
            var factory = new FakeMotifEncoderFactory();
            var settings = new MemoryMotifSettingsStore(MotifSettings.Default);
            var store = new MotifStore(fx.ContextFactory);
            var terms = new MotifTermService(store, factory, settings, messenger: fx.Messenger);
            using var service = new MotifIndexService(
                fx.CreateLibraryService(), factory, loader, settings, terms, store, NullLogger<MotifIndexService>.Instance,
                null, null, fx.Messenger,
                new MotifIndexOptions { StartDelay = TimeSpan.Zero, StatusInterval = TimeSpan.Zero, SuspendedRetryDelay = TimeSpan.FromMilliseconds(200) });
            await service.StartAsync();
            await MotifIndexServiceTests.WaitUntilAsync(() => service.CompletedRuns >= 1 && service.Status.State == MotifIndexState.Idle, "erster Durchgang");

            loader.Paths.Should().NotContain(online, "das Original eines Platzhalters wird nie geladen");
            MotifIndexServiceTests.Scalar(fx, $"SELECT COUNT(*) FROM FileEmbeddings WHERE FileId = {onlineId}").Should().Be(0, "nicht vermerkt — später nachholen");
            MotifIndexServiceTests.Scalar(fx, "SELECT COUNT(*) FROM FileEmbeddings").Should().Be(1, "das lokale Foto ist analysiert");

            File.SetAttributes(online, FileAttributes.Normal); // OneDrive hat die Datei geladen
            var runs = service.CompletedRuns;
            fx.Messenger.Send(new CloudFilesChangedMessage(lib.Id, [onlineId], BecameLocal: true));
            await MotifIndexServiceTests.WaitUntilAsync(() => service.CompletedRuns > runs && service.Status.State == MotifIndexState.Idle, "Durchgang nach „lokal“");

            loader.Paths.Should().Contain(online);
            MotifIndexServiceTests.Scalar(fx, $"SELECT COUNT(*) FROM FileEmbeddings WHERE FileId = {onlineId} AND ErrorCode IS NULL").Should().Be(1);
        }
        finally
        {
            File.SetAttributes(online, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Gesichts_Ausschnitt_wartet_beim_Platzhalter_und_entsteht_sobald_er_lokal_ist()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var files = await world.CreateFilesAsync(1);
        var face = world.InsertFace(files[0], world.Embedding("a"));
        var path = Path.Combine(world.Fx.LibraryRoot, (await world.Fx.FindFileByIdAsync(files[0]))!.Name);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        try
        {
            var renderer = new CountingCropRenderer();
            using var service = new FaceCropService(world.Fx.ContextFactory, world.Fx.Paths, renderer, NullLogger<FaceCropService>.Instance, world.Fx.Messenger, TimeSpan.FromMilliseconds(50));
            service.Start();

            service.Request(world.LibraryId, [face]);
            await Task.Delay(400);
            renderer.Calls.Should().Be(0, "das Original eines Platzhalters wird nie geladen");
            File.Exists(service.GetCropPath(world.LibraryId, face)).Should().BeFalse();

            File.SetAttributes(path, FileAttributes.Normal); // jetzt lokal
            service.Request(world.LibraryId, [face]);
            await MotifIndexServiceTests.WaitUntilAsync(() => File.Exists(service.GetCropPath(world.LibraryId, face)), "Ausschnitt nach „lokal“");
            renderer.Calls.Should().Be(1, "nicht als gescheitert gemerkt — die nächste Anforderung rendert");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Versteckte_Sitzung_fragt_die_Shell_nie_nach_dem_Aufnahmedatum()
    {
        var dir = Directory.CreateTempSubdirectory("lumora-cloud-hidden-");
        try
        {
            var path = Path.Combine(dir.FullName, "foto.jpg");
            File.WriteAllBytes(path, SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12")));
            var catalog = new TestCatalogContext();
            catalog.SetHidden(true);
            using var reader = new ShellCloudPropertyReader(NullLogger<ShellCloudPropertyReader>.Instance, catalog);

            reader.ReadCaptureDate(path).Should().BeNull("keine Spuren in der Shell aus der versteckten Bibliothek");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class RecordingLoader : Lumora.Services.Faces.IFaceImageLoader
    {
        private readonly FakeFaceImageLoader _inner = new();
        private readonly List<string> _paths = new();

        public List<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        public Lumora.Services.Faces.FaceImage Load(string path, int maxEdge)
        {
            lock (_paths)
            {
                _paths.Add(path);
            }

            return _inner.Load(path, maxEdge);
        }
    }

    private sealed class CountingCropRenderer : IFaceCropRenderer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Render(string sourcePath, IReadOnlyList<FaceCropTarget> targets, int size)
        {
            Interlocked.Increment(ref _calls);
            foreach (var target in targets)
            {
                File.WriteAllBytes(target.TargetPath, [0xFF, 0xD8]);
            }
        }
    }
}
