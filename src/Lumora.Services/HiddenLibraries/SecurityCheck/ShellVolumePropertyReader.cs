using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>
/// Liest <c>System.Volume.BitLockerProtection</c> direkt über die Windows-Shell (ADR-0064): Eigenschaftsschlüssel per
/// <c>PSGetPropertyKeyFromName</c>, Eigenschaften des Laufwerks per <c>SHGetPropertyStoreFromParsingName</c>, Wert per
/// <c>IPropertyStore.GetValue</c> — dieselbe Quelle wie das Schloss-Symbol im Explorer, ohne Administratorrechte und ohne
/// fremdes Programm. Läuft auf einem eigenen STA-Thread mit Zeitgrenze. Die Schnittstelle ist nur bis
/// <c>GetValue</c> deklariert: Lumora schreibt keine Eigenschaften.
/// </summary>
public sealed class ShellVolumePropertyReader : IVolumePropertyReader
{
    private const string BitLockerPropertyName = "System.Volume.BitLockerProtection";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public int? ReadBitLockerProtection(char driveLetter)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        if (!char.IsAsciiLetterUpper(letter) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        int? result = null;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = OperatingSystem.IsWindows() ? ReadOnStaThread(letter + @":\") : null;
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Lumora.SecurityCheck.Shell",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(Timeout))
        {
            throw new TimeoutException("Die Shell hat den BitLocker-Zustand nicht rechtzeitig geliefert.");
        }

        error?.Throw();
        return result;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int? ReadOnStaThread(string root)
    {
        Marshal.ThrowExceptionForHR(NativeMethods.PSGetPropertyKeyFromName(BitLockerPropertyName, out var key));
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(NativeMethods.SHGetPropertyStoreFromParsingName(root, IntPtr.Zero, 0, ref iid, out var store));
        try
        {
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try
            {
                if (value.VarType == 0)
                {
                    return null; // VT_EMPTY: Windows kennt keinen Wert
                }

                return NativeMethods.PropVariantToInt32(ref value, out var number) >= 0 ? number : null;
            }
            finally
            {
                _ = NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>PROPVARIANT (x64: 24 Byte) — nur Typ und Rohdaten, Umwandlung über <c>PropVariantToInt32</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Data1;
        public IntPtr Data2;
    }

    /// <summary>IPropertyStore bis <c>GetValue</c> (Reihenfolge der vtable); Schreib-Methoden bewusst nicht deklariert.</summary>
    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);
    }

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier (wie VolumeDeviceQuery).
    private static class NativeMethods
    {
        [DllImport("propsys.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int SHGetPropertyStoreFromParsingName(
            string path,
            IntPtr bindContext,
            int flags,
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("propsys.dll", ExactSpelling = true)]
        public static extern int PropVariantToInt32(ref PropVariant value, out int number);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int PropVariantClear(ref PropVariant value);
    }
#pragma warning restore SYSLIB1054
}
