using CommunityToolkit.Mvvm.Messaging;
using Lumora.Domain.Enums;
using Lumora.Services.Activity;
using Lumora.Services.Archive;
using Lumora.Services.Duplicates;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Messaging;
using Lumora.Services.Motifs;
using Lumora.Services.Scanning;
using Lumora.Services.Stories;
using Lumora.Services.Thumbnails;
using Lumora.Services.Watch;

namespace Lumora.Tests.Activity;

/// <summary>Brücke der Fortschrittsquellen (ADR-0067): Messenger-Records → Läuft/Wartet/Zuletzt fertig mit den Wegen der Quellen.</summary>
public sealed class ActivityBridgeTests : IDisposable
{
    private readonly StrongReferenceMessenger _messenger = new();
    private readonly ManualTimerTime _time = new();
    private readonly ActivityCenter _center;

    public ActivityBridgeTests()
    {
        _center = new ActivityCenter(_time);
    }

    public void Dispose() => _center.Dispose();

    private ActivityBridge StartBridge(
        IScanGate? gate = null,
        IThumbnailService? thumbnails = null,
        IFaceIndexService? faces = null,
        IMotifIndexService? motifs = null)
    {
        var bridge = new ActivityBridge(_center, _messenger, gate, thumbnails: thumbnails, faces: faces, motifs: motifs, time: _time);
        bridge.Start();
        return bridge;
    }

    [Fact]
    public async Task Planer_Pause_je_Aufgabe_Warten_auf_die_Reihe_und_Alle_pausieren()
    {
        using var planner = new Lumora.Services.Background.BackgroundPlanner(time: _time);
        using var bridge = new ActivityBridge(_center, _messenger, time: _time, planner: planner);
        bridge.Start();
        _messenger.Send(new Lumora.Services.Messaging.CaptureDateBackfillStatusMessage(new Lumora.Services.Metadata.CaptureDateBackfillStatus(true, 200, 1000)));
        _messenger.Send(new Lumora.Services.Messaging.GeoBackfillStatusMessage(new Lumora.Services.Places.GeoBackfillStatus(true, 0, 1000)));

        // Aufnahmedatum hält die Datei-Spur, Orte warten auf die Reihe: läuft weiter, Grund in der zweiten Zeile
        var lane = await planner.AcquireAsync(Lumora.Services.Background.BackgroundJob.CaptureDates, CancellationToken.None);
        var places = planner.AcquireAsync(Lumora.Services.Background.BackgroundJob.GeoLocations, CancellationToken.None);
        _center.GetOverview().Running.Single(r => r.Title == "Orte nachtragen").Detail.Should().Be("im Hintergrund · wartet — zuerst Aufnahmedatum");

        // Pause über die Anzeige → Planer
        var dates = _center.GetOverview().Running.Single(r => r.Title == "Aufnahmedatum nachtragen");
        dates.CanPause.Should().BeTrue();
        _center.SetPaused(dates.Id, true);
        planner.IsPaused(Lumora.Services.Background.BackgroundJob.CaptureDates).Should().BeTrue();
        _center.GetOverview().Running.Single(r => r.Id == dates.Id).State.Should().Be(ActivityState.Paused);

        lane.Dispose();
        (await places).Dispose();

        // Alle pausieren: wartende Aufgabe zeigt den Grund
        planner.SetAllPaused(true);
        var again = planner.AcquireAsync(Lumora.Services.Background.BackgroundJob.GeoLocations, CancellationToken.None);
        _center.GetOverview().Waiting.Single(r => r.Title == "Orte nachtragen").WaitReason.Should().Be("alle Hintergrund-Aufgaben pausiert");

        planner.SetAllPaused(false);
        (await again).Dispose();
        _center.GetOverview().Running.Single(r => r.Title == "Orte nachtragen").State.Should().Be(ActivityState.Running);
    }

    private static ScanResult Incremental(int added, int missing) => new(1, @"C:\Bib", 0, 0, 0, 0, TimeSpan.FromSeconds(2), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
    {
        Mode = ScanMode.Incremental,
        Added = added,
        Modified = 3,
        MarkedMissing = missing,
    };

    [Fact]
    public void Einlesen_laeuft_und_endet_mit_Fehlende_zeigen()
    {
        using var bridge = StartBridge();

        _messenger.Send(new ScanProgressMessage(1, new ScanProgress(56, 1234, 10, null)));
        var running = _center.GetOverview().Running.Single();
        running.Kind.Should().Be(ActivityKind.Scan);
        running.IsIndeterminate.Should().BeTrue();
        running.Detail.Should().Be($"{1234:N0} Dateien · 56 Ordner — Ordner durchsuchen");

        _messenger.Send(new ScanCompletedMessage(1, Incremental(34, 2)));

        _center.GetOverview().Running.Should().BeEmpty();
        var done = _center.GetOverview().Recent.Single();
        done.Result.Should().Be("34 neu, 3 geändert, 2 fehlen");
        done.HasAction(ActivityActionKind.ShowMissing).Should().BeTrue();
        done.UserInitiated.Should().BeFalse("Einlesen ohne kurze Meldung");
    }

    [Fact]
    public void Einlesen_ohne_Fehlende_und_abgebrochen()
    {
        using var bridge = StartBridge();
        _messenger.Send(new ScanCompletedMessage(2, Incremental(0, 0) with { Modified = 0 }));
        _center.GetOverview().Recent.Single().Result.Should().Be("Keine Änderungen (2 s)");

        _messenger.Send(new ScanProgressMessage(2, new ScanProgress(1, 1, 1, null)));
        _messenger.Send(new ScanAbortedMessage(2, Canceled: true, Reason: "Benutzer"));
        var aborted = _center.GetOverview().Recent.First();
        aborted.State.Should().Be(ActivityState.Canceled);
        aborted.HasAction(ActivityActionKind.ShowMissing).Should().BeFalse();
    }

    [Fact]
    public void Gesichter_laufen_pausieren_ueber_den_Dienst_und_verschwinden_still()
    {
        var (faces, recorder) = RecordingProxy<IFaceIndexService>.Create();
        using var bridge = StartBridge(faces: faces);

        _messenger.Send(new FaceIndexStatusMessage(new FaceIndexStatus(FaceIndexState.Running, 1240, 5212, 37, "DirectML")));
        var entry = _center.GetOverview().Running.Single();
        entry.Title.Should().Be("Gesichter erkennen");
        entry.Done.Should().Be(1240);
        entry.Detail.Should().Be("im Hintergrund · Grafikkarte");
        entry.CanPause.Should().BeTrue();
        entry.CanCancel.Should().BeTrue();

        _center.SetPaused(entry.Id, true).Should().BeTrue();
        _center.Cancel(entry.Id).Should().BeTrue();
        recorder.Calls.Should().Equal("Pause", "CancelRun", "Resume");

        _messenger.Send(new FaceIndexStatusMessage(new FaceIndexStatus(FaceIndexState.Idle, 5212, 5212)));
        _center.GetOverview().Running.Should().BeEmpty();
        _center.GetOverview().Recent.Should().BeEmpty("Hintergrund-Index ohne Eintrag unter „Zuletzt fertig“");
    }

    [Fact]
    public void Motive_neben_Gesichtern_nennen_die_geteilte_Grafikkarte_und_warten_mit_Grund()
    {
        var (motifs, _) = RecordingProxy<IMotifIndexService>.Create();
        using var bridge = StartBridge(motifs: motifs);

        _messenger.Send(new FaceIndexStatusMessage(new FaceIndexStatus(FaceIndexState.Running, 1, 10, 0, "DirectML")));
        _messenger.Send(new MotifIndexStatusMessage(new MotifIndexStatus(MotifIndexState.Running, 2, 10, 0, "DirectML")));
        _center.GetOverview().Running.Single(r => r.Kind == ActivityKind.Motifs).Detail.Should().Be("im Hintergrund · Grafikkarte (abwechselnd mit „Gesichter“)");

        _messenger.Send(new MotifIndexStatusMessage(new MotifIndexStatus(MotifIndexState.Waiting, 2, 10, 0, "DirectML", "Wartet auf den Scan")));
        _center.GetOverview().Waiting.Single().WaitReason.Should().Be("Wartet auf den Scan");
    }

    [Fact]
    public void Index_Fehler_kommt_einmal_unter_Zuletzt_fertig()
    {
        using var bridge = StartBridge();
        var error = new FaceIndexStatusMessage(new FaceIndexStatus(FaceIndexState.Error, Message: "Modell beschädigt"));
        _messenger.Send(error);
        _messenger.Send(error);

        var entry = _center.GetOverview().Recent.Should().ContainSingle().Subject;
        entry.State.Should().Be(ActivityState.Failed);
        entry.HasAction(ActivityActionKind.Details).Should().BeTrue();
    }

    [Fact]
    public void Einfache_Indizes_laufen_warten_mit_Grund_und_enden_still()
    {
        using var bridge = StartBridge();
        _messenger.Send(new DuplicateIndexStatusMessage(new DuplicateIndexStatus(true, "Prüfsummen", 10, 100)));
        var running = _center.GetOverview().Running.Single();
        running.Title.Should().Be("Doppelte suchen");
        running.Detail.Should().Be("im Hintergrund · Prüfsummen");

        _messenger.Send(new DuplicateIndexStatusMessage(new DuplicateIndexStatus(false, string.Empty, 0, 0, "wartet auf den Katalog")));
        _center.GetOverview().Waiting.Single().WaitReason.Should().Be("wartet auf den Katalog");

        _messenger.Send(new DuplicateIndexStatusMessage(DuplicateIndexStatus.Idle));
        _messenger.Send(new StoryIndexStatusMessage(new StoryIndexStatus(true, "Storys werden berechnet …")));
        _messenger.Send(new StoryIndexStatusMessage(StoryIndexStatus.Idle));
        var overview = _center.GetOverview();
        overview.Running.Should().BeEmpty();
        overview.Waiting.Should().BeEmpty();
        overview.Recent.Should().BeEmpty();
    }

    [Fact]
    public async Task Wartende_Datei_Operation_ohne_Vorgang_wird_eigener_Eintrag_Abbrechen_ueber_die_Sperre()
    {
        var gate = new ScanGate(_messenger);
        using var bridge = StartBridge(gate);
        var lease = await gate.AcquireAsync(7, CancellationToken.None);
        var waitingOp = gate.AcquireForFileOperationAsync(7, CancellationToken.None);

        var waiting = _center.GetOverview().Waiting.Single();
        waiting.Title.Should().Be("Datei-Vorgang");
        waiting.WaitReason.Should().Be("wartet auf laufendes Einlesen derselben Bibliothek");

        _center.Cancel(waiting.Id).Should().BeTrue();

        await FluentActions.Awaiting(() => waitingOp).Should().ThrowAsync<ScanGateWaitCanceledException>();
        _center.GetOverview().Waiting.Should().BeEmpty();
        _center.GetOverview().Recent.Single().Result.Should().Be("Wartende Datei-Operation abgebrochen");
        lease.Dispose();
    }

    [Fact]
    public void Katalog_Schreibsperre_und_Ordnerueberwachung_als_Wartet()
    {
        using var bridge = StartBridge();
        _messenger.Send(new CatalogWriteWaitMessage(2));
        _messenger.Send(new WatchStatusChangedMessage(new WatchStatus(1, WatchState.ChangesPending, _time.GetUtcNow().UtcDateTime.AddSeconds(3))));

        var waiting = _center.GetOverview().Waiting;
        waiting.Select(w => w.WaitReason).Should().Equal(
            "2 Änderungen warten auf den Katalog — bitte Lumora nicht schließen",
            ActivityBridge.WatchReason(_time.GetUtcNow().UtcDateTime.AddSeconds(3)));
        ActivityBridge.WatchReason(new DateTime(2026, 10, 25, 12, 0, 3, DateTimeKind.Utc)).Should().StartWith("Änderungen erkannt — Aktualisierung gegen ");

        _messenger.Send(new CatalogWriteWaitMessage(0));
        _messenger.Send(new WatchStatusChangedMessage(new WatchStatus(1, WatchState.Scanning)));
        _center.GetOverview().Waiting.Should().BeEmpty();
        _center.GetOverview().Recent.Should().BeEmpty();
    }

    [Fact]
    public void Automatisches_Entpacken_mit_Hinweisen_hat_Details()
    {
        using var bridge = StartBridge();
        _messenger.Send(new ArchiveAutoExtractStatusMessage(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Running, 1, 2, "fotos.zip")));
        _center.GetOverview().Running.Single().Detail.Should().Be("„fotos.zip“ · automatisch");

        var summary = new ArchiveAutoExtractSummary(
            1,
            [
                new ArchiveAutoExtractItem(11, @"C:\E\fotos.zip", ArchiveAutoExtractItemState.Extracted, @"C:\E\fotos", 12, true, null),
                new ArchiveAutoExtractItem(12, @"C:\E\kaputt.zip", ArchiveAutoExtractItemState.Failed, null, 0, false, "beschädigt"),
            ],
            null,
            null,
            false);
        _messenger.Send(new ArchiveAutoExtractStatusMessage(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Finished, 2, 2, null, summary)));

        var done = _center.GetOverview().Recent.Single();
        done.State.Should().Be(ActivityState.Warning);
        done.Result.Should().Be("1 Archiv entpackt · 12 Dateien · 1 nicht");
        done.Details.Should().Contain("kaputt.zip: beschädigt");
    }

    [Fact]
    public void Vorschaubilder_aus_der_Warteschlange_mit_Fortschritt_seit_dem_hoechsten_Stand()
    {
        var (thumbs, recorder) = RecordingProxy<IThumbnailService>.Create();
        using var bridge = StartBridge(thumbnails: thumbs);

        recorder.Values["PendingCount"] = 40;
        _time.Advance(ActivityBridge.ThumbnailPollInterval);
        recorder.Values["PendingCount"] = 10;
        _time.Advance(ActivityBridge.ThumbnailPollInterval);
        var entry = _center.GetOverview().Running.Single();
        entry.Title.Should().Be("Vorschaubilder erstellen");
        (entry.Done, entry.Total).Should().Be((30, 40));
        entry.Detail.Should().Be("10 in Warteschlange");

        recorder.Values["PendingCount"] = 0;
        _time.Advance(ActivityBridge.ThumbnailPollInterval);
        _center.GetOverview().Running.Should().BeEmpty();
        _center.GetOverview().Recent.Should().BeEmpty();
    }
}
