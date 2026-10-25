using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Marks;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Marks;

/// <summary>
/// Tag- und Kategorie-Symbole (Mockup 16, 0.4.35): Zahlen je Ordner nur für Dateien DIREKT im Ordner (nicht rekursiv), die
/// häufigsten Tags/Werte absteigend, Bereichs-Trennung über die Bibliotheks-Ids (ADR-0048), Einstellungen in der
/// Settings-Tabelle (Standard an) und die gebündelte Abfrage der Kategorie-Werte einer Galerie-Seite.
/// </summary>
public sealed class FolderMarkCounterTests
{
    private static async Task<(int LibraryId, int A, int B, int C, int D)> ArrangeAsync(TempLibraryFixture fx)
    {
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("x", "a.jpg"));
        fx.WriteLibraryFile(Path.Combine("x", "b.jpg"));
        fx.WriteLibraryFile(Path.Combine("x", "d.jpg"));
        fx.WriteLibraryFile(Path.Combine("x", "sub", "c.jpg"));
        await fx.ScanAsync(library.Id);
        return (library.Id,
            (await fx.FindFileAsync("a.jpg"))!.Id,
            (await fx.FindFileAsync("b.jpg"))!.Id,
            (await fx.FindFileAsync("c.jpg"))!.Id,
            (await fx.FindFileAsync("d.jpg"))!.Id);
    }

    /// <summary>Zweite Bibliothek mit eigener Wurzel (<see cref="TempLibraryFixture.OutsideDir"/>) und einer Datei darin.</summary>
    private static async Task<(int LibraryId, int FileId, int FolderId)> AddSecondLibraryAsync(TempLibraryFixture fx)
    {
        int libraryId;
        await using (var ctx = fx.CreateContext())
        {
            var library = new Library { Name = "Zweite", RootPath = fx.OutsideDir, CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
            ctx.Libraries.Add(library);
            await ctx.SaveChangesAsync();
            ctx.LibraryRoots.Add(new LibraryRoot { LibraryId = library.Id, Path = fx.OutsideDir, Role = LibraryRootRole.Archive, Name = "outside", SortOrder = 0 });
            await ctx.SaveChangesAsync();
            libraryId = library.Id;
        }

        TempLibraryFixture.WriteFile(Path.Combine(fx.OutsideDir, "fremd", "z.jpg"));
        await fx.ScanAsync(libraryId);
        var file = (await fx.FindFileAsync("z.jpg"))!;
        return (libraryId, file.Id, file.FolderId);
    }

    private static async Task<int> FolderIdAsync(TempLibraryFixture fx, params string[] relative)
        => (await fx.FindFolderAsync(Path.Combine([fx.LibraryRoot, .. relative])))!.Id;

    [Fact]
    public async Task Zaehlt_nur_Dateien_direkt_im_Ordner_mit_den_haeufigsten_zuerst()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, a, b, c, d) = await ArrangeAsync(fx);
        var tags = fx.CreateTagService();
        var urlaub = await tags.CreateAsync("Urlaub");
        var berge = await tags.CreateAsync("Berge");
        await tags.AssignAsync([a, b], [urlaub.Id]);
        await tags.AssignAsync([a], [berge.Id]);
        await tags.AssignAsync([c], [berge.Id]);
        using var categories = fx.CreateCategoryService();
        var anlass = await categories.CreateAsync("Anlass", ["Feier", "Reise"]);
        var projekt = await categories.CreateAsync("Projekt", ["Nord"]);
        await categories.SetValueAsync([d], anlass.Id, anlass.Values[1].Id);
        await categories.SetValueAsync([d], projekt.Id, projekt.Values[0].Id);
        await categories.SetValueAsync([b], anlass.Id, anlass.Values[1].Id);

        var counts = await new FolderMarkCounter(fx.ContextFactory).CountAsync([libraryId]);

        var x = counts.Get(await FolderIdAsync(fx, "x"))!;
        x.TaggedFiles.Should().Be(2, "a und b haben Tags — c liegt im Unterordner und zählt hier nicht");
        x.CategorizedFiles.Should().Be(2, "b und d haben Kategorie-Werte (d sogar zwei)");
        x.TopTags.Should().Equal(new MarkUsage(urlaub.Id, 2), new MarkUsage(berge.Id, 1));
        x.TopValues.Should().Equal(new MarkUsage(anlass.Values[1].Id, 2), new MarkUsage(projekt.Values[0].Id, 1));

        var sub = counts.Get(await FolderIdAsync(fx, "x", "sub"))!;
        sub.TaggedFiles.Should().Be(1);
        sub.CategorizedFiles.Should().Be(0);
        sub.TopTags.Should().Equal(new MarkUsage(berge.Id, 1));
        counts.Get(await FolderIdAsync(fx)).Should().BeNull("in der Wurzel selbst liegt keine Datei mit Tags/Kategorien");
    }

    [Fact]
    public async Task Top_begrenzt_die_Namen_je_Ordner()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, a, b, _, d) = await ArrangeAsync(fx);
        var tags = fx.CreateTagService();
        var t1 = await tags.CreateAsync("Eins");
        var t2 = await tags.CreateAsync("Zwei");
        var t3 = await tags.CreateAsync("Drei");
        await tags.AssignAsync([a, b, d], [t3.Id]);
        await tags.AssignAsync([a, b], [t2.Id]);
        await tags.AssignAsync([a], [t1.Id]);

        var counts = await new FolderMarkCounter(fx.ContextFactory).CountAsync([libraryId], top: 2);

        var x = counts.Get(await FolderIdAsync(fx, "x"))!;
        x.TaggedFiles.Should().Be(3);
        x.TopTags.Select(u => u.Id).Should().Equal(t3.Id, t2.Id);
    }

    [Fact]
    public async Task Andere_Bibliotheken_zaehlen_nur_im_gemeinsamen_Bereich()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, a, _, _, _) = await ArrangeAsync(fx);
        var tag = await fx.CreateTagService().CreateAsync("Urlaub");
        var (otherId, z, otherFolder) = await AddSecondLibraryAsync(fx);
        await using (var ctx = fx.CreateContext())
        {
            // direkt in die Tabelle: der Zähler darf sich nur auf die Bibliotheks-Ids verlassen, nicht auf die Tag-Gruppe
            ctx.FileTags.Add(new FileTag { FileId = a, TagId = tag.Id });
            ctx.FileTags.Add(new FileTag { FileId = z, TagId = tag.Id });
            await ctx.SaveChangesAsync();
        }

        var counter = new FolderMarkCounter(fx.ContextFactory);
        var onlyA = await counter.CountAsync([libraryId]);
        var onlyB = await counter.CountAsync([otherId]);
        var linked = await counter.CountAsync([libraryId, otherId]);

        onlyA.Get(otherFolder).Should().BeNull("Bibliothek B gehört nicht zum Bereich von A");
        onlyA.ByFolder.Values.Sum(c => c.TaggedFiles).Should().Be(1);
        onlyB.ByFolder.Keys.Should().Equal(otherFolder);
        linked.ByFolder.Values.Sum(c => c.TaggedFiles).Should().Be(2, "„Alle verknüpften“ zählt beide");
        (await counter.CountAsync([])).ByFolder.Should().BeEmpty();
    }

    [Fact]
    public async Task Kategorie_Werte_einer_Seite_kommen_in_einem_Aufruf()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, a, b, c, d) = await ArrangeAsync(fx);
        using var categories = fx.CreateCategoryService();
        var anlass = await categories.CreateAsync("Anlass", ["Feier"]);
        await categories.SetValueAsync([a, c], anlass.Id, anlass.Values[0].Id);

        // wie GalleryViewModel.ApplyCategoriesToItemsAsync: alle Ids der Seite auf einmal
        var map = await categories.GetValuesForFilesAsync([a, b, c, d]);

        map.Keys.Should().BeEquivalentTo([a, c], "Dateien ohne Wert fehlen im Ergebnis");
        map[a][anlass.Id].Should().Be(anlass.Values[0].Id);
    }

    [Fact]
    public async Task Einstellungen_sind_standardmaessig_an_und_werden_gespeichert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var store = new MarkSettingsStore(fx.ContextFactory);

        (await store.LoadAsync()).Should().Be(MarkSettings.Default);

        await store.SaveAsync(new MarkSettings(ShowOnTiles: false, ShowInLightbox: true, ShowInFolderTree: false));
        (await store.LoadAsync()).Should().Be(new MarkSettings(false, true, false));

        await store.SaveAsync(MarkSettings.Default);
        (await store.LoadAsync()).Should().Be(MarkSettings.Default, "Überschreiben statt doppelter Zeilen");
        await using var ctx = fx.CreateContext();
        (await ctx.Settings.CountAsync(s => s.Key.StartsWith("ui.marks."))).Should().Be(3);
    }

    [Fact]
    public async Task Unlesbare_Werte_fallen_auf_an_zurueck()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        await using (var ctx = fx.CreateContext())
        {
            ctx.Settings.Add(new Setting { Key = MarkSettings.TilesKey, Value = "vielleicht" });
            ctx.Settings.Add(new Setting { Key = MarkSettings.FolderTreeKey, Value = "false" });
            await ctx.SaveChangesAsync();
        }

        (await new MarkSettingsStore(fx.ContextFactory).LoadAsync()).Should().Be(new MarkSettings(true, true, false));
    }
}
