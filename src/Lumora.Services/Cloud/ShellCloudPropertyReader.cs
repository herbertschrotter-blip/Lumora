using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Infrastructure;
using Lumora.Services.Metadata;

namespace Lumora.Services.Cloud;

/// <summary>Eigenschaften eines Platzhalters ohne Download — austauschbar für Tests.</summary>
public interface ICloudPropertyReader
{
    /// <summary>
    /// Aufnahmedatum aus dem Eigenschaftsspeicher der Shell (<c>System.Photo.DateTaken</c>), ohne die Datei zu laden; null =
    /// keins geliefert oder nicht verfügbar. Wirft nicht.
    /// </summary>
    CaptureDateValue? ReadCaptureDate(string path);
}

/// <summary>
/// Liest das Aufnahmedatum eines OneDrive-Platzhalters über <c>SHGetPropertyStoreFromParsingName</c> mit Standard-Flags
/// (<c>GPS_DEFAULT</c>; Spike 19: Datum 30 von 30, GPS 0 von 30, kein Download). Die Shell liefert es als UTC — umgerechnet
/// wie ein Video-Datum in die Zeitzone dieses PCs (<see cref="CaptureDateValue.FromUtc"/>), Herkunft
/// <see cref="CaptureDateSource.Cloud"/> (vorläufig: ohne <c>OffsetTimeOriginal</c>, wird aus der Datei neu gelesen, sobald
/// sie lokal ist). Läuft auf zwei eigenen
/// STA-Threads mit Zeitgrenze. In der versteckten Bibliothek nie (keine Spuren in der Shell; Cloud-Ordner sind dort ohnehin
/// gesperrt). Die Schnittstelle ist nur bis <c>GetValue</c> deklariert: Lumora schreibt keine Eigenschaften.
/// </summary>
public sealed class ShellCloudPropertyReader : ICloudPropertyReader, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>System.Photo.DateTaken.</summary>
    private static readonly PropertyKey DateTakenKey = new() { FormatId = new Guid("14B81DA1-0135-4D31-96D9-6CBFC9671A99"), PropertyId = 36867 };

    private readonly StaWorkerPool _pool = new(2, "Lumora.Cloud.Eigenschaften");
    private readonly ICatalogContext? _catalog;
    private readonly ILogger<ShellCloudPropertyReader> _logger;

    public ShellCloudPropertyReader(ILogger<ShellCloudPropertyReader> logger, ICatalogContext? catalog = null)
    {
        _logger = logger;
        _catalog = catalog;
    }

    public CaptureDateValue? ReadCaptureDate(string path)
    {
        if (string.IsNullOrEmpty(path) || _catalog?.IsHidden == true || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var task = _pool.RunAsync(_ => OperatingSystem.IsWindows() ? ReadDateTakenUtc(path) : null);
            if (!task.Wait(Timeout))
            {
                _logger.LogDebug("Cloud: Eigenschaften nicht rechtzeitig geliefert");
                return null;
            }

            return task.Result is { } utc && CaptureDateValue.IsPlausible(utc) ? CaptureDateValue.FromUtc(utc, CaptureDateSource.Cloud) : null;
        }
        catch (Exception ex) when (ex is AggregateException or COMException or InvalidCastException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Cloud: Aufnahmedatum über die Shell nicht lesbar");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static DateTime? ReadDateTakenUtc(string path)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (NativeMethods.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0 /* GPS_DEFAULT */, ref iid, out var store) < 0 || store is null)
        {
            return null;
        }

        try
        {
            var key = DateTakenKey;
            if (store.GetValue(ref key, out var value) < 0)
            {
                return null;
            }

            try
            {
                if (value.VarType == 0)
                {
                    return null; // VT_EMPTY
                }

                return NativeMethods.PropVariantToFileTime(ref value, 0 /* PSTF_UTC */, out var fileTime) >= 0
                    ? DateTime.FromFileTimeUtc(fileTime)
                    : null;
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

    public void Dispose() => _pool.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>PROPVARIANT (x64: 24 Byte) — nur Typ und Rohdaten, Umwandlung über <c>PropVariantToFileTime</c>.</summary>
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

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier (wie ShellVolumePropertyReader).
    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int SHGetPropertyStoreFromParsingName(
            string path,
            IntPtr bindContext,
            int flags,
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("propsys.dll", ExactSpelling = true)]
        public static extern int PropVariantToFileTime(ref PropVariant value, int flags, out long fileTime);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int PropVariantClear(ref PropVariant value);
    }
#pragma warning restore SYSLIB1054
}
