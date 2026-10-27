using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Lumora.Gallery.ViewModels;

namespace Lumora.Gallery.Tours;

/// <summary>
/// Zeitstrahl der Tour-Leiste (Mockup 24): Linie von Beginn bis Ende, je Foto ein Strich in der Akzentfarbe, je Gipfel die
/// Marke „▲ Name HH:mm“ darüber. Zeichnet selbst (eine Tour hat bis zu einigen hundert Fotos — keine Elemente je Strich).
/// </summary>
public sealed class TourTimeline : FrameworkElement
{
    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
        nameof(Marks), typeof(IReadOnlyList<TourTimelineMark>), typeof(TourTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TourTimelineMark>? Marks
    {
        get => (IReadOnlyList<TourTimelineMark>?)GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 30);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var line = Brush("ControlStrokeColorDefaultBrush", Brushes.Gray);
        var accent = Brush("AccentFillColorDefaultBrush", Brushes.SteelBlue);
        var text = Brush("TextFillColorSecondaryBrush", Brushes.Gray);
        const double barY = 20;
        dc.DrawRoundedRectangle(line, null, new Rect(0, barY - 2, width, 4), 2, 2);
        if (Marks is not { } marks)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var mark in marks)
        {
            var x = Math.Clamp(mark.Fraction * width, 1.5, width - 1.5);
            if (mark.Label is null)
            {
                dc.DrawRoundedRectangle(accent, null, new Rect(x - 1.5, barY - 7, 3, 14), 1.5, 1.5);
                continue;
            }

            var label = new FormattedText(mark.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, text, dpi);
            var left = Math.Clamp(x - (label.Width / 2), 0, Math.Max(0, width - label.Width));
            dc.DrawText(label, new Point(left, 0));
        }
    }

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
