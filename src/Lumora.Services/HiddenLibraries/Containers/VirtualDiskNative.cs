using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>
/// P/Invoke für <c>virtdisk.dll</c> (Windows-Funktionen für virtuelle Festplatten, ADR-0061 Nachtrag 0.4.26):
/// <c>CreateVirtualDisk</c>, <c>OpenVirtualDisk</c>, <c>AttachVirtualDisk</c>, <c>DetachVirtualDisk</c>,
/// <c>GetVirtualDiskPhysicalPath</c> (dazu nur bei Bedarf <c>SeManageVolumePrivilege</c> einschalten). Strukturen nach <c>virtdisk.h</c> für 64-Bit-Prozesse (Unions mit ULONGLONG/Zeigern
/// beginnen bei Versatz 8); <see cref="StructSizes"/> prüfen die Tests.
/// </summary>
internal static class VirtualDiskNative
{
    /// <summary><c>VIRTUAL_STORAGE_TYPE_DEVICE_VHDX</c>.</summary>
    internal const int StorageTypeDeviceVhdx = 3;

    /// <summary><c>CREATE_VIRTUAL_DISK_VERSION_2</c>.</summary>
    internal const int CreateVersion2 = 2;

    /// <summary><c>OPEN_VIRTUAL_DISK_VERSION_2</c>.</summary>
    internal const int OpenVersion2 = 2;

    /// <summary><c>ATTACH_VIRTUAL_DISK_VERSION_1</c>.</summary>
    internal const int AttachVersion1 = 1;

    /// <summary><c>VIRTUAL_DISK_ACCESS_NONE</c> — Pflicht für Version-2-Parameter.</summary>
    internal const int AccessNone = 0;

    /// <summary><c>CREATE_VIRTUAL_DISK_FLAG_NONE</c> = dynamisch erweiterbar (nicht voll vorbelegt).</summary>
    internal const int CreateFlagNone = 0;

    /// <summary><c>ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME</c>: bleibt eingehängt, wenn der Handle geschlossen wird.</summary>
    internal const int AttachFlagPermanentLifetime = 0x4;

    internal const int ErrorSuccess = 0;

    /// <summary><c>ERROR_PRIVILEGE_NOT_HELD</c>.</summary>
    internal const int ErrorPrivilegeNotHeld = 1314;

    private const int TokenAdjustPrivileges = 0x20;
    private const int TokenQuery = 0x8;
    private const int SePrivilegeEnabled = 0x2;
    private const int ErrorNotAllAssigned = 1300;

    /// <summary><c>VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT</c>.</summary>
    internal static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    /// <summary>(VIRTUAL_STORAGE_TYPE, CREATE_VIRTUAL_DISK_PARAMETERS, OPEN_VIRTUAL_DISK_PARAMETERS, ATTACH_VIRTUAL_DISK_PARAMETERS).</summary>
    internal static (int StorageType, int Create, int Open, int Attach) StructSizes
        => (Marshal.SizeOf<VirtualStorageType>(), Marshal.SizeOf<CreateParameters>(), Marshal.SizeOf<OpenParameters>(), Marshal.SizeOf<AttachParameters>());

    /// <summary><c>\\.\PhysicalDrive5</c> (aus <c>GetVirtualDiskPhysicalPath</c>) → 5.</summary>
    internal static int ParseDiskNumber(string physicalPath)
    {
        const string prefix = @"\\.\PhysicalDrive";
        if (!physicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(physicalPath.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException("Unerwarteter Datenträgerpfad.");
        }

        return number;
    }

    internal static VirtualStorageType VhdxType => new() { DeviceId = StorageTypeDeviceVhdx, VendorId = VendorMicrosoft };

    [StructLayout(LayoutKind.Sequential)]
    internal struct VirtualStorageType
    {
        public int DeviceId;
        public Guid VendorId;
    }

    /// <summary>
    /// <c>CREATE_VIRTUAL_DISK_PARAMETERS</c> mit dem Version-2-Teil der Union (64 Bit). Größe großzügig (alle Versionen
    /// passen hinein), damit Windows nie über das Ende liest.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct CreateParameters
    {
        [FieldOffset(0)]
        public int Version;

        [FieldOffset(8)]
        public Guid UniqueId;

        [FieldOffset(24)]
        public ulong MaximumSize;

        [FieldOffset(32)]
        public uint BlockSizeInBytes;

        [FieldOffset(36)]
        public uint SectorSizeInBytes;

        [FieldOffset(40)]
        public uint PhysicalSectorSizeInBytes;

        [FieldOffset(48)]
        public IntPtr ParentPath;

        [FieldOffset(56)]
        public IntPtr SourcePath;

        [FieldOffset(64)]
        public int OpenFlags;

        [FieldOffset(68)]
        public VirtualStorageType ParentVirtualStorageType;

        [FieldOffset(88)]
        public VirtualStorageType SourceVirtualStorageType;

        [FieldOffset(108)]
        public Guid ResiliencyGuid;
    }

    /// <summary><c>OPEN_VIRTUAL_DISK_PARAMETERS</c>, Version 2 (Union bei Versatz 4; Platz bis Version 3).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 44)]
    internal struct OpenParameters
    {
        [FieldOffset(0)]
        public int Version;

        [FieldOffset(4)]
        public int GetInfoOnly;

        [FieldOffset(8)]
        public int ReadOnly;

        [FieldOffset(12)]
        public Guid ResiliencyGuid;
    }

    /// <summary><c>ATTACH_VIRTUAL_DISK_PARAMETERS</c>, Version 1 (Union mit ULONGLONG → Versatz 8; Platz bis Version 2).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct AttachParameters
    {
        [FieldOffset(0)]
        public int Version;

        [FieldOffset(8)]
        public int Reserved;
    }

    /// <summary>
    /// Schaltet <c>SeManageVolumePrivilege</c> („Volumewartungsaufgaben durchführen“) im eigenen, schon erhöhten Prozess ein
    /// — nur als zweiter Versuch, wenn <c>AttachVirtualDisk</c> <see cref="ErrorPrivilegeNotHeld"/> meldet.
    /// </summary>
    internal static bool TryEnableManageVolumePrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeManageVolumePrivilege", out var luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public int Attributes;
    }

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier (wie VolumeDeviceQuery).
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CreateVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        int virtualDiskAccessMask,
        IntPtr securityDescriptor,
        int flags,
        int providerSpecificFlags,
        ref CreateParameters parameters,
        IntPtr overlapped,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        int virtualDiskAccessMask,
        int flags,
        ref OpenParameters parameters,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    internal static extern int AttachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        IntPtr securityDescriptor,
        int flags,
        int providerSpecificFlags,
        ref AttachParameters parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    internal static extern int DetachVirtualDisk(SafeFileHandle virtualDiskHandle, int flags, int providerSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int GetVirtualDiskPhysicalPath(SafeFileHandle virtualDiskHandle, ref int diskPathSizeInBytes, [Out] char[] diskPath);
#pragma warning restore SYSLIB1054
}
