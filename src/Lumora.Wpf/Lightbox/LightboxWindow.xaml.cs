using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Lumora.Shell;
using Lumora.Shell.Dialogs;
using Wpf.Ui.Controls;

namespace Lumora.Lightbox;

/// <summary>
/// Lightbox-Fenster (FluentWindow, schwarz, eigene Titelleiste, Multi-Monitor-fähig). Code-Behind
/// enthält nur Fenster-/Tastatur-Mechanik: Tastenbelegung, Vollbild, Auto-Hide der Toolbar,
/// Bestätigungsdialog und die Anbindung an LibVLCSharps Vordergrundfenster (Airspace).
/// </summary>
public partial class LightboxWindow : AppFluentWindow
{
    private const int MinDecodePixelLimit = 3840;

    private readonly LightboxViewModel _viewModel;
    private readonly ILogger<LightboxWindow> _logger;
    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<Window> _hookedForegroundWindows = new();

    private bool _isFullscreen;
    private Rect _restoreBounds;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;
    private bool _closed;

    public LightboxWindow(LightboxViewModel viewModel, ILogger<LightboxWindow> logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.ZoomTarget = ZoomPan;
        viewModel.ConfirmAsync = ConfirmAsync;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.ToggleFullscreenRequested += OnToggleFullscreenRequested;

        _chromeTimer.Tick += OnChromeTimerTick;

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseMove += OnAnyMouseMove;
        PreviewMouseDown += OnAnyMouseMove;
        SizeChanged += (_, _) => UpdateDecodeLimit();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;

        VideoOverlay.Loaded += OnVideoOverlayLoaded;
        VideoOverlay.PreviewMouseMove += OnAnyMouseMove;
        VideoOverlay.SurfaceElement.MouseLeftButtonDown += OnVideoSurfaceMouseLeftButtonDown;
        InitPhotoMenu(); // Rechtsklick-Menü, Strg+C, Alt+Enter (ADR-0068, LightboxWindow.PhotoMenu.cs)
    }

    public LightboxViewModel ViewModel => _viewModel;

    /// <summary>Fensterrahmen im Normalzustand (auch während Vollbild/Maximiert) — für die Sitzungs-Merkung.</summary>
    public Rect NormalBounds => _isFullscreen ? _restoreBounds : RestoreBounds;

    public bool IsMaximizedOrFullscreenMaximized => _isFullscreen ? _restoreState == WindowState.Maximized : WindowState == WindowState.Maximized;

    public bool IsFullscreen => _isFullscreen;

    // ----- Lebenszyklus -----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateDecodeLimit();
        ShowChrome();
        Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _chromeTimer.Stop();
        try
        {
            _viewModel.StopVideo(); // VLC stoppen, bevor der HwndHost verschwindet
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StopVideo beim Schließen fehlgeschlagen");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.ToggleFullscreenRequested -= OnToggleFullscreenRequested;
        _viewModel.ZoomTarget = null;
        _viewModel.ConfirmAsync = null;

        try
        {
            VideoView.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VideoView.Dispose fehlgeschlagen");
        }

        _viewModel.Dispose();
        _hookedForegroundWindows.Clear();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (!_closed)
        {
            Close();
        }
    }

    private void OnToggleFullscreenRequested(object? sender, EventArgs e) => ToggleFullscreen();

    // ----- Tastatur -----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (HandlePhotoKeys(key))
        {
            e.Handled = true; // Strg+C, Alt+Enter (ADR-0068)
            return;
        }

        var handled = true;

        switch (key)
        {
            case Key.Escape:
                if (_isFullscreen)
                {
                    ExitFullscreen();
                }
                else
                {
                    Close();
                }

                break;
            case Key.Right:
                _viewModel.NextCommand.Execute(null);
                break;
            case Key.Left:
                _viewModel.PreviousCommand.Execute(null);
                break;
            case Key.Home:
                _viewModel.FirstCommand.Execute(null);
                break;
            case Key.End:
                _viewModel.LastCommand.Execute(null);
                break;
            case Key.PageDown:
                _viewModel.PageForwardCommand.Execute(null);
                break;
            case Key.PageUp:
                _viewModel.PageBackCommand.Execute(null);
                break;
            case Key.F:
                // 0.2.38: F schaltet die Gesichtsrahmen (vorher Vollbild — das bleibt auf F11).
                _ = _viewModel.ToggleFacesCommand.ExecuteAsync(null);
                break;
            case Key.F11:
                ToggleFullscreen();
                break;
            case Key.I:
                _viewModel.ToggleInfoCommand.Execute(null);
                break;
            case Key.S when !ctrl:
                _viewModel.StartSlideshowCommand.Execute(null); // „Diashow ab hier“ (ADR-0059)
                break;
            case Key.Delete:
                _ = _viewModel.DeleteCurrentCommand.ExecuteAsync(null);
                break;
            case Key.Space:
                if (_viewModel.IsVideo)
                {
                    _viewModel.PlayPauseCommand.Execute(null);
                }
                else
                {
                    handled = false;
                }

                break;
            case Key.M when ctrl:
                _ = _viewModel.ArchiveCurrentCommand.ExecuteAsync(null);
                break;
            case Key.M:
                if (_viewModel.IsVideo)
                {
                    _viewModel.ToggleMuteCommand.Execute(null);
                }
                else
                {
                    handled = false;
                }

                break;
            case Key.D when ctrl:
                _ = _viewModel.ToggleFavoriteCommand.ExecuteAsync(null);
                break;
            case Key.T when ctrl:
                _ = _viewModel.EditTagsCommand.ExecuteAsync(null);
                break;
            case Key.K when ctrl:
                _ = _viewModel.EditCategoriesCommand.ExecuteAsync(null);
                break;
            case Key.D0 when ctrl:
            case Key.NumPad0 when ctrl:
                _ = _viewModel.SetRatingCommand.ExecuteAsync("0");
                break;
            case Key.D1 when ctrl:
            case Key.NumPad1 when ctrl:
                _ = _viewModel.SetRatingCommand.ExecuteAsync("1");
                break;
            case Key.D2 when ctrl:
            case Key.NumPad2 when ctrl:
                _ = _viewModel.SetRatingCommand.ExecuteAsync("2");
                break;
            case Key.D3 when ctrl:
            case Key.NumPad3 when ctrl:
                _ = _viewModel.SetRatingCommand.ExecuteAsync("3");
                break;
            case Key.D4 when ctrl:
            case Key.NumPad4 when ctrl:
                _ = _viewModel.SetRatingCommand.ExecuteAsync("4");
                break;
            case Key.D5 when ctrl:
            case Key.NumPad5 when ctrl:
                _ = _viewModel.SetRatingCommand.ExecuteAsync("5");
                break;
            case Key.Add:
            case Key.OemPlus:
                if (_viewModel.IsImage)
                {
                    ZoomPan.ZoomIn();
                }

                break;
            case Key.Subtract:
            case Key.OemMinus:
                if (_viewModel.IsImage)
                {
                    ZoomPan.ZoomOut();
                }

                break;
            case Key.D1:
            case Key.NumPad1:
                if (_viewModel.IsImage)
                {
                    ZoomPan.ZoomToActualSize();
                }

                break;
            case Key.D0:
            case Key.NumPad0:
                if (_viewModel.IsImage)
                {
                    ZoomPan.ZoomToFit();
                }

                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            ShowChrome();
            e.Handled = true;
        }
    }

    // ----- Vollbild -----

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen)
        {
            return;
        }

        _restoreState = WindowState;
        _restoreStyle = WindowStyle;
        _restoreResize = ResizeMode;
        _restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

        var monitor = GetCurrentMonitorBoundsDip();
        TitleBar.Visibility = Visibility.Collapsed;
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        if (monitor is { } rect)
        {
            Left = rect.Left;
            Top = rect.Top;
            Width = rect.Width;
            Height = rect.Height;
        }
        else
        {
            WindowState = WindowState.Maximized; // Fallback (Muster aus Spike #03)
        }

        _isFullscreen = true;
        _viewModel.IsFullscreen = true;
        ShowChrome();
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        _isFullscreen = false;
        _viewModel.IsFullscreen = false;
        TitleBar.Visibility = Visibility.Visible;
        WindowStyle = _restoreStyle;
        ResizeMode = _restoreResize;
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        Left = _restoreBounds.Left;
        Top = _restoreBounds.Top;
        Width = _restoreBounds.Width;
        Height = _restoreBounds.Height;
        WindowState = _restoreState;
        ShowChrome();
    }

    /// <summary>Monitor-Rechteck (inkl. Taskleiste) des Monitors, auf dem das Fenster liegt, in DIPs.</summary>
    private Rect? GetCurrentMonitorBoundsDip()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            var source = PresentationSource.FromVisual(this);
            var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = transform.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
            var bottomRight = transform.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));
            return new Rect(topLeft, bottomRight);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Monitor-Rechteck konnte nicht ermittelt werden");
            return null;
        }
    }

    // ----- Auto-Hide der Toolbar -----

    private void OnAnyMouseMove(object sender, MouseEventArgs e) => ShowChrome();

    private void ShowChrome()
    {
        if (!_viewModel.IsChromeVisible)
        {
            _viewModel.IsChromeVisible = true;
        }

        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void OnChromeTimerTick(object? sender, EventArgs e)
    {
        _chromeTimer.Stop();
        if (ImageOverlay.IsChromeHovered || VideoOverlay.IsChromeHovered)
        {
            _chromeTimer.Start();
            return;
        }

        _viewModel.IsChromeVisible = false;
    }

    // ----- Video-Vordergrundfenster (LibVLCSharp.WPF ForegroundWindow) -----

    private void OnVideoOverlayLoaded(object sender, RoutedEventArgs e)
    {
        var foreground = Window.GetWindow(VideoOverlay);
        if (foreground is null || ReferenceEquals(foreground, this) || !_hookedForegroundWindows.Add(foreground))
        {
            return;
        }

        _logger.LogDebug("Video-Overlay an LibVLC-Vordergrundfenster gekoppelt");

        // Tasten landen im Vordergrundfenster, falls es doch Fokus bekommt → gleiche Belegung.
        foreground.PreviewKeyDown += OnPreviewKeyDown;
        foreground.PreviewMouseMove += OnAnyMouseMove;
        foreground.Closed += (_, _) => _hookedForegroundWindows.Remove(foreground);

        // WS_EX_NOACTIVATE: Klicks auf das Overlay sollen den Fokus beim Lightbox-Fenster lassen.
        TrySetNoActivate(foreground);
    }

    private void TrySetNoActivate(Window foreground)
    {
        try
        {
            var hwnd = new WindowInteropHelper(foreground).Handle;
            if (hwnd == IntPtr.Zero)
            {
                foreground.SourceInitialized += (_, _) => TrySetNoActivate(foreground);
                return;
            }

            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
            if ((style & NativeMethods.WsExNoActivate) == 0)
            {
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(style | NativeMethods.WsExNoActivate));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WS_EX_NOACTIVATE für das Video-Overlay konnte nicht gesetzt werden");
        }
    }

    private void OnVideoSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Activate();
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    // ----- Sonstiges -----

    private void UpdateDecodeLimit()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var screenPx = SystemParameters.PrimaryScreenWidth * dpi;
            var windowPx = ActualWidth * dpi;
            _viewModel.DecodePixelLimit = (int)Math.Max(MinDecodePixelLimit, 2 * Math.Max(screenPx, windowPx));
        }
        catch
        {
            _viewModel.DecodePixelLimit = MinDecodePixelLimit;
        }
    }

    private Task<bool> ConfirmAsync(string title, string message, string primaryButtonText)
    {
        // Eigenes Dialogfenster statt Wpf.Ui MessageBox (deren Größenlogik hängt bei umbrechendem Text).
        var outcome = AppDialogWindow.Show(this, new DialogRequest(title, message)
        {
            PrimaryText = primaryButtonText,
            PrimaryIsDestructive = true,
        });
        return Task.FromResult(outcome.Choice == DialogChoice.Primary);
    }

    private static class NativeMethods
    {
        public const uint MonitorDefaultToNearest = 2;
        public const int GwlExStyle = -20;
        public const long WsExNoActivate = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    }
}
