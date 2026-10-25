using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.Logging;
using Lumora.Shell.Dialogs;

namespace Lumora.Slideshow;

/// <summary>
/// Diashow-Fenster (ADR-0059): rahmenloses Vollbild, schwarz. Code-Behind enthält nur Fenster-Mechanik — Tasten,
/// Mausbewegung (Leiste/Mauszeiger), Überblendung zwischen zwei Bild-Ebenen, langsamer Zoom als Animation von Skalierung
/// und Verschiebung (beide linear, so bleibt der Ausschnitt im Bild), Fortschrittsbalken, Anbindung des LibVLC-Players
/// und „Bildschirm nicht abschalten“, solange die Diashow läuft. Der Ablauf liegt im <see cref="SlideshowViewModel"/>.
/// </summary>
public partial class SlideshowWindow : AppFluentWindow
{
    /// <summary>Dauer der Überblendung.</summary>
    public static readonly TimeSpan CrossfadeDuration = TimeSpan.FromSeconds(1);

    /// <summary>Reserve über der Bildschirmgröße beim Dekodieren (Zoom bis 12 % bleibt scharf).</summary>
    private const double DecodeZoomReserve = KenBurnsPlanner.DefaultZoom;

    private readonly SlideshowViewModel _viewModel;
    private readonly SlideshowVideoPlayer? _video;
    private readonly ILogger _logger;
    private readonly HashSet<Window> _hookedForegroundWindows = new();
    private Image? _front;
    private SlideshowSlide? _frontSlide;
    private Point? _lastMouse;
    private bool _closed;

    public SlideshowWindow(SlideshowViewModel viewModel, SlideshowVideoPlayer? video, ILogger logger)
    {
        _viewModel = viewModel;
        _video = video;
        _logger = logger;
        DataContext = viewModel;
        InitializeComponent();
        // FluentWindow setzt mit ExtendsContentIntoTitleBar wieder einen Rahmen-Stil — hier, nach dem Fenster-Handle und beim
        // Laden auf rahmenlos stellen (wie das Vollbild der Lightbox).
        MakeBorderless();
        SourceInitialized += (_, _) => MakeBorderless();
        PrepareLayer(LayerA);
        PrepareLayer(LayerB);

        viewModel.SlideChanged += OnSlideChanged;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (video is not null)
        {
            video.PlayerCreated += OnPlayerCreated;
        }

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseDown += (_, _) => _viewModel.NotifyUserActivity();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        // Nutzungs-Signale (ADR-0072): Folien zählen nur, solange die Diashow vorne ist
        Activated += (_, _) => _viewModel.SetUsageActive(WindowState != WindowState.Minimized);
        Deactivated += (_, _) => _viewModel.SetUsageActive(false);
        StateChanged += (_, _) => _viewModel.SetUsageActive(IsActive && WindowState != WindowState.Minimized);
        VideoOverlay.Loaded += OnVideoOverlayLoaded;
    }

    public SlideshowViewModel ViewModel => _viewModel;

    /// <summary>Video-Player (Rauchtest: läuft das Video?).</summary>
    internal SlideshowVideoPlayer? VideoPlayer => _video;

    /// <summary>Sichtbare Bild-Ebene (Tests).</summary>
    internal Image? FrontLayer => _front;

    // ----- Lebenszyklus -----

    private void MakeBorderless()
    {
        if (WindowStyle != WindowStyle.None)
        {
            WindowStyle = WindowStyle.None;
        }

        ResizeMode = ResizeMode.NoResize;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MakeBorderless();
        UpdateDecodeSize();
        KeepDisplayOn(true);
        Activate();
        Focus();
        _ = _viewModel.StartAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            _video?.Stop(); // VLC stoppen, bevor der HwndHost verschwindet
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow: Video beim Schließen nicht gestoppt");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        KeepDisplayOn(false);
        _viewModel.SlideChanged -= OnSlideChanged;
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        if (_video is not null)
        {
            _video.PlayerCreated -= OnPlayerCreated;
        }

        try
        {
            VideoView.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow: VideoView.Dispose fehlgeschlagen");
        }

        _video?.Dispose();
        ClearLayer(LayerA);
        ClearLayer(LayerB);
        _front = null;
        _frontSlide = null;
        _hookedForegroundWindows.Clear();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (!_closed)
        {
            Close();
        }
    }

    private void OnPlayerCreated(object? sender, EventArgs e)
    {
        if (!_closed && _video?.Player is { } player)
        {
            VideoView.MediaPlayer = player;
        }
    }

    // ----- Tasten und Maus -----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleKey(key))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Tastenbelegung: ←/→ blättern, Leertaste Pause/Weiter, Pos1/Ende Anfang/Ende, Esc beendet, F/F11 ohne Wirkung (die
    /// Diashow bleibt Vollbild). True = behandelt.
    /// </summary>
    internal bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Escape:
                _viewModel.Close();
                return true;
            case Key.Right:
                _viewModel.NextCommand.Execute(null);
                break;
            case Key.Left:
                _viewModel.PreviousCommand.Execute(null);
                break;
            case Key.Space:
                _viewModel.TogglePause();
                break;
            case Key.Home:
                _viewModel.FirstCommand.Execute(null);
                break;
            case Key.End:
                _viewModel.LastCommand.Execute(null);
                break;
            case Key.F:
            case Key.F11:
                break; // Vollbild bleibt
            default:
                return false;
        }

        _viewModel.NotifyUserActivity();
        return true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // WPF meldet MouseMove auch, wenn sich unter dem ruhenden Zeiger nur das Layout ändert (Leiste blendet aus) —
        // nur echte Bewegung zählt, sonst käme die Leiste sofort zurück.
        var position = e.GetPosition(this);
        if (_lastMouse is { } last && Math.Abs(last.X - position.X) < 2 && Math.Abs(last.Y - position.Y) < 2)
        {
            return;
        }

        _lastMouse = position;
        _viewModel.IsChromeHovered = ImageOverlay.IsBarHovered || VideoOverlay.IsBarHovered;
        _viewModel.NotifyUserActivity();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SlideshowViewModel.IsChromeVisible):
                var cursor = _viewModel.IsChromeVisible ? null : Cursors.None;
                Cursor = cursor;
                VideoOverlay.Cursor = cursor;
                if (!_viewModel.IsChromeVisible)
                {
                    _viewModel.IsChromeHovered = false;
                }

                break;
            case nameof(SlideshowViewModel.IsPaused):
                if (_viewModel.IsPaused)
                {
                    FreezeZoom();
                    ImageOverlay.PauseProgress();
                    VideoOverlay.PauseProgress();
                }
                else
                {
                    var remaining = _viewModel.Remaining;
                    ResumeZoom(remaining);
                    ImageOverlay.ResumeProgress(remaining);
                    VideoOverlay.ResumeProgress(remaining);
                }

                break;
            case nameof(SlideshowViewModel.KenBurnsEnabled):
                if (!_viewModel.KenBurnsEnabled)
                {
                    FreezeZoom(); // ab der nächsten Folie ohne Zoom; die aktuelle bleibt, wo sie ist
                }

                break;
        }
    }

    // ----- Folien: Überblendung und langsamer Zoom -----

    private void OnSlideChanged(object? sender, SlideshowSlide slide)
    {
        if (_closed)
        {
            return;
        }

        ImageOverlay.StartProgress(slide.Duration, _viewModel.IsPaused);
        VideoOverlay.StartProgress(slide.Duration, _viewModel.IsPaused);

        if (slide.IsVideo || slide.Image is null)
        {
            // Unter dem Video nichts stehen lassen: beim Zurück zu Bildern blendet das neue Bild aus Schwarz ein.
            ClearLayer(LayerA);
            ClearLayer(LayerB);
            _front = null;
            _frontSlide = null;
            return;
        }

        var incoming = ReferenceEquals(_front, LayerA) ? LayerB : LayerA;
        var outgoing = _front;
        incoming.BeginAnimation(OpacityProperty, null);
        incoming.Opacity = 0;
        incoming.Source = slide.Image;
        var (width, height) = FitSize(slide.Image.PixelWidth, slide.Image.PixelHeight, Stage.ActualWidth, Stage.ActualHeight);
        incoming.Width = width;
        incoming.Height = height;
        Panel.SetZIndex(incoming, 1);
        if (outgoing is not null)
        {
            Panel.SetZIndex(outgoing, 0);
        }

        SetZoom(incoming, slide.Plan.Start, width, height);
        _front = incoming;
        _frontSlide = slide;
        if (!_viewModel.IsPaused)
        {
            AnimateZoom(incoming, slide.Plan.Start, slide.Plan.End, width, height, slide.Duration + CrossfadeDuration);
        }

        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, CrossfadeDuration));
        if (outgoing is not null)
        {
            var fadeOut = new DoubleAnimation(outgoing.Opacity, 0, CrossfadeDuration);
            fadeOut.Completed += (_, _) =>
            {
                if (!ReferenceEquals(outgoing, _front))
                {
                    ClearLayer(outgoing); // Bitmap freigeben (Speicher bleibt bei langen Diashows stabil)
                }
            };
            outgoing.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    /// <summary>Eingepasste Größe (Uniform) eines Bildes in der Bühne.</summary>
    internal static (double Width, double Height) FitSize(int pixelWidth, int pixelHeight, double stageWidth, double stageHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || stageWidth <= 0 || stageHeight <= 0)
        {
            return (Math.Max(1, stageWidth), Math.Max(1, stageHeight));
        }

        var aspect = (double)pixelWidth / pixelHeight;
        return stageWidth / stageHeight > aspect
            ? (stageHeight * aspect, stageHeight)
            : (stageWidth, stageWidth / aspect);
    }

    private static void PrepareLayer(Image layer)
    {
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(1, 1));
        group.Children.Add(new TranslateTransform(0, 0));
        layer.RenderTransform = group;
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) Transforms(Image layer)
    {
        var group = (TransformGroup)layer.RenderTransform;
        return ((ScaleTransform)group.Children[0], (TranslateTransform)group.Children[1]);
    }

    private static void SetZoom(Image layer, SlideshowViewRect rect, double width, double height)
    {
        var (scale, translate) = Transforms(layer);
        var (s, tx, ty) = rect.ToTransform(width, height);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        scale.ScaleX = s;
        scale.ScaleY = s;
        translate.X = tx;
        translate.Y = ty;
    }

    private static void AnimateZoom(Image layer, SlideshowViewRect from, SlideshowViewRect to, double width, double height, TimeSpan duration)
    {
        if (from == to || duration <= TimeSpan.Zero)
        {
            return;
        }

        var (scale, translate) = Transforms(layer);
        var (s0, x0, y0) = from.ToTransform(width, height);
        var (s1, x1, y1) = to.ToTransform(width, height);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s0, s1, duration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s0, s1, duration));
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x0, x1, duration));
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y0, y1, duration));
    }

    /// <summary>Zoom der sichtbaren Ebene an der aktuellen Stelle anhalten.</summary>
    private void FreezeZoom()
    {
        if (_front is null)
        {
            return;
        }

        var (scale, translate) = Transforms(_front);
        var (s, x, y) = (scale.ScaleX, translate.X, translate.Y);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        scale.ScaleX = s;
        scale.ScaleY = s;
        translate.X = x;
        translate.Y = y;
    }

    /// <summary>Nach der Pause: von der aktuellen Stelle zum Ziel der Folie in der Restzeit.</summary>
    private void ResumeZoom(TimeSpan remaining)
    {
        if (_front is null || _frontSlide is not { } slide || !_viewModel.KenBurnsEnabled || slide.Plan.IsStatic)
        {
            return;
        }

        var (scale, translate) = Transforms(_front);
        var (s1, x1, y1) = slide.Plan.End.ToTransform(_front.Width, _front.Height);
        var duration = remaining + CrossfadeDuration;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, s1, duration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, s1, duration));
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(translate.X, x1, duration));
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(translate.Y, y1, duration));
    }

    private static void ClearLayer(Image layer)
    {
        layer.BeginAnimation(OpacityProperty, null);
        layer.Opacity = 0;
        layer.Source = null;
        SetZoom(layer, SlideshowViewRect.Full, 1, 1);
    }

    // ----- Größe, Video-Vordergrundfenster, Bildschirm wach halten -----

    private void UpdateDecodeSize()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            // Maximiert (Rückfall ohne Monitor-Rechteck) sind Width/Height NaN — dann die tatsächliche bzw. die Bildschirmgröße
            var width = Usable(ActualWidth) ?? Usable(Width) ?? SystemParameters.PrimaryScreenWidth;
            var height = Usable(ActualHeight) ?? Usable(Height) ?? SystemParameters.PrimaryScreenHeight;
            _viewModel.DecodeWidth = (int)Math.Ceiling(width * dpi.DpiScaleX * DecodeZoomReserve);
            _viewModel.DecodeHeight = (int)Math.Ceiling(height * dpi.DpiScaleY * DecodeZoomReserve);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow: Bildschirmgröße nicht ermittelt — Standard-Dekodiergröße");
        }
    }

    private static double? Usable(double value) => double.IsFinite(value) && value > 1 ? value : null;

    private void OnVideoOverlayLoaded(object sender, RoutedEventArgs e)
    {
        var foreground = Window.GetWindow(VideoOverlay);
        if (foreground is null || ReferenceEquals(foreground, this) || !_hookedForegroundWindows.Add(foreground))
        {
            return;
        }

        // Tasten landen im Vordergrundfenster, falls es doch Fokus bekommt → gleiche Belegung.
        foreground.PreviewKeyDown += OnPreviewKeyDown;
        foreground.PreviewMouseMove += OnMouseMove;
        foreground.Closed += (_, _) => _hookedForegroundWindows.Remove(foreground);
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
            _logger.LogDebug(ex, "Diashow: WS_EX_NOACTIVATE für das Video-Overlay nicht gesetzt");
        }
    }

    /// <summary>Bildschirmschoner/Abschalten verhindern, solange die Diashow offen ist (wie ein Videoplayer).</summary>
    private void KeepDisplayOn(bool on)
    {
        try
        {
            _ = NativeMethods.SetThreadExecutionState(on
                ? NativeMethods.EsContinuous | NativeMethods.EsDisplayRequired | NativeMethods.EsSystemRequired
                : NativeMethods.EsContinuous);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow: Bildschirm-wach-Anforderung fehlgeschlagen");
        }
    }

    private static class NativeMethods
    {
        public const int GwlExStyle = -20;
        public const long WsExNoActivate = 0x08000000;
        public const uint EsContinuous = 0x80000000;
        public const uint EsSystemRequired = 0x00000001;
        public const uint EsDisplayRequired = 0x00000002;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint flags);
    }
}
