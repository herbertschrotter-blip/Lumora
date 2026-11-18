using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>
/// Kamera einer Quelle: Rotation (Kamera → Welt), Brennweite, Restverzeichnung k1/k2 und Hauptpunkt-Versatz — alles bei
/// voller, gedrehter Auflösung (<see cref="Size"/>). Für „Nur verschieben“ trägt sie statt der Rotation eine
/// Ähnlichkeits-Abbildung in die Leinwand (<see cref="Similarity"/>).
/// </summary>
internal sealed class PanoCamera
{
    /// <summary>Index der Quelle (Position in der Auswahl).</summary>
    public int Source { get; init; }

    public Size Size { get; init; }

    public double F { get; set; }

    public M3 R { get; set; } = M3.I;

    public double K1 { get; set; }

    public double K2 { get; set; }

    public double Ppx { get; set; }

    public double Ppy { get; set; }

    /// <summary>„Nur verschieben“: x' = A·x + B·y + Tx, y' = −B·x + A·y + Ty (volle Auflösung → Leinwand bei Maßstab 1).</summary>
    public (double A, double B, double Tx, double Ty)? Similarity { get; set; }

    public double Cx => (Size.Width - 1) / 2.0 + Ppx;

    public double Cy => (Size.Height - 1) / 2.0 + Ppy;

    public PanoCamera Clone() => new()
    {
        Source = Source,
        Size = Size,
        F = F,
        R = R,
        K1 = K1,
        K2 = K2,
        Ppx = Ppx,
        Ppy = Ppy,
        Similarity = Similarity,
    };

    public static void Undistort(ref double x, ref double y, double k1, double k2)
    {
        double xd = x, yd = y;
        for (var i = 0; i < 5; i++)
        {
            var r2 = x * x + y * y;
            var f = 1 + r2 * (k1 + r2 * k2);
            x = xd / f;
            y = yd / f;
        }
    }

    /// <summary>Pixel (volle Auflösung) → Weltstrahl.</summary>
    public V3 Ray(double px, double py)
    {
        double x = (px - Cx) / F, y = (py - Cy) / F;
        Undistort(ref x, ref y, K1, K2);
        return (R * new V3(x, y, 1)).Normalized();
    }

    /// <summary>Weltstrahl → Pixel (volle Auflösung); false hinter der Kamera bzw. weit außerhalb.</summary>
    public bool Project(V3 w, out double px, out double py)
    {
        var c = R.T * w;
        if (c.Z <= 1e-6)
        {
            px = py = 0;
            return false;
        }

        double xu = c.X / c.Z, yu = c.Y / c.Z, r2 = xu * xu + yu * yu, fac = 1 + r2 * (K1 + r2 * K2);
        px = F * xu * fac + Cx;
        py = F * yu * fac + Cy;
        return r2 < 4;
    }

    /// <summary>true, wenn der Weltstrahl im Bild liegt.</summary>
    public bool Contains(V3 world)
        => Project(world, out var x, out var y) && x >= 0 && y >= 0 && x <= Size.Width - 1 && y <= Size.Height - 1;

    /// <summary>Halber Bildwinkel über die Diagonale (Radiant).</summary>
    public double HalfDiagonalFov => Math.Atan(Math.Sqrt(Math.Pow(Size.Width / 2.0, 2) + Math.Pow(Size.Height / 2.0, 2)) / F);
}

/// <summary>Ein Teil der Leinwand: Kamera (Index in der Kameraliste) und Rechteck.</summary>
internal sealed class PartLayout
{
    public int Cam { get; init; }

    public Rect Roi { get; set; }
}

/// <summary>Leinwand einer Projektion bei einem Maßstab.</summary>
internal sealed class PanoLayout
{
    /// <summary>Pixel je Radiant (bzw. je Einheit der Ebene).</summary>
    public double S { get; init; }

    /// <summary>Maßstab gegenüber voller Auflösung.</summary>
    public double C { get; init; }

    public int U0 { get; set; }

    public int V0 { get; set; }

    public int W { get; set; }

    public int H { get; set; }

    public bool Is360 { get; init; }

    public List<PartLayout> Parts { get; } = [];
}

/// <summary>Projektion der Leinwand (intern).</summary>
internal enum Proj
{
    /// <summary>Equirektangular (Kugel).</summary>
    Spherical,

    /// <summary>Zylinder mit senkrechter Achse (v = tan Breite).</summary>
    Cylindrical,

    /// <summary>Ebene (Perspektive).</summary>
    Plane,

    /// <summary>Zylinder in Mercator-Teilung (v = artanh(sin Breite)) — streckt hohe Schwenks weniger als der Zylinder.</summary>
    Mercator,

    /// <summary>Zylinder mit waagrechter Achse (Welt-x) für senkrechte Schwenks.</summary>
    VerticalCylinder,

    /// <summary>„Nur verschieben“: Ähnlichkeits-Abbildung je Bild, keine Rotation.</summary>
    Flat,
}

/// <summary>Abbildungen Projektion ↔ Weltstrahl.</summary>
internal static class Projections
{
    /// <summary>Projektionskoordinaten (Radiant bzw. normiert) → Weltstrahl (x rechts, y unten, z vorne).</summary>
    public static void Dir(Proj proj, double a, double b, out double dx, out double dy, out double dz)
    {
        switch (proj)
        {
            case Proj.Plane:
            {
                var n = 1 / Math.Sqrt(a * a + b * b + 1);
                dx = a * n;
                dy = b * n;
                dz = n;
                break;
            }

            case Proj.Cylindrical:
            {
                var n = 1 / Math.Sqrt(1 + b * b);
                dx = Math.Sin(a) * n;
                dy = b * n;
                dz = Math.Cos(a) * n;
                break;
            }

            case Proj.Mercator:
            {
                var c = 1 / Math.Cosh(b);
                dx = Math.Sin(a) * c;
                dy = Math.Tanh(b);
                dz = Math.Cos(a) * c;
                break;
            }

            case Proj.VerticalCylinder:
            {
                // Achse = Welt-x: b ist der Winkel um die Achse (senkrecht), a die Lage entlang der Achse (waagrecht)
                var n = 1 / Math.Sqrt(1 + a * a);
                dx = a * n;
                dy = Math.Sin(b) * n;
                dz = Math.Cos(b) * n;
                break;
            }

            default:
            {
                var cb = Math.Cos(b);
                dx = Math.Sin(a) * cb;
                dy = Math.Sin(b);
                dz = Math.Cos(a) * cb;
                break;
            }
        }
    }

    /// <summary>Weltstrahl → Projektionskoordinaten; Länge relativ zu <paramref name="lc"/> ausgerollt.</summary>
    public static void ToProj(Proj proj, V3 d, double lc, out double u, out double v, out bool ok)
    {
        ok = true;
        switch (proj)
        {
            case Proj.Plane:
                if (d.Z <= 1e-6)
                {
                    ok = false;
                    u = v = 0;
                    return;
                }

                u = d.X / d.Z;
                v = d.Y / d.Z;
                return;
            case Proj.Cylindrical:
                u = lc + Wrap(Math.Atan2(d.X, d.Z) - lc);
                v = d.Y / Math.Sqrt(d.X * d.X + d.Z * d.Z);
                return;
            case Proj.Mercator:
                u = lc + Wrap(Math.Atan2(d.X, d.Z) - lc);
                v = Math.Atanh(Math.Clamp(d.Y, -0.9999, 0.9999));
                return;
            case Proj.VerticalCylinder:
                v = lc + Wrap(Math.Atan2(d.Y, d.Z) - lc);
                u = d.X / Math.Sqrt(d.Y * d.Y + d.Z * d.Z);
                return;
            default:
                u = lc + Wrap(Math.Atan2(d.X, d.Z) - lc);
                v = Math.Asin(Math.Clamp(d.Y, -1, 1));
                return;
        }
    }

    /// <summary>Hat die Projektion eine umlaufende Länge (360° möglich)?</summary>
    public static bool Wraps(Proj proj) => proj is Proj.Spherical or Proj.Cylindrical or Proj.Mercator;

    public static double Wrap(double a)
    {
        while (a < -Math.PI)
        {
            a += 2 * Math.PI;
        }

        while (a >= Math.PI)
        {
            a -= 2 * Math.PI;
        }

        return a;
    }
}
