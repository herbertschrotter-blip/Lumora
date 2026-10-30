using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lumora.Services.HiddenLibraries.Relocation;

/// <summary>Findet die Datei hinter einem eingehängten virtuellen Laufwerk (Mockup 26, Schritt „Quelle“).</summary>
public interface IContainerFileLocator
{
    /// <summary>VHD/VHDX-Datei hinter dem Laufwerk („C:\Daten\Privat.vhdx“), null bei VeraCrypt, normalem Laufwerk oder Fehler.</summary>
    string? FindVirtualDiskFile(char driveLetter);
}

/// <summary>
/// Fragt Windows über <c>GetStorageDependencyInformation</c> (virtdisk.dll) nach Host-Laufwerk und Pfad der Datei. Nichts
/// davon wird gespeichert (ADR-0049) — das Ergebnis lebt nur im Assistenten.
/// </summary>
public sealed class VirtualDiskBackingFile : IContainerFileLocator
{
    private const int GetStorageDependencyFlagHostVolumes = 0x1;
    private const int StorageDependencyInfoVersion2 = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int Entry2Size = 64; // x64: 4 Flags + 4 Provider + 20 Speichertyp + 4 Ebene + 4 Pointer à 8
    private const int EntriesOffset = 8;

    public string? FindVirtualDiskFile(char driveLetter)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            return null;
        }

        try
        {
            using var volume = CreateFile($@"\\.\{char.ToUpperInvariant(driveLetter)}:", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (volume.IsInvalid)
            {
                return null;
            }

            var size = 4096;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buffer, 0, StorageDependencyInfoVersion2);
                    Marshal.WriteInt32(buffer, 4, 0);
                    var used = size;
                    var error = GetStorageDependencyInformation(volume, GetStorageDependencyFlagHostVolumes, size, buffer, ref used);
                    if (error == ErrorInsufficientBuffer)
                    {
                        size = Math.Max(used, size * 2);
                        continue;
                    }

                    return error == 0 ? Read(buffer) : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            // kein virtdisk.dll bzw. kein virtuelles Laufwerk
        }

        return null;
    }

    /// <summary>Tiefste Ebene (die Datei, auf der das Laufwerk direkt liegt): Host-Laufwerk + relativer Pfad.</summary>
    private static string? Read(IntPtr info)
    {
        var count = Marshal.ReadInt32(info, 4);
        string? best = null;
        var bestLevel = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var entry = info + EntriesOffset + (i * Entry2Size);
            var level = Marshal.ReadInt32(entry, 28);
            var host = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 40));
            var relative = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 56));
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(relative) || level >= bestLevel)
            {
                continue;
            }

            if (DriveOfVolume(host) is { } drive)
            {
                best = Path.Combine(drive, relative.TrimStart('\\'));
                bestLevel = level;
            }
        }

        return best;
    }

    /// <summary>„\\?\Volume{…}\“ → „C:\“ (erster Bereitstellungspunkt), null ohne Buchstaben.</summary>
    private static string? DriveOfVolume(string volumeName)
    {
        if (volumeName.Length == 3 && volumeName[1] == ':')
        {
            return volumeName;
        }

        var name = volumeName.EndsWith('\\') ? volumeName : volumeName + "\\";
        var chars = new char[1024];
        if (!GetVolumePathNamesForVolumeName(name, chars, chars.Length, out var length) || length == 0)
        {
            return null;
        }

        var first = new string(chars, 0, length).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: >= 3 } && first[1] == ':' ? first[..3] : null;
    }

    private const int FileShareReadWrite = 0x1 | 0x2;
    private const int OpenExisting = 3;

#pragma warning disable SYSLIB1054 // DllImport wie VirtualDiskNative; LibraryImport bräuchte AllowUnsafeBlocks
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, int access, int share, IntPtr security, int creation, int flags, IntPtr template);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    private static extern int GetStorageDependencyInformation(SafeFileHandle handle, int flags, int infoSize, IntPtr info, ref int sizeUsed);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumePathNamesForVolumeNameW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeName(string volumeName, [Out] char[] pathNames, int bufferLength, out int returnLength);
#pragma warning restore SYSLIB1054
}
