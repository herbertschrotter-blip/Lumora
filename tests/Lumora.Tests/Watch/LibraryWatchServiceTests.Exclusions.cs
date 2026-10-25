using Lumora.Services.Scanning.Exclusions;
using Lumora.Services.Watch;

namespace Lumora.Tests.Watch;

/// <summary>
/// Ausschlüsse beim Einlesen in der Ordnerüberwachung (ADR-0066): Ereignisse in „.thumbs“ (jede Tiefe) und in ausgeschlossenen
/// Ordnern der Bibliothek lösen keinen Scan aus; neue Ausschlüsse wirken sofort (Nachricht); „Ordner verschieben“ ist eigen.
/// </summary>
public sealed partial class LibraryWatchServiceTests
{
    [Fact]
    public async Task Dateien_in_thumbs_in_jeder_Tiefe_loesen_keinen_Scan_aus()
    {
        await using var h = await Harness.CreateAsync(start: false);
        await SeedAndStartAsync(h, "a\\IMG_0001.jpg", "2020\\Urlaub\\Tag1\\IMG_0005.jpg"); // Elternordner gibt es schon

        h.WriteFile("a\\.thumbs\\IMG_0001_t.jpg");
        h.WriteFile("2020\\Urlaub\\Tag1\\.thumbs\\IMG_0002_t.jpg");
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0, ".thumbs ist ausgeschlossen");

        h.WriteFile("a\\IMG_0003.jpg");
        await h.WaitForScanStartAsync("eine Datei außerhalb ausgeschlossener Ordner");
    }

    [Fact]
    public async Task Neuer_Ordner_Ausschluss_wirkt_sofort_und_nur_dort()
    {
        await using var h = await Harness.CreateAsync(start: false);
        await SeedAndStartAsync(h, "privat\\IMG_0001.jpg");
        var store = new ScanExclusionStore(h.Fixture.ContextFactory, h.Fixture.Messenger);
        await store.AddFolderAsync(h.LibraryId, Path.Combine(h.Fixture.LibraryRoot, "privat"));
        await Task.Delay(300); // Nachricht → Überwachung lädt die Regeln neu

        h.WriteFile("privat\\IMG_0002.jpg");
        h.WriteFile("privat\\tief\\IMG_0003.jpg");
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0, "Ordner der Bibliothek ausgeschlossen");

        h.WriteFile("privat2\\IMG_0004.jpg");
        await h.WaitForScanStartAsync("gleich beginnender, aber anderer Ordner");
    }

    [Fact]
    public async Task Eigenes_Ordner_Verschieben_loest_keinen_Scan_aus()
    {
        await using var h = await Harness.CreateAsync(start: false);
        await SeedAndStartAsync(h, "Reise\\IMG_0001.jpg", "Reise\\Tag2\\IMG_0002.jpg", "Ziel\\IMG_0003.jpg");
        var ops = h.CreateFileOperations();
        var reise = (await h.Fixture.FindFolderAsync(Path.Combine(h.Fixture.LibraryRoot, "Reise")))!;

        (await ops.MoveFolderAsync(reise.Id, Path.Combine(h.Fixture.LibraryRoot, "Ziel"))).Success.Should().BeTrue();
        (await ops.UndoLastAsync())!.Failed.Should().Be(0);

        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0, "Quelle und Ziel sind eigene, im Katalog gebuchte Änderungen");
        h.Service.Status.State.Should().Be(WatchState.Idle);
    }
}
