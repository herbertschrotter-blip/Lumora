using System;
using Lumora.Services.Places;
using Lumora.Services.Places.Clustering;

namespace Lumora.Places.Map;

/// <summary>
/// Ausschnitt der Karte (ADR-0051): Mittelpunkt in Einheitskoordinaten (<see cref="MercatorProjection"/>), Zoom (auch
/// gebrochen) und Größe in Pixeln. Reines Modell ohne WPF — die Karte (<see cref="MapControl"/>) verschiebt und zoomt es,
/// das ViewModel liest den sichtbaren Bereich für die Gruppen. Hinauszoomen geht bis zur ganzen Welt; ohne Wiederholung
/// über den 180. Längengrad (die Welt bleibt im Bild bzw. mittig).
/// </summary>
public sealed class MapViewport
{
    /// <summary>Feinste Zoomstufe (OSM-Kacheln bis 19).</summary>
    public const double MaxZoomLevel = 19;

    /// <summary>Weiter zoomt „Bereich der Fotos zeigen“ nicht hinein (ein einzelner Ort: Straßenebene, nicht Hausnummer).</summary>
    public const double MaxFitZoom = 16;

    /// <summary>Rand um die Ausdehnung der Fotos: Anteil der Kartengröße je Seite …</summary>
    public const double FitPaddingFraction = 0.1;

    /// <summary>… mindestens so viele Pixel (Marker am Rand bleiben ganz sichtbar).</summary>
    public const double FitPaddingMinPixels = 80;

    public double CenterX { get; private set; } = 0.5;

    public double CenterY { get; private set; } = 0.5;

    public double Zoom { get; private set; } = 2;

    public double Width { get; private set; } = 800;

    public double Height { get; private set; } = 600;

    /// <summary>Kleinste Zoomstufe: die ganze Welt passt ins Bild.</summary>
    public double MinZoomLevel => Math.Max(0, Math.Log2(Math.Max(1, Math.Min(Width, Height)) / MercatorProjection.TileSize));

    /// <summary>Kantenlänge der Welt in Pixeln beim aktuellen Zoom.</summary>
    public double WorldSize => MercatorProjection.WorldSize(Zoom);

    /// <summary>Ganzzahlige Stufe für die Gruppen (abgerundet).</summary>
    public int ClusterZoom => (int)Math.Floor(Zoom + 1e-9);

    /// <summary>Nach jeder Änderung (Größe, Mittelpunkt, Zoom).</summary>
    public event EventHandler? Changed;

    public void SetSize(double width, double height)
    {
        if (!(width > 0) || !(height > 0) || (Math.Abs(width - Width) < 0.5 && Math.Abs(height - Height) < 0.5))
        {
            return;
        }

        Width = width;
        Height = height;
        Apply(CenterX, CenterY, Zoom, force: true);
    }

    public void SetView(double centerX, double centerY, double zoom) => Apply(centerX, centerY, zoom);

    /// <summary>Auf einen geografischen Punkt zentrieren.</summary>
    public void CenterOn(double latitude, double longitude, double zoom)
        => Apply(MercatorProjection.LongitudeToX(longitude), MercatorProjection.LatitudeToY(latitude), zoom);

    /// <summary>Um Pixel verschieben (Ziehen mit der Maus: Inhalt folgt der Maus → negative Mausbewegung).</summary>
    public void PanBy(double dxPixels, double dyPixels)
        => Apply(CenterX + (dxPixels / WorldSize), CenterY + (dyPixels / WorldSize), Zoom);

    /// <summary>Zoomen um den Bildschirmpunkt (sx, sy) — der Ort unter der Maus bleibt stehen.</summary>
    public void ZoomAt(double newZoom, double sx, double sy)
    {
        newZoom = Math.Clamp(newZoom, MinZoomLevel, MaxZoomLevel);
        var (ux, uy) = ScreenToUnit(sx, sy);
        var world = MercatorProjection.WorldSize(newZoom);
        Apply(ux - ((sx - (Width / 2)) / world), uy - ((sy - (Height / 2)) / world), newZoom);
    }

    /// <summary>Ganze Welt.</summary>
    public void ShowWorld() => Apply(0.5, 0.5, MinZoomLevel);

    /// <summary>
    /// Start-Ausschnitt (ADR-0051): die Ausdehnung der Fotos plus Rand (<see cref="FitPaddingFraction"/>, mindestens
    /// <see cref="FitPaddingMinPixels"/>), höchstens <see cref="MaxFitZoom"/>; ohne Ausdehnung die ganze Welt.
    /// </summary>
    /// <param name="maxZoom">Höchster Zoom (Standard <see cref="MaxFitZoom"/>; der Quellen-Vergleich geht näher heran).</param>
    public void FitBounds(GeoBounds? bounds, double maxZoom = MaxFitZoom)
    {
        if (bounds is null)
        {
            ShowWorld();
            return;
        }

        var minX = MercatorProjection.LongitudeToX(Math.Min(bounds.West, bounds.East));
        var maxX = MercatorProjection.LongitudeToX(Math.Max(bounds.West, bounds.East));
        var minY = MercatorProjection.LatitudeToY(Math.Max(bounds.North, bounds.South));
        var maxY = MercatorProjection.LatitudeToY(Math.Min(bounds.North, bounds.South));
        var padX = Math.Max(FitPaddingMinPixels, Width * FitPaddingFraction);
        var padY = Math.Max(FitPaddingMinPixels, Height * FitPaddingFraction);
        var availableW = Math.Max(1, Width - (2 * padX));
        var availableH = Math.Max(1, Height - (2 * padY));
        var spanX = Math.Max(maxX - minX, 1e-12);
        var spanY = Math.Max(maxY - minY, 1e-12);
        var zoom = Math.Log2(Math.Min(availableW / spanX, availableH / spanY) / MercatorProjection.TileSize);
        zoom = Math.Clamp(zoom, MinZoomLevel, Math.Min(maxZoom, MaxZoomLevel));
        Apply((minX + maxX) / 2, (minY + maxY) / 2, zoom);
    }

    public (double X, double Y) ScreenToUnit(double sx, double sy)
        => (CenterX + ((sx - (Width / 2)) / WorldSize), CenterY + ((sy - (Height / 2)) / WorldSize));

    public (double X, double Y) UnitToScreen(double ux, double uy)
        => (((ux - CenterX) * WorldSize) + (Width / 2), ((uy - CenterY) * WorldSize) + (Height / 2));

    /// <summary>Geografischer Punkt unter dem Bildschirmpunkt.</summary>
    public (double Latitude, double Longitude) ScreenToGeo(double sx, double sy)
    {
        var (x, y) = ScreenToUnit(sx, sy);
        return (MercatorProjection.YToLatitude(Math.Clamp(y, 0, 1)), MercatorProjection.XToLongitude(Math.Clamp(x, 0, 1)));
    }

    /// <summary>Sichtbares Rechteck in Einheitskoordinaten, um <paramref name="marginPixels"/> erweitert.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) VisibleUnitRect(double marginPixels = 0)
    {
        var halfW = ((Width / 2) + marginPixels) / WorldSize;
        var halfH = ((Height / 2) + marginPixels) / WorldSize;
        return (CenterX - halfW, CenterY - halfH, CenterX + halfW, CenterY + halfH);
    }

    /// <summary>Sichtbarer Ausschnitt in Grad (auf die Welt begrenzt).</summary>
    public GeoBounds VisibleBounds
    {
        get
        {
            var (minX, minY, maxX, maxY) = VisibleUnitRect();
            return new GeoBounds(
                MercatorProjection.YToLatitude(Math.Clamp(maxY, 0, 1)),
                MercatorProjection.XToLongitude(Math.Clamp(minX, 0, 1)),
                MercatorProjection.YToLatitude(Math.Clamp(minY, 0, 1)),
                MercatorProjection.XToLongitude(Math.Clamp(maxX, 0, 1)));
        }
    }

    private void Apply(double centerX, double centerY, double zoom, bool force = false)
    {
        zoom = Math.Clamp(double.IsFinite(zoom) ? zoom : Zoom, MinZoomLevel, MaxZoomLevel);
        var world = MercatorProjection.WorldSize(zoom);
        centerX = ClampAxis(centerX, Width, world);
        centerY = ClampAxis(centerY, Height, world);
        if (!force && Math.Abs(centerX - CenterX) < 1e-15 && Math.Abs(centerY - CenterY) < 1e-15 && Math.Abs(zoom - Zoom) < 1e-12)
        {
            return;
        }

        CenterX = centerX;
        CenterY = centerY;
        Zoom = zoom;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Welt größer als das Bild: Rand der Welt nicht hineinschieben; kleiner: mittig.</summary>
    private static double ClampAxis(double center, double viewSize, double world)
    {
        if (!double.IsFinite(center))
        {
            return 0.5;
        }

        var half = viewSize / 2 / world;
        return half >= 0.5 ? 0.5 : Math.Clamp(center, half, 1 - half);
    }
}
