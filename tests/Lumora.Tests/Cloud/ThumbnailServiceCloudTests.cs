using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Thumbnails;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Cloud;

/// <summary>
/// Kriterium 2 (ADR-0077): Vorschaubilder für Platzhalter über die Shell (hier ein Fake), nie das Original dekodieren,
/// Ergebnis in Lumoras normalem Cache (gleiche Datei, gleicher Status); ohne Shell-Weg kein Download und kein verbrauchter
/// Versuch; zuletzt angeforderte zuerst, begrenzter Rückstau; Vorrat lässt Platzhalter aus (Kriterium 4).
/// Platzhalter = Datei mit Offline-Attribut (wie „nur online“).
/// </summary>
public sealed class ThumbnailServiceCloudTests
{
    private const int LibraryId = 1;
    private static readonly DateTime Modified = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Platzhalter_bekommt_Shell_Vorschaubild_im_normalen_Cache_ohne_das_Original()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        var cloud = new FakeCloudRenderer();
        using var svc = Create(fx, image, cloud);
        var ready = new TaskCompletionSource<ThumbnailReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.ThumbnailReady += e => ready.TrySetResult(e);
        var request = Placeholder(fx, 42);

        await svc.RequestThumbnailsAsync([request]);
        var args = await ready.Task.WaitAsync(Timeout);
        await WaitForIdleAsync(svc);

        image.Calls.Should().Be(0, "das Original eines Platzhalters wird nie dekodiert (Download)");
        cloud.Paths.Should().Equal(request.SourcePath);
        args.ThumbnailPath.Should().Be(Path.Combine(fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"42_{ThumbnailService.Size}_still.jpg"));
        File.Exists(args.ThumbnailPath).Should().BeTrue();
        var row = await RowAsync(fx, 42);
        row.Status.Should().Be(ThumbnailStatus.Generated);
        row.CachePath.Should().Be(args.ThumbnailPath);
        row.SourceModifiedAtUtc.Should().Be(Modified);
        File.GetAttributes(request.SourcePath).Should().HaveFlag(FileAttributes.Offline, "die Datei bleibt nur online");
        Unmark(request.SourcePath);
    }

    [Fact]
    public async Task Ohne_Shell_Weg_scheitert_der_Platzhalter_ohne_Versuch_und_ohne_Download()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        using var svc = Create(fx, image, cloud: null);
        var request = Placeholder(fx, 7);

        await svc.RequestThumbnailsAsync([request]);
        await WaitForIdleAsync(svc);
        await WaitUntilAsync(async () => (await TryRowAsync(fx, 7))?.Status == ThumbnailStatus.Failed);

        image.Calls.Should().Be(0);
        var row = await RowAsync(fx, 7);
        row.ErrorCode.Should().Be(ThumbnailErrorCodes.CloudUnavailable);
        row.AttemptCount.Should().Be(0, "äußere Ursache — der Platzhalter ist nicht kaputt");
        Unmark(request.SourcePath);
    }

    [Fact]
    public async Task Lokale_Datei_nimmt_weiter_den_normalen_Renderer()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        var cloud = new FakeCloudRenderer();
        using var svc = Create(fx, image, cloud);
        var ready = new TaskCompletionSource<ThumbnailReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.ThumbnailReady += e => ready.TrySetResult(e);

        await svc.RequestThumbnailsAsync([new ThumbnailRequest(LibraryId, 3, fx.WriteLibraryFile("lokal.jpg"), MediaType.Image, Modified)]);
        await ready.Task.WaitAsync(Timeout);

        image.Calls.Should().Be(1);
        cloud.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Gescheiterter_Shell_Abruf_verbraucht_keinen_Versuch()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var cloud = new FakeCloudRenderer { Fail = true };
        using var svc = Create(fx, new FakeImageRenderer(), cloud);
        var request = Placeholder(fx, 9);

        await svc.RequestThumbnailsAsync([request]);
        await WaitUntilAsync(async () => (await TryRowAsync(fx, 9))?.Status == ThumbnailStatus.Failed);
        await WaitForIdleAsync(svc);

        var row = await RowAsync(fx, 9);
        row.ErrorCode.Should().Be(ThumbnailErrorCodes.CloudUnavailable);
        row.AttemptCount.Should().Be(0, "keine Verbindung ist kein Fehler der Datei");
        Unmark(request.SourcePath);
    }

    [Fact]
    public async Task Vorrat_laesst_Platzhalter_aus()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        var cloud = new FakeCloudRenderer();
        using var svc = Create(fx, image, cloud);
        var request = Placeholder(fx, 11);

        (await svc.CreateStockAsync(request)).Should().Be(ThumbnailStockOutcome.Skipped);

        image.Calls.Should().Be(0);
        cloud.Paths.Should().BeEmpty("Vorschaubilder von Platzhaltern nur bei Bedarf, nie auf Vorrat (Spike 19)");
        (await TryRowAsync(fx, 11)).Should().BeNull();
        Unmark(request.SourcePath);
    }

    [Fact]
    public async Task Zuletzt_angeforderte_Platzhalter_zuerst()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var gate = new SemaphoreSlim(0);
        var cloud = new FakeCloudRenderer { Gate = gate };
        using var svc = Create(fx, new FakeImageRenderer(), cloud);
        var first = Enumerable.Range(100, ThumbnailService.CloudWorkerCount).Select(id => Placeholder(fx, id)).ToList();
        var older = Enumerable.Range(200, 3).Select(id => Placeholder(fx, id)).ToList();
        var newer = Enumerable.Range(300, 3).Select(id => Placeholder(fx, id)).ToList();

        // alle Worker belegen, dann eine ältere und eine neuere Anforderung einreihen
        await svc.RequestThumbnailsAsync(first);
        await WaitUntilAsync(() => Task.FromResult(cloud.Started == first.Count));
        await svc.RequestThumbnailsAsync(older);
        await svc.RequestThumbnailsAsync(newer);
        await WaitUntilAsync(() => Task.FromResult(svc.CloudQueueLength == 6));

        // je ein Worker frei → er nimmt den nächsten (so ist die Reihenfolge des Nehmens die des Startens)
        for (var i = 1; i <= 6; i++)
        {
            gate.Release();
            var expected = first.Count + i;
            await WaitUntilAsync(() => Task.FromResult(cloud.Started == expected));
        }

        gate.Release(first.Count + 6);
        await WaitForIdleAsync(svc);

        var order = cloud.StartedPaths.Skip(first.Count).Select(Path.GetFileNameWithoutExtension).ToList();
        order.Should().Equal("p300", "p301", "p302", "p200", "p201", "p202");
        foreach (var r in first.Concat(older).Concat(newer))
        {
            Unmark(r.SourcePath);
        }
    }

    [Fact]
    public void Warteschlange_nimmt_den_hoechsten_Rang_und_laesst_die_niedrigsten_verfallen()
    {
        var ranks = new Dictionary<int, long> { [1] = 10, [2] = 30, [3] = 20, [4] = 5 };
        var queue = new CloudJobQueue(3, id => ranks[id]);
        ThumbnailRequest Req(int id) => new(LibraryId, id, $"C:\\x\\{id}.jpg", MediaType.Image, Modified);

        queue.Add(Req(1)).Should().BeEmpty();
        queue.Add(Req(2)).Should().BeEmpty();
        queue.Add(Req(3)).Should().BeEmpty();
        queue.Add(Req(4)).Select(r => r.FileId).Should().Equal(4);
        ranks[1] = 40; // erneut angefordert (wieder sichtbar)

        queue.TryTake(out var a).Should().BeTrue();
        queue.TryTake(out var b).Should().BeTrue();
        queue.TryTake(out var c).Should().BeTrue();
        new[] { a.FileId, b.FileId, c.FileId }.Should().Equal(1, 2, 3);
        queue.TryTake(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Zu_viele_wartende_Platzhalter_verfallen_und_zaehlen_nicht_mehr()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var gate = new SemaphoreSlim(0);
        var cloud = new FakeCloudRenderer { Gate = gate };
        using var svc = Create(fx, new FakeImageRenderer(), cloud);
        var busy = Enumerable.Range(1000, ThumbnailService.CloudWorkerCount).Select(id => Placeholder(fx, id)).ToList();
        var waiting = Enumerable.Range(2000, ThumbnailService.MaxCloudBacklog + 5).Select(id => Placeholder(fx, id)).ToList();
        var requests = busy.Concat(waiting).ToList();

        await svc.RequestThumbnailsAsync(busy);
        await WaitUntilAsync(() => Task.FromResult(cloud.Started == ThumbnailService.CloudWorkerCount));
        await svc.RequestThumbnailsAsync(waiting);
        await WaitUntilAsync(() => Task.FromResult(svc.PendingCount == ThumbnailService.CloudWorkerCount + ThumbnailService.MaxCloudBacklog));
        svc.CloudQueueLength.Should().Be(ThumbnailService.MaxCloudBacklog);
        gate.Release(requests.Count);
        await WaitForIdleAsync(svc);

        cloud.Paths.Should().HaveCount(ThumbnailService.CloudWorkerCount + ThumbnailService.MaxCloudBacklog, "die 5 mit dem niedrigsten Rang verfallen");
        cloud.Paths.Select(Path.GetFileNameWithoutExtension).Should().NotContain(waiting.TakeLast(5).Select(r => Path.GetFileNameWithoutExtension(r.SourcePath)),
            "innerhalb einer Anforderung zählen die vorderen (sichtbaren) mehr");
        foreach (var r in requests)
        {
            Unmark(r.SourcePath);
        }
    }

    private static ThumbnailService Create(TempLibraryFixture fx, FakeImageRenderer image, ICloudThumbnailRenderer? cloud)
        => new(fx.ContextFactory, fx.Paths, image, new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance, cloudRenderer: cloud)
        {
            MaintenanceStartDelay = System.Threading.Timeout.InfiniteTimeSpan,
        };

    private static ThumbnailRequest Placeholder(TempLibraryFixture fx, int fileId)
    {
        var path = fx.WriteLibraryFile($"p{fileId}.jpg");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        return new ThumbnailRequest(LibraryId, fileId, path, MediaType.Image, Modified);
    }

    private static void Unmark(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal); // sonst räumt die Fixture die Datei nicht weg
        }
    }

    private static async Task WaitForIdleAsync(ThumbnailService svc)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (svc.PendingCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        svc.PendingCount.Should().Be(0);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "Bedingung nicht rechtzeitig erfüllt");
            await Task.Delay(10);
        }
    }

    private static async Task<Thumbnail?> TryRowAsync(TempLibraryFixture fx, int fileId)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Thumbnails.AsNoTracking().SingleOrDefaultAsync(t => t.FileId == fileId && t.Size == ThumbnailService.Size);
    }

    private static async Task<Thumbnail> RowAsync(TempLibraryFixture fx, int fileId)
        => (await TryRowAsync(fx, fileId))!;

    /// <summary>Shell-Ersatz: schreibt ein Fake-Vorschaubild, merkt sich die Reihenfolge, kann warten oder scheitern.</summary>
    private sealed class FakeCloudRenderer : ICloudThumbnailRenderer
    {
        private readonly ConcurrentQueue<string> _paths = new();
        private readonly ConcurrentQueue<string> _startedPaths = new();
        private int _started;

        public bool Fail { get; init; }

        public SemaphoreSlim? Gate { get; init; }

        public bool IsAvailable => true;

        public int Started => Volatile.Read(ref _started);

        public List<string> Paths => [.. _paths];

        public List<string> StartedPaths => [.. _startedPaths];

        public async Task<ThumbnailRenderResult> RenderAsync(string sourcePath, string targetThumbPath, int targetShortEdge, CancellationToken ct)
        {
            _startedPaths.Enqueue(sourcePath);
            Interlocked.Increment(ref _started);
            if (Gate is not null)
            {
                await Gate.WaitAsync(ct);
            }

            _paths.Enqueue(sourcePath);
            return Fail
                ? ThumbnailRenderResult.Fail(ThumbnailErrorCodes.CloudUnavailable, "keine Verbindung (Fake)")
                : FakeRenderHelper.WriteThumb(targetThumbPath, targetShortEdge);
        }
    }
}
