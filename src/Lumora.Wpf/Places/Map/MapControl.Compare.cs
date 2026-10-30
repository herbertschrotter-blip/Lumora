using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Lumora.Services.Places.Clustering;

namespace Lumora.Places.Map;

/// <summary>Art eines Punkts im Quellen-Vergleich (Mockup 25).</summary>
public enum MapCompareKind
{
    /// <summary>„laut Foto“ (grau).</summary>
    Stored,

    /// <summary>„laut Spur“ (Koralle).</summary>
    Track,

    /// <summary>„laut Zeit“ (Violett).</summary>
    Time,

    /// <summary>Spur-Anfang bzw. -Ende beim Uhr-Hinweis (gestrichelter Kreis).</summary>
    Ghost,

    /// <summary>Genaues GPS-Foto davor bzw. danach (grün, nur Beschriftung).</summary>
    Anchor,
}

/// <summary>Beschrifteter Punkt des Vergleichs, Einheitskoordinaten.</summary>
public sealed record MapCompareMark(Point Point, MapCompareKind Kind, string Label, bool IsHighlighted = false);

/// <summary>Gestrichelte Verbindung vom Punkt laut Foto zu einer anderen Quelle mit Abstand in der Mitte.</summary>
public sealed record MapCompareLink(Point From, Point To, MapCompareKind Kind, string Text);

/// <summary>Quellen-Vergleich des ausgewählten Fotos (Mockup 25).</summary>
public sealed record MapCompare(IReadOnlyList<MapCompareMark> Marks, IReadOnlyList<MapCompareLink> Links);

/// <summary>Zeichnen des Quellen-Vergleichs und des Maßstabs (Mockup 25).</summary>
public sealed partial class MapControl
{
    private const double CompareRadius = 8;
    private const double ScaleBarMaxPixels = 110;

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static readonly DependencyProperty TimeBrushProperty = DependencyProperty.Register(
        nameof(TimeBrush), typeof(Brush), typeof(MapControl), new FrameworkPropertyMetadata(Brushes.BlueViolet, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelForegroundProperty = DependencyProperty.Register(
        nameof(LabelForeground), typeof(Brush), typeof(MapControl), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBackgroundProperty = DependencyProperty.Register(
        nameof(LabelBackground), typeof(Brush), typeof(MapControl), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Farbe „laut Zeit“ (Mockup 25: Violett der Marke).</summary>
    public Brush TimeBrush
    {
        get => (Brush)GetValue(TimeBrushProperty);
        set => SetValue(TimeBrushProperty, value);
    }

    /// <summary>Schrift der Beschriftungen auf der Karte.</summary>
    public Brush LabelForeground
    {
        get => (Brush)GetValue(LabelForegroundProperty);
        set => SetValue(LabelForegroundProperty, value);
    }

    /// <summary>Hintergrund der Beschriftungen und des Maßstabs.</summary>
    public Brush LabelBackground
    {
        get => (Brush)GetValue(LabelBackgroundProperty);
        set => SetValue(LabelBackgroundProperty, value);
    }

    /// <summary>Gezeichnete Vergleichspunkte beim letzten Zeichnen (Tests, Rauchtest).</summary>
    public int LastDrawnCompareMarks { get; private set; }

    /// <summary>Text des Maßstabs beim letzten Zeichnen („20 m“), null ohne (Tests, Rauchtest).</summary>
    public string? LastScaleText { get; private set; }

    private Brush CompareBrush(MapCompareKind kind) => kind switch
    {
        MapCompareKind.Stored => StoredBrush,
        MapCompareKind.Track or MapCompareKind.Ghost => TrackBrush,
        MapCompareKind.Time => TimeBrush,
        _ => ExactBrush,
    };

    /// <summary>Verbindungen und Punkte (unter den Korrekturen).</summary>
    private void DrawCompareMarks(DrawingContext dc, MapCompare compare, Func<Point, Point> screen)
    {
        foreach (var link in compare.Links)
        {
            var pen = new Pen(CompareBrush(link.Kind), 1.5) { DashStyle = new DashStyle([4, 3], 0) };
            pen.Freeze();
            dc.PushOpacity(0.8);
            dc.DrawLine(pen, screen(link.From), screen(link.To));
            dc.Pop();
        }

        var white = new Pen(MarkerBorder, 2.5);
        white.Freeze();
        foreach (var mark in compare.Marks)
        {
            var at = screen(mark.Point);
            switch (mark.Kind)
            {
                case MapCompareKind.Anchor:
                    continue; // Punkt zeichnet die Überlagerung, hier nur die Beschriftung
                case MapCompareKind.Ghost:
                    var dashed = new Pen(TrackBrush, 2) { DashStyle = new DashStyle([2, 2], 0) };
                    dashed.Freeze();
                    dc.DrawEllipse(null, dashed, at, 7, 7);
                    break;
                default:
                    var r = mark.IsHighlighted ? CompareRadius + 2 : CompareRadius;
                    dc.DrawEllipse(CompareBrush(mark.Kind), white, at, r, r);
                    break;
            }

            LastDrawnCompareMarks++;
        }
    }

    /// <summary>Beschriftungen und Abstände (über allem).</summary>
    private void DrawCompareLabels(DrawingContext dc, MapCompare compare, Func<Point, Point> screen)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var link in compare.Links)
        {
            var a = screen(link.From);
            var b = screen(link.To);
            if ((a - b).Length < 60 || string.IsNullOrEmpty(link.Text))
            {
                continue; // zu kurz für eine lesbare Zahl
            }

            var text = new FormattedText(link.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11, CompareBrush(link.Kind), dpi);
            var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var box = new Rect(mid.X - (text.Width / 2) - 5, mid.Y - 9, text.Width + 10, 18);
            dc.DrawRoundedRectangle(LabelBackground, null, box, 4, 4);
            dc.DrawText(text, new Point(box.X + 5, box.Y + ((18 - text.Height) / 2)));
        }

        foreach (var mark in compare.Marks)
        {
            var text = new FormattedText(mark.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11, LabelForeground, dpi);
            var at = screen(mark.Point);
            var w = text.Width + 14;
            // feste Seiten je Quelle, damit nahe Punkte (Spur und Zeit) sich nicht überdecken
            var origin = mark.Kind switch
            {
                MapCompareKind.Track => new Point(at.X - w - 10, at.Y + 8),
                MapCompareKind.Ghost => new Point(at.X - w - 10, at.Y - 28),
                MapCompareKind.Anchor => new Point(at.X - (w / 2), at.Y + 10),
                _ => new Point(at.X + 12, at.Y - 28),
            };
            var box = new Rect(origin, new Size(w, 20));
            var pen = new Pen(CompareBrush(mark.Kind), 1.5);
            pen.Freeze();
            dc.DrawRoundedRectangle(LabelBackground, pen, box, 10, 10);
            dc.DrawText(text, new Point(box.X + 7, box.Y + ((20 - text.Height) / 2)));
        }
    }

    /// <summary>Maßstab unten rechts über der Namensnennung (nur mit Vergleich).</summary>
    private void DrawScaleBar(DrawingContext dc, MapViewport vp)
    {
        var latitude = MercatorProjection.YToLatitude(Math.Clamp(vp.CenterY, 0, 1));
        var metersPerPixel = 40_075_016.686 * Math.Cos(latitude * Math.PI / 180) / vp.WorldSize;
        if (!(metersPerPixel > 0))
        {
            return;
        }

        var meters = NiceLength(metersPerPixel * ScaleBarMaxPixels);
        var pixels = meters / metersPerPixel;
        var label = meters >= 1000 ? $"{meters / 1000:0.#} km" : $"{meters:0} m";
        LastScaleText = label;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11, LabelForeground, dpi);
        var right = vp.Width - 14;
        var bottom = vp.Height - 30;
        var box = new Rect(right - Math.Max(pixels, text.Width) - 8, bottom - text.Height - 12, Math.Max(pixels, text.Width) + 12, text.Height + 16);
        dc.PushOpacity(0.85);
        dc.DrawRoundedRectangle(LabelBackground, null, box, 4, 4);
        dc.Pop();
        dc.DrawText(text, new Point(right - text.Width, box.Y + 2));
        var pen = new Pen(LabelForeground, 2);
        pen.Freeze();
        var y = bottom;
        dc.DrawLine(pen, new Point(right - pixels, y), new Point(right, y));
        dc.DrawLine(pen, new Point(right - pixels, y - 5), new Point(right - pixels, y));
        dc.DrawLine(pen, new Point(right, y - 5), new Point(right, y));
    }

    /// <summary>Größte runde Länge (1, 2, 5 · 10ⁿ) bis <paramref name="max"/>.</summary>
    internal static double NiceLength(double max)
    {
        var power = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var step in new[] { 5.0, 2.0, 1.0 })
        {
            if (step * power <= max)
            {
                return step * power;
            }
        }

        return power;
    }
}
