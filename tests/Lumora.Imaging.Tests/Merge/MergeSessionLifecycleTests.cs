using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge;
using Lumora.Imaging.Merge.Panorama;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Tests.Worker;
using Lumora.Imaging.Worker;
using Lumora.Services.Operations;
using Lumora.Tests.Enhance;
using Lumora.Tests.Fixtures;

namespace Lumora.Imaging.Tests.Merge;

/// <summary>
/// Lebenszyklus der Vorschau-Sitzung und des Merge-Dienstes (Befunde der Prüfung, 0.5.48) mit einem Schein-Werkzeug statt
/// OpenCV: Ende der Katalog-Sitzung bricht ein laufendes Speichern ab und wartet auf das Ende seines Rechen-Prozesses, Schließen
/// des Fensters lässt es zu Ende rechnen, nach einer gescheiterten Neu-Ausrichtung wird neu ausgerichtet, ein abgebrochenes
/// Umstellen speichert nie eine nie gezeigte Mischung, ein einmaliger Startfehler sperrt nicht für die ganze Sitzung.
/// </summary>
public sealed class MergeSessionLifecycleTests : IAsyncLifetime
{
    private TempLibraryFixture _fx = null!;
    private FileOperationService _ops = null!;
    private readonly FakeRecycleBin _bin = new() { DeleteFiles = true };
    private List<int> _ids = [];

    public async Task InitializeAsync()
    {
        _fx = await TempLibraryFixture.CreateAsync();
        var library = await _fx.CreateLibraryAsync();
        var dir = Path.Combine(_fx.LibraryRoot, "Pano");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"p{i}.jpg"), Lumora.Tests.Merge.ExifTestJpeg.Build(orientation: 1, gps: null, withThumbnailIfd: false));
        }

        await _fx.ScanAsync(library.Id);
        await using var ctx = _fx.CreateContext();
        _ids = await ctx.Files.Where(f => f.Name.StartsWith("p")).OrderBy(f => f.Name).Select(f => f.Id).ToListAsync();
        _ops = _fx.CreateFileOperationService(_bin);
    }

    public Task DisposeAsync()
    {
        _ops.Dispose();
        _fx.Dispose();
        return Task.CompletedTask;
    }

    private (MergeService Service, FakeStitcher Stitcher, string WorkRoot) Create(IImagingWorkerHost? host = null)
    {
        var storage = new TestStoragePaths(Path.Combine(_fx.RootDir, "appdata"));
        host ??= new ImagingWorkerHost(storage, ImagingWorkerProcessTests.Options());
        var stitcher = new FakeStitcher();
        return (new MergeService(_fx.ContextFactory, host, stitcher, _ops, new MergeSettingsStore(_fx.ContextFactory), storage), stitcher, host.WorkRoot);
    }

    [Fact]
    public async Task Ende_der_Katalog_Sitzung_bricht_ein_laufendes_Speichern_ab_und_wartet_auf_sein_Ende()
    {
        var (service, stitcher, workRoot) = Create();
        var session = service.OpenPreviewSession(new MergeRequest(_ids));
        await session.StartAsync();
        stitcher.HoldRender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var saving = session.SaveAsync(); // wie „Im Hintergrund weiterrechnen“: niemand wartet im Fenster
        await WaitUntilAsync(() => stitcher.RenderRunning, "Rendern läuft");
        service.IsSaving.Should().BeTrue();

        await service.DisposeAsync();

        stitcher.RenderRunning.Should().BeFalse("der Rechen-Prozess ist beendet, bevor die Dienste entsorgt bzw. der Container ausgehängt wird");
        stitcher.RenderCanceled.Should().BeTrue();
        stitcher.SessionsDisposed.Should().Be(1, "auch der Vorschau-Prozess");
        service.IsSaving.Should().BeFalse();
        Func<Task> saved = () => saving;
        await saved.Should().ThrowAsync<OperationCanceledException>();
        Directory.EnumerateFiles(workRoot, "*", SearchOption.AllDirectories).Should().BeEmpty("Zwischenstände weg, nichts still gespeichert");
        await using (var ctx = _fx.CreateContext())
        {
            (await ctx.MergedPhotos.CountAsync()).Should().Be(0);
        }

        var act = () => service.OpenPreviewSession(new MergeRequest(_ids));
        act.Should().Throw<MergeException>("nach dem Ende beginnt nichts mehr");
        await service.DisposeAsync(); // mehrfach aufrufbar
        service.Dispose();
    }

    [Fact]
    public async Task Fenster_schliessen_laesst_das_Speichern_zu_Ende_rechnen_und_jedes_Schliessen_wartet_darauf()
    {
        var (service, stitcher, _) = Create();
        var session = service.OpenPreviewSession(new MergeRequest(_ids));
        await session.StartAsync();
        stitcher.HoldRender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = session.SaveAsync();
        await WaitUntilAsync(() => stitcher.RenderRunning, "Rendern läuft");

        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();
        await Task.Delay(150);
        (first.IsCompleted || second.IsCompleted).Should().BeFalse("ein Speichern rechnet zu Ende — auch der zweite Aufruf wartet");

        stitcher.HoldRender.SetResult();
        var result = await saving;
        await Task.WhenAll(first, second);

        result.Success.Should().BeTrue(result.Error);
        stitcher.RenderCanceled.Should().BeFalse();
        service.IsSaving.Should().BeFalse();
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Nach_gescheiterter_Neu_Ausrichtung_richtet_die_naechste_Vorschau_neu_aus()
    {
        var (service, stitcher, _) = Create();
        await using var session = service.OpenPreviewSession(new MergeRequest(_ids));
        await session.StartAsync();
        stitcher.Estimates.Should().Be(1);

        // Zusammensetzen scheitert an der Prüfung → Wiederholung mit neuem Startwert; diese Ausrichtung scheitert selbst
        stitcher.FailNextComposeCheck = true;
        stitcher.FailNextEstimate = true;
        var failed = () => session.UpdateAsync(session.Options with { Projection = PanoramaProjection.Cylindrical });
        await failed.Should().ThrowAsync<MergeException>();
        stitcher.Aligned.Should().BeFalse("der Hilfsprozess verwirft die Ausrichtung schon vor dem Lesen");

        var next = await session.UpdateAsync(session.Options with { Projection = PanoramaProjection.Perspective });

        next.CanSave.Should().BeTrue();
        stitcher.Estimates.Should().Be(3, "neu ausgerichtet statt „Die Berechnung passt nicht zur Auswahl“");
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Abgebrochenes_Umstellen_speichert_die_gezeigte_Vorschau_und_nie_eine_Mischung()
    {
        var (service, stitcher, _) = Create();
        await using var session = service.OpenPreviewSession(new MergeRequest(_ids));
        var shown = await session.StartAsync();
        var shownOptions = session.Options;
        shownOptions.Crop.Should().Be(MergeCrop.Straight);

        stitcher.HoldCompose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var change = session.UpdateAsync(shownOptions with { Projection = PanoramaProjection.Cylindrical, Crop = MergeCrop.None, Exposure = false });
        await WaitUntilAsync(() => stitcher.ComposeWaiting, "Umstellen läuft");
        session.Cancel();
        Func<Task> changed = () => change;
        await changed.Should().ThrowAsync<OperationCanceledException>();

        session.Options.Should().Be(shownOptions, "Optionen der gezeigten Vorschau");
        session.Current.Should().BeSameAs(shown);
        stitcher.HoldCompose = null;
        var result = await session.SaveAsync();

        result.Success.Should().BeTrue(result.Error);
        var spec = stitcher.Renders.Single();
        (spec.Crop, spec.Exposure, spec.Projection).Should().Be((WireCrop.Straight, true, (WireProjection)(int)shown.Projection), "gerendert wird genau die gezeigte Vorschau");
        await using var ctx = _fx.CreateContext();
        var recipe = MergeRecipe.FromJson((await ctx.MergedPhotos.AsNoTracking().SingleAsync()).RecipeJson)!;
        (recipe.Options.Crop, recipe.Options.Exposure).Should().Be((MergeCrop.Straight, true), "auch das Rezept");
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Abbrechen_und_beenden_bricht_auch_ein_Speichern_ab_das_noch_auf_die_Vorschau_wartet()
    {
        var (service, stitcher, _) = Create();
        await using var session = service.OpenPreviewSession(new MergeRequest(_ids));
        await session.StartAsync();
        stitcher.HoldCompose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = session.UpdateAsync(session.Options with { Crop = MergeCrop.None });
        await WaitUntilAsync(() => stitcher.ComposeWaiting, "Vorschau läuft");
        var saving = session.SaveAsync(); // wartet auf die Sperre der laufenden Vorschau
        await WaitUntilAsync(() => service.IsSaving, "Speichern angefordert");

        service.CancelSaves(); // „Abbrechen und zusperren“
        stitcher.HoldCompose.TrySetResult();

        Func<Task> saved = () => saving;
        await saved.Should().ThrowAsync<OperationCanceledException>("abgebrochen heißt abgebrochen — auch vor dem Rendern");
        stitcher.Renders.Should().BeEmpty();
        service.IsSaving.Should().BeFalse();
        try
        {
            await update;
        }
        catch (OperationCanceledException)
        {
            // die laufende Vorschau durfte mit abbrechen
        }

        await using var ctx = _fx.CreateContext();
        (await ctx.MergedPhotos.CountAsync()).Should().Be(0);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Zweites_Rueckgaengig_gilt_als_erledigt_statt_als_Fehlschlag()
    {
        var (service, _, _) = Create();
        await using var session = service.OpenPreviewSession(new MergeRequest(_ids));
        await session.StartAsync();
        var result = await session.SaveAsync();
        result.Success.Should().BeTrue(result.Error);

        var first = await MergeUndo.UndoAsync(_ops, result.BatchId, "Panorama");
        var second = await MergeUndo.UndoAsync(_ops, result.BatchId, "Panorama"); // z. B. schon über die Aktivitäts-Anzeige

        first.Succeeded.Should().BeTrue(first.Message);
        second.Succeeded.Should().BeTrue("schon rückgängig gemacht");
        second.Message.Should().StartWith("Schon rückgängig");
        MergeUndo.Describe(null, "Panorama").Succeeded.Should().BeFalse();
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Einmaliger_Startfehler_wird_beim_naechsten_Versuch_neu_geprueft_fehlende_Programmdatei_nicht()
    {
        var storage = new TestStoragePaths(Path.Combine(_fx.RootDir, "appdata"));
        var flaky = new FlakyHost(new ImagingWorkerHost(storage, ImagingWorkerProcessTests.Options())) { Failures = 1 };
        var (service, _, _) = Create(flaky);

        var failed = await service.GetAvailabilityAsync();
        failed.Available.Should().BeFalse("erster Start scheitert (z. B. Virenscanner)");
        failed.Transient.Should().BeTrue("nur gerade — nicht merken");
        var second = await service.GetAvailabilityAsync();
        second.Available.Should().BeTrue("nicht für die ganze Sitzung gesperrt");
        (await service.GetAvailabilityAsync()).Should().BeSameAs(second, "Erfolg wird gemerkt");
        flaky.Starts.Should().Be(2);

        var missingHost = new ImagingWorkerHost(storage, new ImagingWorkerOptions { ExePath = Path.Combine(_fx.RootDir, "fehlt.exe") });
        var missing = new MergeService(_fx.ContextFactory, missingHost, new FakeStitcher(), _ops, new MergeSettingsStore(_fx.ContextFactory), storage);
        var first = await missing.GetAvailabilityAsync();
        first.Available.Should().BeFalse();
        (await missing.GetAvailabilityAsync()).Should().BeSameAs(first, "Programmdatei fehlt: bleibt so");
        await service.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException(what + " nicht erreicht");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Scheitert die ersten <see cref="Failures"/> Starts mit „Start fehlgeschlagen“, danach der echte Hilfsprozess.</summary>
    private sealed class FlakyHost(IImagingWorkerHost inner) : IImagingWorkerHost
    {
        public int Failures { get; set; }

        public int Starts { get; private set; }

        public string WorkRoot => inner.WorkRoot;

        public bool IsInstalled => inner.IsInstalled;

        public bool IsBusy => inner.IsBusy;

        public Task<ImagingWorkerProcess> StartAsync(int threads, CancellationToken ct)
        {
            Starts++;
            if (Failures-- > 0)
            {
                throw new ImagingWorkerException(ImagingFailure.StartFailed, "Der Rechen-Prozess hat sich nicht gemeldet.");
            }

            return inner.StartAsync(threads, ct);
        }

        public Task<IDisposable> EnterHeavyAsync(CancellationToken ct) => inner.EnterHeavyAsync(ct);
    }

    /// <summary>
    /// Schein-Werkzeug wie der Hilfsprozess: Ausrichten verwirft die alte Ausrichtung vor dem Lesen, Zusammensetzen ohne
    /// Ausrichtung ist ein Fehler („Erst ausrichten“), Ergebnisse sind kleine JPEG-förmige Dateien.
    /// </summary>
    private sealed class FakeStitcher : IPanoramaStitcher
    {
        public int Estimates { get; private set; }

        public bool Aligned { get; private set; }

        public bool FailNextEstimate { get; set; }

        public bool FailNextComposeCheck { get; set; }

        public TaskCompletionSource? HoldCompose { get; set; }

        public bool ComposeWaiting { get; private set; }

        public TaskCompletionSource? HoldRender { get; set; }

        public bool RenderRunning { get; private set; }

        public bool RenderCanceled { get; private set; }

        public int SessionsDisposed { get; private set; }

        public List<ComposeSpec> Renders { get; } = [];

        public Task<IPanoramaStitchSession> OpenSessionAsync(int threads, CancellationToken ct) => Task.FromResult<IPanoramaStitchSession>(new Session(this));

        public async Task<ComposeResult> RenderAsync(RenderRequestData request, Action<ProgressMessage>? progress, int threads, Action<ImagingWorkerProcess>? started, CancellationToken ct)
        {
            Renders.Add(request.Settings);
            RenderRunning = true;
            try
            {
                if (HoldRender is { } hold)
                {
                    await hold.Task.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                RenderCanceled = true;
                throw;
            }
            finally
            {
                RenderRunning = false;
            }

            return Result(request.OutputPath, (WireProjection)request.Settings.Projection, passed: true);
        }

        private static ComposeResult Result(string path, WireProjection projection, bool passed)
        {
            File.WriteAllBytes(path, Lumora.Tests.Merge.ExifTestJpeg.Plain());
            return new ComposeResult
            {
                OutputPath = path,
                Width = 300,
                Height = 100,
                FullWidth = 300,
                FullHeight = 100,
                Scale = 1,
                Projection = projection == WireProjection.Auto ? WireProjection.Cylindrical : projection,
                Check = passed ? new QualityCheck { Passed = true, UsedImages = 3, TotalImages = 3 } : new QualityCheck { Passed = false, Problems = ["tilted"], UsedImages = 3, TotalImages = 3 },
            };
        }

        private sealed class Session(FakeStitcher owner) : IPanoramaStitchSession
        {
            public bool IsAlive => true;

            public Task<EstimateResult> EstimateAsync(IReadOnlyList<SourceSpec> sources, int decodeEdge, int seed, Action<ProgressMessage>? progress, CancellationToken ct)
            {
                owner.Estimates++;
                owner.Aligned = false; // wie PanoramaJobs.Estimate: alte Quellen und Ausrichtung zuerst weg
                if (owner.FailNextEstimate)
                {
                    owner.FailNextEstimate = false;
                    throw new ImagingWorkerException(ImagingFailure.Error, "Quelle 1: nicht lesbar", WorkerErrorCode.SourceUnreadable);
                }

                owner.Aligned = true;
                return Task.FromResult(new EstimateResult
                {
                    Alignment = new AlignmentSpec { Cameras = sources.Select(s => new CameraSpec { Source = s.Index, Width = 100, Height = 100, F = 100, R = [1, 0, 0, 0, 1, 0, 0, 0, 1] }).ToList(), Seed = seed },
                    SuggestedProjection = WireProjection.Cylindrical,
                });
            }

            public async Task<ComposeResult> ComposeAsync(ComposeSpec settings, string outputPath, int jpegQuality, Action<ProgressMessage>? progress, CancellationToken ct, string? framePath = null)
            {
                if (!owner.Aligned)
                {
                    throw new ImagingWorkerException(ImagingFailure.Error, "Erst ausrichten, dann zusammensetzen.", WorkerErrorCode.BadRequest);
                }

                if (owner.HoldCompose is { } hold)
                {
                    owner.ComposeWaiting = true;
                    try
                    {
                        await hold.Task.WaitAsync(ct);
                    }
                    finally
                    {
                        owner.ComposeWaiting = false;
                    }
                }

                var fail = owner.FailNextComposeCheck;
                owner.FailNextComposeCheck = false;
                return Result(outputPath, settings.Projection, passed: !fail);
            }

            public void SetThreads(int threads)
            {
            }

            public ValueTask DisposeAsync()
            {
                owner.SessionsDisposed++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
