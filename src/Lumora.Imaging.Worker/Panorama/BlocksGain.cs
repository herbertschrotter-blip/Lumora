using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>
/// Belichtungsausgleich in Blöcken je Kanal (wie cv::detail::BlocksChannelsCompensator, σN = 10, σg = 0,1; Port aus Spike 20
/// Runde 2): jeder Block ist ein „Bild“ im GainCompensator-Gleichungssystem, gelöst dünn besetzt per CG; danach
/// Verstärkungskarte je Bild zweimal mit [¼ ½ ¼] geglättet.
/// </summary>
internal static class BlocksGain
{
    private sealed class Block
    {
        public int Part { get; init; }

        public Rect InImg { get; init; }

        public Rect OnCanvas { get; init; }
    }

    /// <summary>Verstärkungskarten (1 oder 3 Kanäle, Blockraster) je Teil; <paramref name="imgs"/> 8UC3.</summary>
    public static unsafe Mat[] Compute(IList<Point> corners, IList<Mat> imgs, IList<Mat> masks, bool perChannel, int blW, int blH, out (double Min, double Max) range)
    {
        var blocks = new List<Block>();
        var grid = new (int Nx, int Ny, int First)[imgs.Count];
        for (var p = 0; p < imgs.Count; p++)
        {
            int w = imgs[p].Width, h = imgs[p].Height;
            int nx = Math.Max(1, (w + blW - 1) / blW), ny = Math.Max(1, (h + blH - 1) / blH);
            int bw = (w + nx - 1) / nx, bh = (h + ny - 1) / ny;
            grid[p] = (nx, ny, blocks.Count);
            for (var by = 0; by < ny; by++)
            {
                for (var bx = 0; bx < nx; bx++)
                {
                    var r = new Rect(bx * bw, by * bh, Math.Max(0, Math.Min(bw, w - bx * bw)), Math.Max(0, Math.Min(bh, h - by * bh)));
                    blocks.Add(new Block { Part = p, InImg = r, OnCanvas = new Rect(r.X + corners[p].X, r.Y + corners[p].Y, r.Width, r.Height) });
                }
            }
        }

        var nb = blocks.Count;
        var nch = perChannel ? 3 : 1;
        var mk = masks.Select(m =>
        {
            var b = new byte[m.Rows * m.Cols];
            System.Runtime.InteropServices.Marshal.Copy(m.Data, b, 0, b.Length);
            return b;
        }).ToArray();

        // Kandidaten über ein grobes Raster der Leinwand
        var buckets = new Dictionary<long, List<int>>();
        const int cell = 64;
        for (var i = 0; i < nb; i++)
        {
            var r = blocks[i].OnCanvas;
            if (r.Width <= 0 || r.Height <= 0)
            {
                continue;
            }

            for (var gy = Floor(r.Top, cell); gy <= Floor(r.Bottom - 1, cell); gy++)
            {
                for (var gx = Floor(r.Left, cell); gx <= Floor(r.Right - 1, cell); gx++)
                {
                    var key = ((long)gy << 32) ^ (uint)gx;
                    if (!buckets.TryGetValue(key, out var lst))
                    {
                        buckets[key] = lst = [];
                    }

                    lst.Add(i);
                }
            }
        }

        var cand = new List<(int I, int J)>();
        var seen = new HashSet<long>();
        foreach (var lst in buckets.Values)
        {
            for (var a = 0; a < lst.Count; a++)
            {
                for (var c = a + 1; c < lst.Count; c++)
                {
                    int i = Math.Min(lst[a], lst[c]), j = Math.Max(lst[a], lst[c]);
                    if (blocks[i].Part != blocks[j].Part && seen.Add(((long)i << 32) | (uint)j))
                    {
                        cand.Add((i, j));
                    }
                }
            }
        }

        var gainsCh = new double[nch][];
        double gMin = double.MaxValue, gMax = double.MinValue;
        for (var ch = 0; ch < nch; ch++)
        {
            // Intensität je Pixel: Betrag des BGR-Vektors (wie OpenCV) bzw. ein Kanal
            var channel = ch;
            var inten = imgs.Select(m =>
            {
                int rows = m.Rows, cols = m.Cols;
                var f = new float[rows * cols];
                var d = (byte*)m.Data;
                var st = m.Step();
                for (var y = 0; y < rows; y++)
                {
                    for (var x = 0; x < cols; x++)
                    {
                        var q = d + y * st + x * 3;
                        f[y * cols + x] = perChannel ? q[channel] : MathF.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2]);
                    }
                }

                return f;
            }).ToArray();
            const double alpha = 0.01, beta = 100;
            var diag = new double[nb];
            var rhs = new double[nb];
            var off = Enumerable.Range(0, nb).Select(_ => new List<(int J, double A)>()).ToArray();
            for (var i = 0; i < nb; i++)
            {
                var b = blocks[i];
                var cnt = 0;
                var m = mk[b.Part];
                var w = imgs[b.Part].Cols;
                for (var y = b.InImg.Top; y < b.InImg.Bottom; y++)
                {
                    for (var x = b.InImg.Left; x < b.InImg.Right; x++)
                    {
                        if (m[y * w + x] != 0)
                        {
                            cnt++;
                        }
                    }
                }

                diag[i] += beta * cnt;
                rhs[i] += beta * cnt;
            }

            foreach (var (i, j) in cand)
            {
                var bi = blocks[i];
                var bj = blocks[j];
                var ov = bi.OnCanvas & bj.OnCanvas;
                if (ov.Width <= 0 || ov.Height <= 0)
                {
                    continue;
                }

                int wi = imgs[bi.Part].Cols, wj = imgs[bj.Part].Cols;
                var ci = corners[bi.Part];
                var cj = corners[bj.Part];
                var mi = mk[bi.Part];
                var mj = mk[bj.Part];
                var ii = inten[bi.Part];
                var ij = inten[bj.Part];
                var n = 0;
                double si = 0, sj = 0;
                for (var y = ov.Top; y < ov.Bottom; y++)
                {
                    for (var x = ov.Left; x < ov.Right; x++)
                    {
                        int oi = (y - ci.Y) * wi + (x - ci.X), oj = (y - cj.Y) * wj + (x - cj.X);
                        if (mi[oi] != 0 && mj[oj] != 0)
                        {
                            n++;
                            si += ii[oi];
                            sj += ij[oj];
                        }
                    }
                }

                if (n == 0)
                {
                    continue;
                }

                double iij = si / n, iji = sj / n;
                diag[i] += beta * n + 2 * alpha * iij * iij * n;
                rhs[i] += beta * n;
                diag[j] += beta * n + 2 * alpha * iji * iji * n;
                rhs[j] += beta * n;
                off[i].Add((j, -2 * alpha * iij * iji * n));
                off[j].Add((i, -2 * alpha * iji * iij * n));
            }

            for (var i = 0; i < nb; i++)
            {
                if (diag[i] == 0)
                {
                    diag[i] = 1;
                    rhs[i] = 1;
                }
            }

            var g = SolveCg(diag, off, rhs);
            gainsCh[ch] = g;
            gMin = Math.Min(gMin, g.Min());
            gMax = Math.Max(gMax, g.Max());
        }

        range = (gMin, gMax);
        var maps = new Mat[imgs.Count];
        using var ker = Mat.FromPixelData(1, 3, MatType.CV_32FC1, new[] { 0.25f, 0.5f, 0.25f });
        for (var p = 0; p < imgs.Count; p++)
        {
            var (nx, ny, first) = grid[p];
            var chans = new Mat[nch];
            for (var ch = 0; ch < nch; ch++)
            {
                var m = new Mat(ny, nx, MatType.CV_32FC1);
                for (var by = 0; by < ny; by++)
                {
                    for (var bx = 0; bx < nx; bx++)
                    {
                        m.Set(by, bx, (float)gainsCh[ch][first + by * nx + bx]);
                    }
                }

                for (var k = 0; k < 2; k++)
                {
                    Cv2.SepFilter2D(m, m, MatType.CV_32F, ker, ker);
                }

                chans[ch] = m;
            }

            if (nch == 1)
            {
                maps[p] = chans[0];
            }
            else
            {
                maps[p] = new Mat();
                Cv2.Merge(chans, maps[p]);
                foreach (var c in chans)
                {
                    c.Dispose();
                }
            }
        }

        return maps;
    }

    private static int Floor(int v, int c) => (int)Math.Floor(v / (double)c);

    private static double[] SolveCg(double[] diag, List<(int J, double A)>[] off, double[] b)
    {
        var n = b.Length;
        var x = new double[n];
        Array.Fill(x, 1.0); // Start bei Verstärkung 1
        double[] Mul(double[] v)
        {
            var r = new double[n];
            for (var i = 0; i < n; i++)
            {
                var s = diag[i] * v[i];
                foreach (var (j, a) in off[i])
                {
                    s += a * v[j];
                }

                r[i] = s;
            }

            return r;
        }

        var ax = Mul(x);
        var res = new double[n];
        for (var i = 0; i < n; i++)
        {
            res[i] = b[i] - ax[i];
        }

        var z = new double[n];
        for (var i = 0; i < n; i++)
        {
            z[i] = res[i] / diag[i];
        }

        var p = (double[])z.Clone();
        double rz = Dot(res, z), bn = Math.Sqrt(Dot(b, b));
        for (var iters = 0; iters < 5000; iters++)
        {
            if (Math.Sqrt(Dot(res, res)) < 1e-10 * bn)
            {
                break;
            }

            var ap = Mul(p);
            var al = rz / Dot(p, ap);
            for (var i = 0; i < n; i++)
            {
                x[i] += al * p[i];
                res[i] -= al * ap[i];
            }

            for (var i = 0; i < n; i++)
            {
                z[i] = res[i] / diag[i];
            }

            var rzn = Dot(res, z);
            var be = rzn / rz;
            rz = rzn;
            for (var i = 0; i < n; i++)
            {
                p[i] = z[i] + be * p[i];
            }
        }

        return x;
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }

    /// <summary>Verstärkungskarte auf Bildgröße (bilinear) und anwenden (wie BlocksCompensator::apply), 1 oder 3 Kanäle.</summary>
    public static unsafe void Apply(Mat img, Mat gainMap)
    {
        using var g = new Mat();
        Cv2.Resize(gainMap, g, img.Size(), 0, 0, InterpolationFlags.Linear);
        var gc = g.Channels();
        var d = (byte*)img.Data;
        var st = img.Step();
        var gp = (float*)g.Data;
        var gst = g.Step() / 4;
        var w = img.Cols;
        Parallel.For(0, img.Rows, new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count }, y =>
        {
            var row = d + y * st;
            var gr = gp + y * gst;
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var k = gr[x * gc + (gc == 3 ? c : 0)];
                    var v = (int)(row[x * 3 + c] * k + 0.5f);
                    row[x * 3 + c] = (byte)(v > 255 ? 255 : v);
                }
            }
        });
    }
}
