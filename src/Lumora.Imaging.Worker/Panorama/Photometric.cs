using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>
/// Photometrisches Modell je Bild nach Hugin-Art (Port aus Spike 20 Runde 2): Belichtung E_k, Weißabgleich W_kR/W_kB
/// (Grün fest), gemeinsame Vignettierung V(r) = b1·r² + b2·r⁴ + b3·r⁶ (log), angepasst an Überlappungsproben im Linearraum:
/// log I_a − log I_b = (E_a − E_b) + (W_ac − W_bc) + V(r_a) − V(r_b). Linear in den Unbekannten → Normalgleichungen,
/// Huber-IRLS. Angewandt nur, wenn das Modell die Überlappungen erklärt (Handy-JPEGs mit lokaler Tonung meist nicht).
/// </summary>
internal sealed class Photometric
{
    /// <summary>Restfehler (log-RMS), ab dem das Modell die Überlappungen nicht erklärt und nicht angewandt wird.</summary>
    public const double MaxRmsToApply = 0.25;

    public double[] E { get; init; } = [];

    public double[] Wr { get; init; } = [];

    public double[] Wb { get; init; } = [];

    public double B1 { get; init; }

    public double B2 { get; init; }

    public double B3 { get; init; }

    public double RmsBefore { get; init; }

    public double RmsAfter { get; init; }

    public bool Applicable => E.Length > 0 && double.IsFinite(RmsAfter) && RmsAfter < MaxRmsToApply;

    private static readonly double[] Dec = Enumerable.Range(0, 256).Select(v =>
    {
        var c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }).ToArray();

    private static readonly byte[] Enc = Enumerable.Range(0, 16385).Select(i =>
    {
        var l = i / 16384.0;
        var c = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(c * 255), 0, 255);
    }).ToArray();

    private readonly record struct Sample(int A, int B, float Ra, float Rb, float LaR, float LaG, float LaB, float LbR, float LbG, float LbB);

    /// <summary>
    /// <paramref name="works"/>: Arbeitsbilder (sRGB 8 Bit) je Kamera-Index, Maßstab <paramref name="ws"/> gegenüber voller
    /// Auflösung.
    /// </summary>
    public static Photometric Fit(IReadOnlyList<PanoCamera> cams, IReadOnlyList<Mat> works, double ws, CancellationToken ct)
    {
        var nc = cams.Count;
        var blurred = new Mat[nc];
        var grads = new Mat[nc];
        var options = new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count, CancellationToken = ct };
        Parallel.For(0, nc, options, i =>
        {
            var b = new Mat();
            Cv2.GaussianBlur(works[i], b, new Size(0, 0), 1.5);
            using var g = new Mat();
            Cv2.CvtColor(b, g, ColorConversionCodes.BGR2GRAY);
            using var gx = new Mat();
            using var gy = new Mat();
            Cv2.Sobel(g, gx, MatType.CV_32F, 1, 0);
            Cv2.Sobel(g, gy, MatType.CV_32F, 0, 1);
            var mag = new Mat();
            Cv2.Magnitude(gx, gy, mag);
            blurred[i] = b;
            grads[i] = mag;
        });
        var samples = new List<Sample>();
        var lockObj = new object();
        var pairList = new List<(int A, int B)>();
        for (var a = 0; a < nc; a++)
        {
            for (var b = a + 1; b < nc; b++)
            {
                var ang = Math.Acos(Math.Clamp(cams[a].R.Col(2).Dot(cams[b].R.Col(2)), -1, 1));
                if (ang < 2 * cams[a].HalfDiagonalFov)
                {
                    pairList.Add((a, b));
                }
            }
        }

        static double HalfDiag(PanoCamera c) => Math.Sqrt(Math.Pow((c.Size.Width - 1) / 2.0, 2) + Math.Pow((c.Size.Height - 1) / 2.0, 2));
        static double R2(PanoCamera c, double x, double y)
        {
            double dx = x - (c.Size.Width - 1) / 2.0, dy = y - (c.Size.Height - 1) / 2.0, hd = HalfDiag(c);
            return (dx * dx + dy * dy) / (hd * hd);
        }

        try
        {
            Parallel.ForEach(pairList, options, pr =>
            {
                var ca = cams[pr.A];
                var cb = cams[pr.B];
                var ba = blurred[pr.A];
                var bb = blurred[pr.B];
                var gra = grads[pr.A];
                int bah = ba.Height, baw = ba.Width, bbh = bb.Height, bbw = bb.Width;
                var step = Math.Max(6, Math.Min(baw, bah) / 45);
                var local = new List<Sample>();
                Span<double> va = stackalloc double[3];
                Span<double> vb = stackalloc double[3];
                for (var y = step / 2; y < bah - 4; y += step)
                {
                    for (var x = step / 2; x < baw - 4; x += step)
                    {
                        if (gra.At<float>(y, x) > 12)
                        {
                            continue; // Kanten/Parallaxe meiden
                        }

                        double xa = (x + 0.5) / ws - 0.5, ya = (y + 0.5) / ws - 0.5;
                        var d = ca.Ray(xa, ya);
                        if (!cb.Project(d, out var xbFull, out var ybFull))
                        {
                            continue;
                        }

                        double xb = (xbFull + 0.5) * ws - 0.5, yb = (ybFull + 0.5) * ws - 0.5;
                        if (xb < 4 || yb < 4 || xb > bbw - 5 || yb > bbh - 5)
                        {
                            continue;
                        }

                        var pa = ba.At<Vec3b>(y, x);
                        Bilinear(bb, xb, yb, vb);
                        va[0] = pa.Item2;
                        va[1] = pa.Item1;
                        va[2] = pa.Item0;
                        var bad = false;
                        for (var c = 0; c < 3; c++)
                        {
                            if (va[c] < 10 || vb[c] < 10 || va[c] > 248 || vb[c] > 248)
                            {
                                bad = true;
                            }
                        }

                        if (bad)
                        {
                            continue;
                        }

                        ToLinear(va);
                        ToLinear(vb);
                        local.Add(new Sample(pr.A, pr.B, (float)R2(ca, xa, ya), (float)R2(cb, xbFull, ybFull),
                            (float)va[0], (float)va[1], (float)va[2], (float)vb[0], (float)vb[1], (float)vb[2]));
                    }
                }

                lock (lockObj)
                {
                    samples.AddRange(local);
                }
            });
        }
        finally
        {
            foreach (var b in blurred)
            {
                b?.Dispose();
            }

            foreach (var g in grads)
            {
                g?.Dispose();
            }
        }

        // Proben deterministisch ordnen (parallel gesammelt)
        samples.Sort((p, q) => p.A != q.A ? p.A.CompareTo(q.A) : p.B != q.B ? p.B.CompareTo(q.B) : p.Ra.CompareTo(q.Ra) != 0 ? p.Ra.CompareTo(q.Ra) : p.LaG.CompareTo(q.LaG));
        if (samples.Count < 50)
        {
            return new Photometric { RmsBefore = double.NaN, RmsAfter = double.NaN };
        }

        var u = 3 * nc + 3;
        var x = new double[u];
        var wts = Enumerable.Repeat(1.0, samples.Count * 3).ToArray();
        double rms0 = 0, rms = 0;
        const double huber = 0.04;
        for (var it = 0; it < 6; it++)
        {
            ct.ThrowIfCancellationRequested();
            var a = new double[u, u];
            var bv = new double[u];
            var idx = new int[8];
            var val = new double[8];
            double sq = 0;
            var cnt = 0;
            for (var s = 0; s < samples.Count; s++)
            {
                var sm = samples[s];
                for (var c = 0; c < 3; c++)
                {
                    double la = c == 0 ? sm.LaR : c == 1 ? sm.LaG : sm.LaB, lb = c == 0 ? sm.LbR : c == 1 ? sm.LbG : sm.LbB;
                    var yv = Math.Log(la) - Math.Log(lb);
                    var k = 0;
                    idx[k] = 3 * sm.A;
                    val[k++] = 1;
                    idx[k] = 3 * sm.B;
                    val[k++] = -1;
                    if (c == 0)
                    {
                        idx[k] = 3 * sm.A + 1;
                        val[k++] = 1;
                        idx[k] = 3 * sm.B + 1;
                        val[k++] = -1;
                    }

                    if (c == 2)
                    {
                        idx[k] = 3 * sm.A + 2;
                        val[k++] = 1;
                        idx[k] = 3 * sm.B + 2;
                        val[k++] = -1;
                    }

                    double ra = sm.Ra, rb = sm.Rb;
                    idx[k] = 3 * nc;
                    val[k++] = ra - rb;
                    idx[k] = 3 * nc + 1;
                    val[k++] = ra * ra - rb * rb;
                    idx[k] = 3 * nc + 2;
                    val[k++] = ra * ra * ra - rb * rb * rb;
                    double pred = 0;
                    for (var q = 0; q < k; q++)
                    {
                        pred += val[q] * x[idx[q]];
                    }

                    var res = yv - pred;
                    sq += res * res;
                    cnt++;
                    var w = wts[s * 3 + c];
                    for (var p = 0; p < k; p++)
                    {
                        bv[idx[p]] += w * val[p] * yv;
                        for (var q = 0; q < k; q++)
                        {
                            a[idx[p], idx[q]] += w * val[p] * val[q];
                        }
                    }
                }
            }

            if (it == 0)
            {
                rms0 = Math.Sqrt(sq / Math.Max(1, cnt));
            }
            else
            {
                rms = Math.Sqrt(sq / Math.Max(1, cnt));
            }

            // Eichung: Σ E = 0, Σ Wr = 0, Σ Wb = 0 (stark); schwache Ridge-Terme
            for (var t = 0; t < 3; t++)
            {
                const double gw = 1e6;
                for (var p = 0; p < nc; p++)
                {
                    for (var q = 0; q < nc; q++)
                    {
                        a[3 * p + t, 3 * q + t] += gw;
                    }
                }
            }

            for (var p = 0; p < nc; p++)
            {
                a[3 * p, 3 * p] += 1;
                a[3 * p + 1, 3 * p + 1] += 50;
                a[3 * p + 2, 3 * p + 2] += 50;
            }

            for (var p = 0; p < 3; p++)
            {
                a[3 * nc + p, 3 * nc + p] += 10;
            }

            using var am = new Mat(u, u, MatType.CV_64FC1);
            using var bm = new Mat(u, 1, MatType.CV_64FC1);
            for (var p = 0; p < u; p++)
            {
                bm.Set(p, 0, bv[p]);
                for (var q = 0; q < u; q++)
                {
                    am.Set(p, q, a[p, q]);
                }
            }

            using var xm = new Mat();
            if (!Cv2.Solve(am, bm, xm, DecompTypes.Cholesky))
            {
                Cv2.Solve(am, bm, xm, DecompTypes.SVD);
            }

            for (var p = 0; p < u; p++)
            {
                x[p] = xm.At<double>(p, 0);
            }

            for (var s = 0; s < samples.Count; s++)
            {
                var sm = samples[s];
                for (var c = 0; c < 3; c++)
                {
                    double la = c == 0 ? sm.LaR : c == 1 ? sm.LaG : sm.LaB, lb = c == 0 ? sm.LbR : c == 1 ? sm.LbG : sm.LbB;
                    var pred = x[3 * sm.A] - x[3 * sm.B] + (c == 0 ? x[3 * sm.A + 1] - x[3 * sm.B + 1] : c == 2 ? x[3 * sm.A + 2] - x[3 * sm.B + 2] : 0)
                               + x[3 * nc] * (sm.Ra - sm.Rb) + x[3 * nc + 1] * (sm.Ra * sm.Ra - sm.Rb * sm.Rb) + x[3 * nc + 2] * (Math.Pow(sm.Ra, 3) - Math.Pow(sm.Rb, 3));
                    var e = Math.Abs(Math.Log(la) - Math.Log(lb) - pred);
                    wts[s * 3 + c] = e <= huber ? 1 : huber / e;
                }
            }
        }

        var pm = new Photometric
        {
            RmsBefore = rms0,
            RmsAfter = rms,
            E = new double[nc],
            Wr = new double[nc],
            Wb = new double[nc],
            B1 = x[3 * nc],
            B2 = x[3 * nc + 1],
            B3 = x[3 * nc + 2],
        };
        for (var k = 0; k < nc; k++)
        {
            pm.E[k] = x[3 * k];
            pm.Wr[k] = x[3 * k + 1];
            pm.Wb[k] = x[3 * k + 2];
        }

        return pm;
    }

    private static void ToLinear(Span<double> c)
    {
        for (var i = 0; i < 3; i++)
        {
            var v = Math.Clamp(c[i], 0, 255);
            var i0 = (int)v;
            var i1 = Math.Min(255, i0 + 1);
            var a = v - i0;
            c[i] = Dec[i0] * (1 - a) + Dec[i1] * a;
        }
    }

    private static void Bilinear(Mat m, double x, double y, Span<double> rgb)
    {
        int x0 = (int)x, y0 = (int)y;
        double ax = x - x0, ay = y - y0;
        var a = m.At<Vec3b>(y0, x0);
        var b = m.At<Vec3b>(y0, x0 + 1);
        var c = m.At<Vec3b>(y0 + 1, x0);
        var d = m.At<Vec3b>(y0 + 1, x0 + 1);
        for (var ch = 0; ch < 3; ch++)
        {
            var bi = 2 - ch; // RGB ← BGR
            rgb[ch] = (a[bi] * (1 - ax) + b[bi] * ax) * (1 - ay) + (c[bi] * (1 - ax) + d[bi] * ax) * ay;
        }
    }

    /// <summary>Wendet das Modell auf ein Bild der Kamera <paramref name="k"/> an (beliebige Auflösung), in place.</summary>
    public unsafe void Apply(Mat img, int k)
    {
        int w = img.Width, h = img.Height;
        var hd2 = Math.Pow((w - 1) / 2.0, 2) + Math.Pow((h - 1) / 2.0, 2);
        const int n = 4096;
        var vlut = new double[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var r = 1.25 * i / n;
            vlut[i] = Math.Exp(-(B1 * r + B2 * r * r + B3 * r * r * r));
        }

        double fr = Math.Exp(-(E[k] + Wr[k])), fg = Math.Exp(-E[k]), fb = Math.Exp(-(E[k] + Wb[k]));
        var p0 = (byte*)img.Data;
        var st = img.Step();
        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count }, y =>
        {
            var row = p0 + y * st;
            var dy2 = Math.Pow(y - (h - 1) / 2.0, 2);
            Span<double> c = stackalloc double[3];
            for (var x = 0; x < w; x++)
            {
                var r2 = (Math.Pow(x - (w - 1) / 2.0, 2) + dy2) / hd2;
                var v = vlut[Math.Min(n, (int)(r2 / 1.25 * n))];
                c[0] = Dec[row[x * 3 + 2]];
                c[1] = Dec[row[x * 3 + 1]];
                c[2] = Dec[row[x * 3]];
                c[0] *= fr * v;
                c[1] *= fg * v;
                c[2] *= fb * v;
                var mx = Math.Max(c[0], Math.Max(c[1], c[2]));
                if (mx > 1)
                {
                    c[0] /= mx;
                    c[1] /= mx;
                    c[2] /= mx;
                }

                row[x * 3 + 2] = Enc[(int)(Math.Clamp(c[0], 0, 1) * 16384 + 0.5)];
                row[x * 3 + 1] = Enc[(int)(Math.Clamp(c[1], 0, 1) * 16384 + 0.5)];
                row[x * 3] = Enc[(int)(Math.Clamp(c[2], 0, 1) * 16384 + 0.5)];
            }
        });
    }
}
