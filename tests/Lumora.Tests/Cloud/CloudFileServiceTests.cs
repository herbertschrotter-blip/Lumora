using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Cloud;
using Lumora.Services.Messaging;

namespace Lumora.Tests.Cloud;

/// <summary>
/// Kriterium 6 (ADR-0077): „Immer auf diesem Gerät behalten“ (+P −U) und „Speicherplatz freigeben“ (+U −P) über
/// SetFileAttributes, Statusmeldung, Beobachtung bis Windows fertig ist (Nachricht für Marke und Analysen); Sync-Wurzeln.
/// Nur Fakes — kein OneDrive in der Suite.
/// </summary>
public sealed class CloudFileServiceTests
{
    private const FileAttributes Placeholder = (FileAttributes)0x00401620;
    private const FileAttributes Local = (FileAttributes)0x00000420;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Zustand_aus_Attributen_und_Sync_Wurzel()
    {
        var fs = new FakeFileSystem();
        fs.Set(@"C:\OneDrive\a.jpg", Placeholder);
        fs.Set(@"C:\OneDrive\b.jpg", Local);
        fs.Set(@"C:\Fotos\c.jpg", FileAttributes.Archive);
        using var service = Create(fs, new StrongReferenceMessenger());

        service.GetState(@"C:\OneDrive\a.jpg").Should().Be(new CloudFileState(true, true, false, false));
        service.GetState(@"C:\OneDrive\b.jpg").Should().Be(new CloudFileState(true, false, false, false), "lokal, aber unter der OneDrive-Wurzel");
        service.GetState(@"C:\Fotos\c.jpg").Should().Be(CloudFileState.NotCloud);
        service.GetState(@"C:\OneDrive\fehlt.jpg").Should().Be(CloudFileState.NotCloud);
        service.MayBeCloud(@"c:\onedrive\x\y.jpg").Should().BeTrue();
        service.MayBeCloud(@"C:\OneDrive2\y.jpg").Should().BeFalse("nur echte Unterordner der Wurzel");
    }

    [Fact]
    public async Task Behalten_setzt_Pinned_meldet_Status_und_nach_dem_Download_die_Nachricht()
    {
        var fs = new FakeFileSystem();
        fs.Set(@"C:\OneDrive\a.jpg", Placeholder | CloudFiles.Unpinned);
        fs.Set(@"C:\OneDrive\b.jpg", Placeholder);
        fs.Set(@"C:\Fotos\c.jpg", FileAttributes.Archive);
        var messenger = new StrongReferenceMessenger();
        var received = new ConcurrentQueue<CloudFilesChangedMessage>();
        messenger.Register<CloudFilesChangedMessage>(this, (_, m) => received.Enqueue(m));
        using var service = Create(fs, messenger);

        var result = service.SetPinState(
            [new CloudFileRef(1, 10, @"C:\OneDrive\a.jpg"), new CloudFileRef(1, 11, @"C:\OneDrive\b.jpg"), new CloudFileRef(1, 12, @"C:\Fotos\c.jpg")],
            CloudPinAction.KeepOnDevice);

        result.Should().Be(new CloudPinResult(CloudPinAction.KeepOnDevice, 2, 1, 0));
        result.StatusText.Should().Be("2 Fotos werden immer auf diesem Gerät behalten — OneDrive lädt sie im Hintergrund herunter · 1 Datei liegt nicht in OneDrive.");
        fs.Get(@"C:\OneDrive\a.jpg").Should().HaveFlag(CloudFiles.Pinned);
        fs.Get(@"C:\OneDrive\a.jpg")!.Value.HasFlag(CloudFiles.Unpinned).Should().BeFalse();
        fs.Get(@"C:\Fotos\c.jpg").Should().Be(FileAttributes.Archive, "Dateien außerhalb von OneDrive bleiben unberührt");

        // Windows lädt a herunter → Nachricht „lokal“ nur für a
        fs.Set(@"C:\OneDrive\a.jpg", Local | CloudFiles.Pinned);
        await WaitUntilAsync(() => !received.IsEmpty);
        received.Should().ContainSingle().Which.Should().Match<CloudFilesChangedMessage>(m => m.BecameLocal && m.LibraryId == 1 && m.FileIds.SequenceEqual(new[] { 10 }));
    }

    [Fact]
    public async Task Freigeben_setzt_Unpinned_und_meldet_sobald_nur_online()
    {
        var fs = new FakeFileSystem();
        fs.Set(@"C:\OneDrive\a.jpg", Local | CloudFiles.Pinned);
        var messenger = new StrongReferenceMessenger();
        var received = new ConcurrentQueue<CloudFilesChangedMessage>();
        messenger.Register<CloudFilesChangedMessage>(this, (_, m) => received.Enqueue(m));
        using var service = Create(fs, messenger);

        var result = service.SetPinState([new CloudFileRef(2, 20, @"C:\OneDrive\a.jpg")], CloudPinAction.FreeUpSpace);

        result.StatusText.Should().Be("Speicherplatz wird freigegeben — 1 Foto liegt danach nur online.");
        fs.Get(@"C:\OneDrive\a.jpg").Should().HaveFlag(CloudFiles.Unpinned);
        fs.Get(@"C:\OneDrive\a.jpg")!.Value.HasFlag(CloudFiles.Pinned).Should().BeFalse();
        fs.Set(@"C:\OneDrive\a.jpg", Placeholder | CloudFiles.Unpinned);
        await WaitUntilAsync(() => !received.IsEmpty);
        received.Should().ContainSingle().Which.Should().Match<CloudFilesChangedMessage>(m => !m.BecameLocal && m.FileIds.Single() == 20);
    }

    [Fact]
    public void Abgelehntes_Attribut_zaehlt_als_gescheitert()
    {
        var fs = new FakeFileSystem { Reject = true };
        fs.Set(@"C:\OneDrive\a.jpg", Placeholder);
        using var service = Create(fs, new StrongReferenceMessenger());

        var result = service.SetPinState([new CloudFileRef(1, 1, @"C:\OneDrive\a.jpg")], CloudPinAction.KeepOnDevice);

        result.Should().Be(new CloudPinResult(CloudPinAction.KeepOnDevice, 0, 0, 1));
        result.StatusText.Should().Be("Nichts zu behalten · 1 Datei: von Windows abgelehnt.");
    }

    [Fact]
    public void Lightbox_meldet_lokal_gewordene_Datei()
    {
        var messenger = new StrongReferenceMessenger();
        CloudFilesChangedMessage? received = null;
        messenger.Register<CloudFilesChangedMessage>(this, (_, m) => received = m);
        using var service = Create(new FakeFileSystem(), messenger);

        service.NotifyBecameLocal(new CloudFileRef(0, 7, @"C:\OneDrive\a.jpg"));

        received.Should().NotBeNull();
        received!.BecameLocal.Should().BeTrue();
        received.FileIds.Should().Equal(7);
    }

    [Fact]
    public void Statustexte_Einzahl_Mehrzahl()
    {
        new CloudPinResult(CloudPinAction.KeepOnDevice, 1, 0, 0).StatusText.Should().Be("1 Foto wird immer auf diesem Gerät behalten — OneDrive lädt es im Hintergrund herunter.");
        new CloudPinResult(CloudPinAction.FreeUpSpace, 3, 2, 2).StatusText.Should().Be("Speicherplatz wird freigegeben — 3 Fotos liegen danach nur online · 2 Dateien liegen nicht in OneDrive · 2 Dateien: von Windows abgelehnt.");
        new CloudPinResult(CloudPinAction.FreeUpSpace, 0, 1, 0).StatusText.Should().Be("Kein Speicherplatz freizugeben · 1 Datei liegt nicht in OneDrive.");
    }

    [Fact]
    public void Sync_Wurzeln_ohne_Gross_Kleinschreibung_und_nur_vollstaendige_Pfade()
    {
        var roots = new CloudSyncRoots([@"C:\Users\x\OneDrive\", @"D:\Firma - OneDrive", "relativ", ""]);

        roots.Roots.Should().Equal(@"C:\Users\x\OneDrive\", @"D:\Firma - OneDrive\");
        roots.Contains(@"c:\users\x\onedrive").Should().BeTrue();
        roots.Contains(@"C:\Users\x\OneDrive\Bilder\a.jpg").Should().BeTrue();
        roots.Contains(@"D:\Firma - OneDrive\a.jpg").Should().BeTrue();
        roots.Contains(@"C:\Users\x\OneDriveAlt\a.jpg").Should().BeFalse();
        roots.Contains(string.Empty).Should().BeFalse();
    }

    private static CloudFileService Create(FakeFileSystem fs, IMessenger messenger)
        => new(NullLogger<CloudFileService>.Instance, fs, new CloudSyncRoots([@"C:\OneDrive"]), messenger)
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "Bedingung nicht rechtzeitig erfüllt");
            await Task.Delay(10);
        }
    }

    internal sealed class FakeFileSystem : ICloudFileSystem
    {
        private readonly ConcurrentDictionary<string, FileAttributes> _attributes = new(StringComparer.OrdinalIgnoreCase);

        public bool Reject { get; init; }

        public void Set(string path, FileAttributes attributes) => _attributes[path] = attributes;

        public FileAttributes? Get(string path) => _attributes.TryGetValue(path, out var a) ? a : null;

        public FileAttributes? TryGetAttributes(string path) => Get(path);

        public void SetAttributes(string path, FileAttributes attributes)
        {
            if (Reject)
            {
                throw new IOException("abgelehnt");
            }

            // wie Windows: die nicht setzbaren (Reparse, Recall …) bleiben, die setzbaren kommen vom Aufrufer
            var kept = Get(path)!.Value & (FileAttributes.ReparsePoint | FileAttributes.SparseFile | CloudFiles.RecallOnDataAccess | CloudFiles.RecallOnOpen);
            _attributes[path] = kept | attributes;
        }
    }
}
