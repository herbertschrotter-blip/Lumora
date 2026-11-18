using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>
/// Mehrband-Überblendung wie cv::detail::MultiBandBlender (CPU-Pfad, Port aus Spike 20): Laplace-Pyramide in CV_16SC3,
/// Gewichte als Gauß-Pyramide in CV_32F, Ränder auf Vielfache von 2^Bänder ausgerichtet, Normierung je Ebene, Rückaufbau.
/// Die Bänder erhalten den Kontrast: hohe Frequenzen wechseln an der Naht hart, nur tiefe Frequenzen werden breit gemischt.
/// </summary>
internal sealed class MultiBandBlender : IDisposable
{
    private readonly int _nb;
    private readonly Rect _dstRoi;
    private readonly Rect _dstRoiFinal;
    private readonly Mat?[] _lap;
    private readonly Mat?[] _wts;

    public MultiBandBlender(Rect dst, int numBands)
    {
        _dstRoiFinal = dst;
        double maxLen = Math.Max(dst.Width, dst.Height);
        _nb = Math.Max(1, Math.Min(numBands, (int)Math.Ceiling(Math.Log(maxLen) / Math.Log(2))));
        var m = 1 << _nb;
        _dstRoi = new Rect(dst.X, dst.Y, dst.Width + (m - dst.Width % m) % m, dst.Height + (m - dst.Height % m) % m);
        _lap = new Mat?[_nb + 1];
        _wts = new Mat?[_nb + 1];
        _lap[0] = new Mat(_dstRoi.Height, _dstRoi.Width, MatType.CV_16SC3, Scalar.All(0));
        _wts[0] = new Mat(_dstRoi.Height, _dstRoi.Width, MatType.CV_32FC1, Scalar.All(0));
        for (var i = 1; i <= _nb; i++)
        {
            _lap[i] = new Mat((_lap[i - 1]!.Rows + 1) / 2, (_lap[i - 1]!.Cols + 1) / 2, MatType.CV_16SC3, Scalar.All(0));
            _wts[i] = new Mat((_wts[i - 1]!.Rows + 1) / 2, (_wts[i - 1]!.Cols + 1) / 2, MatType.CV_32FC1, Scalar.All(0));
        }
    }

    public int Bands => _nb;

    /// <summary>Bänder wie OpenCV: log2 der Blendbreite (5 % der Leinwanddiagonale) − 1.</summary>
    public static int BandsFor(int width, int height)
    {
        var blendWidth = Math.Sqrt((double)width * height) * 5 / 100.0;
        return Math.Max(1, (int)Math.Ceiling(Math.Log(Math.Max(2, blendWidth)) / Math.Log(2)) - 1);
    }

    public unsafe void Feed(Mat img, Mat mask, Point tl)
    {
        var m = 1 << _nb;
        var gap = 3 * m;
        var tlNew = new Point(Math.Max(_dstRoi.X, tl.X - gap), Math.Max(_dstRoi.Y, tl.Y - gap));
        var brNew = new Point(Math.Min(_dstRoi.Right, tl.X + img.Cols + gap), Math.Min(_dstRoi.Bottom, tl.Y + img.Rows + gap));
        tlNew.X = _dstRoi.X + (((tlNew.X - _dstRoi.X) >> _nb) << _nb);
        tlNew.Y = _dstRoi.Y + (((tlNew.Y - _dstRoi.Y) >> _nb) << _nb);
        int width = brNew.X - tlNew.X, height = brNew.Y - tlNew.Y;
        width += (m - width % m) % m;
        height += (m - height % m) % m;
        brNew.X = tlNew.X + width;
        brNew.Y = tlNew.Y + height;
        int dy = Math.Max(brNew.Y - _dstRoi.Bottom, 0), dx = Math.Max(brNew.X - _dstRoi.Right, 0);
        tlNew.X -= dx;
        brNew.X -= dx;
        tlNew.Y -= dy;
        brNew.Y -= dy;
        int top = tl.Y - tlNew.Y, left = tl.X - tlNew.X, bottom = brNew.Y - tl.Y - img.Rows, right = brNew.X - tl.X - img.Cols;

        // Laplace-Pyramide des Bildes (wie createLaplacePyr, 8U-Pfad)
        var srcLap = new Mat[_nb + 1];
        using (var withBorder = new Mat())
        {
            Cv2.CopyMakeBorder(img, withBorder, top, bottom, left, right, BorderTypes.Reflect);
            var current = withBorder;
            Mat? downNext = new Mat();
            Cv2.PyrDown(withBorder, downNext);
            for (var i = 1; i <= _nb; i++)
            {
                Mat? lvlDown = null;
                if (i < _nb)
                {
                    lvlDown = new Mat();
                    Cv2.PyrDown(downNext!, lvlDown);
                }

                using (var up = new Mat())
                {
                    Cv2.PyrUp(downNext!, up, current.Size());
                    srcLap[i - 1] = new Mat();
                    Cv2.Subtract(current, up, srcLap[i - 1], null, MatType.CV_16S);
                }

                if (!ReferenceEquals(current, withBorder))
                {
                    current.Dispose();
                }

                current = downNext!;
                downNext = lvlDown;
            }

            srcLap[_nb] = new Mat();
            current.ConvertTo(srcLap[_nb], MatType.CV_16S);
            if (!ReferenceEquals(current, withBorder))
            {
                current.Dispose();
            }
        }

        // Gewichts-Pyramide
        var wp = new Mat[_nb + 1];
        using (var wm = new Mat())
        {
            mask.ConvertTo(wm, MatType.CV_32F, 1.0 / 255.0);
            wp[0] = new Mat();
            Cv2.CopyMakeBorder(wm, wp[0], top, bottom, left, right, BorderTypes.Constant, Scalar.All(0));
        }

        for (var i = 0; i < _nb; i++)
        {
            wp[i + 1] = new Mat();
            Cv2.PyrDown(wp[i], wp[i + 1]);
        }

        int xTl = tlNew.X - _dstRoi.X, yTl = tlNew.Y - _dstRoi.Y, xBr = brNew.X - _dstRoi.X, yBr = brNew.Y - _dstRoi.Y;
        var options = new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count };
        for (var i = 0; i <= _nb; i++)
        {
            var rc = new Rect(xTl, yTl, xBr - xTl, yBr - yTl);
            var s = srcLap[i];
            var w = wp[i];
            var d = _lap[i]!;
            var dw = _wts[i]!;
            int rows = Math.Min(rc.Height, s.Rows), cols = Math.Min(rc.Width, s.Cols);
            byte* sp = (byte*)s.Data, wpp = (byte*)w.Data, dp = (byte*)d.Data, dwp = (byte*)dw.Data;
            long ss = s.Step(), ws = w.Step(), ds = d.Step(), dws = dw.Step();
            int rx = rc.X, ry = rc.Y;
            Parallel.For(0, rows, options, y =>
            {
                var srow = (short*)(sp + y * ss);
                var wrow = (float*)(wpp + y * ws);
                var drow = (short*)(dp + (y + ry) * ds) + rx * 3;
                var dwrow = (float*)(dwp + (y + ry) * dws) + rx;
                for (var x = 0; x < cols; x++)
                {
                    var ww = wrow[x];
                    if (ww == 0)
                    {
                        continue;
                    }

                    drow[x * 3] = Sat(drow[x * 3] + (int)(srow[x * 3] * ww));
                    drow[x * 3 + 1] = Sat(drow[x * 3 + 1] + (int)(srow[x * 3 + 1] * ww));
                    drow[x * 3 + 2] = Sat(drow[x * 3 + 2] + (int)(srow[x * 3 + 2] * ww));
                    dwrow[x] += ww;
                }
            });
            xTl /= 2;
            yTl /= 2;
            xBr /= 2;
            yBr /= 2;
        }

        foreach (var x in srcLap)
        {
            x.Dispose();
        }

        foreach (var x in wp)
        {
            x.Dispose();
        }
    }

    private static short Sat(int v) => (short)(v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : v);

    /// <summary>Normieren und Pyramide zurückbauen; liefert Bild (8UC3) und Maske der gedeckten Pixel.</summary>
    public unsafe (Mat Img, Mat Mask) Blend()
    {
        const float eps = 1e-5f;
        var options = new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count };
        for (var i = 0; i <= _nb; i++)
        {
            var d = _lap[i]!;
            var w = _wts[i]!;
            byte* dp = (byte*)d.Data, wp = (byte*)w.Data;
            long ds = d.Step(), ws = w.Step();
            var cols = d.Cols;
            Parallel.For(0, d.Rows, options, y =>
            {
                var drow = (short*)(dp + y * ds);
                var wrow = (float*)(wp + y * ws);
                for (var x = 0; x < cols; x++)
                {
                    var k = wrow[x] + eps;
                    drow[x * 3] = (short)(drow[x * 3] / k);
                    drow[x * 3 + 1] = (short)(drow[x * 3 + 1] / k);
                    drow[x * 3 + 2] = (short)(drow[x * 3 + 2] / k);
                }
            });
        }

        for (var i = _nb; i > 0; i--)
        {
            using (var up = new Mat())
            {
                Cv2.PyrUp(_lap[i]!, up, _lap[i - 1]!.Size());
                Cv2.Add(up, _lap[i - 1]!, _lap[i - 1]!);
            }

            _lap[i]!.Dispose();
            _lap[i] = null;
            _wts[i]!.Dispose();
            _wts[i] = null;
        }

        var res = new Mat();
        using (var view = new Mat(_lap[0]!, new Rect(0, 0, _dstRoiFinal.Width, _dstRoiFinal.Height)))
        {
            view.ConvertTo(res, MatType.CV_8U);
        }

        var mask = new Mat();
        using (var wv = new Mat(_wts[0]!, new Rect(0, 0, _dstRoiFinal.Width, _dstRoiFinal.Height)))
        using (var thr = new Mat())
        {
            Cv2.Threshold(wv, thr, eps, 255, ThresholdTypes.Binary);
            thr.ConvertTo(mask, MatType.CV_8U);
        }

        _lap[0]!.Dispose();
        _lap[0] = null;
        _wts[0]!.Dispose();
        _wts[0] = null;
        return (res, mask);
    }

    public void Dispose()
    {
        foreach (var x in _lap)
        {
            x?.Dispose();
        }

        foreach (var x in _wts)
        {
            x?.Dispose();
        }
    }
}
