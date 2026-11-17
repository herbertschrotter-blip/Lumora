using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Services.Cloud;
using Lumora.Services.Infrastructure;

namespace Lumora.Thumbnails.Cloud;

/// <summary>Vorschau eines OneDrive-Platzhalters ohne Download — für die Lightbox; austauschbar für Tests.</summary>
public interface ICloudPreviewSource
{
    /// <summary>
    /// Shell-Vorschaubild, eingepasst in <paramref name="boxSize"/> × <paramref name="boxSize"/> (lange Kante höchstens so
    /// groß), eingefroren; null = keins (keine Verbindung, Zeitgrenze, versteckte Sitzung).
    /// </summary>
    Task<BitmapSource?> GetPreviewAsync(string path, int boxSize, CancellationToken ct);
}

/// <summary>
/// Holt Vorschaubilder über die Windows-Shell wie der Explorer (ADR-0077, Spike 19): <c>SHCreateItemFromParsingName</c> →
/// <c>IShellItemImageFactory::GetImage</c> mit <c>SIIGBF_THUMBNAILONLY</c> (nie ein Symbol, nie das Original). Für einen
/// Platzhalter holt OneDrive das Bild aus der Cloud — die Datei bleibt dabei online. Solange die Shell
/// <c>WTS_E_EXTRACTIONPENDING</c> meldet, wird alle 100 ms erneut gefragt, höchstens <see cref="ExtractionTimeout"/>.
/// Alles läuft auf <see cref="MaxConcurrency"/> STA-Threads (Shell-COM braucht STA; 8 gleichzeitig ≈ 4 Bilder/s). In der
/// versteckten Sitzung nie (der Vorschaubild-Cache von Windows bekäme Spuren; Cloud-Ordner sind dort ohnehin gesperrt).
/// <para>
/// Im App-Container der Claude-App liefert die Shell für Platzhalter keine Bilder (Spike 19); Lumora läuft nativ. Die Suite
/// prüft den Weg deshalb mit Fakes, die echte Prüfung läuft nativ (<c>OneDriveNativeProbeTests</c>, nur mit
/// <c>LUMORA_ONEDRIVE_PROBE</c>).
/// </para>
/// </summary>
public sealed class ShellThumbnailExtractor : ICloudPreviewSource, IDisposable
{
    /// <summary>Gleichzeitige Shell-Abrufe (Spike 19).</summary>
    public const int MaxConcurrency = 8;

    /// <summary>So lange wird bei „Erzeugung läuft noch“ erneut gefragt.</summary>
    public static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private const int ExtractionPending = unchecked((int)0x8004B205); // WTS_E_EXTRACTIONPENDING
    private const int ThumbnailOnly = 0x08; // SIIGBF_THUMBNAILONLY

    private readonly StaWorkerPool _pool = new(MaxConcurrency, "Lumora.Cloud.Vorschau");
    private readonly ICatalogContext? _catalog;

    public ShellThumbnailExtractor(ICatalogContext? catalog = null)
    {
        _catalog = catalog;
    }

    /// <summary>False in der versteckten Sitzung.</summary>
    public bool IsAvailable => _catalog?.IsHidden != true;

    public Task<BitmapSource?> GetPreviewAsync(string path, int boxSize, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(boxSize, 16);
        return IsAvailable
            ? _pool.RunAsync(token => Extract(path, boxSize, token), ct)
            : Task.FromResult<BitmapSource?>(null);
    }

    /// <summary>Auf einem STA-Thread: Vorschaubild holen (mit Wiederholen) und als eingefrorenes Bgr24-Bitmap kopieren.</summary>
    private static BitmapSource? Extract(string path, int boxSize, CancellationToken ct)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) < 0 || factory is null)
        {
            return null;
        }

        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var hr = factory.GetImage(new NativeSize { Width = boxSize, Height = boxSize }, ThumbnailOnly, out var bitmap);
                if (hr == ExtractionPending && clock.Elapsed < ExtractionTimeout)
                {
                    Thread.Sleep(RetryDelay); // OneDrive holt das Bild gerade aus der Cloud
                    continue;
                }

                if (hr < 0 || bitmap == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Copy(bitmap);
                }
                finally
                {
                    NativeMethods.DeleteObject(bitmap);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>HBITMAP → eigenständiges, eingefrorenes Bitmap (Bgr24: Fotos sind deckend, JPEG kennt kein Alpha).</summary>
    private static BitmapSource Copy(IntPtr bitmap)
    {
        var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var cached = new CachedBitmap(converted, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }

    public void Dispose() => _pool.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier (wie die Shell-Leser in Services).
    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int SHCreateItemFromParsingName(
            string path,
            IntPtr bindContext,
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr handle);
    }
#pragma warning restore SYSLIB1054
}
