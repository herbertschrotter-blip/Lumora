using Lumora.Services.Activity;

namespace Lumora.Tests.Activity;

/// <summary>Zentrale der Aktivitäts-Anzeige (ADR-0067): Wichtigkeit, Wartet, Fertig, Begrenzung, Drosselung, Abbrechen, Pause, Rückgängig.</summary>
public sealed class ActivityCenterTests
{
    private static (ActivityCenter Center, ManualTimerTime Time) Create()
    {
        var time = new ManualTimerTime();
        return (new ActivityCenter(time), time);
    }

    [Fact]
    public void Wichtigster_Vorgang_vom_Benutzer_vor_Hintergrund_laufend_vor_angehalten_dann_nach_Art()
    {
        var (center, _) = Create();
        var faces = center.Start(new ActivityStartInfo(ActivityKind.Faces, "Gesichter erkennen", Key: "faces", Pause: () => { }, Resume: () => { }));
        var scan = center.Start(new ActivityStartInfo(ActivityKind.Scan, "Einlesen"));
        var adopt = center.Start(new ActivityStartInfo(ActivityKind.Adoption, "In die Bibliothek übernehmen", UserInitiated: true));
        var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true));
        var thumbs = center.Start(new ActivityStartInfo(ActivityKind.Thumbnails, "Vorschaubilder"));
        faces.SetPaused(true);

        var overview = center.GetOverview();

        overview.Running.Select(r => r.Id).Should().Equal(move.Id, adopt.Id, scan.Id, thumbs.Id, faces.Id);
        overview.Primary!.Title.Should().Be("Verschieben");
        overview.OthersCount.Should().Be(4);
        overview.Running.Last().State.Should().Be(ActivityState.Paused);
    }

    [Fact]
    public void Gleicher_Schluessel_liefert_den_laufenden_Vorgang()
    {
        var (center, _) = Create();
        var a = center.Start(new ActivityStartInfo(ActivityKind.Faces, "Gesichter", Key: "faces"));
        var b = center.Start(new ActivityStartInfo(ActivityKind.Faces, "Gesichter", Key: "faces"));

        b.Should().BeSameAs(a);
        center.Find("faces").Should().BeSameAs(a);
        a.Dismiss();
        center.Find("faces").Should().BeNull();
        a.IsActive.Should().BeFalse("still beendet ist beendet");
        center.Start(new ActivityStartInfo(ActivityKind.Faces, "Gesichter", Key: "faces")).Id.Should().NotBe(a.Id);
    }

    [Fact]
    public void Wartet_mit_Grund_eigenem_Abbrechen_und_laeuft_nach_Fortschritt_weiter()
    {
        var (center, _) = Create();
        var cancels = new List<string>();
        var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true, Cancel: () => cancels.Add("vorgang")));
        move.Wait("wartet auf laufendes Einlesen", () => cancels.Add("warten"));

        var overview = center.GetOverview();
        overview.Running.Should().BeEmpty();
        var waiting = overview.Waiting.Should().ContainSingle().Subject;
        waiting.WaitReason.Should().Be("wartet auf laufendes Einlesen");
        waiting.CanCancel.Should().BeTrue();
        overview.Primary!.Id.Should().Be(move.Id, "ohne Laufendes zeigt die Statusleiste den wartenden");

        move.Report(1, 10);
        center.GetOverview().Running.Should().ContainSingle().Which.WaitReason.Should().BeNull();

        move.Wait("wartet wieder", () => cancels.Add("warten"));
        center.Cancel(move.Id).Should().BeTrue();
        cancels.Should().Equal("warten");
        center.GetOverview().Waiting.Single().CanCancel.Should().BeFalse("Abbrechen nur einmal");
    }

    [Fact]
    public void Fertig_kommt_unter_Zuletzt_fertig_mit_Meldung_Hintergrund_nur_bei_Fehlern()
    {
        var (center, _) = Create();
        var finished = new List<ActivitySnapshot>();
        center.Finished += finished.Add;

        var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", "nach „Urlaub 2024“", UserInitiated: true));
        move.Complete(new ActivityCompletion("340 Dateien verschoben", Actions: [ActivityAction.Undo(_ => Task.FromResult("zurück"))]));
        var dup = center.Start(new ActivityStartInfo(ActivityKind.Duplicates, "Doppelte suchen", KeepInRecent: false));
        dup.Complete(new ActivityCompletion("fertig"));
        var faces = center.Start(new ActivityStartInfo(ActivityKind.Faces, "Gesichter erkennen", KeepInRecent: false));
        faces.Fail("Gesichter erkennen: Modell fehlt", "Details");
        var watch = center.Start(new ActivityStartInfo(ActivityKind.Watch, "Automatische Aktualisierung", KeepInRecent: false));
        watch.Dismiss();

        var overview = center.GetOverview();
        overview.Running.Should().BeEmpty();
        overview.Recent.Select(r => r.Title).Should().Equal("Gesichter erkennen", "Verschieben");
        var moved = overview.Recent[1];
        moved.State.Should().Be(ActivityState.Succeeded);
        moved.Result.Should().Be("340 Dateien verschoben");
        moved.Target.Should().Be("nach „Urlaub 2024“");
        moved.HasAction(ActivityActionKind.Undo).Should().BeTrue();
        overview.Recent[0].HasAction(ActivityActionKind.Details).Should().BeTrue();
        overview.LastFinishedUtc.Should().NotBeNull();
        finished.Select(f => f.Title).Should().Equal("Verschieben", "Doppelte suchen", "Gesichter erkennen");
        finished[0].UserInitiated.Should().BeTrue();

        move.Report(5, 5); // nach dem Ende wirkungslos
        center.GetOverview().Running.Should().BeEmpty();
    }

    [Fact]
    public void Zuletzt_fertig_ist_auf_zehn_begrenzt_neuester_zuerst()
    {
        var (center, _) = Create();
        for (var i = 1; i <= 15; i++)
        {
            center.Start(new ActivityStartInfo(ActivityKind.FileOperation, $"Vorgang {i}", UserInitiated: true)).Complete(new ActivityCompletion("ok"));
        }

        var recent = center.GetOverview().Recent;
        recent.Should().HaveCount(ActivityCenter.RecentLimit);
        recent.First().Title.Should().Be("Vorgang 15");
        recent.Last().Title.Should().Be("Vorgang 6");

        center.ClearRecent();
        center.GetOverview().Recent.Should().BeEmpty();
    }

    [Fact]
    public void Benachrichtigung_ist_gedrosselt_sofort_dann_gebuendelt_am_Intervallende()
    {
        var (center, time) = Create();
        var changed = 0;
        center.Changed += () => Interlocked.Increment(ref changed);
        var scan = center.Start(new ActivityStartInfo(ActivityKind.Scan, "Einlesen"));
        changed.Should().Be(1, "die erste Änderung nach einer Ruhe meldet sofort");

        for (var i = 0; i < 500; i++)
        {
            scan.Report(i, 500);
        }

        changed.Should().Be(1, "alles im Intervall wird gebündelt");
        time.Advance(TimeSpan.FromMilliseconds(149));
        changed.Should().Be(1);
        time.Advance(TimeSpan.FromMilliseconds(1));
        changed.Should().Be(2, "eine nachlaufende Meldung am Intervallende");
        center.GetOverview().Running.Single().Done.Should().Be(499, "die Oberfläche liest den jüngsten Stand");

        time.Advance(TimeSpan.FromSeconds(5));
        changed.Should().Be(2, "ohne Änderung keine weitere Meldung");
        scan.Report(500, 500);
        changed.Should().Be(3, "nach der Ruhe wieder sofort");
    }

    [Fact]
    public void Abbrechen_ruft_den_Weg_der_Quelle_einmal()
    {
        var (center, _) = Create();
        var calls = 0;
        var handle = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true, Cancel: () => calls++));
        var plain = center.Start(new ActivityStartInfo(ActivityKind.Stories, "Storys berechnen"));

        center.GetOverview().Running.Single(r => r.Id == plain.Id).CanCancel.Should().BeFalse();
        center.Cancel(plain.Id).Should().BeFalse();
        center.Cancel(handle.Id).Should().BeTrue();
        center.Cancel(handle.Id).Should().BeFalse();
        calls.Should().Be(1);

        handle.Canceled();
        var entry = center.GetOverview().Recent.Single();
        entry.State.Should().Be(ActivityState.Canceled);
        entry.Result.Should().Be("Abgebrochen");
    }

    [Fact]
    public void Abbrechen_nach_dem_Entsorgen_der_Quelle_wirft_nicht()
    {
        var (center, _) = Create();
        var cts = new CancellationTokenSource();
        var handle = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", Cancel: cts.Cancel));
        cts.Dispose();

        center.Invoking(c => c.Cancel(handle.Id)).Should().NotThrow();
    }

    [Fact]
    public void Pause_und_Fortsetzen_nur_wo_die_Quelle_es_kann()
    {
        var (center, _) = Create();
        var calls = new List<string>();
        var faces = center.Start(new ActivityStartInfo(ActivityKind.Faces, "Gesichter erkennen", Pause: () => calls.Add("pause"), Resume: () => calls.Add("weiter")));
        var dup = center.Start(new ActivityStartInfo(ActivityKind.Duplicates, "Doppelte suchen"));

        center.GetOverview().Running.Single(r => r.Id == faces.Id).CanPause.Should().BeTrue();
        center.GetOverview().Running.Single(r => r.Id == dup.Id).CanPause.Should().BeFalse();
        center.SetPaused(dup.Id, true).Should().BeFalse();

        center.SetPaused(faces.Id, true).Should().BeTrue();
        center.GetOverview().Running.Single(r => r.Id == faces.Id).State.Should().Be(ActivityState.Paused);
        center.SetPaused(faces.Id, false).Should().BeTrue();
        center.GetOverview().Running.Single(r => r.Id == faces.Id).State.Should().Be(ActivityState.Running);
        calls.Should().Equal("pause", "weiter");
    }

    [Fact]
    public async Task Rueckgaengig_nutzt_den_Weg_der_Quelle_und_entfaellt_danach()
    {
        var (center, _) = Create();
        var undone = 0;
        var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true));
        move.Complete(new ActivityCompletion("2 Dateien verschoben", Actions: [ActivityAction.Undo(_ => { undone++; return Task.FromResult("Rückgängig: 2 Dateien zurückverschoben"); })]));

        var text = await center.UndoAsync(move.Id);

        text.Should().Be("Rückgängig: 2 Dateien zurückverschoben");
        undone.Should().Be(1);
        var entry = center.GetOverview().Recent.Single();
        entry.HasAction(ActivityActionKind.Undo).Should().BeFalse();
        entry.Result.Should().Be("2 Dateien verschoben · rückgängig gemacht");
        (await center.UndoAsync(move.Id)).Should().BeNull("kein zweites Mal");
    }

    [Fact]
    public async Task Rueckgaengig_mit_Fehler_liefert_den_Grund_als_Text()
    {
        var (center, _) = Create();
        var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true));
        move.Complete(new ActivityCompletion("1 Datei verschoben", Actions: [ActivityAction.Undo(_ => throw new InvalidOperationException("Ursprungsort ist inzwischen belegt."))]));

        var text = await center.UndoAsync(move.Id);

        text.Should().Be("Rückgängig fehlgeschlagen: Ursprungsort ist inzwischen belegt.");
        var entry = center.GetOverview().Recent.Single();
        entry.Result.Should().Be("1 Datei verschoben", "unverändert");
        entry.HasAction(ActivityActionKind.Undo).Should().BeTrue("erneut versuchbar (z. B. abgebrochenes Warten auf die Scan-Sperre)");
    }

    [Fact]
    public void Restzeit_erst_nach_einigen_Sekunden_Fortschritt()
    {
        var (center, time) = Create();
        var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true));
        move.Report(0, 100);
        time.Advance(TimeSpan.FromSeconds(1));
        move.Report(10, 100);
        center.GetOverview().Running.Single().Remaining.Should().BeNull("noch zu früh");

        time.Advance(TimeSpan.FromSeconds(3)); // gleichmäßig 10 Dateien/s → 60 Dateien = 6 s
        move.Report(40, 100);
        center.GetOverview().Running.Single().Remaining.Should().BeCloseTo(TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(10));
        center.GetOverview().Running.Single().Fraction.Should().BeApproximately(0.4, 0.0001);
    }

    [Fact]
    public void Restzeit_zaehlt_Warten_und_Pause_nicht_mit()
    {
        var (center, time) = Create();
        var dates = center.Start(new ActivityStartInfo(ActivityKind.CaptureDates, "Aufnahmedatum", Key: "capture-dates"));
        dates.Report(0, 100);
        time.Advance(TimeSpan.FromSeconds(2));
        dates.Report(20, 100); // 10 Dateien/s

        dates.Wait("wartet auf das Einlesen");
        time.Advance(TimeSpan.FromMinutes(1));
        dates.Report(20, 100);
        dates.SetPaused(true);
        time.Advance(TimeSpan.FromMinutes(5));
        dates.SetPaused(false);
        dates.Report(20, 100);
        time.Advance(TimeSpan.FromSeconds(2));
        dates.Report(40, 100);

        center.GetOverview().Running.Single().Remaining.Should().BeCloseTo(TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(10), "60 Dateien bei 10 Dateien/s");
    }

    [Fact]
    public void Restzeit_folgt_der_juengsten_Geschwindigkeit()
    {
        var (center, time) = Create();
        var hashes = center.Start(new ActivityStartInfo(ActivityKind.Duplicates, "Doppelte", Key: "duplicates"));
        hashes.Report(0, 100);
        var done = 0;
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            hashes.Report(done += 10, 100); // schnell: 10/s
        }

        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            hashes.Report(done += 2, 100); // langsam: 2/s (z. B. große Videos)
        }

        // Mittel seit Beginn wäre 70/15 s → 6,4 s; die gleitende Geschwindigkeit liegt nahe 2/s → 30 Dateien ≈ 13–14 s
        hashes.Report(done, 100);
        center.GetOverview().Running.Single().Remaining!.Value.TotalSeconds.Should().BeInRange(12, 16);
    }

    [Fact]
    public void Pause_bleibt_auch_beim_Warten_erreichbar()
    {
        var (center, _) = Create();
        var dates = center.Start(new ActivityStartInfo(ActivityKind.CaptureDates, "Aufnahmedatum", Key: "capture-dates", Pause: () => { }, Resume: () => { }));

        dates.Wait("wartet auf das Einlesen");

        center.GetOverview().Waiting.Single().CanPause.Should().BeTrue();
    }

    [Fact]
    public void Entsorgen_leert_die_Liste_Katalogwechsel()
    {
        var (center, _) = Create();
        center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true)).Complete(new ActivityCompletion("ok"));
        center.Start(new ActivityStartInfo(ActivityKind.Scan, "Einlesen"));

        center.Dispose();

        center.GetOverview().Should().BeEquivalentTo(ActivityOverview.Empty);
    }
}
