using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Lumora.Services.Enhance;

namespace Lumora.Enhance;

/// <summary>
/// Bühne der Leiste „Verbessern“ über dem Lightbox-Bild (ADR-0071, Mockup 13): zeigt Vorher (Original) und Nachher (Vorschau)
/// nebeneinander mit verschiebbarem Trennstrich — ziehen oder klicken setzt die Lage — bzw. im Pinsel-Modus das Original mit
/// den markierten Stellen (Klicken/Ziehen markiert). Beide Bilder werden eingepasst gezeichnet (unabhängig vom Zoom der
/// Lightbox). Zeichnet selbst (ein Element, kein Unterbaum).
/// </summary>
public sealed class EnhanceStageOverlay : FrameworkElement
{
    public static readonly DependencyProperty BeforeSourceProperty = DependencyProperty.Register(
        nameof(BeforeSource), typeof(ImageSource), typeof(EnhanceStageOverlay), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AfterSourceProperty = DependencyProperty.Register(
        nameof(AfterSource), typeof(ImageSource), typeof(EnhanceStageOverlay), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(EnhanceStageOverlay),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsBrushModeProperty = DependencyProperty.Register(
        nameof(IsBrushMode), typeof(bool), typeof(EnhanceStageOverlay), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DotsProperty = DependencyProperty.Register(
        nameof(Dots), typeof(IEnumerable), typeof(EnhanceStageOverlay), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDotsChanged));

    public static readonly DependencyProperty BrushSizeProperty = DependencyProperty.Register(
        nameof(BrushSize), typeof(double), typeof(EnhanceStageOverlay), new FrameworkPropertyMetadata(0.03, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushTargetProperty = DependencyProperty.Register(
        nameof(BrushTarget), typeof(IEnhanceBrushTarget), typeof(EnhanceStageOverlay), new PropertyMetadata(null));

    private static readonly Brush Backdrop = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)));
    private static readonly Brush MaskFill = Frozen(new SolidColorBrush(Color.FromArgb(0x73, 0xF9, 0x73, 0x62)));
    private static readonly Pen MaskPen = FrozenPen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x62))), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen SplitPen = FrozenPen(new Pen(Brushes.White, 2));
    private static readonly Pen CursorPen = FrozenPen(new Pen(Brushes.White, 1.5));
    private static readonly Brush LabelBack = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));

    private Point? _mouse;
    private Point _lastDot;
    private bool _dragging;

    public EnhanceStageOverlay()
    {
        Focusable = false;
        ClipToBounds = true;
        Cursor = Cursors.SizeWE;
    }

    public ImageSource? BeforeSource
    {
        get => (ImageSource?)GetValue(BeforeSourceProperty);
        set => SetValue(BeforeSourceProperty, value);
    }

    public ImageSource? AfterSource
    {
        get => (ImageSource?)GetValue(AfterSourceProperty);
        set => SetValue(AfterSourceProperty, value);
    }

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public bool IsBrushMode
    {
        get => (bool)GetValue(IsBrushModeProperty);
        set => SetValue(IsBrushModeProperty, value);
    }

    public IEnumerable? Dots
    {
        get => (IEnumerable?)GetValue(DotsProperty);
        set => SetValue(DotsProperty, value);
    }

    public double BrushSize
    {
        get => (double)GetValue(BrushSizeProperty);
        set => SetValue(BrushSizeProperty, value);
    }

    public IEnhanceBrushTarget? BrushTarget
    {
        get => (IEnhanceBrushTarget?)GetValue(BrushTargetProperty);
        set => SetValue(BrushTargetProperty, value);
    }

    /// <summary>Eingepasstes Rechteck eines Bildes (8 Pixel Rand).</summary>
    public Rect FitRect(ImageSource? source)
    {
        var area = new Rect(8, 8, Math.Max(1, ActualWidth - 16), Math.Max(1, ActualHeight - 16));
        if (source is null || source.Width <= 0 || source.Height <= 0)
        {
            return area;
        }

        var scale = Math.Min(area.Width / source.Width, area.Height / source.Height);
        var w = source.Width * scale;
        var h = source.Height * scale;
        return new Rect(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var before = BeforeSource;
        var after = AfterSource;
        var beforeRect = FitRect(before);
        if (!IsBrushMode && after is not null)
        {
            var afterRect = FitRect(after);
            var splitX = beforeRect.Left + (Math.Clamp(Position, 0, 1) * beforeRect.Width);
            if (before is not null)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(0, splitX), ActualHeight)));
                dc.DrawImage(before, beforeRect);
                dc.Pop();
            }

            dc.PushClip(new RectangleGeometry(new Rect(splitX, 0, Math.Max(0, ActualWidth - splitX), ActualHeight)));
            dc.DrawImage(after, afterRect);
            dc.Pop();
            dc.DrawLine(SplitPen, new Point(splitX, beforeRect.Top), new Point(splitX, beforeRect.Bottom));
            var knob = new Point(splitX, beforeRect.Top + beforeRect.Height / 2);
            dc.DrawEllipse(Brushes.White, null, knob, 16, 16);
            DrawText(dc, "◀ ▶", knob, 10, Brushes.Black, centered: true);
            // Beschriftung unten (oben liegt die Werkzeugleiste der Lightbox)
            DrawLabel(dc, "Vorher (Original)", new Point(beforeRect.Left + 10, beforeRect.Bottom - 34));
            DrawLabel(dc, "Nachher · Vorschau", new Point(Math.Max(splitX + 10, beforeRect.Right - 140), beforeRect.Bottom - 34));
            return;
        }

        if (before is not null)
        {
            dc.DrawImage(before, beforeRect);
        }

        if (IsBrushMode)
        {
            if (Dots is not null)
            {
                foreach (var item in Dots)
                {
                    if (item is EnhanceBrushDot dot)
                    {
                        var r = dot.Radius * beforeRect.Width;
                        dc.DrawEllipse(MaskFill, null, new Point(beforeRect.Left + dot.X * beforeRect.Width, beforeRect.Top + dot.Y * beforeRect.Height), r, r);
                    }
                }
            }

            if (_mouse is { } m && beforeRect.Contains(m))
            {
                var r = BrushSize * beforeRect.Width;
                dc.DrawEllipse(null, CursorPen, m, r, r);
                dc.DrawEllipse(null, MaskPen, m, r + 1.5, r + 1.5);
            }

            DrawLabel(dc, "Pinsel: Störendes übermalen", new Point(beforeRect.Left + 10, beforeRect.Bottom - 34));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        CaptureMouse();
        var p = e.GetPosition(this);
        if (IsBrushMode)
        {
            BrushTarget?.BeginStroke();
            AddDotAt(p, force: true);
        }
        else
        {
            SetPositionFrom(p);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        _mouse = p;
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            if (IsBrushMode)
            {
                AddDotAt(p, force: false);
            }
            else
            {
                SetPositionFrom(p);
            }
        }

        if (IsBrushMode)
        {
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsBrushModeProperty)
        {
            Cursor = IsBrushMode ? Cursors.Cross : Cursors.SizeWE;
        }
    }

    /// <summary>Klick/Zug an <paramref name="p"/> (Elementpixel) setzt die Reglerlage (Tests, Rauchtest).</summary>
    public void SetPositionFrom(Point p)
    {
        var rect = FitRect(BeforeSource);
        if (rect.Width > 0)
        {
            Position = Math.Clamp((p.X - rect.Left) / rect.Width, 0.0, 1.0);
        }
    }

    private void AddDotAt(Point p, bool force)
    {
        var rect = FitRect(BeforeSource);
        if (!rect.Contains(p) || BrushTarget is null)
        {
            return;
        }

        var spacing = Math.Max(2, BrushSize * rect.Width / 3);
        if (!force && (p - _lastDot).Length < spacing)
        {
            return;
        }

        _lastDot = p;
        BrushTarget.AddDot((p.X - rect.Left) / rect.Width, (p.Y - rect.Top) / rect.Height);
    }

    private static void OnDotsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (EnhanceStageOverlay)d;
        if (e.OldValue is INotifyCollectionChanged oldList)
        {
            oldList.CollectionChanged -= overlay.OnDotsCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newList)
        {
            newList.CollectionChanged += overlay.OnDotsCollectionChanged;
        }
    }

    private void OnDotsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var formatted = Format(text, 11, Brushes.White);
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(at.X, at.Y, formatted.Width + 16, formatted.Height + 6), 4, 4);
        dc.DrawText(formatted, new Point(at.X + 8, at.Y + 3));
    }

    private void DrawText(DrawingContext dc, string text, Point at, double size, Brush brush, bool centered)
    {
        var formatted = Format(text, size, brush);
        dc.DrawText(formatted, centered ? new Point(at.X - formatted.Width / 2, at.Y - formatted.Height / 2) : at);
    }

    private FormattedText Format(string text, double size, Brush brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
