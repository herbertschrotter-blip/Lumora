using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Metadata;

/// <summary>
/// Aufnahmedatum im Katalog (ADR-0046): Nachtragen (EXIF/Dateiname/unbekannt, gesperrte Bibliothek, fehlende Dateien,
/// manuelle Daten bleiben), „Datum setzen“ (genau, ungefähr, verschieben, zurücksetzen) samt Protokoll und Rückgängig,
/// „Neu aufbauen“ und Incremental-Scan.
/// </summary>
public sealed class CaptureDateServiceTests : IAsyncLifetime
{
    private TempLibraryFixture _fx = null!;
    private Library _library = null!;
    private readonly List<CaptureDatesChangedMessage> _changed = new();

    public async Task InitializeAsync()
    {
        _fx = await TempLibraryFixture.CreateAsync();
        _library = await _fx.CreateLibraryAsync();
        _fx.Messenger.Register<CaptureDatesChangedMessage>(this, (_, m) =>
        {
            lock (_changed)
            {
                _changed.Add(m);
            }
        });
    }

    public Task DisposeAsync()
    {
        _fx.Dispose();
        return Task.CompletedTask;
    }

    private CaptureDateService CreateService(IScanGate? gate = null, ICaptureDateReader? reader = null)
        => new(_fx.ContextFactory, reader ?? new CaptureDateReader(), NullLogger<CaptureDateService>.Instance, gate, _fx.Messenger) { StartDelay = Timeout.InfiniteTimeSpan };

    private string WriteJpeg(string relative, string? exifDate)
    {
        var path = Path.Combine(_fx.LibraryRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, SyntheticMediaFiles.Jpeg(exifDate is null ? null : SyntheticMediaFiles.Tiff(exifDate, "+02:00")));
        return path;
    }

    private async Task<MediaFile> FileAsync(string name) => (await _fx.FindFileAsync(name))!;

    [Fact]
    public async Task Nachtragen_liest_EXIF_Dateiname_und_merkt_unbekannte()
    {
        WriteJpeg("a/exif.jpg", "2024:07:12 15:30:12");
        WriteJpeg("a/IMG_20230102_030405.jpg", null);
        WriteJpeg("a/ohne.jpg", null);
        await _fx.ScanAsync(_library.Id);
        (await FileAsync("exif.jpg")).CapturedAtSource.Should().BeNull("nach dem Scan noch nicht gelesen");

        using var service = CreateService();
        var report = await service.RunBackfillPassAsync();

        report.Should().Be(new CaptureDateBackfillReport(3, 2, 0));
        var exif = await FileAsync("exif.jpg");
        exif.CapturedAtSource.Should().Be(CaptureDateSource.Exif);
        exif.CapturedAt.Should().Be(new DateTime(2024, 7, 12, 13, 30, 12, DateTimeKind.Utc));
        exif.CapturedAtOffset.Should().Be(120);
        (await FileAsync("IMG_20230102_030405.jpg")).CapturedAtSource.Should().Be(CaptureDateSource.FileName);
        var unknown = await FileAsync("ohne.jpg");
        unknown.CapturedAtSource.Should().Be(CaptureDateSource.Unknown);
        unknown.CapturedAt.Should().BeNull();
        _changed.SelectMany(m => m.FileIds).Should().HaveCount(3);

        (await service.RunBackfillPassAsync()).Should().Be(new CaptureDateBackfillReport(0, 0, 0), "nichts mehr offen");
    }

    [Fact]
    public async Task Nachtragen_ueberspringt_fehlende_Dateien_und_laesst_sie_offen()
    {
        WriteJpeg("b/da.jpg", "2024:07:12 15:30:12");
        var gone = WriteJpeg("b/weg.jpg", "2024:07:12 15:30:12");
        await _fx.ScanAsync(_library.Id);
        File.Delete(gone);

        using var service = CreateService();
        var report = await service.RunBackfillPassAsync();

        report.Read.Should().Be(1);
        report.Skipped.Should().Be(1);
        (await FileAsync("weg.jpg")).CapturedAtSource.Should().BeNull("bleibt für eine spätere Sitzung offen");
    }

    [Fact]
    public async Task Nachtragen_ruht_solange_die_Bibliothek_belegt_ist()
    {
        WriteJpeg("c/x.jpg", "2024:07:12 15:30:12");
        await _fx.ScanAsync(_library.Id);
        var gate = new ScanGate(new WeakReferenceMessenger());
        using var service = CreateService(gate);

        var lease = await gate.AcquireAsync(_library.Id, CancellationToken.None);
        var pass = service.RunBackfillPassAsync();
        await Task.Delay(600);
        pass.IsCompleted.Should().BeFalse("Scan bzw. Datei-Batch belegt die Bibliothek");
        (await FileAsync("x.jpg")).CapturedAtSource.Should().BeNull();

        lease.Dispose();
        (await pass.WaitAsync(TimeSpan.FromSeconds(10))).Read.Should().Be(1);
    }

    [Fact]
    public async Task Nachtragen_ueberschreibt_kein_inzwischen_gesetztes_Datum()
    {
        WriteJpeg("d/y.jpg", "2024:07:12 15:30:12");
        await _fx.ScanAsync(_library.Id);
        var id = (await FileAsync("y.jpg")).Id;
        using var service = CreateService(reader: new SettingReader(() => CreateService().ApplyAsync([id], new CaptureDateEdit.Approximate(1982, null)).GetAwaiter().GetResult()));

        await service.RunBackfillPassAsync();

        var file = await FileAsync("y.jpg");
        file.CapturedAtSource.Should().Be(CaptureDateSource.Manual, "manuell gesetzt, während die Datei gelesen wurde");
        file.CapturedAtPrecision.Should().Be(CaptureDatePrecision.Year);
    }

    [Fact]
    public async Task Datum_setzen_genau_ungefaehr_verschieben_zuruecksetzen()
    {
        WriteJpeg("e/1.jpg", "2024:07:12 15:30:12");
        WriteJpeg("e/2.jpg", null);
        await _fx.ScanAsync(_library.Id);
        using var service = CreateService();
        await service.RunBackfillPassAsync();
        var one = (await FileAsync("1.jpg")).Id;
        var two = (await FileAsync("2.jpg")).Id;

        // Ungefähr 1982 → 1. Juli 12:00 Ortszeit, Genauigkeit Jahr, manuell
        var approx = await service.ApplyAsync([one, two], new CaptureDateEdit.Approximate(1982, null));
        approx.Changed.Should().Be(2);
        var info = await service.GetAsync(one);
        info!.Value!.Value.Local.Should().Be(new DateTime(1982, 7, 1, 12, 0, 0));
        info.Source.Should().Be(CaptureDateSource.Manual);
        info.Text.Should().Be("ca. 1982 (manuell)");
        (await service.ApplyAsync([one], new CaptureDateEdit.Approximate(1982, null))).Should().Match<CaptureDateChangeResult>(r => r.Changed == 0 && r.BatchId == 0, "gleicher Wert: kein Protokolleintrag");

        // Monat + Jahr → 15.
        await service.ApplyAsync([one], new CaptureDateEdit.Approximate(1982, 3));
        (await service.GetAsync(one))!.Value!.Value.Local.Should().Be(new DateTime(1982, 3, 15, 12, 0, 0));

        // Genau
        await service.ApplyAsync([one], new CaptureDateEdit.Exact(new DateTime(2001, 2, 3, 4, 5, 6)));
        var exact = (await service.GetAsync(one))!.Value!.Value;
        exact.Local.Should().Be(new DateTime(2001, 2, 3, 4, 5, 6));
        exact.Precision.Should().Be(CaptureDatePrecision.Exact);

        // Verschieben −1 Stunde (Genauigkeit bleibt)
        await service.ApplyAsync([one], new CaptureDateEdit.Shift(TimeSpan.FromHours(-1)));
        (await service.GetAsync(one))!.Value!.Value.Local.Should().Be(new DateTime(2001, 2, 3, 3, 5, 6));

        // Zurücksetzen → wieder offen, Nachtragen liest EXIF
        await service.ApplyAsync([one], new CaptureDateEdit.Reset());
        (await service.GetAsync(one))!.Source.Should().BeNull();
        await service.RunBackfillPassAsync();
        (await service.GetAsync(one))!.Source.Should().Be(CaptureDateSource.Exif);
    }

    [Fact]
    public async Task Verschieben_ohne_Aufnahmedatum_nimmt_das_Aenderungsdatum()
    {
        WriteJpeg("f/ohne.jpg", null);
        await _fx.ScanAsync(_library.Id);
        var file = await FileAsync("ohne.jpg");
        using var service = CreateService();

        await service.ApplyAsync([file.Id], new CaptureDateEdit.Shift(TimeSpan.FromDays(-2)));

        var after = await FileAsync("ohne.jpg");
        after.CapturedAt.Should().Be(file.ModifiedAtUtc.AddDays(-2));
        after.CapturedAtSource.Should().Be(CaptureDateSource.Manual);
    }

    [Fact]
    public async Task Verschieben_einer_noch_nicht_gelesenen_Datei_liest_erst_das_EXIF_Datum()
    {
        WriteJpeg("f2/kamera.jpg", "2024:07:12 15:30:12");
        await _fx.ScanAsync(_library.Id);
        var file = await FileAsync("kamera.jpg");
        file.CapturedAtSource.Should().BeNull("Nachtragen ist noch nicht gelaufen");
        using var service = CreateService();

        await service.ApplyAsync([file.Id], new CaptureDateEdit.Shift(TimeSpan.FromHours(-1)));

        var after = await FileAsync("kamera.jpg");
        after.CapturedAt.Should().Be(new DateTime(2024, 7, 12, 12, 30, 12, DateTimeKind.Utc), "EXIF 13:30 UTC − 1 Stunde, nicht das Änderungsdatum");
        after.CapturedAtOffset.Should().Be(120);
        after.CapturedAtSource.Should().Be(CaptureDateSource.Manual);
    }

    [Fact]
    public async Task Nachtragen_ueberspringt_Dateien_einer_nicht_erreichbaren_Wurzel()
    {
        WriteJpeg("g2/a.jpg", "2024:07:12 15:30:12");
        await _fx.ScanAsync(_library.Id);
        var offline = Path.Combine(_fx.RootDir, "getrennt");
        await using (var ctx = _fx.CreateContext())
        {
            // Wurzel, die es nicht (mehr) gibt — z. B. getrenntes Laufwerk — mit einer Katalogzeile darunter
            var root = new LibraryRoot { LibraryId = _library.Id, Path = offline, Role = LibraryRootRole.Inbox, Name = "getrennt", SortOrder = 1 };
            ctx.LibraryRoots.Add(root);
            await ctx.SaveChangesAsync();
            var folder = new Folder { LibraryId = _library.Id, RootId = root.Id, Path = Path.Combine(offline, "x"), Name = "x", ScannedAt = DateTime.UtcNow };
            ctx.Folders.Add(folder);
            await ctx.SaveChangesAsync();
            ctx.Files.Add(new MediaFile { LibraryId = _library.Id, FolderId = folder.Id, Name = "weg.jpg", Extension = ".jpg", Size = 1, MediaType = MediaType.Image });
            await ctx.SaveChangesAsync();
        }

        using var service = CreateService();
        var report = await service.RunBackfillPassAsync();

        report.Read.Should().Be(1);
        report.Skipped.Should().Be(1);
        (await FileAsync("weg.jpg")).CapturedAtSource.Should().BeNull();
    }

    [Fact]
    public async Task Protokoll_und_Rueckgaengig()
    {
        WriteJpeg("g/1.jpg", "2024:07:12 15:30:12");
        WriteJpeg("g/2.jpg", null);
        await _fx.ScanAsync(_library.Id);
        using var service = CreateService();
        await service.RunBackfillPassAsync();
        var one = await FileAsync("1.jpg");
        var two = await FileAsync("2.jpg");

        var result = await service.ApplyAsync([one.Id, two.Id], new CaptureDateEdit.Approximate(1982, null));

        var batch = await _fx.GetBatchAsync(result.BatchId);
        batch!.OperationType.Should().Be(OperationType.SetCaptureDate);
        batch.Status.Should().Be(BatchStatus.Applied);
        var ops = await _fx.GetOperationsAsync(result.BatchId);
        ops.Should().HaveCount(2).And.OnlyContain(o => o.Action == OperationType.SetCaptureDate && o.Status == FileOperationStatus.Applied);
        ops.Select(o => o.SourcePath).Should().Contain(Path.Combine(_fx.LibraryRoot, "g", "1.jpg"));
        (await service.GetUndoableBatchIdAsync()).Should().Be(result.BatchId);

        var undo = await service.UndoAsync(result.BatchId);

        undo!.Changed.Should().Be(2);
        var oneAfter = await FileAsync("1.jpg");
        oneAfter.CapturedAt.Should().Be(one.CapturedAt);
        oneAfter.CapturedAtSource.Should().Be(CaptureDateSource.Exif);
        oneAfter.CapturedAtOffset.Should().Be(120);
        (await FileAsync("2.jpg")).CapturedAtSource.Should().Be(CaptureDateSource.Unknown);
        (await _fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Undone);
        (await _fx.GetOperationsAsync(result.BatchId)).Should().OnlyContain(o => o.Status == FileOperationStatus.Undone);
        (await service.GetUndoableBatchIdAsync()).Should().BeNull();
        (await service.UndoAsync(result.BatchId)).Should().BeNull("schon zurückgenommen");
    }

    [Fact]
    public async Task Mit_Hintergrund_Planer_parallel_gelesen_und_pausierbar()
    {
        for (var i = 0; i < 12; i++)
        {
            WriteJpeg($"p/exif{i:00}.jpg", "2024:07:12 15:30:12");
        }

        WriteJpeg("p/IMG_20230102_030405.jpg", null);
        await _fx.ScanAsync(_library.Id);
        using var planner = new Lumora.Services.Background.BackgroundPlanner(storage: new SsdStorage());
        var probe = new FileMetadataProbe();
        using var service = new CaptureDateService(_fx.ContextFactory, new CaptureDateReader(probe), NullLogger<CaptureDateService>.Instance,
            null, _fx.Messenger, planner) { StartDelay = Timeout.InfiniteTimeSpan };

        planner.SetPaused(Lumora.Services.Background.BackgroundJob.CaptureDates, true);
        var pass = service.RunBackfillPassAsync();
        await Task.Delay(200);
        pass.IsCompleted.Should().BeFalse("pausiert");
        planner.SetPaused(Lumora.Services.Background.BackgroundJob.CaptureDates, false);
        await pass;

        for (var i = 0; i < 12; i++)
        {
            (await FileAsync($"exif{i:00}.jpg")).CapturedAtSource.Should().Be(CaptureDateSource.Exif);
        }

        (await FileAsync("IMG_20230102_030405.jpg")).CapturedAtSource.Should().Be(CaptureDateSource.FileName);
        probe.Reads.Should().Be(13, "jede Datei genau einmal");
        new GeoLocationReader(probe).Read(Path.Combine(_fx.LibraryRoot, "p", "exif00.jpg"));
        probe.Reads.Should().Be(13, "Orte nutzen den gemerkten Dateikopf");
    }

    private sealed class SsdStorage : Lumora.Services.Background.IStorageKindProbe
    {
        public Lumora.Services.Background.StorageKind GetKind(string path) => Lumora.Services.Background.StorageKind.Ssd;
    }

    [Fact]
    public async Task Neu_aufbauen_behaelt_manuelle_Daten_und_liest_die_uebrigen_neu()
    {
        WriteJpeg("h/manuell.jpg", "2024:07:12 15:30:12");
        WriteJpeg("h/exif.jpg", "2024:07:12 15:30:12");
        await _fx.ScanAsync(_library.Id);
        using var service = CreateService();
        await service.RunBackfillPassAsync();
        var manualId = (await FileAsync("manuell.jpg")).Id;
        var setResult = await service.ApplyAsync([manualId], new CaptureDateEdit.Approximate(1982, 5));
        var before = await FileAsync("manuell.jpg");

        await _fx.CreateScanService().ScanAsync(_library.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);

        var manual = await FileAsync("manuell.jpg");
        manual.Id.Should().NotBe(manualId, "Neu aufbauen vergibt neue FileIds");
        manual.CapturedAtSource.Should().Be(CaptureDateSource.Manual);
        manual.CapturedAt.Should().Be(before.CapturedAt);
        manual.CapturedAtPrecision.Should().Be(CaptureDatePrecision.Month);
        manual.CapturedAtOffset.Should().Be(before.CapturedAtOffset);
        (await FileAsync("exif.jpg")).CapturedAtSource.Should().BeNull("wird neu gelesen");

        // Rückgängig findet die Datei über Ordnerpfad + Name
        (await service.UndoAsync(setResult.BatchId))!.Changed.Should().Be(1);
        (await FileAsync("manuell.jpg")).CapturedAtSource.Should().Be(CaptureDateSource.Exif);
    }

    [Fact]
    public async Task Incremental_geaendert_liest_neu_ausser_manuell()
    {
        var changedPath = WriteJpeg("i/geaendert.jpg", "2024:07:12 15:30:12");
        var manualPath = WriteJpeg("i/manuell.jpg", "2024:07:12 15:30:12");
        WriteJpeg("i/gleich.jpg", "2024:07:12 15:30:12");
        await _fx.ScanAsync(_library.Id);
        using var service = CreateService();
        await service.RunBackfillPassAsync();
        await service.ApplyAsync([(await FileAsync("manuell.jpg")).Id], new CaptureDateEdit.Approximate(1982, null));

        File.WriteAllBytes(changedPath, SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2020:01:01 00:00:00", "+02:00")));
        File.WriteAllBytes(manualPath, SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2020:01:01 00:00:00", "+02:00")));
        File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddMinutes(5));
        File.SetLastWriteTimeUtc(manualPath, DateTime.UtcNow.AddMinutes(5));
        await _fx.CreateScanService().ScanAsync(_library.Id, ScanMode.Incremental, null, CancellationToken.None);

        (await FileAsync("geaendert.jpg")).CapturedAtSource.Should().BeNull("Inhalt geändert → neu lesen");
        (await FileAsync("manuell.jpg")).CapturedAtSource.Should().Be(CaptureDateSource.Manual);
        (await FileAsync("gleich.jpg")).CapturedAtSource.Should().Be(CaptureDateSource.Exif);

        await service.RunBackfillPassAsync();
        (await FileAsync("geaendert.jpg")).CapturedAt.Should().Be(new DateTime(2019, 12, 31, 22, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Dateien_im_Ordner_mit_und_ohne_Unterordner()
    {
        WriteJpeg("Urlaub/a.jpg", null);
        WriteJpeg("Urlaub/Tag 1/b.jpg", null);
        WriteJpeg("Urlaub/Tag 1/c.jpg", null);
        WriteJpeg("Urlaubsfotos/d.jpg", null); // gleiches Präfix, kein Unterordner
        await _fx.ScanAsync(_library.Id);
        var folder = await _fx.FindFolderAsync(Path.Combine(_fx.LibraryRoot, "Urlaub"));
        using var service = CreateService();

        (await service.GetFileIdsInFolderAsync(folder!.Id, includeSubfolders: false)).Should().HaveCount(1);
        (await service.GetFileIdsInFolderAsync(folder.Id, includeSubfolders: true)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Ungueltige_Eingaben_werden_abgelehnt()
    {
        using var service = CreateService();
        await FluentActions.Awaiting(() => service.ApplyAsync([1], new CaptureDateEdit.Approximate(1700, null))).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => service.ApplyAsync([1], new CaptureDateEdit.Shift(TimeSpan.Zero))).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => service.ApplyAsync([1], new CaptureDateEdit.Exact(DateTime.Now.AddDays(5)))).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>Leser, der beim Lesen eine Aktion auslöst (z. B. manuelles Setzen dazwischen), dann normal liest.</summary>
    private sealed class SettingReader(Action beforeRead) : ICaptureDateReader
    {
        private readonly CaptureDateReader _inner = new();
        private int _fired;

        public CaptureDateValue? Read(string path)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                beforeRead();
            }

            return _inner.Read(path);
        }
    }
}
