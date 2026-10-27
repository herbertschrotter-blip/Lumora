using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Background;
using Lumora.Services.Duplicates;

namespace Lumora.Tests.Duplicates;

/// <summary>
/// Prüfsummen im Leerlauf (ADR-0073 Frage 3): alle Fotos und Videos bekommen eine SHA-256, auch mit einmaliger Größe; nur
/// die gelesene Dateiversion; Lesefehler gemerkt; kleine Stapel; niedrigster Vorrang und Pause über den Planer.
/// </summary>
public sealed class ChecksumBackfillTests : IAsyncLifetime
{
    private DuplicateWorld _w = null!;
    private ChecksumBackfillService _backfill = null!;

    public async Task InitializeAsync()
    {
        _w = await DuplicateWorld.CreateAsync(inbox: false);
        _backfill = Create();
    }

    public async Task DisposeAsync()
    {
        _backfill.Dispose();
        await _w.DisposeAsync();
    }

    private ChecksumBackfillService Create(IBackgroundPlanner? planner = null, long maxBatchBytes = 256L * 1024 * 1024)
        => new(_w.Fx.ContextFactory, NullLogger<ChecksumBackfillService>.Instance, _w.Gate, _w.Fx.Messenger, planner)
        {
            StartDelay = Timeout.InfiniteTimeSpan,
            MaxBatchBytes = maxBatchBytes,
        };

    private byte[]? HashOf(int fileId)
    {
        using var connection = _w.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ContentHash FROM FileFingerprints WHERE FileId = $id";
        cmd.Parameters.AddWithValue("$id", fileId);
        return cmd.ExecuteScalar() as byte[];
    }

    [Fact]
    public async Task Alle_Fotos_und_Videos_bekommen_eine_Pruefsumme_auch_mit_einmaliger_Groesse()
    {
        var photo = DuplicateWorld.Content(1);
        var other = DuplicateWorld.Content(2, 5000);
        var video = DuplicateWorld.Content(3, 7000);
        _w.Write("a/eins.jpg", photo);
        _w.Write("b/zwei.jpg", other);
        _w.Write("c/film.mp4", video);
        await _w.ScanAsync();
        (await _w.Index.RunPassAsync()).Hashed.Should().Be(0, "der Doppelten-Index liest nur gleich große Dateien");

        var report = await _backfill.RunPassAsync();

        report.Should().Be(new ChecksumBackfillReport(3, 0, 0));
        HashOf(await _w.IdAsync("eins.jpg")).Should().Equal(SHA256.HashData(photo));
        HashOf(await _w.IdAsync("zwei.jpg")).Should().Equal(SHA256.HashData(other));
        HashOf(await _w.IdAsync("film.mp4")).Should().Equal(SHA256.HashData(video));
        _backfill.Status.Should().Be(ChecksumBackfillStatus.Idle);

        (await _backfill.RunPassAsync()).Hashed.Should().Be(0, "nichts mehr offen");
    }

    [Fact]
    public async Task Geaenderte_Datei_wird_neu_gerechnet_fehlende_nicht()
    {
        _w.Write("a/eins.jpg", DuplicateWorld.Content(1));
        _w.Write("b/zwei.jpg", DuplicateWorld.Content(2, 5000));
        await _w.ScanAsync();
        await _backfill.RunPassAsync();

        var changed = await _w.IdAsync("eins.jpg");
        _w.Exec("UPDATE Files SET ModifiedAtUtc = ModifiedAtUtc + 60 WHERE Id = $id", ("$id", changed)); // wie nach einem Incremental-Scan
        _w.Exec("UPDATE Files SET Missing = 1 WHERE Name = 'zwei.jpg'");
        _w.Exec("DELETE FROM FileFingerprints WHERE FileId = (SELECT Id FROM Files WHERE Name = 'zwei.jpg')");

        (await _backfill.RunPassAsync()).Should().Be(new ChecksumBackfillReport(1, 0, 0));
        _w.Scalar<long>("SELECT ContentModifiedAt FROM FileFingerprints WHERE FileId = $id", ("$id", changed))
            .Should().Be(_w.Scalar<long>("SELECT ModifiedAtUtc FROM Files WHERE Id = $id", ("$id", changed)));
    }

    [Fact]
    public async Task Kleine_Stapelgrenze_rechnet_trotzdem_alle_eine_Datei_je_Stapel()
    {
        for (var i = 0; i < 5; i++)
        {
            _w.Write($"s/foto{i}.jpg", DuplicateWorld.Content(10 + i, 4000 + i));
        }

        await _w.ScanAsync();
        using var small = Create(maxBatchBytes: 1);

        (await small.RunPassAsync()).Hashed.Should().Be(5);
        _w.Scalar<int>("SELECT COUNT(*) FROM FileFingerprints WHERE ContentHash IS NOT NULL").Should().Be(5);
    }

    [Fact]
    public async Task Nicht_lesbare_Datei_wird_fuer_diese_Version_gemerkt_und_nicht_dauernd_wiederholt()
    {
        var path = _w.Write("a/gesperrt.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        var id = await _w.IdAsync("gesperrt.jpg");

        ChecksumBackfillReport first;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = await _backfill.RunPassAsync();
        }

        first.Should().Be(new ChecksumBackfillReport(0, 0, 1));
        _w.Scalar<int>("SELECT COUNT(*) FROM FileFingerprints WHERE FileId = $id AND ContentHash IS NULL", ("$id", id)).Should().Be(1);
        (await _backfill.RunPassAsync()).Should().Be(new ChecksumBackfillReport(0, 0, 0), "erst eine neue Dateiversion wird wieder gelesen");
    }

    [Fact]
    public async Task Verschwundene_Datei_wird_uebersprungen_und_nicht_als_unlesbar_gemerkt()
    {
        var path = _w.Write("a/weg.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        File.Delete(path); // z. B. Laufwerk während des Durchgangs abgezogen, noch nicht neu eingelesen

        (await _backfill.RunPassAsync()).Should().Be(new ChecksumBackfillReport(0, 1, 0));
        _w.Scalar<int>("SELECT COUNT(*) FROM FileFingerprints").Should().Be(0, "kommt wieder dran, sobald die Datei da ist");

        File.WriteAllBytes(path, DuplicateWorld.Content(1));
        (await _backfill.RunPassAsync()).Hashed.Should().Be(1);
    }

    [Fact]
    public async Task Pausiert_im_Planer_wartet_der_Durchgang_und_laeuft_nach_dem_Fortsetzen_weiter()
    {
        _w.Write("a/eins.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        using var planner = new BackgroundPlanner(_w.Gate);
        using var backfill = Create(planner);
        planner.SetPaused(BackgroundJob.Checksums, true);

        var pass = backfill.RunPassAsync();
        await Task.Delay(300);

        pass.IsCompleted.Should().BeFalse();
        planner.GetState(BackgroundJob.Checksums).Wait.Should().Be(BackgroundWait.Paused);
        planner.SetPaused(BackgroundJob.Checksums, false);
        (await pass.WaitAsync(TimeSpan.FromSeconds(10))).Hashed.Should().Be(1);
    }
}
