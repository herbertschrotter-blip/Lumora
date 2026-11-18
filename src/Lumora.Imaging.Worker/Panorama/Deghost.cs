using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>
/// Entgeisterung im Nahtband (Port aus Spike 20 Runde 2): je Paar benachbarter Teile wird um die Naht (±Band) bei halber
/// Auflösung der optische Fluss zwischen den beiden verzerrten Bildern gemessen (Farneback), beide Bilder werden je zur Hälfte
/// zur Mitte verschoben, außerhalb des Bandes auf 0 ausgeblendet. Die Verschiebung fließt beim Zusammensetzen in dieselbe
/// Rückwärtsabbildung ein (kein zweites Remap). Nebenbei: Nahtgüte je Paar (NCC im texturierten Nahtband, vorher/nachher).
/// </summary>
internal static class Deghost
{
    public sealed class SeamStat
    {
        public int A { get; init; }

        public int B { get; init; }

        public double NccBefore { get; set; }

        public double NccAfter { get; set; }

        public int Px { get; set; }
    }

    /// <param name="ls">Naht-Leinwand (Etiketten aus <paramref name="sMasks"/>).</param>
    /// <param name="lf">Fluss-Leinwand (Kompositionsmaßstab × <paramref name="fs"/>).</param>
    /// <param name="lc">Kompositions-Leinwand.</param>
    /// <param name="source">Quellbild einer Kamera und dessen Maßstab gegenüber voller Auflösung.</param>
    /// <param name="measureOnly">Nur Nahtgüte messen (Vorschau): keine Felder.</param>
    public static (List<FlowPatch>[] Patches, List<SeamStat> Stats) Run(PanoLayout ls, IList<Mat> sMasks, IList<Point> sCorners, PanoLayout lf,
        PanoLayout lc, IReadOnlyList<PanoCamera> cams, Func<int, (Mat Img, double Scale)> source, Proj proj, double bandComposePx, double fs,
        bool measureOnly, CancellationToken ct)
    {
        var np = ls.Parts.Count;
        var label = new int[ls.W * ls.H];
        Array.Fill(label, -1);
        for (var k = 0; k < np; k++)
        {
            var m = sMasks[k];
            var c = sCorners[k];
            int rows = m.Rows, cols = m.Cols;
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < cols; x++)
                {
                    if (m.At<byte>(y, x) != 0)
                    {
                        int xx = c.X + x, yy = c.Y + y;
                        if (xx >= 0 && yy >= 0 && xx < ls.W && yy < ls.H)
                        {
                            label[yy * ls.W + xx] = k;
                        }
                    }
                }
            }
        }

        var bnd = new Dictionary<(int, int), List<Point>>();
        for (var y = 0; y < ls.H - 1; y++)
        {
            for (var x = 0; x < ls.W - 1; x++)
            {
                var l = label[y * ls.W + x];
                if (l < 0)
                {
                    continue;
                }

                foreach (var o in new[] { label[y * ls.W + x + 1], label[(y + 1) * ls.W + x] })
                {
                    if (o < 0 || o == l)
                    {
                        continue;
                    }

                    var key = (Math.Min(l, o), Math.Max(l, o));
                    if (!bnd.TryGetValue(key, out var lst))
                    {
                        bnd[key] = lst = [];
                    }

                    lst.Add(new Point(x, y));
                }
            }
        }

        var patches = Enumerable.Range(0, np).Select(_ => new List<FlowPatch>()).ToArray();
        var stats = new List<SeamStat>();
        var lk = new object();
        var sf = lf.S / ls.S; // Naht-Pixel → Fluss-Pixel
        var bandF = bandComposePx * fs;
        var work = bnd.Where(kv => kv.Value.Count >= 8).OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).ToList();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(WorkerThreads.Count, 6)), CancellationToken = ct };
        Parallel.ForEach(work, options, kv =>
        {
            var (a, b) = kv.Key;
            var pts = kv.Value;
            int minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            Point2d ToF(double x, double y) => new((x + ls.U0) * sf - lf.U0, (y + ls.V0) * sf - lf.V0);
            var p0 = ToF(minX, minY);
            var p1 = ToF(maxX + 1, maxY + 1);
            var pad = (int)Math.Ceiling(bandF) + 4;
            var rf = new Rect((int)Math.Floor(p0.X) - pad, (int)Math.Floor(p0.Y) - pad, (int)Math.Ceiling(p1.X - p0.X) + 2 * pad, (int)Math.Ceiling(p1.Y - p0.Y) + 2 * pad);
            rf &= new Rect(0, 0, lf.W, lf.H);
            rf &= lf.Parts[a].Roi & lf.Parts[b].Roi;
            if (rf.Width < 16 || rf.Height < 16)
            {
                return;
            }

            var ca = cams[ls.Parts[a].Cam];
            var cb = cams[ls.Parts[b].Cam];
            var (sa0, ssa) = source(ls.Parts[a].Cam);
            var (sb0, ssb) = source(ls.Parts[b].Cam);
            var (ia, ma) = Warper.Warp(sa0, ssa, ca, lf, rf, proj, InterpolationFlags.Linear, threads: 1);
            var (ib, mb) = Warper.Warp(sb0, ssb, cb, lf, rf, proj, InterpolationFlags.Linear, threads: 1);
            using var na = Normalize(ia, out var texA);
            using var nb = Normalize(ib, out var texB);
            ia.Dispose();
            ib.Dispose();
            int w = rf.Width, h = rf.Height;
            using var valid = new Mat();
            Cv2.BitwiseAnd(ma, mb, valid);
            ma.Dispose();
            mb.Dispose();
            using var seamImg = new Mat(h, w, MatType.CV_8UC1, Scalar.All(255));
            foreach (var p in pts)
            {
                var q = ToF(p.X + 0.5, p.Y + 0.5);
                Cv2.Circle(seamImg, new Point((int)(q.X - rf.X), (int)(q.Y - rf.Y)), Math.Max(1, (int)(sf / 2)), Scalar.All(0), -1);
            }

            using var dSeam = new Mat();
            Cv2.DistanceTransform(seamImg, dSeam, DistanceTypes.L2, DistanceTransformMasks.Mask5);
            using var dValid = new Mat();
            Cv2.DistanceTransform(valid, dValid, DistanceTypes.L2, DistanceTransformMasks.Mask5);
            var st = new SeamStat { A = a, B = b };
            (st.NccBefore, st.Px) = Ncc(na, nb, dSeam, dValid, texA, texB, bandF * 0.5, new Rect(0, 0, w, h));
            if (measureOnly)
            {
                st.NccAfter = st.NccBefore;
                texA.Dispose();
                texB.Dispose();
                lock (lk)
                {
                    stats.Add(st);
                }

                return;
            }

            using var a8 = new Mat();
            using var b8 = new Mat();
            na.ConvertTo(a8, MatType.CV_8U, 40, 128);
            nb.ConvertTo(b8, MatType.CV_8U, 40, 128);
            using var fab = new Mat();
            using var fba = new Mat();
            Cv2.CalcOpticalFlowFarneback(a8, b8, fab, 0.5, 4, 25, 4, 7, 1.5, OpticalFlowFlags.FarnebackGaussian);
            Cv2.CalcOpticalFlowFarneback(b8, a8, fba, 0.5, 4, 25, 4, 7, 1.5, OpticalFlowFlags.FarnebackGaussian);

            // Verlässlichkeit: Hin-/Rückfluss stimmig (< 1,5 px) und Textur vorhanden; danach vertrauensgewichtet glätten
            using var conf = new Mat(h, w, MatType.CV_32FC1, Scalar.All(0));
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (texA.At<float>(y, x) < 3 && texB.At<float>(y, x) < 3)
                    {
                        continue;
                    }

                    var v = fab.At<Vec2f>(y, x);
                    int x2 = (int)Math.Round(x + v.Item0), y2 = (int)Math.Round(y + v.Item1);
                    if (x2 < 0 || y2 < 0 || x2 >= w || y2 >= h)
                    {
                        continue;
                    }

                    var u = fba.At<Vec2f>(y2, x2);
                    double ex = v.Item0 + u.Item0, ey = v.Item1 + u.Item1;
                    if (ex * ex + ey * ey < 1.5 * 1.5)
                    {
                        conf.Set(y, x, 1f);
                    }
                }
            }

            var fch = fab.Split();
            using var confBlur = new Mat();
            Cv2.GaussianBlur(conf, confBlur, new Size(0, 0), 6);
            for (var c = 0; c < 2; c++)
            {
                Cv2.Multiply(fch[c], conf, fch[c]);
                Cv2.GaussianBlur(fch[c], fch[c], new Size(0, 0), 6);
            }

            using var flow = new Mat(h, w, MatType.CV_32FC2, Scalar.All(0));
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c0 = confBlur.At<float>(y, x);
                    if (c0 < 0.05f)
                    {
                        continue;
                    }

                    var fade = Math.Min(1f, c0 / 0.3f);
                    flow.Set(y, x, new Vec2f(fch[0].At<float>(y, x) / c0 * fade, fch[1].At<float>(y, x) / c0 * fade));
                }
            }

            foreach (var m in fch)
            {
                m.Dispose();
            }

            var fa = new Mat(h, w, MatType.CV_32FC2, Scalar.All(0));
            var fb = new Mat(h, w, MatType.CV_32FC2, Scalar.All(0));
            const double limit = 20; // Fluss-Pixel (= 40 Leinwand-Pixel)
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    float ds = dSeam.At<float>(y, x), dv = dValid.At<float>(y, x);
                    var wgt = Math.Clamp(1 - ds / bandF, 0, 1) * Math.Clamp(dv / 8.0, 0, 1);
                    if (wgt <= 0)
                    {
                        continue;
                    }

                    var v = flow.At<Vec2f>(y, x);
                    var mag = Math.Sqrt(v.Item0 * v.Item0 + v.Item1 * v.Item1);
                    var s = mag > limit ? limit / mag : 1;
                    float vx = (float)(v.Item0 * s * wgt * 0.5 / fs), vy = (float)(v.Item1 * s * wgt * 0.5 / fs);
                    fa.Set(y, x, new Vec2f(-vx, -vy));
                    fb.Set(y, x, new Vec2f(vx, vy));
                }
            }

            using (var mapA = new Mat(h, w, MatType.CV_32FC2))
            using (var mapB = new Mat(h, w, MatType.CV_32FC2))
            {
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var va = fa.At<Vec2f>(y, x);
                        var vb = fb.At<Vec2f>(y, x);
                        mapA.Set(y, x, new Vec2f(x + (float)(va.Item0 * fs), y + (float)(va.Item1 * fs)));
                        mapB.Set(y, x, new Vec2f(x + (float)(vb.Item0 * fs), y + (float)(vb.Item1 * fs)));
                    }
                }

                using var na2 = new Mat();
                using var nb2 = new Mat();
                using var none = new Mat();
                Cv2.Remap(na, na2, mapA, none, InterpolationFlags.Linear, BorderTypes.Reflect);
                Cv2.Remap(nb, nb2, mapB, none, InterpolationFlags.Linear, BorderTypes.Reflect);
                (st.NccAfter, _) = Ncc(na2, nb2, dSeam, dValid, texA, texB, bandF * 0.5, new Rect(0, 0, w, h));
            }

            texA.Dispose();
            texB.Dispose();
            var rc = new Rect(
                (int)Math.Round((rf.X + lf.U0) / lf.S * lc.S - lc.U0),
                (int)Math.Round((rf.Y + lf.V0) / lf.S * lc.S - lc.V0),
                (int)Math.Round(rf.Width / lf.S * lc.S),
                (int)Math.Round(rf.Height / lf.S * lc.S));
            var sa = new Mat();
            var sb = new Mat();
            Cv2.Resize(fa, sa, new Size((w + 1) / 2, (h + 1) / 2), 0, 0, InterpolationFlags.Area);
            Cv2.Resize(fb, sb, new Size((w + 1) / 2, (h + 1) / 2), 0, 0, InterpolationFlags.Area);
            fa.Dispose();
            fb.Dispose();
            var scale = sa.Width / (double)Math.Max(1, rc.Width);
            lock (lk)
            {
                patches[a].Add(new FlowPatch { Canvas = rc, Flow = sa, Scale = scale });
                patches[b].Add(new FlowPatch { Canvas = rc, Flow = sb, Scale = scale });
                stats.Add(st);
            }
        });
        stats.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        foreach (var list in patches)
        {
            list.Sort((x, y) => x.Canvas.X != y.Canvas.X ? x.Canvas.X.CompareTo(y.Canvas.X) : x.Canvas.Y.CompareTo(y.Canvas.Y));
        }

        return (patches, stats);
    }

    /// <summary>Median der Nahtgüte über Nähte mit genug texturierten Pixeln, NaN ohne.</summary>
    public static double MedianNcc(IEnumerable<SeamStat> stats, bool after)
    {
        var v = stats.Where(s => s.Px > 500).Select(s => after ? s.NccAfter : s.NccBefore).Where(double.IsFinite).OrderBy(x => x).ToArray();
        return v.Length == 0 ? double.NaN : v[v.Length / 2];
    }

    /// <summary>Lokale Kontrastnormierung des Grauwerts (σ 6 px); tex = lokale Std.-Abw. in Graustufen.</summary>
    private static Mat Normalize(Mat bgr, out Mat tex)
    {
        using var g = new Mat();
        Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY);
        using var f = new Mat();
        g.ConvertTo(f, MatType.CV_32F);
        using var mu = new Mat();
        Cv2.GaussianBlur(f, mu, new Size(0, 0), 6);
        using var d = new Mat();
        Cv2.Subtract(f, mu, d);
        using var d2 = new Mat();
        Cv2.Multiply(d, d, d2);
        using var v = new Mat();
        Cv2.GaussianBlur(d2, v, new Size(0, 0), 6);
        tex = new Mat();
        Cv2.Sqrt(v, tex);
        using var den = new Mat();
        Cv2.Add(tex, new Scalar(4), den);
        var n = new Mat();
        Cv2.Divide(d, den, n);
        return n;
    }

    private static (double Ncc, int Px) Ncc(Mat a, Mat b, Mat dSeam, Mat dValid, Mat ta, Mat tb, double band, Rect r)
    {
        double sab = 0, saa = 0, sbb = 0;
        var n = 0;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            for (var x = r.Left; x < r.Right; x++)
            {
                if (dSeam.At<float>(y, x) > band || dValid.At<float>(y, x) < 4)
                {
                    continue;
                }

                if (ta.At<float>(y, x) < 3 || tb.At<float>(y, x) < 3)
                {
                    continue;
                }

                double va = a.At<float>(y, x), vb = b.At<float>(y, x);
                sab += va * vb;
                saa += va * va;
                sbb += vb * vb;
                n++;
            }
        }

        return (n == 0 ? double.NaN : sab / Math.Sqrt(saa * sbb + 1e-12), n);
    }
}
