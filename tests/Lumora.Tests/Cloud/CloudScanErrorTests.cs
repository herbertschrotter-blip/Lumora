using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Data.RawSql;
using Lumora.Services.Media;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Cloud;

/// <summary>
/// Kriterium 7 (ADR-0077, Offline-vs-Missing) und ADR-0028 (Nachbesserung 0.5.41): Scheitert das Auflisten eines Ordners
/// mittendrin (OneDrive ohne Verbindung), markiert der Abgleich darin nichts als fehlend; der volle Scan ohne Hook bricht ab
/// wie vor 0.5.41, und „Neu aufbauen“ bricht ab, statt den Ordner samt Benutzerdaten wegfallen zu lassen. Der Fehler kommt
/// von einem Test-Auflister (<see cref="FolderEnumerator.FileLister"/>), der nach der ersten Datei eine IOException wirft.
/// </summary>
public sealed class CloudScanErrorTests
{
    [Fact]
    public void Fehler_mitten_im_Auflisten_ohne_Hook_bricht_das_Aufzaehlen_ab()
    {
        var dir = Directory.CreateTempSubdirectory("lumora-scan-error-");
        try
        {
            var reise = Directory.CreateDirectory(Path.Combine(dir.FullName, "Reise")).FullName;
            File.WriteAllBytes(Path.Combine(reise, "1.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(reise, "2.jpg"), [2]);
            var enumerator = Failing(reise);

            var act = () => enumerator.Enumerate(dir.FullName).ToList();

            act.Should().Throw<IOException>("ohne Hook (voller Scan) wie vor 0.5.41 — kein stilles Weglassen");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Fehler_mitten_im_Auflisten_mit_Hook_meldet_den_Ordner_und_macht_weiter()
    {
        var dir = Directory.CreateTempSubdirectory("lumora-scan-error-");
        try
        {
            var reise = Directory.CreateDirectory(Path.Combine(dir.FullName, "Reise")).FullName;
            var heim = Directory.CreateDirectory(Path.Combine(dir.FullName, "Heim")).FullName;
            File.WriteAllBytes(Path.Combine(reise, "1.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(reise, "2.jpg"), [2]);
            File.WriteAllBytes(Path.Combine(heim, "3.jpg"), [3]);
            var reported = new List<string>();

            var found = Failing(reise).Enumerate(dir.FullName, onIoError: (path, _) => reported.Add(path))
                .Select(f => Path.GetRelativePath(dir.FullName, f.FullPath))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            reported.Should().Equal(reise);
            found.Should().Equal(@"Heim\3.jpg", @"Reise\1.jpg");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Abgleich_markiert_Dateien_eines_nicht_lesbaren_Ordners_nicht_als_fehlend()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(@"Reise\1.jpg");
        fx.WriteLibraryFile(@"Reise\2.jpg");
        var deleted = fx.WriteLibraryFile(@"Heim\3.jpg");
        await fx.ScanAsync(lib.Id);
        File.Delete(deleted); // wirklich weg — wird weiterhin als fehlend erkannt

        var scan = CreateScanService(fx, Failing(Path.Combine(fx.LibraryRoot, "Reise")));
        // bestätigt: 1 von 3 fehlend läge über der Schwelle der Massen-Prüfung (die gehört nicht zu diesem Test)
        await scan.ScanAsync(lib.Id, ScanMode.Incremental, new ScanOptions { ConfirmMassMissing = true }, null, CancellationToken.None);

        (await fx.FindFileAsync("1.jpg"))!.Missing.Should().BeFalse();
        (await fx.FindFileAsync("2.jpg"))!.Missing.Should().BeFalse("nicht lesbar heißt nicht fehlend (OneDrive ohne Verbindung)");
        (await fx.FindFileAsync("3.jpg"))!.Missing.Should().BeTrue("der Abgleich arbeitet in den übrigen Ordnern wie bisher");
    }

    [Fact]
    public async Task Neu_aufbauen_bricht_bei_nicht_lesbarem_Ordner_ab_und_laesst_den_Katalog_unveraendert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(@"Reise\1.jpg");
        fx.WriteLibraryFile(@"Reise\2.jpg");
        fx.WriteLibraryFile(@"Heim\3.jpg");
        await fx.ScanAsync(lib.Id);
        var rated = (await fx.FindFileAsync("2.jpg"))!;
        await using (var ctx = fx.CreateContext())
        {
            var file = await ctx.Files.FindAsync(rated.Id);
            file!.Rating = 4;
            await ctx.SaveChangesAsync();
        }

        var scan = CreateScanService(fx, Failing(Path.Combine(fx.LibraryRoot, "Reise")));
        var act = () => scan.ScanAsync(lib.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        var after = (await fx.FindFileAsync("2.jpg"))!;
        after.Id.Should().Be(rated.Id, "kein Commit — der bisherige Katalog bleibt (ADR-0028)");
        after.Rating.Should().Be((byte)4, "die Bewertung im nicht lesbaren Ordner geht nicht verloren");
        (await fx.FindFileAsync("3.jpg")).Should().NotBeNull();
    }

    [Fact]
    public async Task Neu_aufbauen_ueberspringt_eine_einzelne_nicht_lesbare_Datei_wie_bisher()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(@"Reise\1.jpg");
        fx.WriteLibraryFile(@"Reise\2.jpg");
        await fx.ScanAsync(lib.Id);
        var reise = Path.Combine(fx.LibraryRoot, "Reise");
        var scan = CreateScanService(fx, new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance)
        {
            // eine Datei, deren Eigenschaften sich nicht lesen lassen (hier: inzwischen verschwunden)
            FileLister = folder => Directory.EnumerateFiles(folder).Concat(
                string.Equals(folder, reise, StringComparison.OrdinalIgnoreCase) ? [Path.Combine(folder, "geist.jpg")] : []),
        });

        var result = await scan.ScanAsync(lib.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);

        result.Should().NotBeNull();
        (await fx.FindFileAsync("1.jpg")).Should().NotBeNull();
        (await fx.FindFileAsync("2.jpg")).Should().NotBeNull();
        (await fx.FindFileAsync("geist.jpg")).Should().BeNull("übersprungen — eine Datei verhindert keinen Neuaufbau");
    }

    /// <summary>Scanner, dessen Auflisten in <paramref name="failingFolder"/> nach der ersten Datei scheitert.</summary>
    private static FolderEnumerator Failing(string failingFolder)
        => new(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance)
        {
            FileLister = folder => string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(failingFolder), StringComparison.OrdinalIgnoreCase)
                ? FirstThenFail(folder)
                : Directory.EnumerateFiles(folder),
        };

    private static IEnumerable<string> FirstThenFail(string folder)
    {
        yield return Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.Ordinal).First();
        throw new IOException("Verbindung zu OneDrive unterbrochen (Test)");
    }

    private static ScanService CreateScanService(TempLibraryFixture fx, FolderEnumerator enumerator) => new(
        fx.ContextFactory,
        enumerator,
        new BulkScanWriter(),
        new FtsIndexer(),
        fx.CreateLibraryService(),
        NullLogger<ScanService>.Instance,
        fx.Messenger);
}
