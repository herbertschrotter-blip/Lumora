using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Screenshot eines eigenen Fensters ohne Maus-/Tastatureingaben und ohne Bildschirmausschnitt (der könnte
/// fremde Fenster enthalten). Zwei Quellen, beide auf das eigene Fenster beschränkt:
/// <list type="bullet">
/// <item><see cref="RenderTargetBitmap"/> des WPF-Visualbaums — vollständiger WPF-Inhalt, aber ohne Mica-Backdrop
/// und ohne HwndHost-Inhalte (LibVLC-VideoView bleibt schwarz).</item>
/// <item><c>PrintWindow(PW_RENDERFULLCONTENT)</c> auf das eigene Fenster-Handle — liefert bei WPF-Fenstern mit
/// DirectX-Composition nur die Hintergrundfläche, aber die Child-HWNDs (VideoView) korrekt.</item>
/// </list>
/// Für die Lightbox im Video-Modus werden beide kombiniert: WPF-Fenster + Video-Ausschnitt aus PrintWindow +
/// das transparente LibVLC-Vordergrundfenster mit der Overlay-Toolbar. PNG-Encode über WPF (kein System.Drawing).
/// </summary>
internal static class WindowCapture
{
    private const uint PwRenderFullContent = 0x00000002;

    /// <summary>Speichert das Fenster als PNG und liefert die verwendete Methode (null = kein Bild möglich).</summary>
    public static string? TrySave(Window window, string path, FrameworkElement? hwndHost = null, Window? overlayWindow = null)
    {
        var rendered = RenderVisual(window);
        if (rendered is null)
        {
            return null;
        }

        var method = "RenderTargetBitmap";
        if (hwndHost is { IsVisible: true } && hwndHost.ActualWidth > 0 && hwndHost.ActualHeight > 0)
        {
            var printed = PrintOwnWindow(window);
            if (printed is not null)
            {
                rendered = Composite(window, rendered, printed, hwndHost, overlayWindow);
                method = "RenderTargetBitmap + PrintWindow(HwndHost)" + (overlayWindow is null ? string.Empty : " + Overlay");
            }
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return method;
    }

    /// <summary>
    /// Fenster samt offener Popups (Rechtsklick-Menü, Untermenü) als PNG: RenderTargetBitmap sieht nur den Visualbaum des
    /// Fensters, Popups leben in eigenen Fenstern — sie werden an ihrer Bildschirm-Lage dazugezeichnet (ADR-0078, Rauchtest).
    /// </summary>
    public static string? TrySaveWithPopups(Window window, string path, IEnumerable<FrameworkElement> popups)
    {
        var width = window.ActualWidth;
        var height = window.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var target = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var origin = window.PointToScreen(new Point(0, 0));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = window.Background as SolidColorBrush;
            context.DrawRectangle(background is { Color.A: 255 } ? background : ThemeFallbackBackground(), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(window) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            foreach (var popup in popups.Where(p => p.IsVisible && p.ActualWidth > 0 && p.ActualHeight > 0))
            {
                var at = popup.PointToScreen(new Point(0, 0));
                var rect = new Rect((at.X - origin.X) / dpi.DpiScaleX, (at.Y - origin.Y) / dpi.DpiScaleY, popup.ActualWidth, popup.ActualHeight);
                context.DrawRectangle(ThemeFallbackBackground(), null, rect);
                context.DrawRectangle(new VisualBrush(popup) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, rect);
            }
        }

        target.Render(visual);
        target.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return "RenderTargetBitmap + Popups";
    }

    /// <summary>WPF-Visualbaum des Fensters in Gerätepixeln; transparenter Window.Background (Mica) wird gefüllt.</summary>
    private static BitmapSource? RenderVisual(Window window)
    {
        var width = window.ActualWidth;
        var height = window.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = window.Background as SolidColorBrush;
            context.DrawRectangle(
                background is { Color.A: 255 } ? background : ThemeFallbackBackground(),
                null,
                new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(window) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }

        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static SolidColorBrush ThemeFallbackBackground()
    {
        // Mica-Fenster: Hintergrund aus dem aktiven WPF-UI-Theme, sonst neutrales Dunkelgrau.
        if (Application.Current?.TryFindResource("ApplicationBackgroundBrush") is SolidColorBrush brush && brush.Color.A > 0)
        {
            return brush;
        }

        return new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    }

    /// <summary>Video-Ausschnitt aus PrintWindow in das gerenderte WPF-Bild kopieren und das Overlay-Fenster darüberlegen.</summary>
    private static BitmapSource Composite(Window window, BitmapSource rendered, BitmapSource printed, FrameworkElement hwndHost, Window? overlayWindow)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var hostRectDip = hwndHost.TransformToAncestor(window).TransformBounds(new Rect(0, 0, hwndHost.ActualWidth, hwndHost.ActualHeight));
        var hostRectPx = new Int32Rect(
            (int)Math.Round(hostRectDip.X * dpi.DpiScaleX),
            (int)Math.Round(hostRectDip.Y * dpi.DpiScaleY),
            (int)Math.Round(hostRectDip.Width * dpi.DpiScaleX),
            (int)Math.Round(hostRectDip.Height * dpi.DpiScaleY));

        // PrintWindow liefert das Fensterrechteck inkl. unsichtbarer Rahmen; auf den Client-Bereich ausrichten.
        var hwnd = new WindowInteropHelper(window).Handle;
        var offset = ClientOffset(hwnd);
        var sourceRect = new Int32Rect(hostRectPx.X + offset.X, hostRectPx.Y + offset.Y, hostRectPx.Width, hostRectPx.Height);
        if (sourceRect.X < 0 || sourceRect.Y < 0 || sourceRect.X + sourceRect.Width > printed.PixelWidth || sourceRect.Y + sourceRect.Height > printed.PixelHeight)
        {
            return rendered;
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(rendered, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            context.DrawImage(new CroppedBitmap(printed, sourceRect), hostRectDip);
            if (overlayWindow is { IsVisible: true } && overlayWindow.ActualWidth > 0)
            {
                var overlay = new RenderTargetBitmap(
                    (int)Math.Ceiling(overlayWindow.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(overlayWindow.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX,
                    dpi.PixelsPerInchY,
                    PixelFormats.Pbgra32);
                overlay.Render(overlayWindow);
                context.DrawImage(overlay, hostRectDip);
            }
        }

        var target = new RenderTargetBitmap(rendered.PixelWidth, rendered.PixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Versatz des Client-Bereichs innerhalb des Fensterrechtecks (Gerätepixel).</summary>
    private static (int X, int Y) ClientOffset(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var windowRect))
        {
            return (0, 0);
        }

        var origin = new NativePoint { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);
        return (origin.X - windowRect.Left, origin.Y - windowRect.Top);
    }

    private static BitmapSource? PrintOwnWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var info = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
        };
        var bitmap = CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!PrintWindow(hwnd, memoryDc, PwRenderFullContent))
            {
                return null;
            }

            GdiFlush();
            var stride = width * 4;
            var pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255; // Alpha aus GDI ist undefiniert
            }

            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Das transparente LibVLCSharp-Vordergrundfenster (Overlay) zu einem Lightbox-Fenster, falls vorhanden.</summary>
    public static Window? FindVlcForegroundWindow(Window owner)
        => Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => !ReferenceEquals(w, owner) && w.GetType().Name == "ForegroundWindow" && ReferenceEquals(w.Owner, owner));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
