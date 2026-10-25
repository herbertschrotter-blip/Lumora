using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Scanning;

/// <summary>
/// Ausschlüsse beim Einlesen (ADR-0066): Regeln (Namen in jeder Tiefe, Ordner der Bibliothek, Wurzeln nie), Ablage in der
/// Settings-Tabelle des Katalogs (Standard .thumbs/.temp, je Bibliothek, eigener Katalog = eigene Liste) und Wirkung auf voller
/// Scan, Incremental-Scan und Übernehmen.
/// </summary>
public sealed class ScanExclusionTests
{
    // ------------------------------------------------------------------ Regeln

    [Fact]
    public void Namen_gelten_in_jeder_Tiefe_und_ohne_Gross_Klein_aber_nie_fuer_die_Wurzel()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumora-x", ".temp", "Fotos"); // Wurzelpfad enthält selbst „.temp“
        var set = new ScanExclusionSet([".thumbs", ".temp"], [], [root]);

        set.IsExcludedPath(root).Should().BeFalse("die Wurzel ist nie ausgeschlossen");
        set.IsExcludedPath(Path.Combine(root, "2020")).Should().BeFalse("„.temp“ im Wurzelpfad zählt nicht");
        set.IsExcludedPath(Path.Combine(root, ".thumbs")).Should().BeTrue();
        set.IsExcludedPath(Path.Combine(root, "2020", "Urlaub", ".THUMBS", "a.jpg")).Should().BeTrue("in jeder Tiefe, Groß/Klein egal");
        set.IsExcludedPath(Path.Combine(root, "2020", ".Temp", "x")).Should().BeTrue();
        set.IsExcludedPath(Path.Combine(root, "2020", "thumbs")).Should().BeFalse("nur der genaue Name");
        set.IsExcludedChildDirectory(Path.Combine(root, "a", ".thumbs")).Should().BeTrue();
        set.IsExcludedChildDirectory(root).Should().BeFalse();
    }

    [Fact]
    public void Ordner_der_Bibliothek_gelten_samt_Unterordnern()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumora-y");
        var privat = Path.Combine(root, "2020", "Privat");
        var set = new ScanExclusionSet([], [privat], [root]);

        set.IsExcludedPath(privat).Should().BeTrue();
        set.IsExcludedPath(Path.Combine(privat, "Sub", "b.jpg")).Should().BeTrue();
        set.IsExcludedPath(Path.Combine(root, "2020", "Privat2")).Should().BeFalse("kein Präfix-Treffer auf Namensteile");
        set.IsExcludedChildDirectory(privat).Should().BeTrue();
        ScanExclusionSet.None.IsExcludedPath(privat).Should().BeFalse();
    }

    // ------------------------------------------------------------------ Ablage

    [Fact]
    public async Task Ohne_Eintrag_gelten_thumbs_und_temp_und_eine_leere_Liste_bleibt_leer()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var store = new ScanExclusionStore(fx.ContextFactory, fx.Messenger);

        (await store.GetFolderNamesAsync()).Should().Equal(".thumbs", ".temp");

        await store.SetFolderNamesAsync([" .cache ", ".CACHE", "Export"]);
        (await store.GetFolderNamesAsync()).Should().Equal(".cache", "Export");

        await store.SetFolderNamesAsync([]);
        (await store.GetFolderNamesAsync()).Should().BeEmpty("bewusst geleert — keine Standardnamen mehr");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("x:y")]
    public async Task Ungueltige_Namen_werden_abgelehnt(string name)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var store = new ScanExclusionStore(fx.ContextFactory, fx.Messenger);

        var act = () => store.SetFolderNamesAsync([name]);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Ordner_werden_relativ_zur_Wurzel_gespeichert_und_Wurzel_oder_ausserhalb_abgelehnt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        var store = new ScanExclusionStore(fx.ContextFactory, fx.Messenger);
        var changes = new List<int?>();
        fx.Messenger.Register<List<int?>, ScanExclusionsChangedMessage>(changes, static (r, m) => r.Add(m.LibraryId));
        var privat = Path.Combine(fx.LibraryRoot, "2020", "Privat");

        (await store.AddFolderAsync(library.Id, privat)).Should().BeTrue();
        (await store.AddFolderAsync(library.Id, Path.Combine(privat, "Sub"))).Should().BeFalse("liegt schon in einem ausgeschlossenen Ordner");

        var folders = await store.GetFoldersAsync(library.Id);
        folders.Should().ContainSingle();
        folders[0].RelativePath.Should().Be(Path.Combine("2020", "Privat"));
        folders[0].FullPath.Should().BeEquivalentTo(privat);
        await using (var ctx = fx.CreateContext())
        {
            (await ctx.Settings.SingleAsync(s => s.Key == ScanExclusionStore.FoldersKey(library.Id))).Value.Should().NotContain(fx.LibraryRoot.Replace("\\", "\\\\"), "relativ zur Wurzel");
        }

        await FluentActions.Invoking(() => store.AddFolderAsync(library.Id, fx.LibraryRoot)).Should().ThrowAsync<InvalidOperationException>("Wurzel");
        await FluentActions.Invoking(() => store.AddFolderAsync(library.Id, fx.OutsideDir)).Should().ThrowAsync<InvalidOperationException>("außerhalb");

        (await store.RemoveFolderAsync(library.Id, privat)).Should().BeTrue();
        (await store.GetFoldersAsync(library.Id)).Should().BeEmpty();
        changes.Should().Equal(library.Id, library.Id);
    }

    [Fact]
    public async Task Ausschluesse_darunter_bleiben_stehen_und_Verschieben_zieht_mit()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        var store = new ScanExclusionStore(fx.ContextFactory, fx.Messenger);
        await store.AddFolderAsync(library.Id, Path.Combine(fx.LibraryRoot, "A", "B", "C"));
        await store.AddFolderAsync(library.Id, Path.Combine(fx.LibraryRoot, "A", "B"));
        (await store.GetFoldersAsync(library.Id)).Select(f => f.RelativePath).Should().Equal(Path.Combine("A", "B"), Path.Combine("A", "B", "C"));
        (await store.RemoveFolderAsync(library.Id, Path.Combine(fx.LibraryRoot, "A", "B"))).Should().BeTrue();
        (await store.LoadSetAsync(library.Id)).IsExcludedPath(Path.Combine(fx.LibraryRoot, "A", "B", "C")).Should().BeTrue("der frühere Ausschluss gilt weiter");

        await store.MoveFoldersAsync(library.Id, Path.Combine(fx.LibraryRoot, "A"), Path.Combine(fx.LibraryRoot, "Z", "A"));

        (await store.GetFoldersAsync(library.Id)).Select(f => f.RelativePath).Should().Equal(Path.Combine("Z", "A", "B", "C"));

        await store.MoveFoldersAsync(library.Id, Path.Combine(fx.LibraryRoot, "Z"), fx.OutsideDir);
        (await store.GetFoldersAsync(library.Id)).Should().BeEmpty("aus der Bibliothek hinaus — Eintrag entfällt");
    }

    [Fact]
    public async Task Ordner_gehoeren_der_Bibliothek_und_gehen_mit_ihr()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        var store = new ScanExclusionStore(fx.ContextFactory, fx.Messenger);
        await store.AddFolderAsync(library.Id, Path.Combine(fx.LibraryRoot, "Privat"));

        await fx.CreateLibraryService().DeleteAsync(library.Id);

        await using var ctx = fx.CreateContext();
        (await ctx.Settings.AnyAsync(s => s.Key == ScanExclusionStore.FoldersKey(library.Id))).Should().BeFalse();
    }

    [Fact]
    public async Task Jeder_Katalog_hat_seine_eigene_Liste_wie_die_versteckte_Sitzung()
    {
        using var normal = await TempLibraryFixture.CreateAsync();
        using var hidden = await TempLibraryFixture.CreateAsync(); // eigener Katalog wie eine versteckte Sitzung (ADR-0049)
        var normalStore = new ScanExclusionStore(normal.ContextFactory, normal.Messenger);
        var hiddenStore = new ScanExclusionStore(hidden.ContextFactory, hidden.Messenger);

        await hiddenStore.SetFolderNamesAsync([".geheim"]);

        (await hiddenStore.GetFolderNamesAsync()).Should().Equal(".geheim");
        (await normalStore.GetFolderNamesAsync()).Should().Equal(".thumbs", ".temp");
    }

    // ------------------------------------------------------------------ Scans

    [Fact]
    public async Task Voller_Scan_liest_thumbs_und_temp_in_keiner_Tiefe_ein()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile(Path.Combine("2020", "Urlaub", "b.jpg"));
        fx.WriteLibraryFile(Path.Combine(".thumbs", "t1.jpg"));
        fx.WriteLibraryFile(Path.Combine("2020", "Urlaub", ".thumbs", "t2.jpg"));
        fx.WriteLibraryFile(Path.Combine("2020", "x", "y", ".TEMP", "t3.jpg"));

        var result = await fx.ScanAsync(library.Id);

        result.FilesInserted.Should().Be(2);
        (await fx.FindFileAsync("t2.jpg")).Should().BeNull();
        await using var ctx = fx.CreateContext();
        (await ctx.Folders.AnyAsync(f => f.Path.Contains(".thumbs") || f.Path.Contains(".TEMP"))).Should().BeFalse("ausgeschlossene Ordner bekommen keine Zeile");
    }

    [Fact]
    public async Task Incremental_Scan_ignoriert_neue_Dateien_in_thumbs_und_eigene_Namen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(library.Id);
        await new ScanExclusionStore(fx.ContextFactory, fx.Messenger).SetFolderNamesAsync([".thumbs", "Export"]);
        fx.WriteLibraryFile(Path.Combine("2021", ".thumbs", "neu-t.jpg"));
        fx.WriteLibraryFile(Path.Combine("2021", "Export", "neu-e.jpg"));
        fx.WriteLibraryFile(Path.Combine("2021", "neu.jpg"));

        var result = await fx.CreateScanService().ScanAsync(library.Id, ScanMode.Incremental, null, CancellationToken.None);

        result.Added.Should().Be(1);
        (await fx.FindFileAsync("neu.jpg")).Should().NotBeNull();
        (await fx.FindFileAsync("neu-t.jpg")).Should().BeNull();
        (await fx.FindFileAsync("neu-e.jpg")).Should().BeNull();
    }

    [Fact]
    public async Task Nur_kuenftig_ausgeschlossene_Dateien_bleiben_und_werden_nicht_als_fehlend_markiert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("Privat", "p.jpg"));
        for (var i = 0; i < 8; i++)
        {
            fx.WriteLibraryFile($"a{i}.jpg"); // genug Dateien, damit 1 fehlende unter dem Mass-Missing-Schwellwert bleibt
        }

        await fx.ScanAsync(library.Id);
        var store = new ScanExclusionStore(fx.ContextFactory, fx.Messenger);
        await store.AddFolderAsync(library.Id, Path.Combine(fx.LibraryRoot, "Privat"));
        File.Delete(Path.Combine(fx.LibraryRoot, "Privat", "p.jpg")); // auch eine verschwundene Datei dort bleibt unberührt
        fx.WriteLibraryFile(Path.Combine("Privat", "neu.jpg"));

        var result = await fx.CreateScanService().ScanAsync(library.Id, ScanMode.Incremental, null, CancellationToken.None);

        result.MarkedMissing.Should().Be(0);
        result.Added.Should().Be(0);
        var p = (await fx.FindFileAsync("p.jpg"))!;
        p.Missing.Should().BeFalse("ausgeschlossene Ordner gleicht der Scan nicht ab");
        (await fx.FindFileAsync("neu.jpg")).Should().BeNull();

        // Aufheben → erneutes Einlesen holt die neue Datei und markiert die verschwundene
        await store.RemoveFolderAsync(library.Id, Path.Combine(fx.LibraryRoot, "Privat"));
        var again = await fx.CreateScanService().ScanAsync(library.Id, ScanMode.Incremental, null, CancellationToken.None);
        again.Added.Should().Be(1);
        again.MarkedMissing.Should().Be(1);
    }

    [Fact]
    public async Task Ausschluss_einer_Bibliothek_wirkt_nur_dort()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var first = await fx.CreateLibraryAsync("Erste");
        var second = await AddLibraryAsync(fx, "Zweite", fx.InboxDir);
        fx.WriteLibraryFile(Path.Combine("Privat", "eins.jpg"));
        fx.WriteInboxFile(Path.Combine("Privat", "zwei.jpg"));
        var store = new ScanExclusionStore(fx.ContextFactory, fx.Messenger);
        await store.AddFolderAsync(first.Id, Path.Combine(fx.LibraryRoot, "Privat"));

        await fx.ScanAsync(first.Id);
        await fx.ScanAsync(second.Id);

        (await fx.FindFileAsync("eins.jpg")).Should().BeNull();
        (await fx.FindFileAsync("zwei.jpg")).Should().NotBeNull("gleichnamiger Ordner der anderen Bibliothek bleibt");
        (await store.GetFoldersAsync(second.Id)).Should().BeEmpty();
    }

    [Fact]
    public void Uebernehmen_laesst_ausgeschlossene_Ordner_liegen_die_eingebauten_wie_bisher()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Lumora.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            TempLibraryFixture.WriteFile(Path.Combine(dir, "Reise", "a.jpg"));
            TempLibraryFixture.WriteFile(Path.Combine(dir, "Reise", "Export", "e.jpg"));
            TempLibraryFixture.WriteFile(Path.Combine(dir, "Reise", ".thumbs", "t.jpg"));
            var set = new ScanExclusionSet([".thumbs", "Export"], [], [dir]);

            var snapshot = FolderAdoptionSourceReader.Read(Path.Combine(dir, "Reise"), exclusions: set);

            snapshot.Root.Children.Should().BeEmpty("„Export“ wird nicht übernommen, „.thumbs“ ist Systemordner");
            snapshot.IgnoredEntries.Should().ContainSingle(e => e.EndsWith(".thumbs", StringComparison.Ordinal));
            snapshot.Warnings.Should().ContainSingle(w => w.Contains("ausgeschlossen", StringComparison.Ordinal));
            FolderAdoptionSourceReader.FindRemainingFiles(Path.Combine(dir, "Reise")).Should().Contain(f => f.EndsWith("e.jpg", StringComparison.Ordinal), "bleibt als Rest — der Quellordner wandert nicht in den Papierkorb");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    internal static async Task<Library> AddLibraryAsync(TempLibraryFixture fx, string name, string rootPath)
    {
        await using var ctx = fx.CreateContext();
        var library = new Library { Name = name, RootPath = rootPath, CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
        ctx.Libraries.Add(library);
        await ctx.SaveChangesAsync();
        ctx.LibraryRoots.Add(new LibraryRoot { LibraryId = library.Id, Path = rootPath, Role = LibraryRootRole.Archive, Name = Path.GetFileName(rootPath), SortOrder = 0 });
        await ctx.SaveChangesAsync();
        return library;
    }
}
