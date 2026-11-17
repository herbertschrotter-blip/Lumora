using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Cloud;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Tests.Fixtures;
using Lumora.Tests.Metadata;

namespace Lumora.Tests.Cloud;

/// <summary>
/// Kriterium 3 (ADR-0077): Aufnahmedatum für Platzhalter über den Eigenschaftsspeicher der Shell (hier ein Fake), ohne die
/// Datei zu lesen; ohne Datum bleibt die Datei offen und wird nicht bei jedem Durchgang erneut gefragt; sobald sie lokal
/// ist, liest das normale Nachtragen sie selbst. GPS bleibt leer (kommt nur aus der lokalen Datei).
/// </summary>
public sealed class CaptureDateCloudTests : IAsyncLifetime
{
    private TempLibraryFixture _fx = null!;
    private Library _library = null!;

    public async Task InitializeAsync()
    {
        _fx = await TempLibraryFixture.CreateAsync();
        _library = await _fx.CreateLibraryAsync();
    }

    public Task DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(_fx.LibraryRoot, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        _fx.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Platzhalter_bekommt_das_Datum_aus_der_Shell_ohne_Dateizugriff()
    {
        var local = WriteJpeg("lokal.jpg", "2024:07:12 15:30:12");
        var online = WriteJpeg("online.jpg", "2001:01:01 01:01:01");
        await _fx.ScanAsync(_library.Id);
        MarkOnlineOnly(online);
        var shellDate = new DateTime(2019, 8, 3, 10, 15, 0, DateTimeKind.Utc);
        var shell = new FakePropertyReader { Date = CaptureDateValue.FromUtc(shellDate, CaptureDateSource.Exif) };
        var reader = new CountingReader();
        using var service = Create(reader, shell);

        var report = await service.RunBackfillPassAsync();

        report.Should().Be(new CaptureDateBackfillReport(2, 2, 0));
        reader.Paths.Should().Equal(local);
        shell.Paths.Should().Equal(online);
        var placeholder = (await _fx.FindFileAsync("online.jpg"))!;
        placeholder.CapturedAtSource.Should().Be(CaptureDateSource.Exif);
        placeholder.CapturedAt.Should().Be(shellDate, "das Datum der Shell, nicht das aus der (nicht gelesenen) Datei");
        File.GetAttributes(online).Should().HaveFlag(FileAttributes.Offline, "nichts heruntergeladen");
    }

    [Fact]
    public async Task Ohne_Datum_bleibt_der_Platzhalter_offen_bis_er_lokal_ist()
    {
        var online = WriteJpeg("online.jpg", "2024:07:12 15:30:12");
        await _fx.ScanAsync(_library.Id);
        MarkOnlineOnly(online);
        var shell = new FakePropertyReader { Date = null };
        var reader = new CountingReader();
        using var service = Create(reader, shell);

        (await service.RunBackfillPassAsync()).Should().Be(new CaptureDateBackfillReport(0, 0, 1));
        (await service.RunBackfillPassAsync()).Should().Be(new CaptureDateBackfillReport(0, 0, 1), "nicht noch einmal gefragt");
        shell.Paths.Should().HaveCount(1);
        (await _fx.FindFileAsync("online.jpg"))!.CapturedAtSource.Should().BeNull();
        reader.Paths.Should().BeEmpty();

        // OneDrive hat die Datei geladen (z. B. „Immer behalten“) → das normale Nachtragen liest sie
        File.SetAttributes(online, FileAttributes.Normal);
        _fx.Messenger.Send(new CloudFilesChangedMessage(_library.Id, [(await _fx.FindFileAsync("online.jpg"))!.Id], BecameLocal: true));
        (await service.RunBackfillPassAsync()).Should().Be(new CaptureDateBackfillReport(1, 1, 0));
        reader.Paths.Should().Equal(online);
        (await _fx.FindFileAsync("online.jpg"))!.CapturedAtSource.Should().Be(CaptureDateSource.Exif);
    }

    [Fact]
    public async Task Ohne_Shell_Leser_bleibt_alles_wie_bisher()
    {
        var online = WriteJpeg("online.jpg", "2024:07:12 15:30:12");
        await _fx.ScanAsync(_library.Id);
        MarkOnlineOnly(online);
        var reader = new CountingReader();
        using var service = Create(reader, shell: null);

        (await service.RunBackfillPassAsync()).Should().Be(new CaptureDateBackfillReport(0, 0, 1));
        reader.Paths.Should().BeEmpty("ein Platzhalter wird nie gelesen");
    }

    private CaptureDateService Create(ICaptureDateReader reader, ICloudPropertyReader? shell)
        => new(_fx.ContextFactory, reader, NullLogger<CaptureDateService>.Instance, messenger: _fx.Messenger, cloudProperties: shell)
        {
            StartDelay = Timeout.InfiniteTimeSpan,
        };

    private string WriteJpeg(string relative, string exifDate)
    {
        var path = Path.Combine(_fx.LibraryRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff(exifDate, "+02:00")));
        return path;
    }

    private static void MarkOnlineOnly(string path) => File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);

    private sealed class FakePropertyReader : ICloudPropertyReader
    {
        private readonly List<string> _paths = new();

        public CaptureDateValue? Date { get; init; }

        public List<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        public CaptureDateValue? ReadCaptureDate(string path)
        {
            lock (_paths)
            {
                _paths.Add(path);
            }

            return Date;
        }
    }

    /// <summary>Echter Leser, der sich die gelesenen Dateien merkt.</summary>
    private sealed class CountingReader : ICaptureDateReader
    {
        private readonly CaptureDateReader _inner = new();
        private readonly List<string> _paths = new();

        public List<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        public CaptureDateValue? Read(string path)
        {
            lock (_paths)
            {
                _paths.Add(path);
            }

            return _inner.Read(path);
        }
    }
}
