using System.Collections.Concurrent;
using System.Management;
using System.Runtime.Versioning;

namespace Lumora.Services.Background;

/// <summary>Art des Laufwerks, auf dem die Fotos liegen (bestimmt, wie viele Dateien gleichzeitig gelesen werden).</summary>
public enum StorageKind
{
    Unknown,
    Ssd,
    Hdd,
    Network,
}

/// <summary>Erkennt die Laufwerksart eines Pfads (Tests ersetzen sie).</summary>
public interface IStorageKindProbe
{
    StorageKind GetKind(string path);
}

/// <summary>
/// Laufwerksart über die Storage-Verwaltung von Windows (nur lesend, ohne Administratorrechte): Laufwerksbuchstabe →
/// <c>MSFT_Partition.DiskNumber</c> → <c>MSFT_PhysicalDisk.MediaType</c> (4 = SSD, 3 = HDD). Netzlaufwerke und UNC-Pfade
/// sind „Netz“. Ergebnis je Laufwerk gemerkt; Fehler ergeben „unbekannt“ (dann wird vorsichtig einzeln gelesen).
/// </summary>
public sealed class StorageKindProbe : IStorageKindProbe
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, StorageKind> _cache = new(StringComparer.OrdinalIgnoreCase);

    public StorageKind GetKind(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return StorageKind.Unknown;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return StorageKind.Network;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
        {
            return StorageKind.Unknown;
        }

        return _cache.GetOrAdd(root[..2], Detect);
    }

    private static StorageKind Detect(string drive)
    {
        try
        {
            if (new DriveInfo(drive).DriveType == DriveType.Network)
            {
                return StorageKind.Network;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return StorageKind.Unknown;
        }

        return OperatingSystem.IsWindows() ? DetectMediaType(char.ToUpperInvariant(drive[0])) : StorageKind.Unknown;
    }

    [SupportedOSPlatform("windows")]
    private static StorageKind DetectMediaType(char letter)
    {
        try
        {
            var scope = new ManagementScope(StorageNamespace, new ConnectionOptions { Timeout = WmiTimeout });
            var options = new System.Management.EnumerationOptions { Timeout = WmiTimeout };
            uint? diskNumber = null;
            using (var partitions = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition"), options))
            {
                foreach (ManagementObject partition in partitions.Get())
                {
                    using (partition)
                    {
                        if (partition["DriveLetter"] is char c && char.ToUpperInvariant(c) == letter && partition["DiskNumber"] is uint number)
                        {
                            diskNumber = number;
                        }
                    }
                }
            }

            if (diskNumber is not { } disk)
            {
                return StorageKind.Unknown;
            }

            using var physical = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{disk}'"), options);
            foreach (ManagementObject item in physical.Get())
            {
                using (item)
                {
                    return item["MediaType"] is ushort media
                        ? media switch { 4 => StorageKind.Ssd, 3 => StorageKind.Hdd, _ => StorageKind.Unknown }
                        : StorageKind.Unknown;
                }
            }

            return StorageKind.Unknown;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return StorageKind.Unknown;
        }
    }
}
