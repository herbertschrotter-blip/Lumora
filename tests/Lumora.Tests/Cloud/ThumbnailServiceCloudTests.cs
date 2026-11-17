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
/// Versuch; zuletzt angeforderte zuerst, begrenzter Rückstau, der die jüngste Anforderung nie kürzt; Vorrat lässt Platzhalter
/// aus (Kriterium 4); „nur Cache“ meldet vorhandene ohne Shell-Abruf (Nachbesserung 0.5.41).
/// Platzhalter = Datei mit Offline-Attribut (wie „nur online“).
/// </summary>
public sealed class ThumbnailServiceCloudTests
{
    private const int LibraryId = 1;

    /// <summary>Fehlercode des WIC-Encoders (Wpf-Projekt) — auch der Shell-Renderer meldet ihn, wenn das Schreiben scheitert.</summary>
    private const string EncodeFailed = "wic_encode_failed";
    private static readonly DateTime Modified = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30); // über 100 Abrufe mit je zwei DB-Schreibvorgängen, auch unter Last der ganzen Suite

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
    public void Warteschlange_laesst_die_juengste_Anforderung_nie_verfallen()
    {
        const long stride = 100;
        var ranks = new Dictionary<int, long>();
        var queue = new CloudJobQueue(3, id => ranks[id], stride, hardLimit: 10);
        ThumbnailRequest Req(int id) => new(LibraryId, id, $"C:\\x\\{id}.jpg", MediaType.Image, Modified);

        // eine Anforderung (Ränge 100, 99, …) mit 5 Aufträgen bei 3 Plätzen: nichts verfällt
        for (var i = 0; i < 5; i++)
        {
            ranks[i] = stride - i;
            queue.Add(Req(i)).Should().BeEmpty("die jüngste Anforderung wird nie gekürzt");
        }

        queue.Count.Should().Be(5);

        // die nächste Anforderung (Ränge 200, …): die ältere schrumpft, von hinten
        ranks[10] = 2 * stride;
        queue.Add(Req(10)).Select(r => r.FileId).Should().Equal(4, 3, 2);
        queue.Count.Should().Be(3);
        queue.TryTake(out var first).Should().BeTrue();
        first.FileId.Should().Be(10);
    }

    [Fact]
    public void Auch_die_juengste_Anforderung_hat_eine_harte_Obergrenze()
    {
        var ranks = new Dictionary<int, long>();
        var queue = new CloudJobQueue(3, id => ranks[id], rankStride: 100, hardLimit: 5);
        ThumbnailRequest Req(int id) => new(LibraryId, id, $"C:\\x\\{id}.jpg", MediaType.Image, Modified);
        var dropped = new List<int>();

        for (var i = 0; i < 7; i++)
        {
            ranks[i] = 100 - i;
            dropped.AddRange(queue.Add(Req(i)).Select(r => r.FileId));
        }

        queue.Count.Should().Be(5, "Alben, Orte oder Storys können mehr schicken als je sichtbar ist");
        dropped.Should().Equal(5, 6);
        ThumbnailService.MaxCloudQueue.Should().BeGreaterThan(ThumbnailService.MaxCloudBacklog);
    }

    [Fact]
    public async Task Vorrat_sperrt_eine_Zeile_mit_cloud_unavailable_nicht_endgueltig()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var path = fx.WriteLibraryFile("jetzt-lokal.jpg"); // war ein Platzhalter, liegt jetzt lokal
        await using (var ctx = fx.CreateContext())
        {
            ctx.Thumbnails.Add(new Thumbnail
            {
                FileId = 27, Size = ThumbnailService.Size, Kind = ThumbnailKind.Still, Format = "jpg",
                CachePath = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"27_{ThumbnailService.Size}_still.jpg"),
                SourceModifiedAtUtc = Modified, Status = ThumbnailStatus.Failed, AttemptCount = ThumbnailService.MaxAttempts,
                ErrorCode = ThumbnailErrorCodes.CloudUnavailable,
            });
            await ctx.SaveChangesAsync();
        }

        var image = new FakeImageRenderer();
        using var svc = Create(fx, image, new FakeCloudRenderer());

        (await svc.CreateStockAsync(new ThumbnailRequest(LibraryId, 27, path, MediaType.Image, Modified))).Should().Be(ThumbnailStockOutcome.Generated);
        image.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Eine_Anforderung_mit_mehr_Platzhaltern_als_Plaetze_bekommt_alle_Vorschaubilder()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var gate = new SemaphoreSlim(0);
        var cloud = new FakeCloudRenderer { Gate = gate };
        using var svc = Create(fx, new FakeImageRenderer(), cloud);
        var busy = Enumerable.Range(1000, ThumbnailService.CloudWorkerCount).Select(id => Placeholder(fx, id)).ToList();
        var visible = Enumerable.Range(2000, ThumbnailService.MaxCloudBacklog + 8).Select(id => Placeholder(fx, id)).ToList();
        var requests = busy.Concat(visible).ToList();
        var ready = new ConcurrentDictionary<int, byte>();
        svc.ThumbnailReady += e => ready.TryAdd(e.FileId, 0);

        await svc.RequestThumbnailsAsync(busy);
        await WaitUntilAsync(() => Task.FromResult(cloud.Started == ThumbnailService.CloudWorkerCount));
        await svc.RequestThumbnailsAsync(visible); // z. B. großer Bildschirm, kleine Kacheln: mehr als 96 sichtbar
        await WaitUntilAsync(() => Task.FromResult(svc.CloudQueueLength == visible.Count));
        svc.PendingCount.Should().Be(requests.Count, "kein Auftrag der jüngsten Anforderung ist verfallen");
        gate.Release(requests.Count);
        await WaitForIdleAsync(svc);

        cloud.Paths.Should().HaveCount(requests.Count);
        ready.Keys.Should().BeEquivalentTo(requests.Select(r => r.FileId), "jede Kachel bekommt ihr Vorschaubild, ohne zu scrollen");
        foreach (var r in requests)
        {
            Unmark(r.SourcePath);
        }
    }

    [Fact]
    public async Task Aeltere_Anforderung_verfaellt_erst_mit_der_naechsten_und_von_hinten()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var gate = new SemaphoreSlim(0);
        var cloud = new FakeCloudRenderer { Gate = gate };
        using var svc = Create(fx, new FakeImageRenderer(), cloud);
        var busy = Enumerable.Range(1000, ThumbnailService.CloudWorkerCount).Select(id => Placeholder(fx, id)).ToList();
        var older = Enumerable.Range(2000, ThumbnailService.MaxCloudBacklog + 5).Select(id => Placeholder(fx, id)).ToList();
        var newer = Enumerable.Range(3000, 3).Select(id => Placeholder(fx, id)).ToList();
        var requests = busy.Concat(older).Concat(newer).ToList();

        await svc.RequestThumbnailsAsync(busy);
        await WaitUntilAsync(() => Task.FromResult(cloud.Started == ThumbnailService.CloudWorkerCount));
        await svc.RequestThumbnailsAsync(older);
        await WaitUntilAsync(() => Task.FromResult(svc.CloudQueueLength == older.Count));
        await svc.RequestThumbnailsAsync(newer); // weitergescrollt
        await WaitUntilAsync(() => Task.FromResult(svc.PendingCount == ThumbnailService.CloudWorkerCount + ThumbnailService.MaxCloudBacklog));
        svc.CloudQueueLength.Should().Be(ThumbnailService.MaxCloudBacklog);
        gate.Release(requests.Count);
        await WaitForIdleAsync(svc);

        var dropped = older.Count + newer.Count - ThumbnailService.MaxCloudBacklog;
        cloud.Paths.Should().HaveCount(requests.Count - dropped);
        var rendered = cloud.Paths.Select(Path.GetFileNameWithoutExtension).ToList();
        rendered.Should().Contain(newer.Select(r => Path.GetFileNameWithoutExtension(r.SourcePath)), "die jüngste Anforderung verfällt nie");
        rendered.Should().NotContain(older.TakeLast(dropped).Select(r => Path.GetFileNameWithoutExtension(r.SourcePath)),
            "innerhalb einer Anforderung zählen die vorderen (sichtbaren) mehr");
        foreach (var r in requests)
        {
            Unmark(r.SourcePath);
        }
    }

    [Fact]
    public async Task Abgebrochener_Shell_Abruf_hinterlaesst_keinen_Versuch()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var cloud = new FakeCloudRenderer { Gate = new SemaphoreSlim(0) };
        var svc = Create(fx, new FakeImageRenderer(), cloud);
        var request = Placeholder(fx, 21);

        await svc.RequestThumbnailsAsync([request]);
        await WaitUntilAsync(() => Task.FromResult(cloud.Started == 1));
        await WaitUntilAsync(async () => (await TryRowAsync(fx, 21))?.Status == ThumbnailStatus.Pending);
        svc.Dispose(); // App wird beendet, während OneDrive noch liefert

        var row = await RowAsync(fx, 21);
        row.AttemptCount.Should().Be(0, "ein abgebrochener Shell-Abruf ist kein Versuch — sonst sperrten drei Abbrüche die Datei für immer");
        Unmark(request.SourcePath);
    }

    [Fact]
    public async Task Alte_gesperrte_Zeile_mit_cloud_unavailable_wird_erneut_versucht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var request = Placeholder(fx, 23);
        await using (var ctx = fx.CreateContext())
        {
            ctx.Thumbnails.Add(new Thumbnail
            {
                FileId = 23, Size = ThumbnailService.Size, Kind = ThumbnailKind.Still, Format = "jpg",
                CachePath = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"23_{ThumbnailService.Size}_still.jpg"),
                SourceModifiedAtUtc = Modified, Status = ThumbnailStatus.Failed, AttemptCount = ThumbnailService.MaxAttempts,
                ErrorCode = ThumbnailErrorCodes.CloudUnavailable,
            });
            await ctx.SaveChangesAsync();
        }

        var cloud = new FakeCloudRenderer();
        using var svc = Create(fx, new FakeImageRenderer(), cloud);
        await svc.RequestThumbnailsAsync([request]);
        await WaitUntilAsync(async () => (await TryRowAsync(fx, 23))?.Status == ThumbnailStatus.Generated);

        cloud.Paths.Should().Equal(request.SourcePath);
        Unmark(request.SourcePath);
    }

    [Fact]
    public async Task Echter_Fehler_beim_Platzhalter_zaehlt_einen_Versuch()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var cloud = new FakeCloudRenderer { FailCode = EncodeFailed };
        using var svc = Create(fx, new FakeImageRenderer(), cloud);
        var request = Placeholder(fx, 25);

        await svc.RequestThumbnailsAsync([request]);
        await WaitUntilAsync(async () => (await TryRowAsync(fx, 25))?.Status == ThumbnailStatus.Failed);
        await WaitForIdleAsync(svc);

        var row = await RowAsync(fx, 25);
        row.ErrorCode.Should().Be(EncodeFailed);
        row.AttemptCount.Should().Be(1, "Schreiben des Vorschaubilds gescheitert — ein Fehler, kein fehlendes Netz");
        Unmark(request.SourcePath);
    }

    [Fact]
    public async Task Nur_Cache_meldet_vorhandene_Vorschaubilder_ohne_Shell_Abruf_und_reiht_nichts_ein()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var cached = Placeholder(fx, 31);
        var missing = Placeholder(fx, 32);
        var cloud = new FakeCloudRenderer();
        using (var first = Create(fx, new FakeImageRenderer(), cloud))
        {
            await first.RequestThumbnailsAsync([cached]);
            await WaitUntilAsync(async () => (await TryRowAsync(fx, 31))?.Status == ThumbnailStatus.Generated);
            await WaitForIdleAsync(first);
        }

        var later = new FakeCloudRenderer();
        using var svc = Create(fx, new FakeImageRenderer(), later); // nächster Besuch der Seite (neue Sitzung)
        var ready = new ConcurrentQueue<int>();
        svc.ThumbnailReady += e => ready.Enqueue(e.FileId);

        await svc.ReportCachedThumbnailsAsync([cached, missing]);
        await Task.Delay(200);

        ready.Should().Equal(31);
        svc.PendingCount.Should().Be(0, "nur Cache: nichts eingereiht");
        later.Started.Should().Be(0, "kein Shell-Abruf");
        (await TryRowAsync(fx, 32)).Should().BeNull();
        Unmark(cached.SourcePath);
        Unmark(missing.SourcePath);
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

        /// <summary>Fehlercode statt Erfolg (Standard bei <see cref="Fail"/>: cloud_unavailable).</summary>
        public string? FailCode { get; init; }

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
            return FailCode is { } code
                ? ThumbnailRenderResult.Fail(code, "Fehler (Fake)")
                : Fail
                    ? ThumbnailRenderResult.Fail(ThumbnailErrorCodes.CloudUnavailable, "keine Verbindung (Fake)")
                    : FakeRenderHelper.WriteThumb(targetThumbPath, targetShortEdge);
        }
    }
}
