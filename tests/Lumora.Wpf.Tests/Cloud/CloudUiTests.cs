using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.PhotoMenu;
using Lumora.Services.Cloud;
using Lumora.Services.Thumbnails;
using Lumora.Thumbnails.Cloud;
using Lumora.Wpf.Tests.Fixtures;
using static Lumora.Wpf.Tests.PhotoMenu.PhotoMenuTestKit;

namespace Lumora.Wpf.Tests.Cloud;

/// <summary>
/// OneDrive-Bibliothek in der Oberfläche (ADR-0077, Mockup 27) mit Fakes (keine echte Shell, kein OneDrive):
/// Kriterium 2 — Shell-Vorschau auf Lumoras Vorschaubild-Größe verkleinert und atomar geschrieben, Platzhalter nur
/// sichtbar plus Vorlauf; Kriterium 6 — Wolken-Marke auf der Kachel, Rechtsklick „Immer auf diesem Gerät behalten“ und
/// „Speicherplatz freigeben“ nur für Dateien in OneDrive.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CloudUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Renderer_verkleinert_die_Shell_Vorschau_auf_die_Vorschaubild_Groesse()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            TestUiThread.Run(() =>
            {
                var target = Path.Combine(dir, "thumbs", "42_400_still.jpg");
                var source = new FakePreviewSource { Preview = Frozen(TestImages.LeftRedRightBlue(1024, 768)) };
                var renderer = new ShellCloudThumbnailRenderer(source);

                var result = renderer.RenderAsync(@"C:\OneDrive\a.jpg", target, ThumbnailSizing.ShortEdge, CancellationToken.None).GetAwaiter().GetResult();

                result.Success.Should().BeTrue(result.ErrorMessage);
                (result.Width, result.Height).Should().Be((533, 400));
                result.SourceWidth.Should().Be(0, "die Maße des Originals kennt die Shell-Vorschau nicht");
                source.Requests.Should().Equal((@"C:\OneDrive\a.jpg", ShellCloudThumbnailRenderer.RequestBox));
                File.Exists(target).Should().BeTrue();
                File.Exists(target + ".tmp").Should().BeFalse("atomar geschrieben");
                var written = TestImages.Load(target);
                (written.Width, written.Height).Should().Be((533, 400));
                return 0;
            }, Timeout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Renderer_skaliert_kleine_Vorschau_nicht_hoch_und_meldet_fehlende_als_Cloud_Fehler()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            TestUiThread.Run(() =>
            {
                var small = new ShellCloudThumbnailRenderer(new FakePreviewSource { Preview = Frozen(TestImages.LeftRedRightBlue(300, 200)) })
                    .RenderAsync("x", Path.Combine(dir, "a.jpg"), 400, CancellationToken.None).GetAwaiter().GetResult();
                (small.Width, small.Height).Should().Be((300, 200), "nie hochskalieren (ADR-0043)");

                var none = new ShellCloudThumbnailRenderer(new FakePreviewSource())
                    .RenderAsync("x", Path.Combine(dir, "b.jpg"), 400, CancellationToken.None).GetAwaiter().GetResult();
                none.ErrorCode.Should().Be(ThumbnailErrorCodes.CloudUnavailable);
                File.Exists(Path.Combine(dir, "b.jpg")).Should().BeFalse();

                var broken = new ShellCloudThumbnailRenderer(new FakePreviewSource { Throw = new InvalidOperationException("Shell") })
                    .RenderAsync("x", Path.Combine(dir, "c.jpg"), 400, CancellationToken.None).GetAwaiter().GetResult();
                broken.ErrorCode.Should().Be(ThumbnailErrorCodes.CloudUnavailable);
                return 0;
            }, Timeout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Platzhalter_nur_sichtbar_plus_Vorlauf_sichtbare_zuerst()
    {
        var page = Enumerable.Range(0, 60).Select(i => Tile(i, cloudOnly: i % 2 == 0)).ToList();
        var pixel = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, new byte[3], 3);
        pixel.Freeze();
        page[20].Thumbnail = pixel; // hat schon ein Vorschaubild

        var wanted = GalleryViewModel.CloudItemsInView(page, first: 20, last: 29, lookahead: 4).Select(i => i.Id).ToList();

        wanted.Should().Equal(22, 24, 26, 28, 30, 18, 32, 16); // sichtbare, dann nach Abstand
        wanted.Should().NotContain(20, "schon da");
        GalleryViewModel.CloudItemsInView(page, -1, -1, lookahead: 3).Select(i => i.Id).Should().Equal(0, 2, 4);
        GalleryViewModel.CloudItemsInView([], 0, 5, 3).Should().BeEmpty();
    }

    [Fact]
    public void Galerie_setzt_den_Cloud_Zustand_aus_dem_Dienst()
    {
        var cloud = new FakeCloudFiles();
        cloud.States[@"C:\OneDrive\0.jpg"] = new CloudFileState(true, true, false, false);
        cloud.States[@"C:\OneDrive\1.jpg"] = new CloudFileState(true, false, true, false);
        var items = new[] { Tile(0, false, @"C:\OneDrive"), Tile(1, false, @"C:\OneDrive"), Tile(2, false, @"C:\Fotos"), Tile(3, false, @"\\nas\fotos") };

        var states = GalleryViewModel.ClassifyCloud(cloud, items);
        foreach (var (item, state) in states)
        {
            item.ApplyCloudState(state);
        }

        items[0].IsCloudOnly.Should().BeTrue();
        items[0].IsCloudFile.Should().BeTrue();
        items[1].IsCloudOnly.Should().BeFalse();
        items[1].IsCloudFile.Should().BeTrue("„Immer behalten“ — lokal, aber in OneDrive (Menü)");
        items[2].IsCloudFile.Should().BeFalse();
        cloud.Asked.Should().NotContain(@"\\nas\fotos\3.jpg", "auf Netzlaufwerken gibt es keine Platzhalter — kein Zugriff");
    }

    [Fact]
    public void Rechtsklick_Datei_zeigt_Behalten_und_Freigeben_nur_fuer_OneDrive()
    {
        TestUiThread.Run(() =>
        {
            var kept = 0;
            var freed = 0;
            var menu = new ContextMenu();
            PhotoContextMenuBuilder.Build(menu.Items, new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7]),
                GalleryHost() with { KeepOnDevice = () => kept++, FreeUpSpace = () => freed++ }, Actions());

            var file = Item(menu.Items, "Datei");
            var headers = Headers(file.Items);
            headers.Should().ContainInOrder("In die Bibliothek…", "—", "Immer auf diesem Gerät behalten", "Speicherplatz freigeben", "—", "Im Explorer anzeigen");
            Item(file.Items, "Immer auf diesem Gerät behalten").Command.Execute(null);
            Item(file.Items, "Speicherplatz freigeben").Command.Execute(null);
            (kept, freed).Should().Be((1, 1));

            var local = new ContextMenu();
            PhotoContextMenuBuilder.Build(local.Items, new PhotoMenuContext(PhotoMenuSource.Gallery, Jpeg(), [7]), GalleryHost(), Actions());
            Headers(Item(local.Items, "Datei").Items).Should().NotContain(["Immer auf diesem Gerät behalten", "Speicherplatz freigeben"]);
            return 0;
        }, Timeout);
    }

    [Fact]
    public void Collage_Kachel_zeigt_die_Wolken_Marke_nur_fuer_nur_online()
    {
        TestUiThread.Run(() =>
        {
            using var host = new CollageGridHost(3);
            host.Items[0].IsCloudOnly = true;
            host.ShowAndLayout();

            MarkOf(host, 0)!.Visibility.Should().Be(Visibility.Visible);
            MarkOf(host, 0)!.ToolTip.Should().Be(GalleryItem.CloudOnlyToolTip);
            MarkOf(host, 1)!.Visibility.Should().Be(Visibility.Collapsed);
            host.Items[1].IsCloudOnly = true;
            host.Window.UpdateLayout();
            MarkOf(host, 1)!.Visibility.Should().Be(Visibility.Visible);
            return 0;
        }, Timeout);
    }

    private static FrameworkElement? MarkOf(CollageGridHost host, int index)
    {
        var container = host.ContainerOf(index)!;
        var presenter = GalleryGridHost.FindVisualChild<ContentPresenter>(container)!;
        return host.CollageTemplate.FindName("CloudMark", presenter) as FrameworkElement;
    }

    private static BitmapSource Frozen(BitmapSource source)
    {
        if (!source.IsFrozen)
        {
            source.Freeze();
        }

        return source;
    }

    private static GalleryItem Tile(int id, bool cloudOnly, string folder = @"C:\OneDrive")
    {
        var item = new GalleryItem(new GalleryFileDto(id, 1, $"{id}.jpg", ".jpg", 1000, 1_700_000_000, 0, false, null, false, folder));
        item.IsCloudOnly = cloudOnly;
        return item;
    }

    internal sealed class FakePreviewSource : ICloudPreviewSource
    {
        public BitmapSource? Preview { get; init; }

        public Exception? Throw { get; init; }

        public List<(string Path, int Box)> Requests { get; } = new();

        public Task<BitmapSource?> GetPreviewAsync(string path, int boxSize, CancellationToken ct)
        {
            Requests.Add((path, boxSize));
            return Throw is not null ? Task.FromException<BitmapSource?>(Throw) : Task.FromResult(Preview);
        }
    }

    internal sealed class FakeCloudFiles : ICloudFileService
    {
        public Dictionary<string, CloudFileState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Asked { get; } = new();

        public List<(IReadOnlyList<CloudFileRef> Files, CloudPinAction Action)> Pins { get; } = new();

        public List<CloudFileRef> BecameLocal { get; } = new();

        public CloudFileState GetState(string path)
        {
            lock (Asked)
            {
                Asked.Add(path);
            }

            return States.TryGetValue(path, out var state) ? state : CloudFileState.NotCloud;
        }

        public bool MayBeCloud(string path) => path.StartsWith(@"C:\OneDrive", StringComparison.OrdinalIgnoreCase);

        public CloudPinResult SetPinState(IReadOnlyList<CloudFileRef> files, CloudPinAction action)
        {
            Pins.Add((files, action));
            return new CloudPinResult(action, files.Count, 0, 0);
        }

        public void NotifyBecameLocal(CloudFileRef file) => BecameLocal.Add(file);
    }
}
