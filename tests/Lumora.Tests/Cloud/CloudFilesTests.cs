using System.Diagnostics;
using Lumora.Services.Cloud;
using Lumora.Services.Thumbnails;

namespace Lumora.Tests.Cloud;

/// <summary>
/// Kriterium 1 (ADR-0077): zentrale Platzhalter-Erkennung — Attribute RecallOnDataAccess/RecallOnOpen/Offline (nur online),
/// Pinned/Unpinned, Cloud-Ordner als Reparse-Punkt ohne Linkziel. Werte wie gemessen in einem OneDrive-Ordner (nativ,
/// Spike 19): Platzhalter 0x00401620, „immer behalten“ 0x00080420, lokal 0x00000420, Ordner 0x00000410.
/// </summary>
public sealed class CloudFilesTests
{
    private const FileAttributes Placeholder = (FileAttributes)0x00401620; // RecallOnDataAccess | Offline | ReparsePoint | Sparse | Archive
    private const FileAttributes PinnedLocal = (FileAttributes)0x00080420;
    private const FileAttributes HydratedLocal = (FileAttributes)0x00000420;

    [Fact]
    public void Platzhalter_ist_nur_online_und_in_der_Cloud()
    {
        CloudFiles.IsOnlineOnly(Placeholder).Should().BeTrue();
        CloudFiles.Classify(Placeholder).Should().Be(new CloudFileState(IsCloud: true, IsOnlineOnly: true, IsPinned: false, IsUnpinned: false));
        CloudFiles.Classify(FileAttributes.Offline).IsOnlineOnly.Should().BeTrue("Offline gilt wie bisher als nicht lokal");
        CloudFiles.Classify(CloudFiles.RecallOnOpen).IsOnlineOnly.Should().BeTrue();
    }

    [Fact]
    public void Immer_behalten_und_lokale_Cloud_Datei_sind_lokal()
    {
        CloudFiles.Classify(PinnedLocal).Should().Be(new CloudFileState(true, false, true, false));
        CloudFiles.Classify(HydratedLocal).Should().Be(CloudFileState.NotCloud, "ohne Cloud-Attribut und ohne Sync-Wurzel keine Cloud-Datei");
        CloudFiles.Classify(HydratedLocal, underSyncRoot: true).Should().Be(new CloudFileState(true, false, false, false));
        CloudFiles.Classify(FileAttributes.Archive).Should().Be(CloudFileState.NotCloud);
        CloudFiles.Classify(CloudFiles.Unpinned | Placeholder).IsUnpinned.Should().BeTrue();
    }

    [Fact]
    public void Ordner_ist_nie_nur_online()
    {
        CloudFiles.IsOnlineOnly(FileAttributes.Directory | FileAttributes.Offline | CloudFiles.RecallOnOpen).Should().BeFalse();
    }

    [Fact]
    public void Behalten_setzt_P_und_nimmt_U_Freigeben_umgekehrt_nur_setzbare_Attribute()
    {
        var current = Placeholder | CloudFiles.Unpinned | FileAttributes.ReadOnly;

        var keep = CloudFiles.WithPin(current, keepOnDevice: true);
        keep.Should().HaveFlag(CloudFiles.Pinned).And.HaveFlag(FileAttributes.ReadOnly).And.HaveFlag(FileAttributes.Archive);
        keep.HasFlag(CloudFiles.Unpinned).Should().BeFalse();
        (keep & (FileAttributes.ReparsePoint | FileAttributes.SparseFile | CloudFiles.RecallOnDataAccess)).Should().Be((FileAttributes)0,
            "von Windows verwaltete Attribute gibt Lumora nie an SetFileAttributes");

        var free = CloudFiles.WithPin(PinnedLocal, keepOnDevice: false);
        free.Should().HaveFlag(CloudFiles.Unpinned);
        free.HasFlag(CloudFiles.Pinned).Should().BeFalse();
    }

    [Fact]
    public void Lokale_Pruefung_liest_nur_Attribute_und_ThumbnailService_leitet_weiter()
    {
        var dir = Directory.CreateTempSubdirectory("lumora-cloud-");
        try
        {
            var local = Path.Combine(dir.FullName, "lokal.jpg");
            var placeholder = Path.Combine(dir.FullName, "online.jpg");
            File.WriteAllBytes(local, [1, 2, 3]);
            File.WriteAllBytes(placeholder, [1, 2, 3]);
            File.SetAttributes(placeholder, File.GetAttributes(placeholder) | FileAttributes.Offline); // wie „nur online“

            CloudFiles.IsLocallyAvailable(local).Should().BeTrue();
            CloudFiles.IsOnlineOnly(local).Should().BeFalse();
            CloudFiles.IsLocallyAvailable(placeholder).Should().BeFalse();
            CloudFiles.IsOnlineOnly(placeholder).Should().BeTrue();
            CloudFiles.IsOnlineOnly(Path.Combine(dir.FullName, "fehlt.jpg")).Should().BeFalse("eine fehlende Datei ist kein Platzhalter");
            CloudFiles.IsLocallyAvailable(dir.FullName).Should().BeFalse();
            ThumbnailService.IsLocallyAvailable(placeholder).Should().BeFalse("die bestehende Prüfung nutzt die zentrale Erkennung");
            ThumbnailService.IsLocallyAvailable(local).Should().BeTrue();
            File.SetAttributes(placeholder, FileAttributes.Normal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Normale_Datei_und_Ordner_sind_keine_Verknuepfung_Junction_schon()
    {
        var dir = Directory.CreateTempSubdirectory("lumora-cloud-link-");
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(dir.FullName, "ziel"));
            var file = Path.Combine(target.FullName, "a.jpg");
            File.WriteAllBytes(file, [1]);
            CloudFiles.IsLink(new FileInfo(file)).Should().BeFalse();
            CloudFiles.IsLink(target).Should().BeFalse();

            var junction = Path.Combine(dir.FullName, "verknuepfung");
            if (CreateJunction(junction, target.FullName))
            {
                CloudFiles.IsLink(new DirectoryInfo(junction)).Should().BeTrue("Junctions bleiben ausgeschlossen (keine Schleifen)");
                Directory.Delete(junction);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Junction ohne Administratorrechte (mklink /J); false, wenn das System es nicht erlaubt.</summary>
    internal static bool CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.WaitForExit(10_000);
        return Directory.Exists(link);
    }
}
