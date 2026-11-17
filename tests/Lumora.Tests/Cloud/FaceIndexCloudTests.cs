using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Messaging;
using Lumora.Tests.Faces;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Cloud;

/// <summary>
/// Kriterium 4 (ADR-0077): Die Gesichtserkennung lässt Platzhalter aus (lädt das Original nicht — das wäre ein Download)
/// und vermerkt sie nicht; sobald die Datei lokal ist (<see cref="CloudFilesChangedMessage"/>), holt ein Durchgang sie nach.
/// </summary>
public sealed class FaceIndexCloudTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Platzhalter_wird_ausgelassen_und_nach_dem_Laden_nachgeholt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("lokal.jpg");
        var online = fx.WriteLibraryFile("online.jpg");
        await fx.ScanAsync(lib.Id);
        File.SetAttributes(online, File.GetAttributes(online) | FileAttributes.Offline);
        var loader = new RecordingLoader();
        using var service = new FaceIndexService(
            fx.ContextFactory,
            fx.CreateLibraryService(),
            new FakeFaceAnalyzerFactory(),
            loader,
            new MemoryFaceSettingsStore(new FaceIndexSettings(true, true)),
            NullLogger<FaceIndexService>.Instance,
            fx.Messenger,
            new FaceIndexOptions { StartDelay = TimeSpan.Zero, StatusInterval = TimeSpan.Zero, SuspendedRetryDelay = TimeSpan.FromMilliseconds(200) });
        await service.StartAsync();
        await WaitUntilAsync(() => service.CompletedRuns >= 1 && service.Status.State == FaceIndexState.Idle, "erster Durchgang");

        loader.Paths.Should().NotContain(online, "das Original eines Platzhalters wird nie geladen");
        FaceIndexScalar(fx, "SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId WHERE f.Name = 'online.jpg'").Should().Be(0, "nicht vermerkt — später nachholen");
        FaceIndexScalar(fx, "SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId WHERE f.Name = 'lokal.jpg'").Should().Be(1);

        File.SetAttributes(online, FileAttributes.Normal); // OneDrive hat die Datei geladen
        var id = (await fx.FindFileAsync("online.jpg"))!.Id;
        var runs = service.CompletedRuns;
        fx.Messenger.Send(new CloudFilesChangedMessage(lib.Id, [id], BecameLocal: true));
        await WaitUntilAsync(() => service.CompletedRuns > runs && service.Status.State == FaceIndexState.Idle, "Durchgang nach „lokal“");

        loader.Paths.Should().Contain(online);
        FaceIndexScalar(fx, "SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId WHERE f.Name = 'online.jpg'").Should().Be(1);
    }

    private static long FaceIndexScalar(TempLibraryFixture fx, string sql) => FaceIndexServiceTests.Scalar(fx, sql);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timeout: {what}");
            }

            await Task.Delay(20);
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
}
