using Microsoft.EntityFrameworkCore;
using Lumora.Services.Scanning;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Tests.Fixtures;
using Lumora.Tests.Operations;

namespace Lumora.Tests.Scanning;

/// <summary>
/// #22: „Neu aufbauen“ nach „Nur künftig ausschließen“ — mit <see cref="ScanOptions.KeepCatalogedInExcludedFolders"/> bleiben genau
/// die schon katalogisierten Einträge in ausgeschlossenen Ordnern (Benutzerdaten über UserDataCarryOver), neue Dateien dort bleiben
/// draußen, verschwundene fallen weg; ohne die Option fallen alle heraus (bisheriges Verhalten).
/// </summary>
public sealed class RebuildKeepExcludedTests
{
    private sealed record World(TempLibraryFixture Fx, int LibraryId, int TagId, int AlbumId) : IDisposable
    {
        public void Dispose() => Fx.Dispose();
    }

    /// <summary>Bibliothek mit a.jpg, Privat\p1.jpg (Sterne, Tag, Album), Privat\Sub\p2.jpg, Privat\weg.jpg; danach „Privat“ nur künftig ausgeschlossen.</summary>
    private static async Task<World> ArrangeAsync()
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile(Path.Combine("Privat", "p1.jpg"));
        fx.WriteLibraryFile(Path.Combine("Privat", "Sub", "p2.jpg"));
        fx.WriteLibraryFile(Path.Combine("Privat", "weg.jpg"));
        await fx.ScanAsync(library.Id);
        var p1 = (await fx.FindFileAsync("p1.jpg"))!.Id;
        var (tagId, albumId, _) = await RemovalTestKit.DecorateAsync(fx, library.Id, p1, rating: 5);
        await new ScanExclusionStore(fx.ContextFactory, fx.Messenger).AddFolderAsync(library.Id, Path.Combine(fx.LibraryRoot, "Privat"));
        File.Delete(Path.Combine(fx.LibraryRoot, "Privat", "weg.jpg"));
        fx.WriteLibraryFile(Path.Combine("Privat", "neu.jpg"));
        return new World(fx, library.Id, tagId, albumId);
    }

    private static Task<ScanResult> RebuildAsync(World w, bool keep)
        => w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.ClearAndRescan, new ScanOptions { KeepCatalogedInExcludedFolders = keep }, null, CancellationToken.None);

    [Fact]
    public async Task Behalten_nimmt_genau_die_katalogisierten_Eintraege_mit_ihren_Angaben_wieder_auf()
    {
        using var w = await ArrangeAsync();
        var oldP1 = (await w.Fx.FindFileAsync("p1.jpg"))!.Id;

        var result = await RebuildAsync(w, keep: true);

        result.KeptExcluded.Should().Be(2, "p1 und p2; weg.jpg gibt es nicht mehr");
        var p1 = (await w.Fx.FindFileAsync("p1.jpg"))!;
        p1.Id.Should().NotBe(oldP1, "Neu aufbauen vergibt neue FileIds");
        p1.Rating.Should().Be(5, "Benutzerdaten über UserDataCarryOver");
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM FileTags WHERE FileId = {p1.Id} AND TagId = {w.TagId}")).Should().Be(1);
        (await RemovalTestKit.CountAsync(w.Fx, $"SELECT COUNT(*) FROM AlbumItems WHERE FileId = {p1.Id} AND AlbumId = {w.AlbumId}")).Should().Be(1);
        (await w.Fx.FindFileAsync("p2.jpg")).Should().NotBeNull();
        (await w.Fx.FindFolderAsync(Path.Combine(w.Fx.LibraryRoot, "Privat", "Sub"))).Should().NotBeNull("Ordnerkette für behaltene Einträge");
        (await w.Fx.FindFileAsync("neu.jpg")).Should().BeNull("neue Dateien im ausgeschlossenen Ordner bleiben draußen");
        (await w.Fx.FindFileAsync("weg.jpg")).Should().BeNull("verschwundene Dateien fallen wie sonst weg");
        (await w.Fx.FindFileAsync("a.jpg")).Should().NotBeNull();
        w.Fx.FtsRowIds("p1").Should().Equal(p1.Id);

        // Ein späteres „Aktualisieren“ lässt sie stehen (ausgeschlossene Ordner gleicht es nicht ab)
        var incremental = await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.Incremental, null, CancellationToken.None);
        incremental.MarkedMissing.Should().Be(0);
        incremental.Added.Should().Be(0);
    }

    [Fact]
    public async Task Herausnehmen_laesst_alle_Eintraege_in_ausgeschlossenen_Ordnern_weg()
    {
        using var w = await ArrangeAsync();

        var result = await RebuildAsync(w, keep: false);

        result.KeptExcluded.Should().Be(0);
        (await w.Fx.FindFileAsync("p1.jpg")).Should().BeNull();
        (await w.Fx.FindFileAsync("p2.jpg")).Should().BeNull();
        (await w.Fx.FindFileAsync("a.jpg")).Should().NotBeNull();
        await using var ctx = w.Fx.CreateContext();
        (await ctx.Files.CountAsync(f => f.LibraryId == w.LibraryId)).Should().Be(1);
    }

    [Fact]
    public async Task Die_Option_wirkt_nur_beim_Neuaufbau()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine(".thumbs", "t.jpg"));
        fx.WriteLibraryFile("a.jpg");

        var fresh = await fx.CreateScanService().ScanAsync(library.Id, ScanMode.Fresh, new ScanOptions { KeepCatalogedInExcludedFolders = true }, null, CancellationToken.None);

        fresh.KeptExcluded.Should().Be(0);
        (await fx.FindFileAsync("t.jpg")).Should().BeNull("Standardausschluss .thumbs gilt beim ersten Einlesen weiter");
    }

    [Fact]
    public async Task Veraltete_RootId_wird_ueber_den_Pfad_zugeordnet()
    {
        using var w = await ArrangeAsync();
        await using (var ctx = w.Fx.CreateContext())
        {
            await ctx.Folders.Where(f => f.Path.Contains("Privat")).ExecuteUpdateAsync(s => s.SetProperty(f => f.RootId, 9999));
        }

        var result = await RebuildAsync(w, keep: true);

        result.KeptExcluded.Should().Be(2, "Wurzel entfernt und neu angelegt — der Pfad liegt weiter unter der Wurzel");
        (await w.Fx.FindFileAsync("p1.jpg"))!.Rating.Should().Be(5);
    }
}
