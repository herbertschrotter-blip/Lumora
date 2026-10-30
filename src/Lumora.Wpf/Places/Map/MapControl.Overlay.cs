using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Lumora.Services.Places.Clustering;

namespace Lumora.Places.Map;

/// <summary>Eine Korrektur auf der Karte (ADR-0062): gespeicherter Punkt (grau) und Vorschlag (Akzent), Einheitskoordinaten.</summary>
/// <param name="Id">FileId.</param>
public sealed record MapCorrection(int Id, Point? Stored, Point? Suggested, bool IsSelected, string StoredTip, string SuggestionTip);

/// <summary>
/// Überlagerung „Ungenaue Orte“ (Mockup 10): Spur der Aktivität als Linie, Fotos mit echtem GPS als kleine Punkte,
/// gespeicherte Positionen grau, Vorschläge in der Akzentfarbe, gestrichelte Korrektur-Linie dazwischen. Alles in
/// Einheitskoordinaten (Web-Mercator 0…1). <paramref name="Compare"/>: Quellen-Vergleich des ausgewählten Fotos (Mockup 25).
/// </summary>
public sealed record MapOverlay(IReadOnlyList<Point> Track, IReadOnlyList<Point> Anchors, IReadOnlyList<MapCorrection> Corrections, MapCompare? Compare = null)
{
    public static Point ToUnit(double latitude, double longitude)
        => new(MercatorProjection.LongitudeToX(longitude), MercatorProjection.LatitudeToY(latitude));
}

/// <summary>Vorschlag auf der Karte verschoben.</summary>
public sealed class MapSuggestionMovedEventArgs(int id, double latitude, double longitude) : EventArgs
{
    public int Id { get; } = id;

    public double Latitude { get; } = latitude;

    public double Longitude { get; } = longitude;
}

/// <summary>Punkt der Überlagerung angeklickt.</summary>
public sealed class MapOverlayClickedEventArgs(int id) : EventArgs
{
    public int Id { get; } = id;
}

/// <summary>Überlagerung der Karte für ungenaue Orte (ADR-0062): Zeichnen, Treffer, Ziehen des Vorschlags.</summary>
public sealed partial class MapControl
{
    private const double OverlayRadius = 7;
    private const double OverlaySelectedRadius = 9.5;

    public static readonly DependencyProperty OverlayProperty = DependencyProperty.Register(
        nameof(Overlay), typeof(MapOverlay), typeof(MapControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(MapControl), new FrameworkPropertyMetadata(Brushes.Coral, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StoredBrushProperty = DependencyProperty.Register(
        nameof(StoredBrush), typeof(Brush), typeof(MapControl), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ExactBrushProperty = DependencyProperty.Register(
        nameof(ExactBrush), typeof(Brush), typeof(MapControl), new FrameworkPropertyMetadata(Brushes.ForestGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    private int? _overlayDragId;
    private Point? _overlayDragPoint;
    private int? _overlayPressId;
    private bool _overlayTipShown;
    private Point _overlayDown;
    private Vector _overlayGrab; // Abstand Mauspunkt → Mittelpunkt des Vorschlags beim Greifen
    private bool _overlayDragging;

    public MapOverlay? Overlay
    {
        get => (MapOverlay?)GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    /// <summary>Farbe der Spur (Mockup: Korallenrot der Marke).</summary>
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Farbe der gespeicherten, geschätzten Position.</summary>
    public Brush StoredBrush
    {
        get => (Brush)GetValue(StoredBrushProperty);
        set => SetValue(StoredBrushProperty, value);
    }

    /// <summary>Farbe der Fotos mit echtem GPS.</summary>
    public Brush ExactBrush
    {
        get => (Brush)GetValue(ExactBrushProperty);
        set => SetValue(ExactBrushProperty, value);
    }

    /// <summary>Gezeichnete Spurpunkte beim letzten Zeichnen (Tests, Rauchtest).</summary>
    public int LastDrawnTrackPoints { get; private set; }

    /// <summary>Gezeichnete Korrekturen beim letzten Zeichnen (Tests, Rauchtest).</summary>
    public int LastDrawnCorrections { get; private set; }

    public event EventHandler<MapSuggestionMovedEventArgs>? SuggestionMoved;

    public event EventHandler<MapOverlayClickedEventArgs>? OverlayClicked;

    private void DrawOverlay(DrawingContext dc, MapViewport vp)
    {
        LastDrawnTrackPoints = 0;
        LastDrawnCorrections = 0;
        LastDrawnCompareMarks = 0;
        LastScaleText = null;
        if (Overlay is not { } overlay)
        {
            return;
        }

        Point Screen(Point unit)
        {
            var (x, y) = vp.UnitToScreen(unit.X, unit.Y);
            return new Point(x, y);
        }

        if (overlay.Track.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(Screen(overlay.Track[0]), false, false);
                for (var i = 1; i < overlay.Track.Count; i++)
                {
                    ctx.LineTo(Screen(overlay.Track[i]), true, false);
                }
            }

            geometry.Freeze();
            var trackPen = new Pen(TrackBrush, 3.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            trackPen.Freeze();
            dc.PushOpacity(0.85);
            dc.DrawGeometry(null, trackPen, geometry);
            dc.Pop();
            LastDrawnTrackPoints = overlay.Track.Count;
        }

        var white = new Pen(MarkerBorder, 1.5);
        white.Freeze();
        foreach (var anchor in overlay.Anchors)
        {
            dc.DrawEllipse(ExactBrush, white, Screen(anchor), 4.5, 4.5);
        }

        if (overlay.Compare is { } compare)
        {
            DrawCompareMarks(dc, compare, Screen);
        }

        var thickWhite = new Pen(MarkerBorder, 2);
        thickWhite.Freeze();
        foreach (var c in overlay.Corrections)
        {
            var suggested = c.Id == _overlayDragId && _overlayDragPoint is { } drag ? drag : c.Suggested is { } s ? Screen(s) : (Point?)null;
            var stored = c.Stored is { } st ? Screen(st) : (Point?)null;
            var compared = c.IsSelected && overlay.Compare is not null;
            // im Vergleich zeigen die Quellen-Punkte den Weg; der Vorschlag ist ein Ring um die gewählte Quelle
            var onSource = compared && suggested is { } sp && overlay.Compare!.Marks.Any(m => m.Kind is MapCompareKind.Track or MapCompareKind.Time && (Screen(m.Point) - sp).Length < 2);
            if (stored is { } a && suggested is { } b && !compared)
            {
                var dash = new Pen(AccentBrush, c.IsSelected ? 2.5 : 1.5) { DashStyle = new DashStyle([4, 3], 0) };
                dash.Freeze();
                dc.PushOpacity(c.IsSelected ? 1 : 0.55);
                dc.DrawLine(dash, a, b);
                dc.Pop();
            }

            var r = c.IsSelected ? OverlaySelectedRadius : OverlayRadius;
            if (stored is { } p)
            {
                dc.DrawEllipse(StoredBrush, thickWhite, p, r, r);
            }

            if (suggested is { } q)
            {
                if (c.IsSelected)
                {
                    var halo = new Pen(AccentBrush, compared ? 2.5 : 1.5);
                    halo.Freeze();
                    dc.PushOpacity(compared ? 1 : 0.5);
                    dc.DrawEllipse(null, halo, q, 16, 16);
                    dc.Pop();
                }

                if (!onSource)
                {
                    dc.DrawEllipse(AccentBrush, thickWhite, q, r + 1, r + 1);
                }
            }

            LastDrawnCorrections++;
        }

        if (overlay.Compare is { } labels)
        {
            DrawCompareLabels(dc, labels, Screen);
            DrawScaleBar(dc, vp);
        }
    }

    /// <summary>Treffer auf einen Punkt der Überlagerung: (Id, ob Vorschlag); ausgewählte zuerst.</summary>
    internal (int Id, bool IsSuggestion)? OverlayAt(Point point)
    {
        if (Overlay is not { } overlay || Viewport is not { } vp)
        {
            return null;
        }

        (int, bool)? hit = null;
        foreach (var c in overlay.Corrections)
        {
            var r = (c.IsSelected ? OverlaySelectedRadius : OverlayRadius) + 4;
            foreach (var (unit, suggestion) in new[] { (c.Suggested, true), (c.Stored, false) })
            {
                if (unit is not { } u)
                {
                    continue;
                }

                var (x, y) = vp.UnitToScreen(u.X, u.Y);
                if ((point - new Point(x, y)).LengthSquared <= r * r)
                {
                    if (c.IsSelected)
                    {
                        return (c.Id, suggestion);
                    }

                    hit ??= (c.Id, suggestion);
                }
            }
        }

        return hit;
    }

    /// <summary>Linke Taste: auf dem Vorschlag des ausgewählten Fotos beginnt das Ziehen, sonst Klick auf einen Punkt.</summary>
    private bool OverlayMouseDown(Point pos)
    {
        _overlayPressId = null;
        if (OverlayAt(pos) is not { } hit)
        {
            return false;
        }

        var correction = FindCorrection(hit.Id);
        _overlayDragId = null;
        _overlayDragPoint = null;
        _overlayDragging = false;
        _overlayDown = pos;
        if (hit.IsSuggestion && correction is { IsSelected: true, Suggested: { } unit } && Viewport is { } vp)
        {
            var (cx, cy) = vp.UnitToScreen(unit.X, unit.Y);
            _overlayDragId = hit.Id;
            _overlayGrab = new Point(cx, cy) - pos;
        }

        _overlayPressId = hit.Id;
        CaptureMouse();
        return true;
    }

    private bool OverlayMouseMove(Point pos)
    {
        if (_overlayDragId is null)
        {
            return false;
        }

        // erst ab einer kleinen Bewegung ziehen — ein Klick mit zitternder Maus verschiebt nichts
        if (!_overlayDragging && (pos - _overlayDown).Length < Math.Max(SystemParameters.MinimumHorizontalDragDistance, 4))
        {
            return true;
        }

        _overlayDragging = true;
        _overlayDragPoint = pos + _overlayGrab;
        Cursor = Cursors.SizeAll;
        InvalidateVisual();
        return true;
    }

    private bool OverlayMouseUp(Point pos)
    {
        if (_overlayPressId is not { } id)
        {
            return false;
        }

        _overlayPressId = null;
        ReleaseMouseCapture();
        if (_overlayDragging && _overlayDragId is { } dragId && _overlayDragPoint is { } drop && Viewport is { } vp)
        {
            _overlayDragId = null;
            _overlayDragPoint = null;
            _overlayDragging = false;
            Cursor = null;
            var (lat, lon) = vp.ScreenToGeo(drop.X, drop.Y);
            SuggestionMoved?.Invoke(this, new MapSuggestionMovedEventArgs(dragId, lat, lon));
            InvalidateVisual();
            return true;
        }

        _overlayDragId = null;
        _overlayDragPoint = null;
        _overlayDragging = false;
        Cursor = null;
        OverlayClicked?.Invoke(this, new MapOverlayClickedEventArgs(id));
        return true;
    }

    /// <summary>Tooltip über Punkten der Überlagerung; true, wenn einer getroffen ist.</summary>
    private bool OverlayHover(Point pos)
    {
        if (OverlayAt(pos) is not { } hit || FindCorrection(hit.Id) is not { } c)
        {
            if (_overlayTipShown)
            {
                _overlayTipShown = false;
                ToolTip = null;
                Cursor = null;
                _hover = null; // Marker-Tooltip beim nächsten Treffer neu setzen
            }

            return false;
        }

        _overlayTipShown = true;
        var tip = hit.IsSuggestion ? c.SuggestionTip : c.StoredTip;
        if (!Equals(ToolTip, tip))
        {
            ToolTip = tip;
        }

        Cursor = hit.IsSuggestion && c.IsSelected ? Cursors.SizeAll : Cursors.Hand;
        return true;
    }

    private MapCorrection? FindCorrection(int id)
    {
        if (Overlay is not { } overlay)
        {
            return null;
        }

        foreach (var c in overlay.Corrections)
        {
            if (c.Id == id)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>Bildschirmpunkt des Vorschlags (Tests).</summary>
    public Point? SuggestionScreenPoint(int id)
        => FindCorrection(id)?.Suggested is { } u && Viewport is { } vp ? new Point(vp.UnitToScreen(u.X, u.Y).X, vp.UnitToScreen(u.X, u.Y).Y) : null;
}
