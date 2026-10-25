using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Services.Enhance;
using Lumora.Services.Faces;
using Lumora.Services.ModelPackages;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Enhance;

/// <summary>Bausteine von „Foto verbessern“ (ADR-0071): Bildoperationen, Automatisch, Dateinamen, EXIF, Einstellungen, Modell-Register.</summary>
public sealed class EnhanceBuildingBlocksTests
{
    private static EnhanceImage Numbered(int w, int h)
    {
        var img = EnhanceImage.Blank(w, h);
        for (var i = 0; i < w * h; i++)
        {
            img.Pixels[i * 3] = (byte)i;
        }

        return img;
    }

    [Fact]
    public void Drehen_im_Uhrzeigersinn_und_zurueck()
    {
        var src = Numbered(3, 2); // 0 1 2 / 3 4 5
        var r90 = EnhanceImageOps.Rotate(src, 90);
        (r90.Width, r90.Height).Should().Be((2, 3));
        r90.Pixels.Where((_, i) => i % 3 == 0).Should().Equal(3, 0, 4, 1, 5, 2);
        EnhanceImageOps.Rotate(EnhanceImageOps.Rotate(r90, 180), 90).Pixels.Should().Equal(src.Pixels);
        EnhanceImageOps.Rotate(src, 0).Should().BeSameAs(src);
    }

    [Fact]
    public void Zuschnitt_mittig_und_relativ()
    {
        var crop = EnhanceImageOps.CenteredCrop(400, 200, 1.0);
        crop.Should().Be(new EnhanceCrop(0.25, 0, 0.5, 1));
        var tall = EnhanceImageOps.CenteredCrop(200, 400, 16.0 / 9);
        tall.Width.Should().Be(1);
        tall.Height.Should().BeApproximately(200.0 / 400 / (16.0 / 9) * 1, 0.0001);
        var cut = EnhanceImageOps.Crop(Numbered(4, 2), new EnhanceCrop(0.5, 0, 0.5, 1));
        (cut.Width, cut.Height).Should().Be((2, 2));
        cut.Pixels.Where((_, i) => i % 3 == 0).Should().Equal(2, 3, 6, 7);
    }

    [Fact]
    public void Licht_hellt_auf_und_Kontrast_spreizt_um_die_Mitte()
    {
        var gray = FakeEnhanceCodec.Gray(2, 1, 100);
        EnhanceImageOps.AdjustLight(gray, 0.5, 0).Pixels[0].Should().Be(148);
        var mixed = new EnhanceImage([60, 60, 60, 200, 200, 200], 2, 1);
        var contrast = EnhanceImageOps.AdjustLight(mixed, 0, 0.5);
        contrast.Pixels[0].Should().BeLessThan(60);
        contrast.Pixels[3].Should().BeGreaterThan(200);
        mixed.Pixels[0].Should().Be(60, "das Eingangsbild bleibt unverändert");
    }

    [Fact]
    public void Skalieren_verkleinert_mit_Mittel_und_vergroessert_bilinear()
    {
        var img = new EnhanceImage([0, 0, 0, 200, 200, 200, 0, 0, 0, 200, 200, 200], 2, 2);
        var half = EnhanceImageOps.Resize(img, 1, 1);
        half.Pixels[0].Should().Be(100);
        var up = EnhanceImageOps.Resize(img, 4, 4);
        (up.Width, up.Height).Should().Be((4, 4));
        var (fw, fh) = EnhanceImageOps.FitPixels(10_000, 10_000, 64_000_000);
        ((long)fw * fh).Should().BeLessThanOrEqualTo(64_000_000);
        fw.Should().BeInRange(7990, 8000);
        EnhanceImageOps.FitPixels(100, 50, 64_000_000).Should().Be((100, 50));
    }

    [Fact]
    public void Graustufen_erkannt_Farbe_nicht()
    {
        EnhanceImageOps.IsGrayscale(FakeEnhanceCodec.Gray(50, 50)).Should().BeTrue();
        EnhanceImageOps.IsGrayscale(FakeEnhanceCodec.Gradient(50, 50)).Should().BeFalse();
    }

    [Fact]
    public void Lab_hin_und_zurueck_und_Farbe_mit_Helligkeit_des_Originals()
    {
        var (l, a, b) = EnhanceImageOps.RgbToLab(200, 120, 40);
        var (r, g, bl) = EnhanceImageOps.LabToRgb(l, a, b);
        ((int)r).Should().BeInRange(199, 201);
        ((int)g).Should().BeInRange(119, 121);
        ((int)bl).Should().BeInRange(39, 41);

        var gray = FakeEnhanceCodec.Gray(4, 4, 128);
        var red = new EnhanceImage(Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 220, 40, 40 }).ToArray(), 4, 4);
        var merged = EnhanceImageOps.MergeLabColor(gray, red);
        merged.Pixels[0].Should().BeGreaterThan(merged.Pixels[1], "Farbe aus dem Modell");
        EnhanceImageOps.RgbToLab(merged.Pixels[0], merged.Pixels[1], merged.Pixels[2]).L
            .Should().BeApproximately(EnhanceImageOps.RgbToLab(128, 128, 128).L, 1.5, "Helligkeit aus dem Original");
    }

    [Fact]
    public void Pinselstriche_werden_zur_Maske_und_Kratzer_auf_ruhigem_Grund_gefunden()
    {
        var mask = EnhanceImageOps.RasterizeDots(100, 50, [new EnhanceBrushDot(0.5, 0.5, 0.05)]);
        mask[25 * 100 + 50].Should().Be(255);
        mask[0].Should().Be(0);
        EnhanceImageOps.Bounds(mask, 100, 50)!.Value.Width.Should().BeInRange(9, 11);

        var scan = FakeEnhanceCodec.Gray(80, 60, 120);
        for (var y = 5; y < 55; y++)
        {
            var o = (y * 80 + 40) * 3;
            scan.Pixels[o] = scan.Pixels[o + 1] = scan.Pixels[o + 2] = 250; // heller Kratzer
        }

        var scratches = EnhanceImageOps.DetectScratches(scan);
        scratches[30 * 80 + 40].Should().Be(255);
        scratches[30 * 80 + 10].Should().Be(0);

        var noise = EnhanceImage.Blank(80, 60);
        new Random(3).NextBytes(noise.Pixels);
        EnhanceImageOps.DetectScratches(noise).Count(v => v != 0).Should().BeLessThan(80 * 60 / 20, "Struktur ist (fast) nie ein Kratzer");
        var edge = FakeEnhanceCodec.Gray(80, 60, 40);
        for (var i = 0; i < 80 * 60; i++)
        {
            if (i % 80 >= 40)
            {
                edge.Pixels[i * 3] = edge.Pixels[i * 3 + 1] = edge.Pixels[i * 3 + 2] = 220;
            }
        }

        EnhanceImageOps.DetectScratches(edge).Should().OnlyContain(v => v == 0, "eine Kante ist kein Kratzer");
    }

    [Fact]
    public void Gesicht_wird_mit_Staerke_eingeblendet()
    {
        var target = FakeEnhanceCodec.Gray(64, 64, 0);
        var crop = FakeEnhanceCodec.Gray(32, 32, 200);
        var toCrop = Lumora.Services.Faces.Alignment.FaceAligner.Estimate(
            [new FacePoint(16, 16), new FacePoint(48, 16), new FacePoint(32, 48)],
            [new FacePoint(0, 0), new FacePoint(32, 0), new FacePoint(16, 32)]);
        EnhanceImageOps.PasteFace(target, crop, toCrop, 0.5);
        target.Pixels[(32 * 64 + 32) * 3].Should().BeInRange(95, 105, "Mitte: halbe Stärke");
        target.Pixels[0].Should().Be(0, "außerhalb des Gesichts unverändert");
    }

    [Fact]
    public void Automatisch_waehlt_nach_Regeln_und_nur_mit_geladenen_Modellen()
    {
        var all = new HashSet<EnhanceTool> { EnhanceTool.Upscale, EnhanceTool.Faces, EnhanceTool.Colorize, EnhanceTool.Erase };
        var smallFace = new[] { new FacePoint(10, 10), new FacePoint(40, 10), new FacePoint(25, 25), new FacePoint(15, 35), new FacePoint(35, 35) };

        var scan = EnhanceAutoPlanner.Decide(new EnhanceOptions { Auto = true }, FakeEnhanceCodec.Gray(800, 600), all, [smallFace]);
        scan.Options.Should().Match<EnhanceOptions>(o => o.Colorize && o.Erase && o.Scratches && o.Faces && o.Upscale && o.UpscaleFactor == 4 && !o.Auto);

        var large = EnhanceAutoPlanner.Decide(new EnhanceOptions { Auto = true, Brightness = 0.2 }, FakeEnhanceCodec.Gradient(2400, 1600), all, []);
        large.Options.Should().Match<EnhanceOptions>(o => !o.Colorize && !o.Upscale && o.Brightness == 0.2);
        large.HasWork.Should().BeTrue("Licht bleibt");

        var none = EnhanceAutoPlanner.Decide(new EnhanceOptions { Auto = true }, FakeEnhanceCodec.Gray(1200, 900), new HashSet<EnhanceTool>(), null);
        none.HasWork.Should().BeFalse();
        none.Notes.Should().Contain(n => n.Contains("Einfärben", StringComparison.Ordinal)).And.Contain(n => n.Contains("Hochskalieren", StringComparison.Ordinal));
        EnhanceAutoPlanner.Decide(new EnhanceOptions { Auto = true }, FakeEnhanceCodec.Gradient(1500, 1000), all, []).Options.UpscaleFactor.Should().Be(2);
    }

    [Theory]
    [InlineData("Garten.jpg", null, "Garten_verbessert.jpg")]
    [InlineData("Garten.PNG", "{name}-hd", "Garten-hd.jpg")]
    [InlineData("Garten.jpg", "_neu", "Garten_neu.jpg")]
    [InlineData("Garten.jpg", "{name}", "Garten_verbessert.jpg")]
    [InlineData("Garten.jpg", "{name}:<x>", "Garten__x_.jpg")]
    public void Dateiname_aus_dem_Muster_nie_der_Name_des_Originals(string original, string? pattern, string expected)
        => EnhanceNaming.FileName(original, pattern).Should().Be(expected);

    [Fact]
    public void Exif_Kopierer_ersetzt_vorhandenen_Block_und_laesst_Bilddaten_unberuehrt()
    {
        var source = EnhanceWorld.JpegWithExif(FakeEnhanceCodec.Gradient(4, 4), orientation: 8);
        var target = EnhanceWorld.JpegWithExif(FakeEnhanceCodec.Gray(4, 4), orientation: 3);
        var exif = JpegExifCopier.ReadExifSegment(source)!;

        var merged = JpegExifCopier.Insert(target, exif)!;

        JpegExifCopier.ReadExifSegment(merged)![28].Should().Be(1);
        CountOf(merged, "Exif\0\0"u8.ToArray()).Should().Be(1, "der alte Block des Ziels ist ersetzt");
        merged.AsSpan(merged.Length - (4 * 4 * 3) - 2).ToArray().Should().Equal(target.AsSpan(target.Length - (4 * 4 * 3) - 2).ToArray());
        JpegExifCopier.Insert([1, 2, 3, 4], exif).Should().BeNull("kein JPEG");
        JpegExifCopier.ReadExifSegment(FakeEnhanceCodec.Encode(FakeEnhanceCodec.Gray(2, 2))).Should().BeNull();
    }

    private static int CountOf(byte[] data, byte[] pattern)
    {
        var count = 0;
        for (var i = 0; i + pattern.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public async Task Einstellungen_werden_gespeichert_und_melden_sich()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var store = new EnhanceSettingsStore(fx.ContextFactory, fx.Messenger);
        EnhanceSettingsChangedMessage? message = null;
        fx.Messenger.Register<EnhanceSettingsChangedMessage>(this, (_, m) => message = m);

        (await store.LoadAsync()).Should().Be(EnhanceSettings.Default);
        var changed = new EnhanceSettings(EnhanceSaveLocation.Subfolder, "Bessere", "{name} (hd)", false, false, true, false);
        await store.SaveAsync(changed);

        (await store.LoadAsync()).Should().Be(changed);
        message!.Settings.ShowBestVariant.Should().BeTrue();
        await store.SaveAsync(changed with { SubfolderName = "..\\weg", FileNamePattern = "  " });
        (await store.LoadAsync()).Should().Match<EnhanceSettings>(s => s.SubfolderName == "weg" && s.FileNamePattern == EnhanceSettings.DefaultPattern);
    }

    [Fact]
    public void Modell_Register_mit_festen_Commits_Groessen_Pruefsummen_und_freien_Lizenzen()
    {
        EnhanceModelCatalog.All.Select(e => e.Definition.Id).Should().Equal(
            EnhanceModelCatalog.UpscaleId, EnhanceModelCatalog.FacesId, EnhanceModelCatalog.ColorizeId, EnhanceModelCatalog.InpaintId);
        foreach (var (definition, baseUri) in EnhanceModelCatalog.All)
        {
            baseUri.Scheme.Should().Be("https");
            Regex.IsMatch(baseUri.AbsoluteUri, "/resolve/[0-9a-f]{40}/$").Should().BeTrue("fester Commit statt main: " + baseUri);
            definition.Group.Should().Be(ModelPackageGroup.Model);
            definition.LicenseRestricted.Should().BeFalse("CodeFormer (nur nicht-kommerziell) ist bewusst nicht dabei");
            definition.FileList.Should().ContainSingle();
            definition.FileList[0].Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
            definition.DownloadSize.Should().Be(definition.InstalledSize).And.BeGreaterThan(50_000_000);
            definition.Origin.Website!.Host.Should().Be("github.com");
        }
    }

    [Fact]
    public async Task Modelle_sind_im_Manager_registriert_und_Bereitschaft_kommt_von_dort()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumora-enhance-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IModelStorage>(new ModelStorage(root));
            services.AddSingleton<IModelDownloader>(new RefusingDownloader());
            services.AddSingleton<IModelMetadataClient>(new RefusingDownloader());
            foreach (var (definition, baseUri) in EnhanceModelCatalog.All)
            {
                services.AddFileSetModelPackage(definition, baseUri);
            }

            services.AddSingleton<IModelPackageManager>(sp => new ModelPackageManager(sp.GetServices<IModelPackageSource>(), sp.GetRequiredService<IModelMetadataClient>()));
            await using var provider = services.BuildServiceProvider();
            var manager = provider.GetRequiredService<IModelPackageManager>();
            var access = new EnhanceModelAccess(manager);

            manager.Packages.Select(p => p.Definition.Id).Should().HaveCount(4);
            (await access.GetStateAsync(EnhanceTool.Upscale, default)).Should().Be(EnhanceModelState.NotInstalled);
            (await access.GetStateAsync(EnhanceTool.Adjust, default)).Should().Be(EnhanceModelState.Ready, "Zuschneiden/Licht braucht kein Modell");
            access.GetModelPath(EnhanceTool.Erase).Should().Be(Path.Combine(root, "enhance-inpaint", EnhanceModelCatalog.InpaintFile));

            // „Geladen“ = Datei mit passender Größe (Prüfsumme nur beim Laden/Prüfen)
            var path = access.GetModelPath(EnhanceTool.Upscale)!;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(path))
            {
                fs.SetLength(EnhanceModelCatalog.Upscale.DownloadSize);
            }

            (await access.GetStateAsync(EnhanceTool.Upscale, default)).Should().Be(EnhanceModelState.Ready);
            (await new EnhanceModelAccess(new ModelPackageManager([], new RefusingDownloader())).GetStateAsync(EnhanceTool.Colorize, default))
                .Should().Be(EnhanceModelState.NoSource, "nicht registriert = Quelle folgt");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Kein Netz: jeder Abruf scheitert.</summary>
    private sealed class RefusingDownloader : IModelDownloader, IModelMetadataClient
    {
        public Task DownloadAsync(Uri source, string target, long expectedSize, IProgress<long>? progress, CancellationToken ct)
            => throw new InvalidOperationException("kein Download im Test");

        public Task<string> GetStringAsync(Uri uri, CancellationToken ct) => throw new InvalidOperationException("kein Netz im Test");
    }
}
