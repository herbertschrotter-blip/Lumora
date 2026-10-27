using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Background;
using Lumora.Services.Thumbnails;
using Lumora.Tests.Activity;
using Lumora.Tests.Fixtures;
using Lumora.Tests.Thumbnails;

namespace Lumora.Tests.Background;

/// <summary>Vorrang für Lightbox und Galerie, Kern-Obergrenze für Hintergrund-Rechnen (ADR-0074 Nachtrag 0.5.15).</summary>
public sealed class ForegroundHoldTests
{
    private readonly ForegroundWork _work = new();
    private readonly ManualTimerTime _time = new();

    [Fact]
    public void Haelt_den_Vorrang_waehrend_der_Arbeit_und_noch_kurz_danach()
    {
        using var hold = new ForegroundHold(_work, ForegroundNeed.Cpu, "Lightbox", _time);

        var first = hold.Enter();
        _work.Need.Should().Be(ForegroundNeed.Cpu);
        _work.What.Should().Be("Lightbox");

        first.Dispose();
        _time.Advance(ForegroundHold.Linger - TimeSpan.FromMilliseconds(100));
        _work.Need.Should().Be(ForegroundNeed.Cpu, "beim Blättern bleibt der Vorrang stehen");

        _time.Advance(TimeSpan.FromMilliseconds(200));
        _work.Need.Should().Be(ForegroundNeed.None);
        hold.IsHeld.Should().BeFalse();
    }

    [Fact]
    public void Mehrere_Arbeiten_melden_nur_einmal_an_und_eine_neue_verlaengert_den_Vorrang()
    {
        using var hold = new ForegroundHold(_work, ForegroundNeed.Cpu, "Lightbox", _time);
        var changes = 0;
        _work.Changed += () => changes++;

        var a = hold.Enter();
        var b = hold.Enter();
        a.Dispose();
        a.Dispose(); // doppeltes Dispose zählt nicht doppelt
        _time.Advance(ForegroundHold.Linger * 2);
        _work.Need.Should().Be(ForegroundNeed.Cpu, "die zweite Arbeit läuft noch");

        b.Dispose();
        _time.Advance(ForegroundHold.Linger / 2);
        hold.Enter().Dispose(); // nächstes Bild während des Nachlaufs
        _time.Advance(ForegroundHold.Linger / 2 + TimeSpan.FromMilliseconds(100));
        _work.Need.Should().Be(ForegroundNeed.Cpu, "der Nachlauf beginnt mit der letzten Arbeit neu");

        _time.Advance(ForegroundHold.Linger);
        _work.Need.Should().Be(ForegroundNeed.None);
        changes.Should().Be(2, "eine Anmeldung, eine Abmeldung");
    }

    [Fact]
    public void Dispose_gibt_den_Vorrang_sofort_frei_und_ohne_Anmeldestelle_passiert_nichts()
    {
        var hold = new ForegroundHold(_work, ForegroundNeed.Cpu, "Lightbox", _time);
        _ = hold.Enter();
        hold.Dispose();
        _work.Need.Should().Be(ForegroundNeed.None);
        hold.Enter().Dispose(); // nach Dispose harmlos
        _work.Need.Should().Be(ForegroundNeed.None);

        using var none = new ForegroundHold(null, ForegroundNeed.Cpu, "Lightbox", _time);
        none.Enter().Dispose();
        none.IsHeld.Should().BeFalse();
    }

    [Fact]
    public void Hintergrund_rechnet_hoechstens_mit_der_Haelfte_der_Kerne()
    {
        BackgroundCpu.MaxThreads.Should().BeInRange(1, Math.Max(1, Environment.ProcessorCount / 2));
        BackgroundCpu.OnnxThreads.Should().BeInRange(1, 4);
        BackgroundCpu.Options(CancellationToken.None).MaxDegreeOfParallelism.Should().Be(BackgroundCpu.MaxThreads);
    }

    [Fact]
    public async Task Vorschaubilder_fuer_die_Galerie_haben_Vorrang_bis_sie_fertig_sind()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var renderer = new GateRenderer();
        using var svc = new ThumbnailService(fx.ContextFactory, fx.Paths, renderer, new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance,
            foreground: _work);

        await svc.RequestThumbnailsAsync([new ThumbnailRequest(1, 1, fx.WriteLibraryFile("img1.jpg"), MediaType.Image, DateTime.UtcNow)]);
        renderer.Entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        _work.Need.Should().Be(ForegroundNeed.Cpu);
        _work.What.Should().Be("Vorschaubilder");

        renderer.Release.Set();
        var deadline = DateTime.UtcNow + ForegroundHold.Linger + TimeSpan.FromSeconds(10);
        while (_work.Need != ForegroundNeed.None && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        _work.Need.Should().Be(ForegroundNeed.None, "nach dem letzten Vorschaubild und dem Nachlauf laufen die Hintergrund-Aufgaben weiter");
    }

    private sealed class GateRenderer : IImageThumbnailRenderer
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetWidth)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(15));
            return FakeRenderHelper.WriteThumb(targetThumbPath, targetWidth);
        }
    }
}
