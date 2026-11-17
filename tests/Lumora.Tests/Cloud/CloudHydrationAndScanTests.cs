using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Cloud;
using Lumora.Services.Media;
using Lumora.Services.Scanning;

namespace Lumora.Tests.Cloud;

/// <summary>
/// Kriterium 5 (Original laden mit Abbrechen, ohne Internet eine Meldung statt „fehlt“) und Kriterium 7 (Offline-vs-Missing:
/// nicht lesbar heißt nie fehlend) sowie der Scanner: Platzhalter (hier: Datei mit Offline-Attribut) werden eingelesen,
/// Junctions weiterhin nicht.
/// </summary>
public sealed class CloudHydrationAndScanTests
{
    [Fact]
    public async Task Original_laden_liest_die_Datei_ganz_mit_Fortschritt()
    {
        var dir = Directory.CreateTempSubdirectory("lumora-hydrate-");
        try
        {
            var path = Path.Combine(dir.FullName, "gross.jpg");
            await File.WriteAllBytesAsync(path, new byte[(3 << 20) + 17]);
            var reports = new List<double>();
            var progress = new SyncProgress(reports.Add);

            await new CloudFileHydrator().HydrateAsync(path, progress, CancellationToken.None);

            reports.Should().NotBeEmpty();
            reports[^1].Should().Be(1);
            reports.Should().BeInAscendingOrder();
            reports.Count.Should().BeGreaterThan(3, "Fortschritt je Block zu 1 MB");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Abbrechen_beendet_das_Laden()
    {
        var dir = Directory.CreateTempSubdirectory("lumora-hydrate-");
        try
        {
            var path = Path.Combine(dir.FullName, "a.jpg");
            await File.WriteAllBytesAsync(path, new byte[4 << 20]);
            using var cts = new CancellationTokenSource();
            var progress = new SyncProgress(p =>
            {
                if (p > 0)
                {
                    cts.Cancel(); // nach dem ersten Block
                }
            });

            var act = () => new CloudFileHydrator().HydrateAsync(path, progress, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Nicht_ladbar_wird_zu_CloudFileUnavailable_nie_fehlt()
    {
        var act = () => new CloudFileHydrator().HydrateAsync(Path.Combine(Path.GetTempPath(), "lumora-gibt-es-nicht-" + Guid.NewGuid() + ".jpg"), null, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CloudFileUnavailableException>();
        thrown.Which.Message.Should().Contain("nur in OneDrive").And.NotContain("fehlt");
    }

    [Fact]
    public void Nicht_lesbare_Ordner_decken_ihre_Dateien_in_jeder_Tiefe()
    {
        var unreadable = new UnreadablePaths();
        unreadable.CoversFolder(@"C:\Bib\A").Should().BeFalse("ohne Fehler wird nichts geschützt");

        unreadable.Add(@"C:\Bib\OneDrive\Reise\");
        unreadable.Add(@"C:\Bib\Einzeln\kaputt.jpg");

        unreadable.CoversFolder(@"C:\Bib\OneDrive\Reise").Should().BeTrue();
        unreadable.CoversFolder(@"c:\bib\onedrive\reise\tag 2").Should().BeTrue();
        unreadable.CoversFolder(@"C:\Bib\OneDrive\Reisen").Should().BeFalse("nur der Ordner selbst und darunter");
        unreadable.CoversFolder(@"C:\Bib\Einzeln").Should().BeFalse();
        unreadable.IsUnreadableFile(@"C:\Bib\Einzeln", "kaputt.jpg").Should().BeTrue();
        unreadable.IsUnreadableFile(@"C:\Bib\Einzeln", "heil.jpg").Should().BeFalse();

        var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Bib"] = 1, [@"C:\Bib\OneDrive"] = 2, [@"C:\Bib\OneDrive\Reise"] = 3, [@"C:\Bib\OneDrive\Reise\Tag 2"] = 4, [@"C:\Bib\Einzeln"] = 5,
        };
        unreadable.CoveredFolderIds(folders).Should().BeEquivalentTo([3, 4]);
    }

    [Fact]
    public void Scanner_liest_Platzhalter_ein_und_ueberspringt_Junctions()
    {
        var dir = Directory.CreateTempSubdirectory("lumora-scan-cloud-");
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(dir.FullName, "Reise"));
            var local = Path.Combine(sub.FullName, "lokal.jpg");
            var online = Path.Combine(sub.FullName, "online.jpg");
            File.WriteAllBytes(local, [1, 2, 3]);
            File.WriteAllBytes(online, [1, 2, 3, 4]);
            File.SetAttributes(online, File.GetAttributes(online) | FileAttributes.Offline);
            var linked = CloudFilesTests.CreateJunction(Path.Combine(dir.FullName, "Verknuepfung"), sub.FullName);

            var enumerator = new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance);
            var found = enumerator.Enumerate(dir.FullName).Select(f => Path.GetRelativePath(dir.FullName, f.FullPath)).OrderBy(p => p, StringComparer.Ordinal).ToList();

            found.Should().Equal(@"Reise\lokal.jpg", @"Reise\online.jpg");
            if (linked)
            {
                Directory.Delete(Path.Combine(dir.FullName, "Verknuepfung"));
            }

            File.SetAttributes(online, FileAttributes.Normal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Fortschritt ohne SynchronizationContext-Umweg (direkt im Lesethread).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
