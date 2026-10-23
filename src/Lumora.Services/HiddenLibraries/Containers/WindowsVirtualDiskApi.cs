using System.ComponentModel;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>
/// Die echte Windows-Seite des erhöhten Schritts (ADR-0061, Nachtrag 0.4.26): VHDX über <c>virtdisk.dll</c>
/// (<see cref="VirtualDiskNative"/>), Datenträger/Partition/Volume über die Storage-Verwaltung von Windows
/// (WMI-Namensraum <c>root\Microsoft\Windows\Storage</c>: <c>MSFT_Disk.Initialize</c>, <c>MSFT_Disk.CreatePartition</c>,
/// <c>MSFT_Volume.Format</c>, <c>MSFT_Partition.AddAccessPath</c> — dieselben Klassen, die auch die Windows-Datenträger-
/// verwaltung nutzt). Kein Fremdprogramm, kein Skript. Nur im erhöhten Lauf; nie in Tests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVirtualDiskApi : IVirtualDiskApi, IDisposable
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    private const ushort PartitionStyleRaw = 0;
    private const ushort PartitionStyleGpt = 2;

    private static readonly TimeSpan WmiTimeout = TimeSpan.FromMinutes(2);

    /// <summary>So lange darf der neue Datenträger brauchen, bis ihn die Storage-Verwaltung meldet.</summary>
    private static readonly TimeSpan DiskAppearTimeout = TimeSpan.FromSeconds(20);

    private ManagementScope? _scope;

    /// <summary>
    /// Ein Handle je Lauf: Einhängen, Datenträgernummer und Abhängen laufen über denselben Handle (wie in den Microsoft-
    /// Beispielen) — die eingehängte VHDX wird nicht ein zweites Mal geöffnet.
    /// </summary>
    private SafeFileHandle? _handle;
    private string? _handlePath;

    public void Dispose()
    {
        _handle?.Dispose(); // dauerhaft eingehängt bleibt eingehängt
        _handle = null;
    }

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string? GetLocalDriveFormat(string driveRoot)
    {
        try
        {
            var drive = new DriveInfo(driveRoot);
            return drive.DriveType == DriveType.Fixed && drive.IsReady ? drive.DriveFormat : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsDriveLetterInUse(char letter)
        => new VolumeDeviceQuery().GetDeviceName(letter) is not null || Directory.Exists($"{letter}:\\");

    public bool IsReparsePoint(string directory)
        => (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;

    public void CreateDynamicVhdx(string path, ulong maximumSizeBytes)
    {
        EnsureSupported();
        var type = VirtualDiskNative.VhdxType;
        var parameters = new VirtualDiskNative.CreateParameters
        {
            Version = VirtualDiskNative.CreateVersion2,
            MaximumSize = maximumSizeBytes,
        };
        var result = VirtualDiskNative.CreateVirtualDisk(
            ref type, path, VirtualDiskNative.AccessNone, IntPtr.Zero, VirtualDiskNative.CreateFlagNone, 0, ref parameters, IntPtr.Zero, out var handle);
        using (handle)
        {
            ThrowIfFailed(result, "CreateVirtualDisk");
        }
    }

    public void AttachPermanently(string path)
    {
        var handle = Handle(path);
        var parameters = new VirtualDiskNative.AttachParameters { Version = VirtualDiskNative.AttachVersion1 };
        var result = VirtualDiskNative.AttachVirtualDisk(handle, IntPtr.Zero, VirtualDiskNative.AttachFlagPermanentLifetime, 0, ref parameters, IntPtr.Zero);
        if (result == VirtualDiskNative.ErrorPrivilegeNotHeld && VirtualDiskNative.TryEnableManageVolumePrivilege())
        {
            // Nur falls Windows es verlangt: „Volumewartungsaufgaben durchführen“ für diesen erhöhten Prozess einschalten
            result = VirtualDiskNative.AttachVirtualDisk(handle, IntPtr.Zero, VirtualDiskNative.AttachFlagPermanentLifetime, 0, ref parameters, IntPtr.Zero);
        }

        ThrowIfFailed(result, "AttachVirtualDisk");
    }

    public int GetDiskNumber(string path)
    {
        var handle = Handle(path);
        var buffer = new char[512];
        var size = buffer.Length * sizeof(char);
        ThrowIfFailed(VirtualDiskNative.GetVirtualDiskPhysicalPath(handle, ref size, buffer), "GetVirtualDiskPhysicalPath");
        var text = new string(buffer);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return VirtualDiskNative.ParseDiskNumber(end >= 0 ? text[..end] : text);
    }

    public void InitializeGpt(int diskNumber)
    {
        using var disk = WaitForDisk(diskNumber);
        if (disk["IsOffline"] is true)
        {
            Invoke(disk, "Online", null).Dispose();
        }

        if (disk["IsReadOnly"] is true)
        {
            Invoke(disk, "SetAttributes", p => p["IsReadOnly"] = false).Dispose();
        }

        if (Convert.ToUInt16(disk["PartitionStyle"], CultureInfo.InvariantCulture) != PartitionStyleRaw)
        {
            throw new InvalidOperationException("Der neue Datenträger ist nicht leer.");
        }

        Invoke(disk, "Initialize", p => p["PartitionStyle"] = PartitionStyleGpt).Dispose();
    }

    public int CreateMaximumPartition(int diskNumber)
    {
        using var disk = WaitForDisk(diskNumber);
        using var result = Invoke(disk, "CreatePartition", p =>
        {
            p["UseMaximumSize"] = true;
            p["AssignDriveLetter"] = false; // Typ: Standard „Basisdaten“ auf GPT
        });
        if (result["CreatedPartition"] is not ManagementBaseObject created)
        {
            throw new InvalidOperationException("Keine Partition gemeldet.");
        }

        using (created)
        {
            return Convert.ToInt32(created["PartitionNumber"], CultureInfo.InvariantCulture);
        }
    }

    public void FormatNtfs(int diskNumber, int partitionNumber, string label)
    {
        using var volume = WaitForVolume(diskNumber, partitionNumber);
        Invoke(volume, "Format", p =>
        {
            p["FileSystem"] = "NTFS";
            p["FileSystemLabel"] = label;
            p["Full"] = false;
            p["Force"] = true;
        }).Dispose();
    }

    public void AssignDriveLetter(int diskNumber, int partitionNumber, char letter)
    {
        using var partition = FindPartition(diskNumber, partitionNumber);

        // Hat Windows inzwischen von sich aus einen anderen Buchstaben vergeben, diesen wieder entfernen
        var current = DriveLetterOf(partition["DriveLetter"]);
        if (current is { } other && other != letter)
        {
            Invoke(partition, "RemoveAccessPath", p => p["AccessPath"] = $"{other}:\\").Dispose();
        }

        if (current != letter)
        {
            Invoke(partition, "AddAccessPath", p => p["AccessPath"] = $"{letter}:\\").Dispose();
        }
    }

    public void Detach(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        // Ergebnis bewusst nicht ausgewertet: war die VHDX gar nicht eingehängt, ist das hier kein Fehler; bleibt sie
        // eingehängt, scheitert das anschließende Löschen und die Oberfläche meldet die liegengebliebene Datei.
        _ = VirtualDiskNative.DetachVirtualDisk(Handle(path), 0, 0);
        Dispose(); // Handle schließen, damit die Datei gelöscht werden kann
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void EnsureSupported()
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("Nur im 64-Bit-Prozess.");
        }
    }

    private SafeFileHandle Handle(string path)
    {
        if (_handle is { IsClosed: false } && string.Equals(_handlePath, path, StringComparison.OrdinalIgnoreCase))
        {
            return _handle;
        }

        Dispose();
        _handle = Open(path);
        _handlePath = path;
        return _handle;
    }

    private static SafeFileHandle Open(string path)
    {
        EnsureSupported();
        var type = VirtualDiskNative.VhdxType;
        var parameters = new VirtualDiskNative.OpenParameters { Version = VirtualDiskNative.OpenVersion2 };
        var result = VirtualDiskNative.OpenVirtualDisk(ref type, path, VirtualDiskNative.AccessNone, 0, ref parameters, out var handle);
        if (result != VirtualDiskNative.ErrorSuccess)
        {
            handle.Dispose();
            throw new Win32Exception(result, "OpenVirtualDisk");
        }

        return handle;
    }

    private static void ThrowIfFailed(int result, string function)
    {
        if (result != VirtualDiskNative.ErrorSuccess)
        {
            throw new Win32Exception(result, function);
        }
    }

    private static char? DriveLetterOf(object? value)
    {
        var c = value switch
        {
            char ch => ch,
            ushort u => (char)u,
            _ => '\0',
        };
        return char.IsAsciiLetter(c) ? char.ToUpperInvariant(c) : null;
    }

    private ManagementScope Scope()
    {
        if (_scope is null)
        {
            var scope = new ManagementScope(StorageNamespace, new ConnectionOptions { Timeout = WmiTimeout });
            scope.Connect();
            _scope = scope;
        }

        return _scope;
    }

    private ManagementObject? QuerySingle(string wql)
    {
        using var searcher = new ManagementObjectSearcher(Scope(), new ObjectQuery(wql), new System.Management.EnumerationOptions { Timeout = WmiTimeout });
        using var results = searcher.Get();
        return FirstAndDisposeRest(results);
    }

    private static ManagementObject? FirstAndDisposeRest(ManagementObjectCollection items)
    {
        ManagementObject? found = null;
        foreach (ManagementObject item in items)
        {
            if (found is null)
            {
                found = item;
            }
            else
            {
                item.Dispose();
            }
        }

        return found;
    }

    /// <summary>Fragt nach, bis Windows das Gesuchte meldet (neue Datenträger/Volumes erscheinen mit kurzer Verzögerung).</summary>
    private static ManagementObject Poll(Func<ManagementObject?> query, string notFound)
    {
        var deadline = DateTime.UtcNow + DiskAppearTimeout;
        while (true)
        {
            if (query() is { } found)
            {
                return found;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException(notFound);
            }

            Thread.Sleep(500);
        }
    }

    /// <summary>Der eben eingehängte Datenträger erscheint in der Storage-Verwaltung manchmal erst nach einem Moment.</summary>
    private ManagementObject WaitForDisk(int diskNumber)
        => Poll(
            () => QuerySingle(string.Create(CultureInfo.InvariantCulture, $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}")),
            "Der neue Datenträger wurde nicht gemeldet.");

    /// <summary>Das Volume der neuen Partition meldet Windows ebenfalls erst nach einem Moment.</summary>
    private ManagementObject WaitForVolume(int diskNumber, int partitionNumber)
        => Poll(
            () =>
            {
                using var partition = FindPartition(diskNumber, partitionNumber);
                using var volumes = partition.GetRelated("MSFT_Volume");
                return FirstAndDisposeRest(volumes);
            },
            "Kein Volume zur neuen Partition.");

    private ManagementObject FindPartition(int diskNumber, int partitionNumber)
        => QuerySingle(string.Create(CultureInfo.InvariantCulture, $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}"))
           ?? throw new InvalidOperationException("Die neue Partition wurde nicht gefunden.");

    /// <summary>Ruft eine Methode der Storage-Verwaltung auf; <c>ReturnValue</c> ≠ 0 ist ein Fehler.</summary>
    private static ManagementBaseObject Invoke(ManagementObject target, string method, Action<ManagementBaseObject>? fill)
    {
        using var input = target.GetMethodParameters(method);
        fill?.Invoke(input);
        var output = target.InvokeMethod(method, input, new InvokeMethodOptions(null, WmiTimeout));
        var code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u, CultureInfo.InvariantCulture);
        if (code != 0)
        {
            output?.Dispose();
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{method} meldet {code}."));
        }

        return output!;
    }
}
