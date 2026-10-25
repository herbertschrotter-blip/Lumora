using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Activity;
using Lumora.Services.Export;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Export;

/// <summary>
/// Wege nach außen (ADR-0068): Dateien für Ziehen/Strg+C (fehlende und Archive ausgelassen, versteckt gesperrt) und
/// „Exportieren (verkleinert)“ — JPEG-Kopien ohne Katalog, Namen ohne Überschreiben, Videos ausgelassen, Vorgang in der
/// Aktivitäts-Anzeige.
/// </summary>
public sealed class PhotoExportServiceTests
{
    private sealed class FakeEncoder : IPhotoExportEncoder
    {
        public List<(string Source, string Target, int Edge)> Calls { get; } = [];

        public string? FailFor { get; set; }

        public void EncodeJpeg(string sourcePath, string targetPath, int maxLongEdge, int jpegQuality)
        {
            Calls.Add((sourcePath, targetPath, maxLongEdge));
            if (FailFor is not null && sourcePath.EndsWith(FailFor, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("kaputt");
            }

            File.WriteAllBytes(targetPath, [0xFF, 0xD8, 0xFF, 0xD9]);
        }
    }

    private static async Task<(TempLibraryFixture Fx, Dictionary<string, int> Ids)> ArrangeAsync()
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("A", "eins.jpg"));
        fx.WriteLibraryFile(Path.Combine("B", "eins.jpg"));
        fx.WriteLibraryFile("film.mp4");
        fx.WriteLibraryFile("paket.zip");
        fx.WriteLibraryFile("weg.jpg");
        await fx.ScanAsync(library.Id);
        await using var ctx = fx.CreateContext();
        var rows = await ctx.Files.Join(ctx.Folders, f => f.FolderId, d => d.Id, (f, d) => new { f.Id, f.Name, d.Path }).ToListAsync();
        var ids = rows.ToDictionary(r => Path.GetRelativePath(fx.LibraryRoot, Path.Combine(r.Path, r.Name)), r => r.Id);
        File.Delete(Path.Combine(fx.LibraryRoot, "weg.jpg")); // fehlt auf der Platte (noch nicht als fehlend markiert)
        return (fx, ids);
    }

    [Fact]
    public async Task Weg_nach_aussen_laesst_Fehlende_und_Archive_aus_und_ist_versteckt_gesperrt()
    {
        var (fx, ids) = await ArrangeAsync();
        using var _ = fx;
        var service = new PhotoExportService(fx.ContextFactory, NullLogger<PhotoExportService>.Instance, new FakeEncoder(), catalog: new TestCatalogContext());

        var set = await service.GetExternalFilesAsync(ids.Values.ToList());

        set.Blocked.Should().BeFalse();
        set.Paths.Select(p => Path.GetRelativePath(fx.LibraryRoot, p)).Should().BeEquivalentTo([Path.Combine("A", "eins.jpg"), Path.Combine("B", "eins.jpg"), "film.mp4"]);
        set.SkippedMissing.Should().Be(1);
        set.SkippedArchives.Should().Be(1);
        set.SkippedText.Should().Be("1 fehlende Datei und 1 Archiv ausgelassen");

        var hidden = new PhotoExportService(fx.ContextFactory, NullLogger<PhotoExportService>.Instance, new FakeEncoder(), catalog: HiddenCatalog());
        hidden.IsBlocked.Should().BeTrue();
        (await hidden.GetExternalFilesAsync(ids.Values.ToList())).Should().Be(ExternalFileSet.BlockedSet);
        (await hidden.ExportAsync(ids.Values.ToList(), fx.OutsideDir, new PhotoExportOptions())).IsBlocked.Should().BeTrue();
        Directory.EnumerateFiles(fx.OutsideDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Export_schreibt_verkleinerte_JPEGs_ohne_Katalog_und_meldet_sich_in_der_Aktivitaets_Anzeige()
    {
        var (fx, ids) = await ArrangeAsync();
        using var _ = fx;
        var encoder = new FakeEncoder { FailFor = Path.Combine("B", "eins.jpg") };
        var center = new ActivityCenter();
        var service = new PhotoExportService(fx.ContextFactory, NullLogger<PhotoExportService>.Instance, encoder, center, new TestCatalogContext());
        var target = Path.Combine(fx.OutsideDir, "Export");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(target).FullName, "eins.jpg"), "vorhanden");
        var filesBefore = await CountFilesAsync(fx);

        var result = await service.ExportAsync(ids.Values.ToList(), target, new PhotoExportOptions(1600));

        result.Exported.Should().Be(1);
        result.Failed.Should().Be(1);
        result.SkippedVideos.Should().Be(1);
        result.SkippedMissing.Should().Be(1);
        result.SkippedArchives.Should().Be(1);
        encoder.Calls.Should().OnlyContain(c => c.Edge == 1600);
        File.ReadAllText(Path.Combine(target, "eins.jpg")).Should().Be("vorhanden", "nie überschreiben");
        File.Exists(Path.Combine(target, "eins (2).jpg")).Should().BeTrue();
        Directory.EnumerateFiles(target, "*.tmp").Should().BeEmpty();
        (await CountFilesAsync(fx)).Should().Be(filesBefore, "Exporte kommen nicht in den Katalog");
        var recent = center.GetOverview().Recent.Should().ContainSingle().Subject;
        recent.Title.Should().Be("Exportieren (verkleinert)");
        recent.State.Should().Be(ActivityState.Warning);
        recent.Result.Should().Contain("1 Foto exportiert");
    }

    private static TestCatalogContext HiddenCatalog()
    {
        var catalog = new TestCatalogContext();
        catalog.SetHidden(true);
        return catalog;
    }

    private static async Task<int> CountFilesAsync(TempLibraryFixture fx)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Files.CountAsync();
    }
}
