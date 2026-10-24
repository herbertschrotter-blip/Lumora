using System.Management;
using System.Reflection;
using System.Runtime.Versioning;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>
/// Die echte Windows-Seite von <see cref="IVirtualDiskEjector"/> (Nachtrag 0.4.31 zu ADR-0049/ADR-0061): Erkennen über die
/// Storage-Verwaltung (<c>MSFT_Partition</c> → <c>MSFT_Disk.BusType</c>, lesend, ohne Administratorrechte), Auswerfen über
/// das Shell-Verb „Eject“ — derselbe Weg wie Explorer › „Auswerfen“. <c>Dismount-DiskImage</c> brauchte Administratorrechte
/// und scheiterte deshalb im normalen Lumora-Prozess. Kein Skript, kein Fremdprogramm. Nie in Tests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellVirtualDiskEjector : IVirtualDiskEjector
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    /// <summary><c>MSFT_Disk.BusType</c> 15 = „File Backed Virtual“ (eingehängte VHD/VHDX/ISO).</summary>
    private const ushort BusTypeFileBackedVirtual = 15;

    /// <summary>Shell-Namensraum „Dieser PC“ (<c>ssfDRIVES</c>).</summary>
    private const int ShellDrivesFolder = 17;

    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public bool IsVirtualDisk(char driveLetter)
    {
        var letter = char.ToUpperInvariant(driveLetter);
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
                return false;
            }

            using var disks = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT BusType FROM MSFT_Disk WHERE Number = {disk}"), options);
            foreach (ManagementObject item in disks.Get())
            {
                using (item)
                {
                    if (item["BusType"] is ushort busType)
                    {
                        return busType == BusTypeFileBackedVirtual;
                    }
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    public Task<bool> EjectAsync(char driveLetter, Func<bool> isMounted, TimeSpan timeout, CancellationToken ct)
    {
        // Shell-Objekte gehören in einen STA-Thread; Thread.Join pumpt dort COM-Nachrichten, solange das Auswerfen läuft.
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => done.TrySetResult(EjectOnStaThread(driveLetter, isMounted, timeout, ct)))
        {
            IsBackground = true,
            Name = "Lumora Auswerfen",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static bool EjectOnStaThread(char driveLetter, Func<bool> isMounted, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null || Activator.CreateInstance(shellType) is not { } shell)
            {
                return false;
            }

            var drives = Invoke(shell, "NameSpace", ShellDrivesFolder);
            var item = drives is null ? null : Invoke(drives, "ParseName", $"{char.ToUpperInvariant(driveLetter)}:\\");
            if (item is null)
            {
                return false;
            }

            Invoke(item, "InvokeVerb", "Eject");
        }
        catch (Exception ex) when (ex is TargetInvocationException or System.Runtime.InteropServices.COMException or MissingMethodException)
        {
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (isMounted() && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            Thread.CurrentThread.Join(PollInterval);
        }

        return !isMounted();
    }

    private static object? Invoke(object target, string method, object argument)
        => target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, binder: null, target, [argument], System.Globalization.CultureInfo.InvariantCulture);
}
