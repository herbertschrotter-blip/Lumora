using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Faces;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Faces;

/// <summary>
/// Hintergrund-Index der Gesichtserkennung mit Fake-Analysator (ADR-0035): alle Fotos analysieren und schreiben,
/// Pause/Fortsetzen, neue Dateien, Wechsel der Modell-Version (Bestätigungen bleiben), kein Doppel-Lauf, aus/kein
/// Modell, fehlende und nicht dekodierbare Dateien, Index neu aufbauen.
/// </summary>
public sealed class FaceIndexServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static FaceIndexOptions Options => new()
    {
        StartDelay = TimeSpan.Zero,
        WriteBatchSize = 2,
        ReadBatchSize = 3,
        StatusInterval = TimeSpan.Zero,
        SuspendedRetryDelay = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task Analysiert_alle_Fotos_schreibt_Gesichter_und_FaceScans_ohne_Videos()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile("p2_b.jpg");
        fx.WriteLibraryFile("sub\\p3_c.jpg");
        fx.WriteLibraryFile("p0_leer.jpg");
        fx.WriteLibraryFile("film.mp4");
        await fx.ScanAsync(lib.Id);
        var statuses = new List<FaceIndexStatus>();
        fx.Messenger.Register<List<FaceIndexStatus>, FaceIndexStatusMessage>(statuses, (r, m) => { lock (r) { r.Add(m.Status); } });
        var factory = new FakeFaceAnalyzerFactory();

        using var service = Create(fx, factory, enabled: true);
        await service.StartAsync();
        await WaitForIdleAsync(service, total: 4);

        Scalar(fx, "SELECT COUNT(*) FROM FaceScans").Should().Be(4, "nur Bilder, keine Videos");
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans WHERE ModelVersion = 'fake/v1' AND ErrorCode IS NULL").Should().Be(4);
        Scalar(fx, "SELECT COUNT(*) FROM Faces").Should().Be(6);
        Scalar(fx, "SELECT SUM(FaceCount) FROM FaceScans").Should().Be(6);
        Scalar(fx, "SELECT COUNT(*) FROM Faces WHERE length(Embedding) = 2048 AND length(Landmarks) = 40 AND State = 0 AND PersonId IS NULL").Should().Be(6);
        Scalar(fx, "SELECT COUNT(*) FROM Faces WHERE BoxX BETWEEN 0 AND 1 AND BoxW > 0 AND BoxW <= 1").Should().Be(6, "Box relativ");
        service.Status.Done.Should().Be(4);
        service.Status.Total.Should().Be(4);
        lock (statuses)
        {
            statuses.Should().Contain(s => s.State == FaceIndexState.Running);
            statuses[^1].State.Should().Be(FaceIndexState.Idle);
        }

        using var connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT f.Embedding FROM Faces f JOIN Files x ON x.Id = f.FileId WHERE x.Name = 'p2_b.jpg' ORDER BY f.BoxX";
        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue();
        var embedding = FaceEmbeddingCodec.Decode((byte[])reader[0]);
        embedding[0].Should().Be(1f, "Fingerabdruck verlustfrei gespeichert");
        embedding.Sum(v => v * v).Should().BeApproximately(1f, 1e-6f);
    }

    [Fact]
    public async Task Pause_haelt_nach_dem_aktuellen_Foto_an_und_Fortsetzen_macht_weiter()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        for (var i = 0; i < 6; i++)
        {
            fx.WriteLibraryFile($"f{i}.jpg");
        }

        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory { Gate = new SemaphoreSlim(0) };
        using var service = Create(fx, factory, enabled: true);
        await service.StartAsync();

        factory.Gate.Release(2);
        await WaitUntilAsync(() => factory.Calls == 2, "zwei Fotos analysiert");
        service.Pause();
        service.IsPaused.Should().BeTrue();
        factory.Gate.Release(10); // höchstens das bereits wartende Foto läuft noch zu Ende
        await WaitUntilAsync(() => service.Status.State == FaceIndexState.Paused, "pausiert");
        await Task.Delay(400);
        var callsWhilePaused = factory.Calls;
        await Task.Delay(400);
        factory.Calls.Should().Be(callsWhilePaused, "während der Pause beginnt kein weiteres Foto");
        callsWhilePaused.Should().BeLessThan(6);

        service.Resume();
        await WaitForIdleAsync(service, total: 6);
        factory.Calls.Should().Be(6);
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans").Should().Be(6);
    }

    [Fact]
    public async Task Neue_Dateien_nach_einem_Scan_werden_nachgeholt_ohne_alte_erneut_zu_analysieren()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile("b.jpg");
        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory();
        using var service = Create(fx, factory, enabled: true);
        await service.StartAsync();
        await WaitForIdleAsync(service, total: 2);
        factory.Calls.Should().Be(2);

        fx.WriteLibraryFile("neu\\c.jpg");
        var scan = await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.Incremental, null, CancellationToken.None);
        scan.Added.Should().Be(1);

        await WaitForIdleAsync(service, total: 3);
        factory.Calls.Should().Be(3, "nur die neue Datei");
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans").Should().Be(3);
    }

    [Fact]
    public async Task Neue_Modell_Version_analysiert_neu_und_behaelt_bestaetigte_Zuordnung_samt_Face_Id()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("p2_paar.jpg");
        fx.WriteLibraryFile("p1_solo.jpg");
        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory("fake/v1");
        using (var first = Create(fx, factory, enabled: true))
        {
            await first.StartAsync();
            await WaitForIdleAsync(first, total: 2);
        }

        Exec(fx, "INSERT INTO Persons (Id, Name, CreatedAtUtc, CoverFaceId, IsHidden) VALUES (1, 'Anna', 1, NULL, 0), (2, 'Ben', 1, NULL, 0);");
        var confirmedId = Scalar(fx, "SELECT MIN(x.Id) FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE f.Name = 'p2_paar.jpg'");
        var suggestedId = Scalar(fx, "SELECT MAX(x.Id) FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE f.Name = 'p2_paar.jpg'");
        Exec(fx, $"UPDATE Faces SET PersonId = 1, State = 2 WHERE Id = {confirmedId};");
        Exec(fx, $"UPDATE Faces SET SuggestedPersonId = 2, SuggestionScore = 0.7, State = 1 WHERE Id = {suggestedId};");
        Exec(fx, $"INSERT INTO FaceRejections (FaceId, PersonId) VALUES ({confirmedId}, 2);");
        Exec(fx, $"UPDATE Persons SET CoverFaceId = {confirmedId} WHERE Id = 1;");

        var v2 = new FakeFaceAnalyzerFactory("fake/v2");
        using var second = Create(fx, v2, enabled: true);
        await second.StartAsync();
        await WaitForIdleAsync(second, total: 2);

        v2.Calls.Should().Be(2, "andere Modell-Version → alle Fotos neu");
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans WHERE ModelVersion = 'fake/v2'").Should().Be(2);
        Scalar(fx, "SELECT COUNT(*) FROM Faces WHERE ModelVersion = 'fake/v2'").Should().Be(3);
        Scalar(fx, "SELECT COUNT(*) FROM Faces").Should().Be(3, "fortgeschrieben statt doppelt");
        Scalar(fx, $"SELECT PersonId FROM Faces WHERE Id = {confirmedId} AND State = 2").Should().Be(1, "Bestätigung bleibt mit derselben Face-Id");
        Scalar(fx, $"SELECT COUNT(*) FROM Faces WHERE Id = {suggestedId} AND State = 0 AND SuggestedPersonId IS NULL").Should().Be(1, "alter Vorschlag fällt zurück");
        Scalar(fx, "SELECT COUNT(*) FROM FaceRejections").Should().Be(1);
        Scalar(fx, "SELECT CoverFaceId FROM Persons WHERE Id = 1").Should().Be(confirmedId);
    }

    [Fact]
    public async Task Nie_zwei_Durchgaenge_gleichzeitig_auch_bei_vielen_Anstoessen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        for (var i = 0; i < 8; i++)
        {
            fx.WriteLibraryFile($"f{i}.jpg");
        }

        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory { Delay = TimeSpan.FromMilliseconds(30) };
        using var service = Create(fx, factory, enabled: true);
        await service.StartAsync();
        for (var i = 0; i < 20; i++)
        {
            service.RequestRun();
            fx.Messenger.Send(new FilesChangedMessage(lib.Id, FilesChangeKind.Added, [1]));
            await Task.Delay(10);
        }

        await WaitForIdleAsync(service, total: 8);
        await Task.Delay(300);
        factory.MaxConcurrent.Should().Be(1);
        factory.Calls.Should().Be(8, "jede Datei genau einmal");
        factory.Created.Should().Be(1, "das Modell wird einmal geladen und wiederverwendet");
    }

    [Fact]
    public async Task Ausgeschaltet_oder_ohne_Modell_passiert_nichts_Einschalten_startet()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory { IsAvailable = false };
        var settings = new MemoryFaceSettingsStore(FaceIndexSettings.Default);
        using var service = new FaceIndexService(
            fx.ContextFactory, fx.CreateLibraryService(), factory, new FakeFaceImageLoader(), settings,
            NullLogger<FaceIndexService>.Instance, fx.Messenger, Options);
        await service.StartAsync();
        FaceIndexSettings.Default.Enabled.Should().BeFalse("Standard aus");
        await WaitUntilAsync(() => service.CompletedRuns >= 1, "erster Durchgang");
        service.Status.State.Should().Be(FaceIndexState.Disabled);
        factory.Created.Should().Be(0);

        await service.SetEnabledAsync(true);
        settings.Current.Enabled.Should().BeTrue("gespeichert");
        await WaitUntilAsync(() => service.Status.State == FaceIndexState.ModelMissing, "kein Modell");
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans").Should().Be(0);

        factory.IsAvailable = true;
        await service.SetUseGpuAsync(false);
        await WaitForIdleAsync(service, total: 1);
        factory.LastUseGpu.Should().BeFalse();
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans").Should().Be(1);
    }

    [Fact]
    public async Task Fehlende_Datei_wird_nicht_vermerkt_kaputte_mit_Fehlercode_und_neu_aufbauen_analysiert_erneut()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("gut.jpg");
        fx.WriteLibraryFile("kaputt.jpg");
        var gone = fx.WriteLibraryFile("weg.jpg");
        await fx.ScanAsync(lib.Id);
        File.Delete(gone); // im Katalog, auf der Platte weg (z. B. Wurzel offline)
        var factory = new FakeFaceAnalyzerFactory();
        using var service = Create(fx, factory, enabled: true);
        await service.StartAsync();
        await WaitUntilAsync(() => service.CompletedRuns >= 1 && service.Status.State == FaceIndexState.Idle, "Durchgang fertig");

        Scalar(fx, "SELECT COUNT(*) FROM FaceScans").Should().Be(2);
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId WHERE f.Name = 'kaputt.jpg' AND s.ErrorCode = 'decode_failed' AND s.FaceCount = 0").Should().Be(1);
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId WHERE f.Name = 'weg.jpg'").Should().Be(0, "fehlende Datei später erneut");
        service.Status.Done.Should().Be(2);
        service.Status.Total.Should().Be(3);

        var reset = await service.RebuildAsync();
        reset.Should().Be(2);
        await WaitUntilAsync(() => factory.Calls >= 2 && service.Status.State == FaceIndexState.Idle && Scalar(fx, "SELECT COUNT(*) FROM FaceScans") == 2, "neu aufgebaut");
        Scalar(fx, "SELECT COUNT(*) FROM Faces").Should().Be(1, "Gesicht von gut.jpg abgeglichen statt verdoppelt");
    }

    [Fact]
    public async Task Abbrechen_beendet_den_Durchgang_der_naechste_Anstoss_setzt_fort()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        for (var i = 0; i < 5; i++)
        {
            fx.WriteLibraryFile($"f{i}.jpg");
        }

        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory { Gate = new SemaphoreSlim(0) };
        using var service = Create(fx, factory, enabled: true);
        await service.StartAsync();
        factory.Gate.Release(1);
        await WaitUntilAsync(() => factory.Calls == 1, "ein Foto");

        service.CancelRun();
        factory.Gate.Release(100);
        await WaitUntilAsync(() => service.Status.State == FaceIndexState.Idle && service.Status.Message == "Abgebrochen", "abgebrochen");
        var afterCancel = factory.Calls;
        afterCancel.Should().BeLessThan(5);

        service.RequestRun();
        await WaitForIdleAsync(service, total: 5);
        factory.Calls.Should().Be(5);
    }

    [Fact]
    public async Task Abbrechen_schreibt_schon_analysierte_Fotos_unabhaengig_vom_Schreib_Intervall()
    {
        // Wackel-Test unter Last (Issue #20, 0.4.41): War nach dem ersten Foto das Schreib-Intervall abgelaufen, wurde es
        // allein geschrieben und das zweite ging beim Abbruch verloren (später doppelt analysiert). Hier ohne Intervall:
        // nichts wird vor dem Abbruch geschrieben — beide fertigen Fotos müssen trotzdem gespeichert sein.
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        for (var i = 0; i < 5; i++)
        {
            fx.WriteLibraryFile($"f{i}.jpg");
        }

        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory { Gate = new SemaphoreSlim(0) };
        using var service = new FaceIndexService(
            fx.ContextFactory, fx.CreateLibraryService(), factory, new FakeFaceImageLoader(),
            new MemoryFaceSettingsStore(new FaceIndexSettings(true, true)), NullLogger<FaceIndexService>.Instance, fx.Messenger,
            Options with { WriteBatchSize = 100, WriteInterval = TimeSpan.FromHours(1) });
        await service.StartAsync();
        factory.Gate.Release(1);
        await WaitUntilAsync(() => factory.Calls == 1, "ein Foto");

        service.CancelRun(); // das zweite Foto wartet am Tor
        factory.Gate.Release(100);
        await WaitUntilAsync(() => service.Status.State == FaceIndexState.Idle && service.Status.Message == "Abgebrochen", "abgebrochen");
        factory.Calls.Should().Be(2);
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans").Should().Be(2, "beide fertig analysierten Fotos sind geschrieben");

        service.RequestRun();
        await WaitForIdleAsync(service, total: 5);
        factory.Calls.Should().Be(5, "kein Foto wird doppelt analysiert");
    }

    [Fact]
    public async Task Einzelner_Analysefehler_wird_vermerkt_drei_hintereinander_beenden_den_Durchgang_nach_dem_Schreiben()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile("gift1.jpg");
        fx.WriteLibraryFile("b.jpg");
        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory();
        using (var service = Create(fx, factory, enabled: true))
        {
            await service.StartAsync();
            await WaitForIdleAsync(service, total: 3);
        }

        Scalar(fx, "SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId WHERE f.Name = 'gift1.jpg' AND s.ErrorCode = 'analyze_failed'")
            .Should().Be(1, "sonst stünde die Datei bei jedem Durchgang wieder vorn");

        for (var i = 2; i <= 5; i++)
        {
            fx.WriteLibraryFile($"gift{i}.jpg");
        }

        await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.Incremental, null, CancellationToken.None);
        using var second = Create(fx, factory, enabled: true);
        await second.StartAsync();
        await WaitUntilAsync(() => second.Status.State == FaceIndexState.Error, "Fehlerzustand nach drei Fehlern");
        second.Status.Message.Should().Contain("3×");
        Scalar(fx, "SELECT COUNT(*) FROM FaceScans WHERE ErrorCode = 'analyze_failed'").Should().Be(4, "die drei Fehler vor dem Abbruch sind geschrieben");
    }

    [Fact]
    public async Task Geaenderte_Datei_wird_neu_analysiert_Bestaetigung_bleibt_ueber_die_Box()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var path = fx.WriteLibraryFile("p1_foto.jpg");
        fx.WriteLibraryFile("p1_andere.jpg");
        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory();
        using var service = Create(fx, factory, enabled: true);
        await service.StartAsync();
        await WaitForIdleAsync(service, total: 2);
        Exec(fx, "INSERT INTO Persons (Id, Name, CreatedAtUtc, IsHidden) VALUES (1, 'Anna', 1, 0);");
        var faceId = Scalar(fx, "SELECT x.Id FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE f.Name = 'p1_foto.jpg'");
        Exec(fx, $"UPDATE Faces SET PersonId = 1, State = 2 WHERE Id = {faceId};");

        File.WriteAllBytes(path, new byte[5000]); // Inhalt an Ort und Stelle geändert (Größe/Zeit anders)
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        var scan = await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.Incremental, null, CancellationToken.None);
        scan.Modified.Should().Be(1);

        await WaitUntilAsync(() => factory.Calls == 3 && service.Status is { State: FaceIndexState.Idle, Done: 2 }, "geänderte Datei erneut analysiert");
        Scalar(fx, $"SELECT PersonId FROM Faces WHERE Id = {faceId} AND State = 2").Should().Be(1);
    }

    [Fact]
    public async Task Nicht_mehr_dekodierbar_entfernt_unbestaetigte_Gesichter_behaelt_bestaetigte()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var path = fx.WriteLibraryFile("p2_bild.jpg");
        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory();
        using (var first = Create(fx, factory, enabled: true))
        {
            await first.StartAsync();
            await WaitForIdleAsync(first, total: 1);
        }

        Exec(fx, "INSERT INTO Persons (Id, Name, CreatedAtUtc, CoverFaceId, IsHidden) VALUES (1, 'Anna', 1, NULL, 0);");
        var confirmed = Scalar(fx, "SELECT MIN(Id) FROM Faces");
        var other = Scalar(fx, "SELECT MAX(Id) FROM Faces");
        Exec(fx, $"UPDATE Faces SET PersonId = 1, State = 2 WHERE Id = {confirmed};");
        Exec(fx, $"UPDATE Persons SET CoverFaceId = {other} WHERE Id = 1;");

        // Neue Modell-Version, die Datei ist inzwischen nicht mehr dekodierbar.
        File.Exists(path).Should().BeTrue();
        var broken = new BrokenLoader();
        using var second = new FaceIndexService(
            fx.ContextFactory, fx.CreateLibraryService(), new FakeFaceAnalyzerFactory("fake/v2"), broken,
            new MemoryFaceSettingsStore(new FaceIndexSettings(true, true)), NullLogger<FaceIndexService>.Instance, fx.Messenger, Options);
        await second.StartAsync();
        await WaitForIdleAsync(second, total: 1);

        Scalar(fx, "SELECT COUNT(*) FROM FaceScans WHERE ErrorCode = 'decode_failed' AND ModelVersion = 'fake/v2'").Should().Be(1);
        Scalar(fx, "SELECT COUNT(*) FROM Faces").Should().Be(1, "unbestätigtes Gesicht mit altem Fingerabdruck entfernt");
        Scalar(fx, $"SELECT COUNT(*) FROM Faces WHERE Id = {confirmed} AND PersonId = 1").Should().Be(1, "bestätigtes bleibt");
        Scalar(fx, "SELECT COUNT(*) FROM Persons WHERE CoverFaceId IS NULL").Should().Be(1, "Titelbild auf ein gelöschtes Gesicht wird gelöst");
    }

    private sealed class BrokenLoader : IFaceImageLoader
    {
        public FaceImage Load(string path, int maxEdge) => throw new FaceImageDecodeException("kaputt");
    }

    private static FaceIndexService Create(TempLibraryFixture fx, FakeFaceAnalyzerFactory factory, bool enabled)
        => new(
            fx.ContextFactory,
            fx.CreateLibraryService(),
            factory,
            new FakeFaceImageLoader(),
            new MemoryFaceSettingsStore(new FaceIndexSettings(enabled, true)),
            NullLogger<FaceIndexService>.Instance,
            fx.Messenger,
            Options);

    private static Task WaitForIdleAsync(FaceIndexService service, int total)
        => WaitUntilAsync(
            () => service.Status is { State: FaceIndexState.Idle } s && s.Total == total && s.Done == total,
            $"Index fertig ({total})",
            () => $"{service.Status}");

    private static async Task WaitUntilAsync(Func<bool> condition, string what, Func<string>? detail = null)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timeout: {what} {detail?.Invoke()}");
            }

            await Task.Delay(20);
        }
    }

    internal static long Scalar(TempLibraryFixture fx, string sql)
    {
        using var connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    internal static void Exec(TempLibraryFixture fx, string sql)
    {
        using SqliteConnection connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
