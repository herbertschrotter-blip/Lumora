using Microsoft.EntityFrameworkCore;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Scanning;

/// <summary>
/// Ordnerleiste › „Neu einlesen“ (ADR-0068): Incremental-Scan nur eines Ordners samt Unterordnern — neue und fehlende Dateien
/// darin werden gebucht, alles außerhalb bleibt unberührt (nicht „fehlt“, nicht neu). Dazu: das Ordner-Titelbild übersteht
/// „Neu aufbauen“ (ClearAndRescan) über den Ordnerpfad.
/// </summary>
public sealed class FolderScopedScanTests
{
    [Fact]
    public async Task Neu_einlesen_eines_Ordners_bucht_nur_darin()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("Reise", "alt.jpg"));
        for (var i = 1; i <= 5; i++)
        {
            fx.WriteLibraryFile(Path.Combine("Reise", $"mehr{i}.jpg")); // unter der 20-%-Schwelle der Fehlend-Prüfung bleiben
        }

        fx.WriteLibraryFile(Path.Combine("Reise", "Tag2", "weg.jpg"));
        fx.WriteLibraryFile(Path.Combine("Anderes", "bleibt.jpg"));
        await fx.ScanAsync(library.Id);

        fx.WriteLibraryFile(Path.Combine("Reise", "neu.jpg"));
        fx.WriteLibraryFile(Path.Combine("Anderes", "nicht-eingelesen.jpg"));
        File.Delete(Path.Combine(fx.LibraryRoot, "Reise", "Tag2", "weg.jpg"));
        File.Delete(Path.Combine(fx.LibraryRoot, "Anderes", "bleibt.jpg"));

        var result = await fx.CreateScanService().ScanAsync(
            library.Id, ScanMode.Incremental, new ScanOptions { SubtreePath = Path.Combine(fx.LibraryRoot, "Reise") }, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.Added.Should().Be(1);
        result.MarkedMissing.Should().Be(1);
        (await fx.FindFileAsync("neu.jpg")).Should().NotBeNull();
        (await fx.FindFileAsync("weg.jpg"))!.Missing.Should().BeTrue();
        (await fx.FindFileAsync("nicht-eingelesen.jpg")).Should().BeNull("außerhalb des Ordners wird nichts eingelesen");
        (await fx.FindFileAsync("bleibt.jpg"))!.Missing.Should().BeFalse("außerhalb des Ordners wird nichts als fehlend markiert");
    }

    [Fact]
    public async Task Ordner_Titelbild_uebersteht_Neu_aufbauen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("Reise", "a.jpg"));
        fx.WriteLibraryFile(Path.Combine("Reise", "b.jpg"));
        await fx.ScanAsync(library.Id);
        var cover = (await fx.FindFileAsync("b.jpg"))!;
        await using (var ctx = fx.CreateContext())
        {
            await ctx.Folders.Where(f => f.Id == cover.FolderId).ExecuteUpdateAsync(s => s.SetProperty(f => f.CoverFileId, cover.Id));
        }

        var result = await fx.CreateScanService().ScanAsync(library.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        var newCover = (await fx.FindFileAsync("b.jpg"))!;
        newCover.Id.Should().NotBe(cover.Id, "„Neu aufbauen“ vergibt neue FileIds");
        var folder = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "Reise")))!;
        folder.CoverFileId.Should().Be(newCover.Id);
    }
}
