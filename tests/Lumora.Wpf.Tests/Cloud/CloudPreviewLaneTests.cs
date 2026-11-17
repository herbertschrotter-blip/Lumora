using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Thumbnails.Cloud;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Cloud;

/// <summary>
/// Shell-Abrufe für Platzhalter (ADR-0077, Nachbesserung 0.5.41): Die Vorschau der Lightbox läuft auf eigenen STA-Threads
/// und wartet nie hinter Abrufen der Galerie (offline bis 30 s); in der versteckten Sitzung fragt Lumora die Shell nie
/// (Kriterium 8). Statt der Shell ein Test-Abruf auf denselben STA-Threads.
/// </summary>
public sealed class CloudPreviewLaneTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Lightbox_wartet_nicht_hinter_den_Abrufen_der_Galerie()
    {
        using var hanging = new ManualResetEventSlim(false);
        var started = 0;
        using var extractor = new ShellThumbnailExtractor(null, (path, _, ct) =>
        {
            if (path.StartsWith("galerie", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref started);
                hanging.Wait(ct); // wie ein Abruf ohne Verbindung
            }

            return Pixel();
        });

        var gallery = Enumerable.Range(0, ShellThumbnailExtractor.MaxConcurrency + 4)
            .Select(i => extractor.GetPreviewAsync($"galerie{i}.jpg", 1024, CancellationToken.None))
            .ToList();
        await WaitUntilAsync(() => Volatile.Read(ref started) == ShellThumbnailExtractor.MaxConcurrency);

        var preview = await extractor.Lightbox.GetPreviewAsync("lightbox.jpg", 1024, CancellationToken.None).WaitAsync(Timeout);

        preview.Should().NotBeNull("die Lightbox hat eigene Threads");
        gallery.Should().OnlyContain(t => !t.IsCompleted, "alle Galerie-Threads hängen noch");
        hanging.Set();
        await Task.WhenAll(gallery).WaitAsync(Timeout);
    }

    [Fact]
    public async Task Versteckte_Sitzung_ruft_die_Shell_nie()
    {
        var calls = 0;
        var catalog = new TestCatalogContext();
        catalog.SetHidden(true);
        using var extractor = new ShellThumbnailExtractor(catalog, (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Pixel();
        });

        extractor.IsAvailable.Should().BeFalse();
        (await extractor.GetPreviewAsync("a.jpg", 1024, CancellationToken.None)).Should().BeNull();
        (await extractor.Lightbox.GetPreviewAsync("a.jpg", 1024, CancellationToken.None)).Should().BeNull();
        new ShellCloudThumbnailRenderer(extractor).IsAvailable.Should().BeFalse("der Vorschaubild-Dienst meldet cloud_unavailable, statt die Shell zu fragen");
        calls.Should().Be(0, "keine Spuren im Vorschaubild-Cache von Windows");

        catalog.SetHidden(false);
        (await extractor.Lightbox.GetPreviewAsync("a.jpg", 1024, CancellationToken.None)).Should().NotBeNull();
        calls.Should().Be(1, "nach dem Schließen der versteckten Sitzung wieder über die Shell");
    }

    private static BitmapSource Pixel()
    {
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[3], 3);
        pixel.Freeze();
        return pixel;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "Bedingung nicht rechtzeitig erfüllt");
            await Task.Delay(10);
        }
    }
}
