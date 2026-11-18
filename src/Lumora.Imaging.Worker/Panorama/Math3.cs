using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>Vektor im Raum (Welt: x rechts, y unten, z vorne).</summary>
internal readonly struct V3(double x, double y, double z)
{
    public readonly double X = x, Y = y, Z = z;

    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;

    public V3 Cross(V3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public double Norm => Math.Sqrt(X * X + Y * Y + Z * Z);

    public V3 Normalized()
    {
        var n = Norm;
        return n > 0 ? this * (1 / n) : this;
    }
}

/// <summary>3×3-Matrix, zeilenweise. Kamera-Rotation R: Weltstrahl = R · K⁻¹ · p (wie OpenCV CameraParams.R).</summary>
internal readonly struct M3
{
    public readonly double A00, A01, A02, A10, A11, A12, A20, A21, A22;

    public M3(double a00, double a01, double a02, double a10, double a11, double a12, double a20, double a21, double a22)
    {
        A00 = a00; A01 = a01; A02 = a02; A10 = a10; A11 = a11; A12 = a12; A20 = a20; A21 = a21; A22 = a22;
    }

    public static readonly M3 I = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static readonly M3 Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static M3 FromCols(V3 c0, V3 c1, V3 c2) => new(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);

    public static M3 FromRows(V3 r0, V3 r1, V3 r2) => new(r0.X, r0.Y, r0.Z, r1.X, r1.Y, r1.Z, r2.X, r2.Y, r2.Z);

    public V3 Col(int i) => i == 0 ? new(A00, A10, A20) : i == 1 ? new(A01, A11, A21) : new(A02, A12, A22);

    public M3 T => new(A00, A10, A20, A01, A11, A21, A02, A12, A22);

    public static V3 operator *(M3 m, V3 v)
        => new(m.A00 * v.X + m.A01 * v.Y + m.A02 * v.Z, m.A10 * v.X + m.A11 * v.Y + m.A12 * v.Z, m.A20 * v.X + m.A21 * v.Y + m.A22 * v.Z);

    public static M3 operator *(M3 a, M3 b) => FromCols(a * b.Col(0), a * b.Col(1), a * b.Col(2));

    public static M3 operator +(M3 a, M3 b)
        => new(a.A00 + b.A00, a.A01 + b.A01, a.A02 + b.A02, a.A10 + b.A10, a.A11 + b.A11, a.A12 + b.A12, a.A20 + b.A20, a.A21 + b.A21, a.A22 + b.A22);

    public double[] ToArray() => [A00, A01, A02, A10, A11, A12, A20, A21, A22];

    public static M3 From(IReadOnlyList<double> a) => new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8]);

    public static M3 Rodrigues(V3 w)
    {
        var th = w.Norm;
        if (th < 1e-12)
        {
            return new M3(1, -w.Z, w.Y, w.Z, 1, -w.X, -w.Y, w.X, 1);
        }

        var k = w * (1 / th);
        double c = Math.Cos(th), s = Math.Sin(th), t = 1 - c;
        return new M3(
            c + k.X * k.X * t, k.X * k.Y * t - k.Z * s, k.X * k.Z * t + k.Y * s,
            k.Y * k.X * t + k.Z * s, c + k.Y * k.Y * t, k.Y * k.Z * t - k.X * s,
            k.Z * k.X * t - k.Y * s, k.Z * k.Y * t + k.X * s, c + k.Z * k.Z * t);
    }

    public V3 ToRodrigues()
    {
        var src = new double[3, 3] { { A00, A01, A02 }, { A10, A11, A12 }, { A20, A21, A22 } };
        Cv2.Rodrigues(src, out double[] vec, out _);
        return new V3(vec[0], vec[1], vec[2]);
    }

    /// <summary>Nächste Rotationsmatrix (SVD, det = +1).</summary>
    public M3 Orthonormalized()
    {
        using var m = Mat.FromPixelData(3, 3, MatType.CV_64F, ToArray());
        using var w = new Mat();
        using var u = new Mat();
        using var vt = new Mat();
        Cv2.SVDecomp(m, w, u, vt);
        u.GetArray(out double[] ua);
        vt.GetArray(out double[] va);
        var res = From(ua) * From(va);
        if (res.Det() < 0)
        {
            ua[2] = -ua[2];
            ua[5] = -ua[5];
            ua[8] = -ua[8];
            res = From(ua) * From(va);
        }

        return res;
    }

    public double Det() => A00 * (A11 * A22 - A12 * A21) - A01 * (A10 * A22 - A12 * A20) + A02 * (A10 * A21 - A11 * A20);

    public static double AngleDeg(M3 a, M3 b)
    {
        var r = a.T * b;
        var c = Math.Clamp((r.A00 + r.A11 + r.A22 - 1) / 2, -1, 1);
        return Math.Acos(c) * 180 / Math.PI;
    }

    /// <summary>Rotation um die Welt-y-Achse (nach unten) um Winkel a.</summary>
    public static M3 RotY(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new M3(c, 0, s, 0, 1, 0, -s, 0, c);
    }
}
