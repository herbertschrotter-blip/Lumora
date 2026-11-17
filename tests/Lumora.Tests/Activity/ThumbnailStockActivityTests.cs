using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Activity;
using Lumora.Services.Background;
using Lumora.Services.Thumbnails;

namespace Lumora.Tests.Activity;

/// <summary>
/// Vorschaubilder auf Vorrat in der Aktivitäts-Anzeige (0.5.40, Akzeptanzkriterium 5): eigener Eintrag mit Fortschritt und
/// Restzeit, Pause/Fortsetzen über den Planer (auch ohne offene Arbeit sichtbar), Wartegründe des Planers und der Galerie,
/// still fertig.
/// </summary>
public sealed class ThumbnailStockActivityTests : IDisposable
{
    private readonly StrongReferenceMessenger _messenger = new();
    private readonly ManualTimerTime _time = new();
    private readonly ActivityCenter _center;
    private readonly BackgroundPlanner _planner;
    private readonly ActivityBridge _bridge;

    public ThumbnailStockActivityTests()
    {
        _center = new ActivityCenter(_time);
        _planner = new BackgroundPlanner(time: _time);
        _bridge = new ActivityBridge(_center, _messenger, time: _time, planner: _planner);
        _bridge.Start();
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _planner.Dispose();
        _center.Dispose();
    }

    private void Send(ThumbnailStockStatus status) => _messenger.Send(new ThumbnailStockStatusMessage(status));

    [Fact]
    public void Eigener_Eintrag_mit_Fortschritt_und_Restzeit_still_fertig()
    {
        Send(new ThumbnailStockStatus(true, 0, 1000));
        _time.Advance(TimeSpan.FromSeconds(2));
        Send(new ThumbnailStockStatus(true, 100, 1000));
        _time.Advance(TimeSpan.FromSeconds(2));
        Send(new ThumbnailStockStatus(true, 200, 1000)); // 50 Dateien/s

        var running = _center.GetOverview().Running.Single();
        running.Kind.Should().Be(ActivityKind.Thumbnails);
        running.Title.Should().Be("Vorschaubilder auf Vorrat");
        running.Detail.Should().Be("im Hintergrund · für alle Fotos und Videos");
        (running.Done, running.Total).Should().Be((200, 1000));
        running.Remaining.Should().NotBeNull();
        running.Remaining!.Value.TotalSeconds.Should().BeApproximately(16, 0.5, "800 Dateien bei 50 Dateien/s");
        running.CanPause.Should().BeTrue();
        running.UserInitiated.Should().BeFalse();

        Send(ThumbnailStockStatus.Idle);
        var overview = _center.GetOverview();
        overview.Running.Should().BeEmpty();
        overview.Recent.Should().BeEmpty("still fertig, kein Eintrag unter „Zuletzt fertig“");
    }

    [Fact]
    public void Pause_und_Fortsetzen_ueber_den_Planer_pausiert_bleibt_sichtbar()
    {
        Send(new ThumbnailStockStatus(true, 5, 50));
        var entry = _center.GetOverview().Running.Single();

        _center.SetPaused(entry.Id, true).Should().BeTrue();
        _planner.IsPaused(BackgroundJob.ThumbnailStock).Should().BeTrue();
        Send(ThumbnailStockStatus.Idle);
        _center.GetOverview().Running.Single(r => r.Title == "Vorschaubilder auf Vorrat").State.Should().Be(ActivityState.Paused,
            "„Fortsetzen“ bleibt erreichbar");

        _center.SetPaused(entry.Id, false).Should().BeTrue();
        _planner.IsPaused(BackgroundJob.ThumbnailStock).Should().BeFalse();
        _center.GetOverview().Running.Should().BeEmpty();
    }

    [Fact]
    public async Task Wartegruende_Galerie_und_alle_pausiert()
    {
        Send(new ThumbnailStockStatus(false, 10, 100, ThumbnailStockService.QueueWaitReason));
        _center.GetOverview().Waiting.Single().WaitReason.Should().Be("wartet — zuerst angeforderte Vorschaubilder");

        Send(new ThumbnailStockStatus(true, 10, 100));
        _planner.SetAllPaused(true);
        var waiter = _planner.AcquireAsync(BackgroundJob.ThumbnailStock, CancellationToken.None);
        _center.GetOverview().Waiting.Single().WaitReason.Should().Be("alle Hintergrund-Aufgaben pausiert");

        _planner.SetAllPaused(false);
        (await waiter).Dispose();
        _center.GetOverview().Running.Single().State.Should().Be(ActivityState.Running);
    }
}
