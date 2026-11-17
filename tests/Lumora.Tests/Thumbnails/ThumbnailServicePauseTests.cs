using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Thumbnails;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Thumbnails;

/// <summary>Pause der Thumbnail-Worker für den Umzug der Katalog-DB (ADR-0026).</summary>
public sealed class ThumbnailServicePauseTests
{
    private static readonly DateTime Modified = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Hält jeden Render-Aufruf an, bis <see cref="Release"/> gesetzt ist.</summary>
    private sealed class BlockingImageRenderer : IImageThumbnailRenderer
    {
        private int _calls;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetWidth)
        {
            Interlocked.Increment(ref _calls);
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(15));
            return FakeRenderHelper.WriteThumb(targetThumbPath, targetWidth);
        }
    }

    [Fact]
    public async Task Pause_wartet_auf_den_laufenden_Auftrag_und_nimmt_bis_Resume_nichts_Neues_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var renderer = new BlockingImageRenderer();
        using var svc = new ThumbnailService(fx.ContextFactory, fx.Paths, renderer, new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance);
        ThumbnailRequest Request(int id) => new(1, id, fx.WriteLibraryFile($"img{id}.jpg"), MediaType.Image, Modified);

        await svc.RequestThumbnailsAsync([Request(1)]);
        renderer.Entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var pause = svc.PauseAsync();
        await Task.Delay(100);
        pause.IsCompleted.Should().BeFalse("der laufende Auftrag schreibt noch in die DB");

        renderer.Release.Set();
        await pause.WaitAsync(TimeSpan.FromSeconds(10));
        svc.PendingCount.Should().Be(0);

        await svc.RequestThumbnailsAsync([Request(2)]);
        await Task.Delay(100);
        renderer.Calls.Should().Be(1, "pausiert: keine DB-Zugriffe, Anforderungen werden zurückgestellt");

        svc.Resume();
        await svc.RequestThumbnailsAsync([Request(2)]);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (renderer.Calls < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        renderer.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Waehrend_der_Pause_angeforderte_Vorschaubilder_kommen_nach_Resume_ohne_neue_Anforderung()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var renderer = new FakeImageRenderer();
        using var svc = new ThumbnailService(fx.ContextFactory, fx.Paths, renderer, new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance);
        var ready = new System.Collections.Concurrent.ConcurrentBag<int>();
        svc.ThumbnailReady += e => ready.Add(e.FileId);
        ThumbnailRequest Request(int id, DateTime modified) => new(1, id, fx.WriteLibraryFile($"img{id}.jpg"), MediaType.Image, modified);

        await svc.PauseAsync();
        await svc.RequestThumbnailsAsync([Request(1, Modified.AddDays(-1)), Request(2, Modified)]);
        await svc.RequestThumbnailsAsync([Request(1, Modified)]); // dieselbe Datei erneut: die neueste Anforderung gilt
        await svc.RequestThumbnailsAsync([new ThumbnailRequest(1, 3, "archiv.zip", MediaType.Archive, Modified)]);
        await Task.Delay(100);
        renderer.Calls.Should().Be(0, "pausiert: zurückgestellt, nicht gerendert");

        svc.Resume(); // Rücknahme des Umzugs — die Galerie fordert nichts neu an

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((renderer.Calls < 2 || ready.Count < 2) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        renderer.Calls.Should().Be(2, "je Datei genau ein Auftrag, Archive haben kein Vorschaubild");
        ready.Should().BeEquivalentTo([1, 2]);

        // Nachgeholt heißt erledigt: eine weitere Anforderung trifft den Cache statt neu zu rendern
        await svc.RequestThumbnailsAsync([Request(1, Modified)]);
        await Task.Delay(100);
        renderer.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Pausen_schachteln_sich_erst_das_letzte_Resume_setzt_fort()
    {
        // 0.5.40: „Cache leeren“ pausiert ebenfalls — sein Resume darf eine gehaltene Pause des Katalog-Umzugs nicht aufheben
        using var fx = await TempLibraryFixture.CreateAsync();
        var renderer = new FakeImageRenderer();
        using var svc = new ThumbnailService(fx.ContextFactory, fx.Paths, renderer, new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance);
        var request = new ThumbnailRequest(1, 1, fx.WriteLibraryFile("img1.jpg"), MediaType.Image, Modified);

        await svc.PauseAsync(); // Umzug
        await svc.PauseAsync(); // Cache leeren
        await svc.RequestThumbnailsAsync([request]);
        svc.Resume(); // Cache leeren fertig
        await Task.Delay(150);
        renderer.Calls.Should().Be(0, "der Umzug hält den Dienst weiter an");

        svc.Resume(); // Umzug zurückgenommen
        await WaitUntilAsync(() => renderer.Calls == 1);
        svc.Resume(); // überzählig: keine Wirkung
        renderer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Abgebrochene_Pause_haelt_den_Dienst_nicht_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var renderer = new BlockingImageRenderer();
        using var svc = new ThumbnailService(fx.ContextFactory, fx.Paths, renderer, new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance);
        ThumbnailRequest Request(int id) => new(1, id, fx.WriteLibraryFile($"img{id}.jpg"), MediaType.Image, Modified);
        await svc.RequestThumbnailsAsync([Request(1)]);
        renderer.Entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)); // z. B. Zeitlimit des Umzugs
        await FluentActions.Awaiting(() => svc.PauseAsync(timeout.Token)).Should().ThrowAsync<OperationCanceledException>();
        renderer.Release.Set();

        await svc.RequestThumbnailsAsync([Request(2)]);
        await WaitUntilAsync(() => renderer.Calls == 2);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "Bedingung erreicht");
            await Task.Delay(10);
        }
    }
}
