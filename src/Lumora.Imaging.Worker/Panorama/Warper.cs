using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>Verschiebungsfeld der Entgeisterung für einen Teil: Leinwand-Rechteck (Kompositionsmaßstab), Feld CV_32FC2 in Leinwand-Pixeln.</summary>
internal sealed class FlowPatch : IDisposable
{
    public required Rect Canvas { get; init; }

    public required Mat Flow { get; init; }

    /// <summary>Maßstab Feld / Leinwand.</summary>
    public double Scale { get; init; }

    public void Dispose() => Flow.Dispose();
}

/// <summary>
/// Rückwärts-Abbildung Leinwand → Bild (wie cv::detail::SphericalWarper/CylindricalWarper/PlaneWarper::warp, Port aus
/// Spike 20 Runde 2): je Leinwandpixel (+ Verschiebung der Entgeisterung) Weltstrahl → Kamera → Restverzeichnung (k1, k2,
/// Hauptpunkt) → Bildpixel, dann EIN Remap mit wählbarer Interpolation. Die Quelle darf verkleinert sein
/// (<paramref name="srcScale"/> = Quellbreite / volle Breite), Typ beliebig (8UC3, 32FC1 …). In Streifen, damit die Tabellen
/// klein bleiben.
/// </summary>
internal static class Warper
{
    public static unsafe (Mat Img, Mat Mask) Warp(Mat src, double srcScale, PanoCamera cam, PanoLayout lay, Rect roi, Proj proj,
        InterpolationFlags interp, IReadOnlyList<FlowPatch>? disp = null, int threads = 0)
    {
        var img = new Mat(roi.Height, roi.Width, src.Type(), Scalar.All(0));
        var mask = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.All(0));
        var s = lay.S;
        double w = cam.Size.Width, h = cam.Size.Height;
        double cx = cam.Cx, cy = cam.Cy, f = cam.F, k1 = cam.K1, k2 = cam.K2;
        double sw = src.Width - 1, sh = src.Height - 1;
        var rt = cam.R.T;
        var flat = proj == Proj.Flat && cam.Similarity is not null;
        double ia = 0, ib = 0, tx = 0, ty = 0;
        if (flat)
        {
            var (a, b, ttx, tty) = cam.Similarity!.Value;
            var det = a * a + b * b;
            ia = a / det;
            ib = b / det;
            tx = ttx;
            ty = tty;
        }

        const int strip = 256;
        var width = roi.Width;
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : WorkerThreads.Count };
        for (var y0 = 0; y0 < roi.Height; y0 += strip)
        {
            var shh = Math.Min(strip, roi.Height - y0);
            using var mapX = new Mat(shh, width, MatType.CV_32FC1);
            using var mapY = new Mat(shh, width, MatType.CV_32FC1);
            var px = (float*)mapX.Data;
            var py = (float*)mapY.Data;
            float[]? ddx = null, ddy = null;
            if (disp is { Count: > 0 })
            {
                var stripRect = new Rect(roi.X, roi.Y + y0, width, shh);
                foreach (var p in disp)
                {
                    var inter = stripRect & p.Canvas;
                    if (inter.Width <= 0 || inter.Height <= 0)
                    {
                        continue;
                    }

                    ddx ??= new float[shh * width];
                    ddy ??= new float[shh * width];
                    var fl = p.Flow;
                    int fw = fl.Width, fh = fl.Height;
                    var fp = (float*)fl.Data;
                    var fst = fl.Step() / 4;
                    for (var yy = inter.Top; yy < inter.Bottom; yy++)
                    {
                        var fy = Math.Clamp((yy - p.Canvas.Y + 0.5) * p.Scale - 0.5, 0, fh - 1);
                        int fy0 = (int)fy, fy1 = Math.Min(fy0 + 1, fh - 1);
                        var ay = (float)(fy - fy0);
                        var row = (yy - roi.Y - y0) * width;
                        for (var xx = inter.Left; xx < inter.Right; xx++)
                        {
                            var fx = Math.Clamp((xx - p.Canvas.X + 0.5) * p.Scale - 0.5, 0, fw - 1);
                            int fx0 = (int)fx, fx1 = Math.Min(fx0 + 1, fw - 1);
                            var ax = (float)(fx - fx0);
                            float* ra = fp + fy0 * fst, rb = fp + fy1 * fst;
                            var vx = (ra[fx0 * 2] * (1 - ax) + ra[fx1 * 2] * ax) * (1 - ay) + (rb[fx0 * 2] * (1 - ax) + rb[fx1 * 2] * ax) * ay;
                            var vy = (ra[fx0 * 2 + 1] * (1 - ax) + ra[fx1 * 2 + 1] * ax) * (1 - ay) + (rb[fx0 * 2 + 1] * (1 - ax) + rb[fx1 * 2 + 1] * ax) * ay;
                            ddx[row + xx - roi.X] += vx;
                            ddy[row + xx - roi.X] += vy;
                        }
                    }
                }
            }

            var pm = (byte*)mask.Data + (long)y0 * mask.Step();
            var mstep = mask.Step();
            var dxArr = ddx;
            var dyArr = ddy;
            Parallel.For(0, shh, options, yy =>
            {
                var rx = px + (long)yy * width;
                var ry = py + (long)yy * width;
                var rm = pm + yy * mstep;
                double vBase = roi.Y + y0 + yy + lay.V0;
                for (var x = 0; x < width; x++)
                {
                    double u = roi.X + x + lay.U0, v = vBase;
                    if (dxArr is not null)
                    {
                        u += dxArr[yy * width + x];
                        v += dyArr![yy * width + x];
                    }

                    double sx, sy;
                    bool inside;
                    if (flat)
                    {
                        // Leinwand (Maßstab 1) → Bild: M⁻¹ · (P − T)
                        double qx = u / s - tx, qy = v / s - ty;
                        sx = ia * qx - ib * qy;
                        sy = ib * qx + ia * qy;
                        inside = sx >= 0 && sx <= w - 1 && sy >= 0 && sy <= h - 1;
                    }
                    else
                    {
                        Projections.Dir(proj, u / s, v / s, out var dx, out var dy, out var dz);
                        var ccx = rt.A00 * dx + rt.A01 * dy + rt.A02 * dz;
                        var ccy = rt.A10 * dx + rt.A11 * dy + rt.A12 * dz;
                        var ccz = rt.A20 * dx + rt.A21 * dy + rt.A22 * dz;
                        if (ccz <= 1e-6)
                        {
                            rx[x] = -1e4f;
                            ry[x] = -1e4f;
                            rm[x] = 0;
                            continue;
                        }

                        double xu = ccx / ccz, yu = ccy / ccz;
                        var r2 = xu * xu + yu * yu;
                        var fac = 1 + r2 * (k1 + r2 * k2);
                        sx = f * xu * fac + cx;
                        sy = f * yu * fac + cy;
                        inside = sx >= 0 && sx <= w - 1 && sy >= 0 && sy <= h - 1 && r2 < 4;
                    }

                    double xs = (sx + 0.5) * srcScale - 0.5, ys = (sy + 0.5) * srcScale - 0.5;
                    if (xs < -sw)
                    {
                        xs = -sw;
                    }
                    else if (xs > 2 * sw)
                    {
                        xs = 2 * sw;
                    }

                    if (ys < -sh)
                    {
                        ys = -sh;
                    }
                    else if (ys > 2 * sh)
                    {
                        ys = 2 * sh;
                    }

                    rx[x] = (float)xs;
                    ry[x] = (float)ys;
                    rm[x] = (byte)(inside ? 255 : 0);
                }
            });
            using var dst = new Mat(img, new Rect(0, y0, width, shh));
            Cv2.Remap(src, dst, mapX, mapY, interp, BorderTypes.Reflect);
        }

        return (img, mask);
    }
}
