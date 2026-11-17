using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Background;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Thumbnails;

/// <summary>
/// Vorschaubilder auf Vorrat (0.5.40, ADR-0074 Nachtrag 0.5.40) — je Akzeptanzkriterium: (1) niedrigster Vorrang der
/// Datei-Spur, Abgabe je Datei an wichtigere Aufgaben; (2) derselbe Dienst/Renderer, gleiche Cache-Datei und DB-Status, nur
/// ohne gültiges Vorschaubild, Fotos vor Videos, keine Archive; (3) fehlend, Wurzel offline, Platzhalter ohne Dateizugriff;
/// (4) Vordergrund je Datei, Galerie-Warteschlange, Einlesen, Katalog-Umzug, Kern-Obergrenze und Laufwerksart, ein FFmpeg
/// zugleich; (5) Auslöser: Startverzögerung, Einlesen, neue Dateien, Cache leeren (auch mitten im Durchgang); (6) Schalter
/// je Katalog; (7) versteckte Bibliothek: nichts außerhalb des Katalogs, keine Pfade im Protokoll. Nur Fakes, keine Fotos.
/// </summary>
public sealed class ThumbnailStockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // ----- (1) Vorrang -----

    [Fact]
    public async Task Vorrat_kommt_in_der_Datei_Spur_erst_nach_den_Pruefsummen()
    {
        using var planner = new BackgroundPlanner(storage: new Storage(StorageKind.Hdd));
        var quality = await planner.AcquireAsync(BackgroundJob.Quality, CancellationToken.None);
        var stock = planner.AcquireAsync(BackgroundJob.ThumbnailStock, CancellationToken.None);
        var checksums = planner.AcquireAsync(BackgroundJob.Checksums, CancellationToken.None);

        planner.UsesFileLane(BackgroundJob.ThumbnailStock).Should().BeTrue();
        JobNames.Of(BackgroundJob.ThumbnailStock).Should().Be("Vorschaubilder auf Vorrat");
        quality.Dispose();

        checksums.IsCompleted.Should().BeTrue("Prüfsummen vor dem Vorrat, obwohl später angefragt");
        stock.IsCompleted.Should().BeFalse();
        planner.GetState(BackgroundJob.ThumbnailStock).Reason.Should().Be("wartet — zuerst Prüfsummen");
        (await checksums).Dispose();
        stock.IsCompleted.Should().BeTrue();
        (await stock).Dispose();
    }

    [Fact]
    public async Task Planer_meldet_wartende_wichtigere_Aufgabe_nur_wenn_sie_arbeiten_duerfte()
    {
        using var planner = new BackgroundPlanner(storage: new Storage(StorageKind.Hdd));
        var lane = await planner.AcquireAsync(BackgroundJob.ThumbnailStock, CancellationToken.None);
        planner.HasWaitingAhead(BackgroundJob.ThumbnailStock).Should().BeFalse();

        var checksums = planner.AcquireAsync(BackgroundJob.Checksums, CancellationToken.None);
        planner.HasWaitingAhead(BackgroundJob.ThumbnailStock).Should().BeTrue();
        planner.HasWaitingAhead(BackgroundJob.Checksums).Should().BeFalse("nichts Wichtigeres wartet");
        planner.HasWaitingAhead(BackgroundJob.Faces).Should().BeFalse("ohne Datei-Spur kein Vorrang");

        planner.SetPaused(BackgroundJob.Checksums, true);
        planner.HasWaitingAhead(BackgroundJob.ThumbnailStock).Should().BeFalse("pausierte Aufgabe darf nicht arbeiten");
        planner.SetPaused(BackgroundJob.Checksums, false);

        lane.Dispose();
        (await checksums).Dispose();
    }

    [Fact]
    public async Task Wichtigere_Dateiaufgabe_bekommt_die_Spur_nach_der_laufenden_Datei()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg", "c.jpg"]);
        var events = new ConcurrentQueue<string>();
        w.Renderer.OnRender = (name, call) =>
        {
            events.Enqueue("render " + name);
            if (call == 1)
            {
                _ = w.Planner!.AcquireAsync(BackgroundJob.CaptureDates, CancellationToken.None).ContinueWith(t =>
                {
                    events.Enqueue("Aufnahmedatum");
                    t.Result.Dispose();
                }, TaskScheduler.Default);
            }
        };

        var report = await w.Stock.RunPassAsync().WaitAsync(Timeout);

        report.Should().Be(new ThumbnailStockReport(3, 0, 0));
        events.Should().Equal("render a.jpg", "Aufnahmedatum", "render b.jpg", "render c.jpg");
    }

    // ----- (2) derselbe Dienst, nur offene Dateien, Fotos vor Videos, keine Archive -----

    [Fact]
    public async Task Erstellt_fehlende_Vorschaubilder_mit_Cache_Datei_und_Status_wie_die_Galerie()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.png", "c.jpg"]);
        var seen = new ConcurrentBag<int>();
        w.Renderer.OnRender = (_, _) => seen.Add(w.Thumbs.PendingCount);
        var ready = new ConcurrentBag<int>();
        w.Thumbs.ThumbnailReady += e => ready.Add(e.FileId);

        var report = await w.Stock.RunPassAsync().WaitAsync(Timeout);

        report.Should().Be(new ThumbnailStockReport(3, 0, 0));
        seen.Should().OnlyContain(p => p == 0, "der Vorrat zählt nicht als Warteschlange der Galerie");
        foreach (var file in await w.FilesAsync())
        {
            var row = (await w.RowsAsync(file.Id)).Should().ContainSingle().Subject;
            row.Status.Should().Be(ThumbnailStatus.Generated);
            row.Size.Should().Be(ThumbnailService.Size);
            row.SourceModifiedAtUtc.Should().Be(file.ModifiedAtUtc);
            row.CachePath.Should().Be(Path.Combine(w.Fx.Paths.GetLibraryThumbnailsPath(w.LibraryId), $"{file.Id}_{ThumbnailService.Size}_still.jpg"));
            File.Exists(row.CachePath).Should().BeTrue();
        }

        ready.Should().HaveCount(3, "offene Kacheln der Galerie bekommen das Bild wie bei eigenen Aufträgen");
        (await w.Stock.RunPassAsync()).Should().Be(ThumbnailStockReport.Empty, "nichts mehr offen");
        w.Renderer.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task Nur_Dateien_ohne_gueltiges_Vorschaubild_der_aktuellen_Groesse()
    {
        await using var w = await World.CreateAsync(["gueltig.jpg", "veraltet.jpg", "aufgegeben.jpg", "nochmal.jpg", "alt320.jpg"]);
        var files = (await w.FilesAsync()).ToDictionary(f => f.Name);
        await w.SeedAsync(files["gueltig.jpg"], ThumbnailService.Size, ThumbnailStatus.Generated);
        await w.SeedAsync(files["veraltet.jpg"], ThumbnailService.Size, ThumbnailStatus.Generated, modified: files["veraltet.jpg"].ModifiedAtUtc.AddMinutes(-5));
        await w.SeedAsync(files["aufgegeben.jpg"], ThumbnailService.Size, ThumbnailStatus.Failed, attempts: ThumbnailService.MaxAttempts);
        await w.SeedAsync(files["nochmal.jpg"], ThumbnailService.Size, ThumbnailStatus.Failed, attempts: 1);
        await w.SeedAsync(files["alt320.jpg"], ThumbnailSizing.LegacySizes[0], ThumbnailStatus.Generated);

        var report = await w.Stock.RunPassAsync().WaitAsync(Timeout);

        report.Should().Be(new ThumbnailStockReport(3, 0, 0));
        w.Renderer.Calls.Should().BeEquivalentTo(["veraltet.jpg", "nochmal.jpg", "alt320.jpg"]);
        (await w.RowsAsync(files["alt320.jpg"].Id)).Should().ContainSingle("die ältere Größe wird wie bei der Galerie ersetzt")
            .Which.Size.Should().Be(ThumbnailService.Size);
        (await w.RowsAsync(files["aufgegeben.jpg"].Id)).Single().Status.Should().Be(ThumbnailStatus.Failed);
    }

    [Fact]
    public async Task Fotos_zuerst_Videos_danach_Archive_nie()
    {
        await using var w = await World.CreateAsync(["1-film.mp4", "2-foto.jpg", "3-archiv.zip", "4-film.mov", "5-foto.png"]);

        var report = await w.Stock.RunPassAsync().WaitAsync(Timeout);

        report.Should().Be(new ThumbnailStockReport(4, 0, 0));
        w.Renderer.Calls.Should().Equal("2-foto.jpg", "5-foto.png", "1-film.mp4", "4-film.mov");
        var archive = (await w.FilesAsync()).Single(f => f.MediaType == MediaType.Archive);
        (await w.RowsAsync(archive.Id)).Should().BeEmpty("Archive haben kein Vorschaubild");
    }

    [Fact]
    public async Task Ohne_FFmpeg_bleiben_Videos_aussen_vor()
    {
        await using var w = await World.CreateAsync(["film.mp4", "foto.jpg"]);
        w.Renderer.IsAvailable = false;

        var report = await w.Stock.RunPassAsync().WaitAsync(Timeout);

        report.Should().Be(new ThumbnailStockReport(1, 0, 0), "Videos zählen ohne FFmpeg nicht als offen");
        w.Renderer.Calls.Should().Equal("foto.jpg");
    }

    // ----- (3) fehlend, offline, Platzhalter: ohne Dateizugriff, kein Download -----

    [Fact]
    public async Task Ueberspringt_fehlende_Wurzeln_offline_und_Platzhalter_ohne_den_Renderer()
    {
        await using var w = await World.CreateAsync(["fehlt.jpg", "weg.jpg", "platzhalter.jpg", "da.jpg"], inbox: ["eingang.jpg"]);
        var files = (await w.FilesAsync()).ToDictionary(f => f.Name);
        await w.MarkMissingAsync(files["fehlt.jpg"].Id);
        File.Delete(Path.Combine(w.Fx.LibraryRoot, "weg.jpg")); // noch nicht neu eingelesen
        var placeholder = Path.Combine(w.Fx.LibraryRoot, "platzhalter.jpg");
        File.SetAttributes(placeholder, File.GetAttributes(placeholder) | FileAttributes.Offline); // wie „nur online“
        ThumbnailService.IsLocallyAvailable(placeholder).Should().BeFalse();
        Directory.Move(w.Fx.InboxDir, w.Fx.InboxDir + "-abgezogen"); // Wurzel nicht erreichbar

        var report = await w.Stock.RunPassAsync().WaitAsync(Timeout);

        report.Should().Be(new ThumbnailStockReport(1, 2, 0), "„fehlt“ und Dateien unter einer nicht erreichbaren Wurzel zählen gar nicht als offen");
        w.Renderer.Calls.Should().Equal("da.jpg");
        foreach (var name in new[] { "fehlt.jpg", "weg.jpg", "platzhalter.jpg", "eingang.jpg" })
        {
            (await w.RowsAsync(files[name].Id)).Should().BeEmpty($"{name}: kein Versuch, keine Zeile");
        }

        File.SetAttributes(placeholder, FileAttributes.Normal);
    }

    [Fact]
    public async Task Nicht_lokale_Datei_wird_gemerkt_und_nach_Ablauf_wieder_geprueft()
    {
        await using var w = await World.CreateAsync(["platzhalter.jpg"]);
        var placeholder = Path.Combine(w.Fx.LibraryRoot, "platzhalter.jpg");
        File.SetAttributes(placeholder, File.GetAttributes(placeholder) | FileAttributes.Offline);
        using var forgetful = w.CreateStock(null, notLocalMemory: TimeSpan.Zero);

        (await w.Stock.RunPassAsync().WaitAsync(Timeout)).Should().Be(new ThumbnailStockReport(0, 1, 0));
        (await forgetful.RunPassAsync().WaitAsync(Timeout)).Should().Be(new ThumbnailStockReport(0, 1, 0));
        File.SetAttributes(placeholder, FileAttributes.Normal); // z. B. „immer auf diesem Gerät behalten“

        (await w.Stock.RunPassAsync().WaitAsync(Timeout)).Should().Be(ThumbnailStockReport.Empty, "gemerkt: zählt nicht als offen, nicht erneut abgefragt");
        w.Renderer.Calls.Should().BeEmpty();
        (await forgetful.RunPassAsync().WaitAsync(Timeout)).Should().Be(new ThumbnailStockReport(1, 0, 0), "nach Ablauf wieder nachgesehen");
    }

    [Fact]
    public async Task Nur_Platzhalter_und_Wurzeln_offline_ohne_Eintrag_und_ohne_Info_Zeile()
    {
        var log = new ListLogger<ThumbnailStockService>();
        await using var w = await World.CreateAsync(["platzhalter1.jpg", "platzhalter2.jpg"], inbox: ["eingang.jpg"], stockLogger: log);
        foreach (var name in new[] { "platzhalter1.jpg", "platzhalter2.jpg" })
        {
            var path = Path.Combine(w.Fx.LibraryRoot, name);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        }

        Directory.Move(w.Fx.InboxDir, w.Fx.InboxDir + "-abgezogen");
        var statuses = new ConcurrentQueue<ThumbnailStockStatus>();
        w.Fx.Messenger.Register<ConcurrentQueue<ThumbnailStockStatus>, ThumbnailStockStatusMessage>(statuses, static (q, m) => q.Enqueue(m.Status));
        try
        {
            (await w.Stock.RunPassAsync().WaitAsync(Timeout)).Should().Be(new ThumbnailStockReport(0, 2, 0));
            (await w.Stock.RunPassAsync().WaitAsync(Timeout)).Should().Be(ThumbnailStockReport.Empty, "nach jedem weiteren Auslöser: nichts offen");

            statuses.Should().BeEmpty("kein Eintrag in der Aktivitäts-Anzeige, wenn nichts entsteht");
            log.Messages.Where(m => m.Level >= LogLevel.Information).Should().BeEmpty("keine Info-Zeile, wenn nichts entsteht");
            w.Renderer.Calls.Should().BeEmpty();
        }
        finally
        {
            foreach (var name in new[] { "platzhalter1.jpg", "platzhalter2.jpg" })
            {
                File.SetAttributes(Path.Combine(w.Fx.LibraryRoot, name), FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public async Task Fortschritt_und_Gesamt_zaehlen_nur_was_wirklich_entsteht()
    {
        var log = new ListLogger<ThumbnailStockService>();
        await using var w = await World.CreateAsync(["a-platzhalter.jpg", "b-lokal.jpg", "c-platzhalter.jpg", "d-lokal.jpg"], stockLogger: log);
        var placeholders = new[] { "a-platzhalter.jpg", "c-platzhalter.jpg" }.Select(n => Path.Combine(w.Fx.LibraryRoot, n)).ToList();
        placeholders.ForEach(p => File.SetAttributes(p, File.GetAttributes(p) | FileAttributes.Offline));
        try
        {
            (await w.Stock.RunPassAsync().WaitAsync(Timeout)).Should().Be(new ThumbnailStockReport(2, 2, 0));

            // die lokalen Dateien wieder ohne Vorschaubild: der nächste Durchgang kennt die Platzhalter schon
            await w.ClearCacheAsync();
            var statuses = new ConcurrentQueue<ThumbnailStockStatus>();
            w.Fx.Messenger.Register<ConcurrentQueue<ThumbnailStockStatus>, ThumbnailStockStatusMessage>(statuses, static (q, m) => q.Enqueue(m.Status));
            var infoBefore = log.Messages.Count;

            (await w.Stock.RunPassAsync().WaitAsync(Timeout)).Should().Be(new ThumbnailStockReport(2, 0, 0), "gemerkte Platzhalter werden gar nicht erst geladen");

            var running = statuses.Where(s => s.IsRunning).ToList();
            running.Should().NotBeEmpty();
            running.Should().OnlyContain(s => s.Total == 2 && s.Done <= 2, "nur die beiden lokalen Dateien zählen");
            statuses.Last().Should().Be(ThumbnailStockStatus.Idle);
            log.Messages.Skip(infoBefore).Where(m => m.Level == LogLevel.Information).Select(m => m.Text)
                .Should().Contain("Vorschaubilder auf Vorrat: 2 Fotos und Videos noch ohne Vorschaubild");
        }
        finally
        {
            placeholders.ForEach(p => File.SetAttributes(p, FileAttributes.Normal));
        }
    }

    // ----- (4) Vordergrund, Galerie, Einlesen, Katalog-Umzug, Kerne, Laufwerk -----

    [Fact]
    public async Task Vordergrund_wirkt_je_Datei_und_danach_geht_es_von_selbst_weiter()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg", "c.jpg"]);
        IDisposable? lightbox = null;
        w.Renderer.OnRender = (_, call) =>
        {
            if (call == 1)
            {
                lightbox = w.Foreground.Begin(ForegroundNeed.Cpu, "Lightbox");
            }
        };

        var pass = w.Stock.RunPassAsync();
        await WaitUntilAsync(() => w.Planner!.GetState(BackgroundJob.ThumbnailStock).Wait == BackgroundWait.Foreground);

        w.Renderer.Calls.Should().Equal("a.jpg");
        w.Planner!.GetState(BackgroundJob.ThumbnailStock).Reason.Should().Be("wartet — Vorrang für „Lightbox“");
        pass.IsCompleted.Should().BeFalse();
        lightbox!.Dispose();
        (await pass.WaitAsync(Timeout)).Should().Be(new ThumbnailStockReport(3, 0, 0));
    }

    [Fact]
    public async Task Galerie_Auftraege_gehen_vor_und_werden_nicht_doppelt_erzeugt()
    {
        await using var w = await World.CreateAsync(["galerie.jpg", "vorrat1.jpg", "vorrat2.jpg"], foreground: false);
        using var gallery = new ManualResetEventSlim();
        w.Renderer.OnRender = (name, _) =>
        {
            if (name == "galerie.jpg")
            {
                gallery.Wait(Timeout);
            }
        };
        var galleryFile = (await w.FilesAsync()).Single(f => f.Name == "galerie.jpg");
        await w.Thumbs.RequestThumbnailsAsync([w.Request(galleryFile)]);
        w.Thumbs.PendingCount.Should().Be(1);
        var statuses = new ConcurrentQueue<ThumbnailStockStatus>();
        w.Fx.Messenger.Register<ConcurrentQueue<ThumbnailStockStatus>, ThumbnailStockStatusMessage>(statuses, static (q, m) => q.Enqueue(m.Status));

        var pass = w.Stock.RunPassAsync();
        await WaitUntilAsync(() => w.Renderer.Calls.Count == 1);
        await Task.Delay(150);
        w.Renderer.Calls.Should().Equal("galerie.jpg");
        pass.IsCompleted.Should().BeFalse();
        statuses.Should().BeEmpty("vor der ersten eigenen Datei wartet der Vorrat still");

        gallery.Set();
        (await pass.WaitAsync(Timeout)).Generated.Should().Be(2);
        w.Renderer.Calls.Should().BeEquivalentTo(["galerie.jpg", "vorrat1.jpg", "vorrat2.jpg"], "jede Datei genau einmal");
    }

    [Fact]
    public async Task Galerie_Auftrag_mitten_im_Durchgang_zeigt_den_Wartegrund()
    {
        await using var w = await World.CreateAsync(["a-vorrat.jpg", "b-galerie.jpg", "c-vorrat.jpg"], foreground: false);
        var galleryFile = (await w.FilesAsync()).Single(f => f.Name == "b-galerie.jpg");
        using var gallery = new ManualResetEventSlim();
        w.Renderer.OnRender = (name, _) =>
        {
            if (name == "a-vorrat.jpg")
            {
                w.Thumbs.RequestThumbnailsAsync([w.Request(galleryFile)]).GetAwaiter().GetResult(); // Galerie blättert dorthin
            }
            else if (name == "b-galerie.jpg")
            {
                gallery.Wait(Timeout);
            }
        };
        var waits = new ConcurrentQueue<string?>();
        w.Fx.Messenger.Register<ConcurrentQueue<string?>, ThumbnailStockStatusMessage>(waits, static (q, m) => q.Enqueue(m.Status.Message));

        var pass = w.Stock.RunPassAsync();
        await WaitUntilAsync(() => waits.Contains(ThumbnailStockService.QueueWaitReason) && w.Renderer.Calls.Count == 2);
        await Task.Delay(150);
        w.Renderer.Calls.Should().Equal("a-vorrat.jpg", "b-galerie.jpg");
        pass.IsCompleted.Should().BeFalse();

        gallery.Set();
        (await pass.WaitAsync(Timeout)).Generated.Should().Be(2);
        w.Renderer.Calls.Should().BeEquivalentTo(["a-vorrat.jpg", "b-galerie.jpg", "c-vorrat.jpg"], "jede Datei genau einmal");
    }

    [Fact]
    public async Task Video_der_Galerie_wartet_auf_das_Video_des_Vorrats()
    {
        // höchstens ein FFmpeg zugleich (ADR-0007), auch zwischen Video-Worker der Galerie und Vorrat
        await using var w = await World.CreateAsync(["film1.mp4", "film2.mp4"], foreground: false);
        var galleryFile = (await w.FilesAsync()).Single(f => f.Name == "film2.mp4");
        w.Renderer.OnRender = (name, _) =>
        {
            if (name == "film1.mp4")
            {
                w.Thumbs.RequestThumbnailsAsync([w.Request(galleryFile)]).GetAwaiter().GetResult();
                Thread.Sleep(300); // der Video-Worker der Galerie hätte jetzt Zeit, FFmpeg zu starten
            }
        };

        var report = await w.Stock.RunPassAsync().WaitAsync(Timeout);
        await WaitUntilAsync(() => w.Thumbs.PendingCount == 0);

        w.Renderer.MaxConcurrent.Should().Be(1, "das Video der Galerie beginnt erst nach dem des Vorrats");
        w.Renderer.Calls.Should().Equal("film1.mp4", "film2.mp4");
        report.Generated.Should().Be(1, "film2 hat die Galerie erstellt");
    }

    [Fact]
    public async Task Ruht_beim_Einlesen_und_laeuft_danach_weiter()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg"]);
        var scan = await w.Gate.AcquireAsync(w.LibraryId, CancellationToken.None);

        var pass = w.Stock.RunPassAsync();
        await WaitUntilAsync(() => w.Planner!.GetState(BackgroundJob.ThumbnailStock).Wait == BackgroundWait.Scan);
        w.Renderer.Calls.Should().BeEmpty();

        scan.Dispose();
        (await pass.WaitAsync(Timeout)).Generated.Should().Be(2);
    }

    [Fact]
    public async Task Ruht_waehrend_des_Katalog_Umzugs()
    {
        await using var w = await World.CreateAsync(["a.jpg"]);
        var move = w.Fx.ContextFactory.Suspend("Test");

        var pass = w.Stock.RunPassAsync();
        await Task.Delay(300);
        pass.IsCompleted.Should().BeFalse();
        w.Renderer.Calls.Should().BeEmpty();

        move.Dispose();
        (await pass.WaitAsync(Timeout)).Generated.Should().Be(1);
    }

    [Fact]
    public async Task Pausierter_Vorschaubild_Dienst_stellt_zurueck_und_dieselben_Dateien_kommen_danach_dran()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg"]);
        await w.Thumbs.PauseAsync(); // Katalog-Umzug (ADR-0026), Dienst angehalten

        var pass = w.Stock.RunPassAsync();
        await Task.Delay(300);
        pass.IsCompleted.Should().BeFalse();
        w.Renderer.Calls.Should().BeEmpty();

        w.Thumbs.Resume();
        (await pass.WaitAsync(Timeout)).Should().Be(new ThumbnailStockReport(2, 0, 0), "nichts als übersprungen verbucht");
    }

    [Fact]
    public async Task Katalogfehler_vor_dem_Rendern_gibt_die_Datei_fuer_die_Galerie_frei()
    {
        await using var w = await World.CreateAsync(["a.jpg"]);
        var failing = new FailingContextFactory(w.Fx.ContextFactory, failOnCreate: 1);
        using var thumbs = new ThumbnailService(failing, w.Fx.Paths, w.Renderer, w.Renderer, NullLogger<ThumbnailService>.Instance)
        {
            MaintenanceStartDelay = System.Threading.Timeout.InfiniteTimeSpan,
        };
        var request = w.Request((await w.FilesAsync()).Single());

        await FluentActions.Awaiting(() => thumbs.CreateStockAsync(request)).Should().ThrowAsync<InvalidOperationException>();
        await thumbs.RequestThumbnailsAsync([request]);
        await WaitUntilAsync(() => thumbs.PendingCount == 0 && w.Renderer.Calls.Count == 1);

        (await w.RowsAsync(request.FileId)).Single().Status.Should().Be(ThumbnailStatus.Generated, "kein hängender „in Arbeit“-Zustand");
    }

    // ----- (5) Auslöser: Start nach StartDelay, Einlesen, neue Dateien, Cache leeren -----

    [Fact]
    public async Task Erster_Durchgang_erst_nach_der_Startverzoegerung()
    {
        await using var w = await World.CreateAsync(["a.jpg"]);
        using (var defaults = new ThumbnailStockService(w.Fx.ContextFactory, w.Thumbs, NullLogger<ThumbnailStockService>.Instance))
        {
            defaults.StartDelay.Should().Be(TimeSpan.FromSeconds(60), "Standard: Start, erste Galerie-Seite und die übrigen Dienste gehen vor");
        }

        using var stock = w.CreateStock(null, startDelay: TimeSpan.FromSeconds(1));

        await stock.StartAsync();
        w.Renderer.Calls.Should().BeEmpty("vor Ablauf der Startverzögerung");
        await WaitUntilAsync(() => w.Renderer.Calls.Count == 1);
    }

    [Fact]
    public async Task Einlesen_stoesst_einen_Durchgang_an()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg"]);
        using var stock = await w.StartedStockAsync();

        await w.ClearCacheAsync(sendMessage: false);
        await Task.Delay(200);
        w.Renderer.Calls.Should().HaveCount(2, "ohne Auslöser kein Durchgang");

        w.Fx.Messenger.Send(new ScanCompletedMessage(w.LibraryId, new ScanResult(
            w.LibraryId, w.Fx.LibraryRoot, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) { Mode = ScanMode.Incremental }));
        await WaitUntilAsync(() => w.Renderer.Calls.Count == 4 && !stock.Status.IsRunning);
    }

    [Fact]
    public async Task Nur_neue_Dateien_stossen_einen_Durchgang_an()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg"]);
        using var stock = await w.StartedStockAsync();
        var ids = (await w.FilesAsync()).Select(f => f.Id).ToList();
        await w.ClearCacheAsync(sendMessage: false);

        foreach (var kind in new[] { FilesChangeKind.Metadata, FilesChangeKind.Moved, FilesChangeKind.Removed })
        {
            w.Fx.Messenger.Send(new FilesChangedMessage(w.LibraryId, kind, ids));
        }

        await Task.Delay(300);
        w.Renderer.Calls.Should().HaveCount(2, "Bewerten, Verschieben und Entfernen bringen keine Datei ohne Vorschaubild");

        w.Fx.Messenger.Send(new FilesChangedMessage(w.LibraryId, FilesChangeKind.Added, ids));
        await WaitUntilAsync(() => w.Renderer.Calls.Count == 4 && !stock.Status.IsRunning);
    }

    [Fact]
    public async Task Cache_leeren_stoesst_einen_Durchgang_an()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg"]);
        using var stock = await w.StartedStockAsync();

        var result = await w.ClearCacheAsync(); // wie Einstellungen › Speicher & Wartung › „Cache leeren“

        result.Rows.Should().Be(2);
        result.DeletedFiles.Should().Be(2);
        await WaitUntilAsync(() => w.Renderer.Calls.Count == 4 && !stock.Status.IsRunning);
        foreach (var file in await w.FilesAsync())
        {
            var row = (await w.RowsAsync(file.Id)).Single();
            row.Status.Should().Be(ThumbnailStatus.Generated);
            File.Exists(row.CachePath).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Cache_leeren_waehrend_eines_Durchgangs_hinterlaesst_keine_Zeile_ohne_Cache_Datei()
    {
        // Ohne Pause des Vorschaubild-Dienstes schrieb der Vorrat zwischen dem Löschen der Zeilen und dem langen Löschen der
        // Dateien neue Zeilen „Generated“, deren Datei das Löschen gleich wieder entfernte — und hielt sie danach für erledigt.
        var names = Enumerable.Range(0, 60).Select(i => $"f{i:00}.jpg").ToArray();
        await using var w = await World.CreateAsync(names);
        w.Renderer.Delay = TimeSpan.FromMilliseconds(15);
        var ballast = Path.Combine(w.Fx.Paths.ThumbnailsRoot, "0"); // vor dem Ordner der Bibliothek: das Löschen dauert
        Directory.CreateDirectory(ballast);
        for (var i = 0; i < 3000; i++)
        {
            File.WriteAllBytes(Path.Combine(ballast, $"{i}.jpg"), [1]);
        }

        var pass = w.Stock.RunPassAsync();
        await WaitUntilAsync(() => w.Renderer.Calls.Count >= 3);
        var result = await w.ClearCacheAsync(sendMessage: false).WaitAsync(Timeout);
        await pass.WaitAsync(Timeout);

        result.DeletedFiles.Should().BeGreaterThanOrEqualTo(3000);
        var rows = await w.AllRowsAsync();
        rows.Where(r => r.Status == ThumbnailStatus.Generated).Should().OnlyContain(r => File.Exists(r.CachePath),
            "jede Zeile „Generated“ hat ihre Cache-Datei");

        // der nächste Durchgang (ThumbnailCacheClearedMessage) erstellt den Rest
        await w.Stock.RunPassAsync().WaitAsync(Timeout);
        foreach (var file in await w.FilesAsync())
        {
            var row = (await w.RowsAsync(file.Id)).Should().ContainSingle().Subject;
            row.Status.Should().Be(ThumbnailStatus.Generated);
            File.Exists(row.CachePath).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Cache_leeren_haelt_den_Dienst_erst_nach_den_Dateien_und_nur_fuer_die_Zeilen_an()
    {
        // Das Löschen der Dateien dauert bei 500 000 Vorschaubildern Minuten — so lange soll die Galerie nicht stillstehen
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg"]);
        (await w.Stock.RunPassAsync().WaitAsync(Timeout)).Generated.Should().Be(2);
        var ballast = Path.Combine(w.Fx.Paths.ThumbnailsRoot, "0");
        Directory.CreateDirectory(ballast);
        for (var i = 0; i < 200; i++)
        {
            File.WriteAllBytes(Path.Combine(ballast, $"{i}.jpg"), [1]);
        }

        var probe = new PauseProbe(w.Thumbs, () =>
            (Directory.EnumerateFiles(w.Fx.Paths.ThumbnailsRoot, "*", SearchOption.AllDirectories).Count(), w.RowCountAsync().GetAwaiter().GetResult()));

        var result = await w.ClearCacheAsync(sendMessage: false, thumbs: probe).WaitAsync(Timeout);

        result.Should().Be(new ThumbnailCacheClearResult(202, 0, 2));
        probe.Events.Should().Equal("Pause (0 Dateien, 2 Zeilen)", "InvalidateAll", "Resume (0 Dateien, 0 Zeilen)");
    }

    [Fact]
    public async Task Cache_leeren_wartet_hoechstens_das_Zeitlimit_auf_einen_laufenden_Auftrag()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg"], foreground: false);
        var files = await w.FilesAsync();
        using var release = new ManualResetEventSlim();
        w.Renderer.OnRender = (name, _) =>
        {
            if (name == "a.jpg")
            {
                release.Wait(Timeout); // z. B. ein Bild auf einem langsamen Netzlaufwerk
            }
        };
        await w.Thumbs.RequestThumbnailsAsync([w.Request(files[0])]);
        await WaitUntilAsync(() => w.Renderer.Calls.Count == 1);

        var result = await w.ClearCacheAsync(pauseTimeout: TimeSpan.FromMilliseconds(300)).WaitAsync(Timeout);

        result.Rows.Should().Be(1, "die Zeile „Pending“ des laufenden Auftrags");
        release.Set();
        await WaitUntilAsync(() => w.Thumbs.PendingCount == 0);
        await w.Thumbs.RequestThumbnailsAsync([w.Request(files[1])]);
        await WaitUntilAsync(() => w.Renderer.Calls.Count == 2); // der Dienst ist nicht angehalten geblieben
        foreach (var row in (await w.AllRowsAsync()).Where(r => r.Status == ThumbnailStatus.Generated))
        {
            File.Exists(row.CachePath).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Pause_im_Planer_haelt_an_Fortsetzen_laeuft_weiter()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg"]);
        w.Planner!.SetPaused(BackgroundJob.ThumbnailStock, true);

        var pass = w.Stock.RunPassAsync();
        await WaitUntilAsync(() => w.Planner.GetState(BackgroundJob.ThumbnailStock).Wait == BackgroundWait.Paused);
        w.Renderer.Calls.Should().BeEmpty();

        w.Planner.SetPaused(BackgroundJob.ThumbnailStock, false);
        (await pass.WaitAsync(Timeout)).Generated.Should().Be(2);
    }

    [Fact]
    public void Gleichzeitig_nach_Laufwerksart_hoechstens_die_Kern_Obergrenze_Videos_einzeln()
    {
        ThumbnailStockService.Parallelism(MediaType.Image, 6).Should().Be(Math.Min(6, BackgroundCpu.MaxThreads));
        ThumbnailStockService.Parallelism(MediaType.Image, 1).Should().Be(1);
        ThumbnailStockService.Parallelism(MediaType.Image, 0).Should().Be(1);
        ThumbnailStockService.Parallelism(MediaType.Video, 6).Should().Be(1, "FFmpeg einzeln (ADR-0007)");
    }

    [Theory]
    [InlineData(StorageKind.Hdd)]
    [InlineData(StorageKind.Ssd)]
    public async Task Gleichzeitige_Vorschaubilder_passend_zum_Laufwerk(StorageKind kind)
    {
        var names = Enumerable.Range(0, 12).Select(i => $"f{i:00}.jpg").Concat(["v1.mp4", "v2.mp4", "v3.mp4"]).ToArray();
        await using var w = await World.CreateAsync(names, storage: kind);
        var imageMax = -1;
        w.Renderer.Delay = TimeSpan.FromMilliseconds(50);
        w.Renderer.OnRender = (name, _) =>
        {
            // erstes Video: alle Fotos sind fertig (Fotos zuerst) — ihr Höchstwert merken, ab hier nur noch Videos messen
            if (name.StartsWith('v') && Interlocked.CompareExchange(ref imageMax, w.Renderer.MaxConcurrent, -1) == -1)
            {
                w.Renderer.ResetMax();
            }
        };

        (await w.Stock.RunPassAsync().WaitAsync(Timeout)).Generated.Should().Be(15);

        var expected = ThumbnailStockService.Parallelism(MediaType.Image, w.Planner!.ReadParallelism(w.Fx.LibraryRoot));
        imageMax.Should().BeLessThanOrEqualTo(expected);
        if (expected > 1)
        {
            imageMax.Should().BeGreaterThan(1, "auf einer SSD entstehen mehrere zugleich");
        }

        w.Renderer.MaxConcurrent.Should().Be(1, "Videos einzeln");
    }

    // ----- (6) Schalter je Katalog -----

    [Fact]
    public async Task Schalter_Standard_an_je_Katalog_gespeichert_aus_tut_nichts()
    {
        await using var w = await World.CreateAsync(["a.jpg"]);
        var store = new ThumbnailStockSettingsStore(w.Fx.ContextFactory);
        (await store.LoadEnabledAsync()).Should().BeTrue("Standard an");

        using (var first = w.CreateStock(store))
        {
            await first.StartAsync();
            first.IsEnabled.Should().BeTrue();
            await first.SetEnabledAsync(false);
        }

        (await w.SettingAsync()).Should().Be("false");
        using var second = w.CreateStock(store);
        await second.StartAsync();
        second.IsEnabled.Should().BeFalse("aus dem Katalog gelesen");
        (await second.RunPassAsync()).Should().Be(ThumbnailStockReport.Empty);
        w.Renderer.Calls.Should().BeEmpty();

        await second.SetEnabledAsync(true);
        (await w.SettingAsync()).Should().Be("true");
        (await second.RunPassAsync().WaitAsync(Timeout)).Generated.Should().Be(1);
    }

    [Fact]
    public async Task Ausschalten_beendet_den_laufenden_Durchgang_nach_der_laufenden_Datei()
    {
        await using var w = await World.CreateAsync(["a.jpg", "b.jpg", "c.jpg"]);
        w.Renderer.OnRender = (_, call) =>
        {
            if (call == 1)
            {
                w.Stock.SetEnabledAsync(false).GetAwaiter().GetResult();
            }
        };

        var report = await w.Stock.RunPassAsync().WaitAsync(Timeout);

        report.Should().Be(new ThumbnailStockReport(1, 0, 0));
        w.Stock.Status.Should().Be(ThumbnailStockStatus.Idle);
        w.Stock.IsEnabled.Should().BeFalse();
    }

    // ----- (7) versteckte Bibliothek: alles bleibt im Katalog-Ordner -----

    [Fact]
    public async Task Versteckte_Bibliothek_nichts_ausserhalb_des_Katalogs_und_keine_Pfade_im_Protokoll()
    {
        // Katalog (DB + Vorschaubilder) liegt wie in der versteckten Sitzung im selben Wurzelordner wie die Bibliothek
        var log = new ListLogger<ThumbnailStockService>();
        var thumbLog = new ListLogger<ThumbnailService>();
        await using var w = await World.CreateAsync(["geheim1.jpg", "geheim2.jpg", "film.mp4"], stockLogger: log, thumbLogger: thumbLog);
        var before = Snapshot(w.Fx.RootDir);

        (await w.Stock.RunPassAsync().WaitAsync(Timeout)).Generated.Should().Be(3);

        var created = Snapshot(w.Fx.RootDir).Except(before, StringComparer.OrdinalIgnoreCase).ToList();
        created.Should().NotBeEmpty();
        created.Should().OnlyContain(p => p.StartsWith(w.Fx.Paths.AppDataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "nur Katalog und Vorschaubild-Cache der Sitzung (ICatalogStoragePaths/IAppDataPaths) werden beschrieben");
        foreach (var message in log.Messages.Concat(thumbLog.Messages).Where(m => m.Level >= LogLevel.Information).Select(m => m.Text))
        {
            message.Should().NotContain(w.Fx.LibraryRoot).And.NotContain("geheim", "keine Pfade oder Dateinamen im Protokoll");
        }
    }

    // ----- Hilfen -----

    private static HashSet<string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "Bedingung erreicht");
            await Task.Delay(10);
        }
    }

    private sealed class Storage(StorageKind kind) : IStorageKindProbe
    {
        public StorageKind GetKind(string path) => kind;
    }

    /// <summary>Vorschaubild-Dienst mit Mitschrift von Pause, Zustand verwerfen und Fortsetzen (Stand von Cache und Zeilen).</summary>
    private sealed class PauseProbe(IThumbnailService inner, Func<(int Files, int Rows)> state) : IThumbnailService
    {
        public List<string> Events { get; } = [];

        public int ThumbnailSize => inner.ThumbnailSize;

        public int PendingCount => inner.PendingCount;

        public event Action<ThumbnailReadyEventArgs>? ThumbnailReady
        {
            add => inner.ThumbnailReady += value;
            remove => inner.ThumbnailReady -= value;
        }

        public Task RequestThumbnailsAsync(IReadOnlyList<ThumbnailRequest> requests, CancellationToken cancellationToken = default)
            => inner.RequestThumbnailsAsync(requests, cancellationToken);

        public async Task PauseAsync(CancellationToken ct = default)
        {
            await inner.PauseAsync(ct);
            var (files, rows) = state();
            Events.Add($"Pause ({files} Dateien, {rows} Zeilen)");
        }

        public void InvalidateAll()
        {
            Events.Add("InvalidateAll");
            inner.InvalidateAll();
        }

        public void Resume()
        {
            var (files, rows) = state();
            Events.Add($"Resume ({files} Dateien, {rows} Zeilen)");
            inner.Resume();
        }
    }

    /// <summary>Bild- und Video-Renderer in einem: schreibt Reihenfolge und gleichzeitige Aufrufe mit; optional ein Haken.</summary>
    private sealed class RecordingRenderer : IImageThumbnailRenderer, IVideoThumbnailRenderer
    {
        private readonly Lock _gate = new();
        private readonly List<string> _calls = [];
        private int _running;
        private int _max;

        public bool IsAvailable { get; set; } = true;

        public Action<string, int>? OnRender { get; set; }

        public TimeSpan Delay { get; set; }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public int MaxConcurrent => Volatile.Read(ref _max);

        public void ResetMax() => Volatile.Write(ref _max, 0);

        public ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetShortEdge)
            => Record(sourcePath, targetThumbPath, targetShortEdge);

        public Task<ThumbnailRenderResult> RenderAsync(string sourcePath, string targetThumbPath, int targetShortEdge, CancellationToken cancellationToken)
            => Task.FromResult(Record(sourcePath, targetThumbPath, targetShortEdge));

        private ThumbnailRenderResult Record(string sourcePath, string targetThumbPath, int targetShortEdge)
        {
            var name = Path.GetFileName(sourcePath);
            int call;
            lock (_gate)
            {
                _calls.Add(name);
                call = _calls.Count;
                _running++;
                _max = Math.Max(_max, _running);
            }

            try
            {
                OnRender?.Invoke(name, call);
                if (Delay > TimeSpan.Zero)
                {
                    Thread.Sleep(Delay);
                }

                return FakeRenderHelper.WriteThumb(targetThumbPath, targetShortEdge);
            }
            finally
            {
                lock (_gate)
                {
                    _running--;
                }
            }
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _messages = new();

        public IReadOnlyCollection<(LogLevel Level, string Text)> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>Bibliothek mit gescannten Dateien, echtem ThumbnailService (Fake-Renderer), Planer und Vorrat.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly ILogger<ThumbnailStockService> _stockLogger;

        private World(TempLibraryFixture fx, int libraryId, RecordingRenderer renderer, ThumbnailService thumbs, ScanGate gate,
            ForegroundWork foreground, BackgroundPlanner planner, ILogger<ThumbnailStockService> stockLogger)
        {
            Fx = fx;
            LibraryId = libraryId;
            Renderer = renderer;
            Thumbs = thumbs;
            Gate = gate;
            Foreground = foreground;
            Planner = planner;
            _stockLogger = stockLogger;
            Stock = CreateStock(null);
        }

        public TempLibraryFixture Fx { get; }

        public int LibraryId { get; }

        public RecordingRenderer Renderer { get; }

        public ThumbnailService Thumbs { get; }

        public ScanGate Gate { get; }

        public ForegroundWork Foreground { get; }

        public BackgroundPlanner? Planner { get; }

        public ThumbnailStockService Stock { get; }

        public static async Task<World> CreateAsync(
            string[] names,
            string[]? inbox = null,
            StorageKind storage = StorageKind.Hdd,
            bool foreground = true,
            ILogger<ThumbnailStockService>? stockLogger = null,
            ILogger<ThumbnailService>? thumbLogger = null)
        {
            var fx = await TempLibraryFixture.CreateAsync();
            var library = await fx.CreateLibraryAsync();
            foreach (var name in names)
            {
                fx.WriteLibraryFile(name);
            }

            if (inbox is { Length: > 0 })
            {
                await fx.AddInboxRootAsync(library.Id);
                foreach (var name in inbox)
                {
                    fx.WriteInboxFile(name);
                }
            }

            await fx.ScanAsync(library.Id);
            var renderer = new RecordingRenderer();
            var work = new ForegroundWork();
            var thumbs = new ThumbnailService(fx.ContextFactory, fx.Paths, renderer, renderer,
                thumbLogger ?? NullLogger<ThumbnailService>.Instance, null, null, foreground ? work : null)
            {
                MaintenanceStartDelay = System.Threading.Timeout.InfiniteTimeSpan,
            };
            var gate = new ScanGate(fx.Messenger);
            var planner = new BackgroundPlanner(gate, fx.ContextFactory, new Storage(storage), foreground: work);
            return new World(fx, library.Id, renderer, thumbs, gate, work, planner, stockLogger ?? NullLogger<ThumbnailStockService>.Instance);
        }

        public ThumbnailStockService CreateStock(IThumbnailStockSettingsStore? settings, TimeSpan? startDelay = null, TimeSpan? notLocalMemory = null)
            => new(Fx.ContextFactory, Thumbs, _stockLogger, settings, Planner, Gate, Fx.Messenger, Renderer)
            {
                StartDelay = startDelay ?? System.Threading.Timeout.InfiniteTimeSpan,
                QueuePollInterval = TimeSpan.FromMilliseconds(20),
                NotLocalMemory = notLocalMemory ?? TimeSpan.FromMinutes(30),
            };

        /// <summary>Vorrat mit <c>StartDelay</c> 0 gestartet; kehrt zurück, wenn der erste Durchgang alles erstellt hat.</summary>
        public async Task<ThumbnailStockService> StartedStockAsync()
        {
            var stock = CreateStock(null, startDelay: TimeSpan.Zero);
            await stock.StartAsync();
            var count = (await FilesAsync()).Count(f => f.MediaType != MediaType.Archive);
            await WaitUntilAsync(() => Renderer.Calls.Count == count && !stock.Status.IsRunning);
            return stock;
        }

        /// <summary>„Cache leeren“ wie in den Einstellungen; ohne Nachricht, wenn der Auslöser selbst geprüft wird.</summary>
        public Task<ThumbnailCacheClearResult> ClearCacheAsync(bool sendMessage = true, IThumbnailService? thumbs = null, TimeSpan? pauseTimeout = null)
            => new ThumbnailCacheCleaner(Fx.ContextFactory, Fx.Paths, thumbs ?? Thumbs, sendMessage ? Fx.Messenger : new StrongReferenceMessenger())
            {
                PauseTimeout = pauseTimeout ?? TimeSpan.FromSeconds(30),
            }.ClearAsync();

        public async Task<int> RowCountAsync()
        {
            await using var ctx = Fx.CreateContext();
            return await ctx.Thumbnails.CountAsync();
        }

        public async Task<List<Thumbnail>> AllRowsAsync()
        {
            await using var ctx = Fx.CreateContext();
            return await ctx.Thumbnails.AsNoTracking().ToListAsync();
        }

        public ThumbnailRequest Request(MediaFile file)
            => new(LibraryId, file.Id, Path.Combine(Fx.LibraryRoot, file.Name), file.MediaType, file.ModifiedAtUtc);

        public async Task<List<MediaFile>> FilesAsync()
        {
            await using var ctx = Fx.CreateContext();
            return await ctx.Files.AsNoTracking().OrderBy(f => f.Id).ToListAsync();
        }

        public async Task<List<Thumbnail>> RowsAsync(int fileId)
        {
            await using var ctx = Fx.CreateContext();
            return await ctx.Thumbnails.AsNoTracking().Where(t => t.FileId == fileId).ToListAsync();
        }

        public async Task<string?> SettingAsync()
        {
            await using var ctx = Fx.CreateContext();
            return await ctx.Settings.AsNoTracking().Where(s => s.Key == ThumbnailStockSettingsStore.EnabledKey).Select(s => s.Value).FirstOrDefaultAsync();
        }

        public async Task MarkMissingAsync(int fileId)
        {
            await using var ctx = Fx.CreateContext();
            await ctx.Files.Where(f => f.Id == fileId).ExecuteUpdateAsync(u => u.SetProperty(f => f.Missing, true));
        }

        public async Task SeedAsync(MediaFile file, int size, ThumbnailStatus status, DateTime? modified = null, int attempts = 1)
        {
            var path = Path.Combine(Fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"{file.Id}_{size}_still.jpg");
            TempLibraryFixture.WriteFile(path, 300);
            await using var ctx = Fx.CreateContext();
            ctx.Thumbnails.Add(new Thumbnail
            {
                FileId = file.Id,
                Size = size,
                Kind = ThumbnailKind.Still,
                Format = "jpg",
                CachePath = path,
                SourceModifiedAtUtc = modified ?? file.ModifiedAtUtc,
                GeneratedAtUtc = status == ThumbnailStatus.Generated ? file.ModifiedAtUtc : null,
                ByteSize = 300,
                Status = status,
                AttemptCount = attempts,
            });
            await ctx.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Stock.Dispose();
            Planner?.Dispose();
            Thumbs.Dispose();
            await Task.Yield();
            Fx.Dispose();
        }
    }
}
