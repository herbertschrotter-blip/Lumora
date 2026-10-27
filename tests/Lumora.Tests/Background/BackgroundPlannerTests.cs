using Lumora.Services.Background;
using Lumora.Services.Scanning;
using Lumora.Tests.Activity;

namespace Lumora.Tests.Background;

/// <summary>Hintergrund-Planer (ADR-0074): Datei-Spur nach Vorrang und abwechselnd, Warten beim Einlesen, Pause, Abbrechen, Lesen je Laufwerk.</summary>
public sealed class BackgroundPlannerTests : IDisposable
{
    private readonly ManualTimerTime _time = new();
    private readonly ScanGate _scanGate = new(new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
    private readonly FakeStorage _storage = new();
    private readonly ForegroundWork _foreground = new();
    private readonly BackgroundPlanner _planner;

    public BackgroundPlannerTests()
    {
        _planner = new BackgroundPlanner(_scanGate, storage: _storage, time: _time, foreground: _foreground);
    }

    public void Dispose() => _planner.Dispose();

    [Fact]
    public async Task Datei_Spur_geht_nach_Vorrang_an_die_wichtigste_wartende_Aufgabe()
    {
        var dates = await _planner.AcquireAsync(BackgroundJob.CaptureDates, CancellationToken.None);
        var quality = _planner.AcquireAsync(BackgroundJob.Quality, CancellationToken.None);
        var duplicates = _planner.AcquireAsync(BackgroundJob.Duplicates, CancellationToken.None);

        quality.IsCompleted.Should().BeFalse();
        duplicates.IsCompleted.Should().BeFalse();
        _planner.GetState(BackgroundJob.Quality).Should().Be(new BackgroundJobState(BackgroundJob.Quality, BackgroundWait.Turn, "wartet — zuerst Aufnahmedatum"));

        dates.Dispose();

        duplicates.IsCompleted.Should().BeTrue("Doppelte vor Foto-Güte, obwohl später angefragt");
        quality.IsCompleted.Should().BeFalse();
        (await duplicates).Dispose();
        quality.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Pruefsummen_im_Leerlauf_kommen_erst_nach_allen_anderen_Dateidiensten()
    {
        var quality = await _planner.AcquireAsync(BackgroundJob.Quality, CancellationToken.None);
        var checksums = _planner.AcquireAsync(BackgroundJob.Checksums, CancellationToken.None);
        var duplicates = _planner.AcquireAsync(BackgroundJob.Duplicates, CancellationToken.None);

        _planner.UsesFileLane(BackgroundJob.Checksums).Should().BeTrue();
        _planner.GetState(BackgroundJob.Checksums).Reason.Should().Be("wartet — zuerst Foto-Güte");
        quality.Dispose();

        duplicates.IsCompleted.Should().BeTrue("Doppelte vor den Prüfsummen, obwohl später angefragt");
        checksums.IsCompleted.Should().BeFalse();
        _planner.GetState(BackgroundJob.Checksums).Reason.Should().Be("wartet — zuerst Doppelte");
        (await duplicates).Dispose();
        checksums.IsCompleted.Should().BeTrue();
        (await checksums).Dispose();
    }

    [Fact]
    public async Task Gleicher_Vorrang_wechselt_sich_Stapel_um_Stapel_ab()
    {
        var dates = await _planner.AcquireAsync(BackgroundJob.CaptureDates, CancellationToken.None);
        var places = _planner.AcquireAsync(BackgroundJob.GeoLocations, CancellationToken.None);

        dates.Dispose();
        var datesAgain = _planner.AcquireAsync(BackgroundJob.CaptureDates, CancellationToken.None);

        places.IsCompleted.Should().BeTrue("Orte warteten schon länger");
        datesAgain.IsCompleted.Should().BeFalse();
        _planner.GetState(BackgroundJob.CaptureDates).Reason.Should().Be("wartet — zuerst Orte nachtragen");
        (await places).Dispose();
        datesAgain.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Beim_Einlesen_warten_alle_auch_Gesichter_und_es_geht_danach_von_selbst_weiter()
    {
        var scan = await _scanGate.AcquireAsync(7, CancellationToken.None);

        var dates = _planner.AcquireAsync(BackgroundJob.CaptureDates, CancellationToken.None);
        var faces = _planner.AcquireAsync(BackgroundJob.Faces, CancellationToken.None);

        dates.IsCompleted.Should().BeFalse();
        faces.IsCompleted.Should().BeFalse();
        _planner.GetState(BackgroundJob.Faces).Should().Be(new BackgroundJobState(BackgroundJob.Faces, BackgroundWait.Scan, "wartet auf das Einlesen"));
        _planner.CanRun(BackgroundJob.Stories).Should().BeFalse();

        scan.Dispose();
        _time.Advance(BackgroundPlanner.PollInterval);

        dates.IsCompleted.Should().BeTrue();
        faces.IsCompleted.Should().BeTrue();
        _planner.GetState(BackgroundJob.Faces).Wait.Should().Be(BackgroundWait.None);
    }

    [Fact]
    public async Task Pausierte_Aufgabe_wartet_die_anderen_laufen_weiter()
    {
        _planner.SetPaused(BackgroundJob.Duplicates, true);

        var duplicates = _planner.AcquireAsync(BackgroundJob.Duplicates, CancellationToken.None);
        var dates = _planner.AcquireAsync(BackgroundJob.CaptureDates, CancellationToken.None);

        duplicates.IsCompleted.Should().BeFalse();
        _planner.GetState(BackgroundJob.Duplicates).Should().Be(new BackgroundJobState(BackgroundJob.Duplicates, BackgroundWait.Paused, "pausiert"));
        dates.IsCompleted.Should().BeTrue();
        (await dates).Dispose();
        duplicates.IsCompleted.Should().BeFalse("weiter pausiert, auch bei freier Spur");

        _planner.SetPaused(BackgroundJob.Duplicates, false);

        duplicates.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Alle_pausieren_haelt_auch_Gesichter_Motive_und_Storys_an()
    {
        var changes = 0;
        _planner.Changed += () => changes++;

        _planner.SetAllPaused(true);
        var motifs = _planner.AcquireAsync(BackgroundJob.Motifs, CancellationToken.None);

        _planner.AllPaused.Should().BeTrue();
        _planner.CanRun(BackgroundJob.Stories).Should().BeFalse();
        motifs.IsCompleted.Should().BeFalse();
        _planner.GetState(BackgroundJob.Motifs).Reason.Should().Be("alle Hintergrund-Aufgaben pausiert");

        _planner.SetAllPaused(false);

        motifs.IsCompleted.Should().BeTrue();
        (await motifs).Dispose();
        _planner.CanRun(BackgroundJob.Stories).Should().BeTrue();
        changes.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Braucht_der_Benutzer_die_Grafikkarte_warten_Gesichter_und_Motive_und_laufen_danach_von_selbst_weiter()
    {
        var changes = 0;
        _planner.Changed += () => changes++;
        var claim = _foreground.Begin(ForegroundNeed.Gpu, "Foto verbessern");

        var faces = _planner.AcquireAsync(BackgroundJob.Faces, CancellationToken.None);
        var motifs = _planner.AcquireAsync(BackgroundJob.Motifs, CancellationToken.None);
        var dates = _planner.AcquireAsync(BackgroundJob.CaptureDates, CancellationToken.None);

        faces.IsCompleted.Should().BeFalse();
        motifs.IsCompleted.Should().BeFalse();
        _planner.GetState(BackgroundJob.Faces).Should().Be(
            new BackgroundJobState(BackgroundJob.Faces, BackgroundWait.Foreground, "wartet — Vorrang für „Foto verbessern“"));
        dates.IsCompleted.Should().BeTrue("Dateien lesen braucht die Grafikkarte nicht");
        _planner.CanRun(BackgroundJob.Stories).Should().BeTrue();

        claim.Dispose();

        faces.IsCompleted.Should().BeTrue("ohne Warten auf den nächsten Takt");
        motifs.IsCompleted.Should().BeTrue();
        _planner.GetState(BackgroundJob.Motifs).Wait.Should().Be(BackgroundWait.None);
        changes.Should().BeGreaterThanOrEqualTo(2);
        (await dates).Dispose();
    }

    [Fact]
    public async Task Braucht_der_Benutzer_den_Prozessor_warten_alle_Hintergrund_Aufgaben()
    {
        Task<IDisposable> dates, quality;
        using (_foreground.Begin(ForegroundNeed.Cpu, "Assistent"))
        {
            dates = _planner.AcquireAsync(BackgroundJob.CaptureDates, CancellationToken.None);
            quality = _planner.AcquireAsync(BackgroundJob.Quality, CancellationToken.None);

            dates.IsCompleted.Should().BeFalse();
            quality.IsCompleted.Should().BeFalse();
            _planner.CanRun(BackgroundJob.Stories).Should().BeFalse();
            _planner.GetState(BackgroundJob.CaptureDates).Reason.Should().Be("wartet — Vorrang für „Assistent“");

            using (_foreground.Begin(ForegroundNeed.Gpu, "Foto verbessern"))
            {
                _planner.GetState(BackgroundJob.CaptureDates).Reason.Should().Be("wartet — Vorrang für „Foto verbessern“", "die jüngste Arbeit wird genannt");
            }

            dates.IsCompleted.Should().BeFalse("der Assistent rechnet noch");
        }

        _planner.CanRun(BackgroundJob.Stories).Should().BeTrue();
        dates.IsCompleted.Should().BeTrue();
        quality.IsCompleted.Should().BeFalse("Foto-Güte steht in der Datei-Spur hinter dem Aufnahmedatum");
        (await dates).Dispose();
        quality.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Abgebrochenes_Warten_gibt_die_Reihe_frei()
    {
        var dates = await _planner.AcquireAsync(BackgroundJob.CaptureDates, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var places = _planner.AcquireAsync(BackgroundJob.GeoLocations, cts.Token);
        var duplicates = _planner.AcquireAsync(BackgroundJob.Duplicates, CancellationToken.None);

        await cts.CancelAsync();
        dates.Dispose();

        places.IsCanceled.Should().BeTrue();
        _planner.GetState(BackgroundJob.GeoLocations).Wait.Should().Be(BackgroundWait.None, "kein alter Wartegrund");
        duplicates.IsCompleted.Should().BeTrue("die abgebrochene Aufgabe blockiert die Spur nicht");
    }

    [Fact]
    public async Task Erlaubnis_meldet_ob_gewartet_wurde()
    {
        (await BackgroundPlanner.WaitForPermissionAsync(_planner, BackgroundJob.Faces, CancellationToken.None)).Should().BeFalse();
        (await BackgroundPlanner.WaitForPermissionAsync(null, BackgroundJob.Faces, CancellationToken.None)).Should().BeFalse();

        _planner.SetAllPaused(true);
        var waiting = BackgroundPlanner.WaitForPermissionAsync(_planner, BackgroundJob.Faces, CancellationToken.None);
        waiting.IsCompleted.Should().BeFalse();
        _planner.SetAllPaused(false);

        (await waiting).Should().BeTrue();
    }

    [Fact]
    public void Gleichzeitig_gelesen_wird_nur_auf_SSD_und_Netz()
    {
        var cores = Environment.ProcessorCount;
        _storage.Kind = StorageKind.Ssd;
        _planner.ReadParallelism(@"X:\Fotos\a.jpg").Should().Be(Math.Clamp(cores / 2, 2, 6));
        _storage.Kind = StorageKind.Network;
        _planner.ReadParallelism(@"X:\Fotos\a.jpg").Should().Be(2);
        _storage.Kind = StorageKind.Hdd;
        _planner.ReadParallelism(@"X:\Fotos\a.jpg").Should().Be(1);
        _storage.Kind = StorageKind.Unknown;
        _planner.ReadParallelism(@"X:\Fotos\a.jpg").Should().Be(1);
    }

    [Fact]
    public void Laufwerksart_UNC_ist_Netz_leerer_Pfad_unbekannt()
    {
        var probe = new StorageKindProbe();

        probe.GetKind(@"\\server\freigabe\a.jpg").Should().Be(StorageKind.Network);
        probe.GetKind(string.Empty).Should().Be(StorageKind.Unknown);
        probe.GetKind("relativ.jpg").Should().Be(StorageKind.Unknown);
    }

    private sealed class FakeStorage : IStorageKindProbe
    {
        public StorageKind Kind { get; set; } = StorageKind.Hdd;

        public StorageKind GetKind(string path) => Kind;
    }
}
