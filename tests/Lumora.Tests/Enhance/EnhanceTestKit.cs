using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Activity;
using Lumora.Services.Enhance;
using Lumora.Services.Faces;
using Lumora.Services.Infrastructure;
using Lumora.Services.Operations;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Enhance;

/// <summary>
/// Bild-Codec ohne WIC für „Foto verbessern“: schreibt JPEG-förmige Dateien (SOI, APP0, SOS, Rohdaten mit Kennung „LUMF“,
/// EOI) — genug für den EXIF-Kopierer (Segmente vor SOS) und zum Zurücklesen. Unbekannte Dateien (Testbibliothek mit
/// Dummy-Bytes) werden als festes Bild <see cref="DefaultWidth"/> × <see cref="DefaultHeight"/> gelesen.
/// </summary>
public sealed class FakeEnhanceCodec : IEnhanceImageCodec
{
    public const int DefaultWidth = 64;
    public const int DefaultHeight = 48;
    private static readonly byte[] Magic = "LUMF"u8.ToArray();

    /// <summary>Wenn gesetzt, liefert Load dieses Bild (z. B. Graustufen für „Automatisch“).</summary>
    public EnhanceImage? NextImage { get; set; }

    public List<string> Written { get; } = [];

    public EnhanceImage Load(string path, int maxEdge)
    {
        if (NextImage is { } next)
        {
            return next.Clone();
        }

        var bytes = File.ReadAllBytes(path);
        var at = IndexOf(bytes, Magic);
        if (at < 0)
        {
            return Gradient(DefaultWidth, DefaultHeight);
        }

        var w = BitConverter.ToInt32(bytes, at + 4);
        var h = BitConverter.ToInt32(bytes, at + 8);
        var pixels = bytes.AsSpan(at + 12, w * h * 3).ToArray();
        return new EnhanceImage(pixels, w, h);
    }

    public void SaveJpeg(EnhanceImage image, string path, int quality)
    {
        File.WriteAllBytes(path, Encode(image));
        lock (Written)
        {
            Written.Add(path);
        }
    }

    public static byte[] Encode(EnhanceImage image)
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        ms.Write([0xFF, 0xDA, 0x00, 0x02]);
        ms.Write(Magic);
        ms.Write(BitConverter.GetBytes(image.Width));
        ms.Write(BitConverter.GetBytes(image.Height));
        ms.Write(image.Pixels);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    public static EnhanceImage Gradient(int w, int h)
    {
        var img = EnhanceImage.Blank(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 3;
                img.Pixels[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                img.Pixels[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                img.Pixels[o + 2] = 90;
            }
        }

        return img;
    }

    public static EnhanceImage Gray(int w, int h, byte level = 128)
    {
        var img = EnhanceImage.Blank(w, h);
        Array.Fill(img.Pixels, level);
        return img;
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Fake-Modelle: Hochskalieren = nächster Nachbar, Einfärben = rot getönt, Gesichter/Entfernen = Bild mit Marke.</summary>
public sealed class FakeEngineFactory : IEnhanceEngineFactory
{
    public List<EnhanceTool> Created { get; } = [];

    public List<EnhanceTool> Processed { get; } = [];

    public int Disposed;

    /// <summary>Vor jedem Lauf (z. B. blockieren, abbrechen).</summary>
    public Action<EnhanceTool, CancellationToken>? BeforeProcess { get; set; }

    public IEnhanceEngine Create(EnhanceTool tool, string modelPath, bool useGpu)
    {
        lock (Created)
        {
            Created.Add(tool);
        }

        return new FakeEngine(this, tool);
    }

    private sealed class FakeEngine(FakeEngineFactory owner, EnhanceTool tool) : IEnhanceEngine
    {
        public EnhanceTool Tool => tool;

        public string ExecutionProvider => "Fake";

        public EnhanceImage Process(EnhanceStepInput input, IProgress<double>? progress, CancellationToken ct)
        {
            owner.BeforeProcess?.Invoke(tool, ct);
            ct.ThrowIfCancellationRequested();
            lock (owner.Processed)
            {
                owner.Processed.Add(tool);
            }

            var src = input.Image;
            switch (tool)
            {
                case var upscale when EnhanceUpscaleModels.IsEngine(upscale):
                    var f = input.Factor;
                    var dst = EnhanceImage.Blank(src.Width * f, src.Height * f);
                    for (var y = 0; y < dst.Height; y++)
                    {
                        for (var x = 0; x < dst.Width; x++)
                        {
                            Buffer.BlockCopy(src.Pixels, ((y / f) * src.Width + x / f) * 3, dst.Pixels, (y * dst.Width + x) * 3, 3);
                        }
                    }

                    return dst;
                case EnhanceTool.Colorize:
                    var c = src.Clone();
                    for (var i = 0; i < c.Pixels.Length; i += 3)
                    {
                        c.Pixels[i] = (byte)Math.Min(255, c.Pixels[i] + 60);
                    }

                    return c;
                default:
                    var m = src.Clone();
                    m.Pixels[0] = 7;
                    return m;
            }
        }

        public void Dispose() => Interlocked.Increment(ref owner.Disposed);
    }
}

/// <summary>Werkzeuge mit „geladenem“ Modell.</summary>
public sealed class FakeModelAccess(params EnhanceTool[] ready) : IEnhanceModelAccess
{
    public HashSet<EnhanceTool> Ready { get; } = [.. ready];

    public Task<EnhanceModelState> GetStateAsync(EnhanceTool tool, CancellationToken ct)
        => Task.FromResult(EnhanceModelCatalog.ModelIdOf(tool) is null || Ready.Contains(tool) ? EnhanceModelState.Ready : EnhanceModelState.NotInstalled);

    public string? GetModelPath(EnhanceTool tool) => EnhanceModelCatalog.FileOf(tool);
}

/// <summary>Gesichter an festen Stellen.</summary>
public sealed class FakeFaceLocator(params FacePoint[][] faces) : IEnhanceFaceLocator
{
    public bool IsAvailable { get; set; } = true;

    public IReadOnlyList<FacePoint[]> Locate(EnhanceImage image, CancellationToken ct) => faces;
}

/// <summary>Speicherorte des Katalogs (Temp-Ordner) für Tests; <see cref="IsHidden"/> simuliert die versteckte Sitzung.</summary>
public sealed class TestStoragePaths(string root, bool hidden = false) : ICatalogStoragePaths
{
    public bool IsHidden { get; } = hidden;

    public string CacheRoot { get; } = Path.Combine(root, "cache");

    public string TempRoot { get; } = Path.Combine(root, "temp");
}

/// <summary>Aufbau eines <see cref="PhotoEnhanceService"/> auf einer Test-Bibliothek.</summary>
public sealed class EnhanceWorld : IAsyncDisposable
{
    private EnhanceWorld(TempLibraryFixture fx)
    {
        Fx = fx;
    }

    public TempLibraryFixture Fx { get; }

    public int LibraryId { get; private set; }

    public FileOperationService Ops { get; private set; } = null!;

    public FakeRecycleBin Bin { get; } = new() { DeleteFiles = true };

    public FakeEnhanceCodec Codec { get; } = new();

    public FakeEngineFactory Engines { get; } = new();

    public FakeModelAccess Models { get; private set; } = new();

    public EnhanceSettingsStore Settings { get; private set; } = null!;

    public ActivityCenter Activities { get; } = new();

    public TestStoragePaths Storage { get; private set; } = null!;

    public PhotoEnhanceService Service { get; private set; } = null!;

    public static async Task<EnhanceWorld> CreateAsync(string[] files, EnhanceTool[]? ready = null, bool hidden = false, IEnhanceFaceLocator? faces = null)
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var world = new EnhanceWorld(fx);
        var library = await fx.CreateLibraryAsync();
        world.LibraryId = library.Id;
        foreach (var file in files)
        {
            fx.WriteLibraryFile(file);
        }

        await fx.ScanAsync(library.Id);
        world.Ops = fx.CreateFileOperationService(world.Bin);
        world.Models = new FakeModelAccess(ready ?? [EnhanceTool.Upscale]);
        world.Settings = new EnhanceSettingsStore(fx.ContextFactory, fx.Messenger);
        // versteckte Sitzung: Temp-Ordner im Container (Bibliotheksordner\.lumora), sonst im AppData des Tests
        world.Storage = new TestStoragePaths(hidden ? Path.Combine(fx.LibraryRoot, ".lumora") : Path.Combine(fx.RootDir, "appdata"), hidden);
        world.Service = new PhotoEnhanceService(
            fx.ContextFactory,
            world.Storage,
            world.Codec,
            world.Engines,
            world.Models,
            world.Settings,
            world.Ops,
            new GpuGate(),
            faces ?? new FakeFaceLocator(),
            world.Activities,
            NullLogger<PhotoEnhanceService>.Instance,
            fx.Messenger);
        return world;
    }

    public async Task<int> IdAsync(string name) => (await Fx.FindFileAsync(name))!.Id;

    public string PathOf(params string[] parts) => Path.Combine([Fx.LibraryRoot, .. parts]);

    public ValueTask DisposeAsync()
    {
        Service.Dispose();
        Ops.Dispose();
        Activities.Dispose();
        Fx.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>JPEG-förmige Datei mit EXIF (Ausrichtung 6, Aufnahmedatum) — als „Original“ mit Metadaten.</summary>
    public static byte[] JpegWithExif(EnhanceImage image, ushort orientation = 6)
    {
        var plain = FakeEnhanceCodec.Encode(image);
        var tiff = new List<byte>();
        tiff.AddRange("II"u8.ToArray());
        tiff.AddRange(BitConverter.GetBytes((ushort)42));
        tiff.AddRange(BitConverter.GetBytes(8u));
        tiff.AddRange(BitConverter.GetBytes((ushort)2)); // zwei Einträge
        tiff.AddRange(BitConverter.GetBytes((ushort)0x0112));
        tiff.AddRange(BitConverter.GetBytes((ushort)3)); // SHORT
        tiff.AddRange(BitConverter.GetBytes(1u));
        tiff.AddRange(BitConverter.GetBytes(orientation));
        tiff.AddRange(BitConverter.GetBytes((ushort)0));
        tiff.AddRange(BitConverter.GetBytes((ushort)0x0132)); // DateTime
        tiff.AddRange(BitConverter.GetBytes((ushort)2)); // ASCII
        tiff.AddRange(BitConverter.GetBytes(20u));
        tiff.AddRange(BitConverter.GetBytes(38u)); // Offset der Zeichenkette
        tiff.AddRange(BitConverter.GetBytes(0u)); // kein weiteres IFD
        tiff.AddRange(Encoding.ASCII.GetBytes("1987:07:12 10:00:00\0"));
        var payload = new List<byte>();
        payload.AddRange("Exif\0\0"u8.ToArray());
        payload.AddRange(tiff);
        var length = payload.Count + 2;
        var segment = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) };
        segment.AddRange(payload);
        // nach SOI einfügen
        return [plain[0], plain[1], .. segment, .. plain.AsSpan(2).ToArray()];
    }
}
