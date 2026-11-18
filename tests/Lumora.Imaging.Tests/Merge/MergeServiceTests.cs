using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge;
using Lumora.Imaging.Merge.Panorama;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Tests.Synthetic;
using Lumora.Imaging.Tests.Worker;
using Lumora.Imaging.Worker;
using Lumora.Services.Activity;
using Lumora.Services.Background;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Services.Operations;
using Lumora.Tests.Enhance;
using Lumora.Tests.Fixtures;

namespace Lumora.Imaging.Tests.Merge;

/// <summary>
/// Merge-Dienst von der Auswahl bis zur Katalogzeile (ADR-0078, Abnahme Stufe 1): Analyse mit Zeit-Nachbarn, Vorschau-Sitzung
/// mit Umschalten der Aufnahmeart ohne neues Ausrichten, Vorrang nur beim Rechnen, Speichern als neue Datei neben dem ersten
/// Einzelbild (Zeit/Ort im EXIF, Rezept), Rückgängig über die Aktivität, versteckte Sitzung ohne Spuren außerhalb des
/// Containers. Echter Hilfsprozess, synthetische Bilder.
/// </summary>
public sealed class MergeServiceTests : IAsyncLifetime
{
    private TempLibraryFixture _fx = null!;
    private FileOperationService _ops = null!;
    private readonly FakeRecycleBin _bin = new() { DeleteFiles = true };
    private readonly ActivityCenter _activities = new();
    private readonly ForegroundWork _foreground = new();
    private int _libraryId;

    public async Task InitializeAsync()
    {
        _fx = await TempLibraryFixture.CreateAsync();
        _libraryId = (await _fx.CreateLibraryAsync()).Id;
    }

    public Task DisposeAsync()
    {
        _ops?.Dispose();
        _activities.Dispose();
        _fx.Dispose();
        return Task.CompletedTask;
    }

    private MergeService Service(bool hidden)
    {
        _ops = _fx.CreateFileOperationService(_bin);
        var storage = new TestStoragePaths(hidden ? Path.Combine(_fx.LibraryRoot, ".lumora") : Path.Combine(_fx.RootDir, "appdata"), hidden);
        var host = new ImagingWorkerHost(storage, ImagingWorkerProcessTests.Options());
        return new MergeService(_fx.ContextFactory, host, new WorkerPanoramaStitcher(host), _ops, new MergeSettingsStore(_fx.ContextFactory), storage,
            null, _activities, _foreground, _fx.Messenger);
    }

    /// <summary>Fünf überlappende Ansichten (30°-Schritte) in „Pano“, die erste mit EXIF-Zeit und die dritte mit GPS samt Höhe.</summary>
    private async Task<List<int>> SeedAsync()
    {
        using var scene = new SyntheticScene(seed: 31);
        var dir = Path.Combine(_fx.LibraryRoot, "Pano");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 5; i++)
        {
            using var view = scene.Render(-60 + i * 30, 0, 700, 800, 600);
            Cv2.ImEncode(".jpg", view, out var jpeg, [(int)ImwriteFlags.JpegQuality, 92]);
            var exif = Lumora.Tests.Merge.ExifTestJpeg.Build(
                orientation: 1,
                dateTimeOriginal: $"2024:07:12 10:15:{30 + i:00}",
                gps: i == 2 ? new Lumora.Tests.Merge.ExifTestJpeg.Gps(47.5, 13.5, 2100, new DateTime(2024, 7, 12, 8, 15, 32, DateTimeKind.Utc)) : null,
                withThumbnailIfd: false,
                pixelX: 800,
                pixelY: 600);
            var segment = Lumora.Services.Enhance.JpegExifCopier.ReadExifSegment(exif)!;
            File.WriteAllBytes(Path.Combine(dir, $"p{i}.jpg"), Lumora.Services.Enhance.JpegExifCopier.Insert(jpeg, segment)!);
        }

        // eine fremde Datei als Zeit-Nachbar-Gegenbeispiel (andere Kamera)
        await _fx.ScanAsync(_libraryId);
        await using var ctx = _fx.CreateContext();
        var files = await ctx.Files.Where(f => f.Name.StartsWith("p")).OrderBy(f => f.Name).ToListAsync();
        for (var i = 0; i < files.Count; i++)
        {
            files[i].CapturedAt = new DateTime(2024, 7, 12, 8, 15, 30 + i, DateTimeKind.Utc);
            files[i].CapturedAtSource = CaptureDateSource.Exif;
            files[i].CapturedAtOffset = 120;
            files[i].CameraModel = "TestCam TC-1";
            files[i].Rating = (byte)(i == 4 ? 5 : 1);
            if (i == 2)
            {
                files[i].Latitude = 47.5;
                files[i].Longitude = 13.5;
                files[i].GeoSource = GeoSource.Exif;
                files[i].GeoAccuracy = GeoAccuracy.Exact;
            }
        }

        await ctx.SaveChangesAsync();
        return files.Select(f => f.Id).ToList();
    }

    [Fact]
    public async Task Ein_Foto_zeigt_die_Zeit_Nachbarn_und_mehrere_werden_nach_Aufnahmezeit_sortiert()
    {
        var ids = await SeedAsync();
        var service = Service(hidden: false);

        var single = await service.AnalyzeAsync([ids[2]]);
        single.Neighbors.Select(n => n.FileId).Should().BeEquivalentTo(new[] { ids[0], ids[1], ids[3], ids[4] });
        single.CanPreview.Should().BeFalse();
        single.Methods.Single(m => m.Method == MergeMethod.Panorama).Reason.Should().Contain("nur 1 Foto");
        single.Methods.Where(m => m.Method != MergeMethod.Panorama).Should().OnlyContain(m => !m.Built && m.Reason == "kommt später");

        var many = await service.AnalyzeAsync([ids[4], ids[0], ids[2]]);
        many.Sources.Select(s => s.FileId).Should().Equal(ids[0], ids[2], ids[4]);
        many.CanPreview.Should().BeTrue();
    }

    [Fact]
    public async Task Vorschau_Umschalten_und_Speichern_legen_ein_Panorama_neben_das_erste_Einzelbild_mit_Rueckgaengig()
    {
        var ids = await SeedAsync();
        var service = Service(hidden: false);
        var priorityChanges = 0;
        _foreground.Changed += () => Interlocked.Increment(ref priorityChanges);
        var saved = new List<MergeSavedMessage>();
        _fx.Messenger.Register<MergeSavedMessage>(this, (_, m) => saved.Add(m));

        await using var session = service.OpenPreviewSession(new MergeRequest([ids[3], ids[1], ids[0], ids[2], ids[4]]));
        var steps = new List<WorkerStep>();
        session.ProgressChanged += p => steps.Add(p.Step);
        var first = await session.StartAsync();

        first.CanSave.Should().BeTrue(string.Join(" ", first.Quality.Problems.Select(p => p.Text)));
        File.Exists(first.ImagePath).Should().BeTrue();
        first.ImagePath.Should().StartWith(Path.Combine(_fx.RootDir, "appdata", "temp", "merge"));
        (first.Width * (long)first.Height).Should().BeLessThanOrEqualTo(MergeLimits.PreviewMaxPixels + 50_000);
        steps.Should().Contain(WorkerStep.Match);
        priorityChanges.Should().BeGreaterThan(0, "Vorrang während der Berechnung");

        // Umschalten: nur neu zusammensetzen
        steps.Clear();
        var second = await session.UpdateAsync(session.Options with { Projection = PanoramaProjection.Spherical360 });
        second.Projection.Should().Be(PanoramaProjection.Spherical360);
        steps.Should().NotContain([WorkerStep.Load, WorkerStep.Features, WorkerStep.Match]);
        File.Exists(first.ImagePath).Should().BeFalse("ältere Vorschauen werden weggeräumt");
        session.Options.ColorBoost.Should().Be(MergeLimits.ColorBoostDefault, "erstes Mal: Standard 100");

        // Farbe kräftiger aus und wieder an: nur neu tonen — weder ausrichten noch zusammensetzen
        steps.Clear();
        var plain = await session.UpdateAsync(session.Options with { ColorBoost = MergeLimits.ColorBoostOff });
        steps.Should().NotContain([WorkerStep.Load, WorkerStep.Features, WorkerStep.Match, WorkerStep.Seams, WorkerStep.Warp, WorkerStep.Blend]);
        (plain.Width, plain.Height).Should().Be((second.Width, second.Height));
        second = await session.UpdateAsync(session.Options with { ColorBoost = 120 });
        session.Options.ColorBoost.Should().Be(120);

        var estimate = session.Estimate();
        estimate.Width.Should().BeGreaterThanOrEqualTo(second.Width, "die kleinen Testbilder liegen schon in voller Größe vor");
        estimate.Scale.Should().Be(1, "volle Auflösung ist Standard");

        var result = await session.SaveAsync();

        result.Success.Should().BeTrue(result.Error);
        result.Path.Should().Be(Path.Combine(_fx.LibraryRoot, "Pano", "p0_panorama.jpg"));
        File.Exists(result.Path).Should().BeTrue();
        var row = (await _fx.FindFileByIdAsync(result.ResultFileId))!;
        row.CapturedAt.Should().Be(new DateTime(2024, 7, 12, 8, 15, 30, DateTimeKind.Utc));
        (row.Latitude, row.GeoAccuracy).Should().Be((47.5, GeoAccuracy.Exact));
        row.Rating.Should().Be((byte)5);
        // in der Datei: Zeit der ersten Quelle, GPS samt Höhe der dritten → beim Nachlesen echtes GPS
        MediaDateReader.Read(result.Path!)!.Value.Local.Should().Be(new DateTime(2024, 7, 12, 10, 15, 30));
        ExifGpsDetailsReader.Read(result.Path!)!.Value.HasAltitude.Should().BeTrue();
        await using (var ctx = _fx.CreateContext())
        {
            var merged = await ctx.MergedPhotos.AsNoTracking().SingleAsync();
            merged.Projection.Should().Be(PanoramaProjection.Spherical360);
            var recipe = MergeRecipe.FromJson(merged.RecipeJson)!;
            recipe.Alignment.Cameras.Should().HaveCount(5);
            recipe.ColorBoost.Should().Be(120, "die gewählte Farbverstärkung steht im Rezept");
            (await ctx.MergedPhotoSources.AsNoTracking().OrderBy(s => s.Position).Select(s => s.SourceFileId).ToListAsync()).Should().Equal(ids);
        }

        (await service.LoadSettingsAsync()).ColorBoost.Should().Be(120, "die letzte Wahl ist die Vorauswahl beim nächsten Mal");
        saved.Should().ContainSingle(m => m.ResultFileId == result.ResultFileId);
        var entry = _activities.GetOverview().Recent.First(r => r.Kind == Lumora.Services.Activity.ActivityKind.Merge);
        entry.Result.Should().StartWith("Panorama gespeichert");
        entry.Actions.Should().Contain(a => a.Kind == ActivityActionKind.ShowResult && a.FileIds!.Single() == result.ResultFileId);
        _foreground.Need.Should().Be(ForegroundNeed.None, "nach dem Speichern kein Vorrang mehr");

        var undoText = await _activities.UndoAsync(entry.Id);
        undoText.Should().Contain("Papierkorb");
        File.Exists(result.Path).Should().BeFalse();
        (await _fx.FindFileByIdAsync(result.ResultFileId)).Should().BeNull();
    }

    [Fact]
    public async Task Versteckte_Sitzung_rechnet_nur_im_Container_und_raeumt_die_Zwischenstaende_weg()
    {
        var ids = await SeedAsync();
        var service = Service(hidden: true);
        var container = Path.Combine(_fx.LibraryRoot, ".lumora");
        var tempBefore = Directory.EnumerateFileSystemEntries(Path.GetTempPath()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? preview;
        MergeSaveResult result;
        await using (var session = service.OpenPreviewSession(new MergeRequest(ids.Take(3).ToList())))
        {
            preview = (await session.StartAsync()).ImagePath;
            preview.Should().StartWith(container);
            result = await session.SaveAsync();
        }

        result.Success.Should().BeTrue(result.Error);
        result.Path.Should().StartWith(_fx.LibraryRoot);
        Directory.EnumerateFiles(Path.Combine(container, "temp", "merge"), "*", SearchOption.AllDirectories).Should().BeEmpty("Sitzung geschlossen: Zwischenstände weg");
        var newInTemp = Directory.EnumerateFileSystemEntries(Path.GetTempPath()).Where(e => !tempBefore.Contains(e))
            .Where(e => Path.GetFileName(e).Contains("opencv", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(e).StartsWith("Lumora.Imaging", StringComparison.OrdinalIgnoreCase))
            .ToList();
        newInTemp.Should().BeEmpty("der Hilfsprozess legt nichts im Temp-Ordner des Benutzers an");
    }

    [Fact]
    public async Task Gemischte_Bibliotheken_sind_gesperrt()
    {
        var ids = await SeedAsync();
        await using (var ctx = _fx.CreateContext())
        {
            var other = new Lumora.Domain.Entities.Library { Name = "Zweite", RootPath = _fx.OutsideDir, CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
            ctx.Libraries.Add(other);
            await ctx.SaveChangesAsync();
            await ctx.Files.Where(f => f.Id == ids[0]).ExecuteUpdateAsync(s => s.SetProperty(f => f.LibraryId, other.Id));
        }

        var service = Service(hidden: false);
        var analysis = await service.AnalyzeAsync(ids);
        analysis.MixedLibraries.Should().BeTrue();
        analysis.CanPreview.Should().BeFalse();
        await using var session = service.OpenPreviewSession(new MergeRequest(ids));
        var act = () => session.StartAsync();
        (await act.Should().ThrowAsync<MergeException>()).Which.Message.Should().Contain("verschiedenen Bibliotheken");
    }

    [Fact]
    public async Task Verfuegbarkeit_meldet_OpenCV_und_fehlenden_Hilfsprozess()
    {
        var service = Service(hidden: false);
        var ok = await service.GetAvailabilityAsync();
        ok.Available.Should().BeTrue(ok.Reason);
        ok.OpenCvVersion.Should().StartWith("4.");

        var storage = new TestStoragePaths(Path.Combine(_fx.RootDir, "x"));
        var missingHost = new ImagingWorkerHost(storage, new ImagingWorkerOptions { ExePath = Path.Combine(_fx.RootDir, "fehlt.exe") });
        var missing = new MergeService(_fx.ContextFactory, missingHost, new WorkerPanoramaStitcher(missingHost), _ops, new MergeSettingsStore(_fx.ContextFactory), storage);
        (await missing.GetAvailabilityAsync()).Available.Should().BeFalse();
    }
}
