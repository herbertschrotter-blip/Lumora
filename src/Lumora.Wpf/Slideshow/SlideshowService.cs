using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Lumora.Lightbox;

namespace Lumora.Slideshow;

/// <summary>
/// Singleton-Implementierung von <see cref="ISlideshowService"/>: lädt die Einstellungen, legt bei „zufällig“ eine
/// <see cref="ShuffledSlideshowSequence"/> darüber, erzeugt ViewModel, Video-Player und <see cref="SlideshowWindow"/> und
/// zeigt es rahmenlos auf dem ganzen Bildschirm des Hauptfensters (Mehrschirm wie die Lightbox). Höchstens eine Diashow;
/// ein neuer Start schließt die laufende. Das Fenster besitzt ViewModel und Player und gibt sie beim Schließen frei.
/// </summary>
public sealed class SlideshowService : ISlideshowService, IDisposable
{
    private readonly ISlideshowItemLoader _loader;
    private readonly ISlideshowImageDecoder _decoder;
    private readonly ISlideshowSettingsStore _settings;
    private readonly LibVlcHost _vlc;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<SlideshowService> _logger;
    private readonly Lumora.Services.Usage.IUsageSignals? _usage;
    private SlideshowWindow? _window;
    private int _startVersion;

    // LibVlcHost wird injiziert, damit er vor diesem Dienst entsteht und nach ihm freigegeben wird (Player vor LibVLC).
    public SlideshowService(
        ISlideshowItemLoader loader,
        ISlideshowImageDecoder decoder,
        ISlideshowSettingsStore settings,
        LibVlcHost vlc,
        ILoggerFactory loggers,
        Lumora.Services.Usage.IUsageSignals? usage = null)
    {
        _usage = usage; // Nutzungs-Signale: Diashow zählt gedämpft (ADR-0072)
        _loader = loader;
        _decoder = decoder;
        _settings = settings;
        _vlc = vlc;
        _loggers = loggers;
        _logger = loggers.CreateLogger<SlideshowService>();
    }

    public bool IsOpen => _window is not null;

    /// <summary>Offenes Fenster (Rauchtest).</summary>
    internal SlideshowWindow? Window => _window;

    public void Start(IReadOnlyList<int> fileIds, int startIndex = 0, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        Start(new ListSlideshowSequence(fileIds), startIndex, title);
    }

    public void Start(ISlideshowSequence sequence, int startIndex = 0, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.Count == 0)
        {
            _logger.LogInformation("Diashow ohne Dateien nicht gestartet");
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => Start(sequence, startIndex, title));
            return;
        }

        _ = StartAsync(sequence, startIndex, title, ++_startVersion);
    }

    private async Task StartAsync(ISlideshowSequence sequence, int startIndex, string? title, int version)
    {
        try
        {
            var settings = await _settings.LoadAsync().ConfigureAwait(true);
            if (version != _startVersion)
            {
                return; // inzwischen erneut gestartet
            }

            startIndex = Math.Clamp(startIndex, 0, sequence.Count - 1);
            if (settings.Order == SlideshowOrder.Random && sequence.Count > 1)
            {
                sequence = new ShuffledSlideshowSequence(sequence, startIndex, Random.Shared);
                startIndex = 0;
            }

            _window?.Close(); // eine Diashow zur Zeit
            var window = CreateWindow(sequence, startIndex, title, settings);
            _window = window;
            window.Show();
            _logger.LogInformation(
                "Diashow gestartet: {Count} Dateien, Start {Index}, {Order}, {Seconds} s, Zoom {Zoom}, Videos {Videos}",
                sequence.Count, startIndex, settings.Order, settings.SlideSeconds, settings.KenBurns, settings.PlayVideos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diashow konnte nicht gestartet werden");
        }
    }

    private SlideshowWindow CreateWindow(ISlideshowSequence sequence, int startIndex, string? title, SlideshowSettings settings)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        var video = new SlideshowVideoPlayer(_vlc, _loggers.CreateLogger<SlideshowVideoPlayer>());
        var viewModel = new SlideshowViewModel(
            sequence,
            startIndex,
            title,
            settings,
            _loader,
            _decoder,
            video,
            TimeProvider.System,
            action =>
            {
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvoke(action);
                }
            },
            _loggers.CreateLogger<SlideshowViewModel>(),
            _settings)
        {
            UsageTracker = _usage?.CreateTracker(Lumora.Services.Usage.UsageViewContext.Slideshow), // ADR-0072
        };
        var window = new SlideshowWindow(viewModel, video, _loggers.CreateLogger<SlideshowWindow>());

        var owner = Application.Current?.MainWindow;
        var ownerHandle = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
        if (owner is not null && ownerHandle != IntPtr.Zero && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        if (MonitorBoundsDip(owner, ownerHandle) is { } bounds)
        {
            window.Left = bounds.Left;
            window.Top = bounds.Top;
            window.Width = bounds.Width;
            window.Height = bounds.Height;
        }
        else
        {
            window.WindowState = WindowState.Maximized;
        }

        window.Closed += OnWindowClosed;
        return window;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is SlideshowWindow window)
        {
            window.Closed -= OnWindowClosed;
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }

            _logger.LogInformation("Diashow beendet ({Skipped} Dateien übersprungen)", window.ViewModel.SkippedCount);
        }
    }

    /// <summary>Ganzer Bildschirm (inkl. Taskleiste) des Monitors, auf dem das Hauptfenster liegt — in DIPs des Hauptfensters.</summary>
    private Rect? MonitorBoundsDip(Window? owner, IntPtr ownerHandle)
    {
        try
        {
            if (owner is null || ownerHandle == IntPtr.Zero)
            {
                return null;
            }

            var monitor = NativeMethods.MonitorFromWindow(ownerHandle, NativeMethods.MonitorDefaultToNearest);
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            var transform = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = transform.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
            var bottomRight = transform.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));
            return new Rect(topLeft, bottomRight);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow: Bildschirm des Hauptfensters nicht ermittelt — maximiert");
            return null;
        }
    }

    public void Dispose()
    {
        var window = _window;
        _window = null;
        try
        {
            window?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow-Fenster konnte beim Beenden nicht geschlossen werden");
        }
    }

    private static class NativeMethods
    {
        public const uint MonitorDefaultToNearest = 2;

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
    }
}
