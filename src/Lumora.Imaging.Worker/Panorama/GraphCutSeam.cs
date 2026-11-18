using OpenCvSharp;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>
/// Nahtsuche wie cv::detail::GraphCutSeamFinder (COST_COLOR, Port aus Spike 20 Runde 2): paarweise über alle überlappenden
/// Teile, Graph auf der Überlappung + 10 px Rand, Kantengewicht = |I1−I2|²(p) + |I1−I2|²(q) + 1, Strafe 1000 außerhalb
/// gemeinsamer Fläche, Endknoten-Gewicht 10000. Dazu die Schärfe als Datenterm (lokale Laplace-Energie, log; die Naht
/// bevorzugt das schärfere Bild). Paare laufen in Runden parallel (Paare einer Runde teilen kein Bild).
/// </summary>
internal static class GraphCutSeam
{
    private const double TerminalCost = 10000, BadRegionPenalty = 1000, WeightEps = 1;
    private const double SharpDeadZone = 0.15;

    public static void Find(IList<Mat> imgs, IList<Point> corners, IList<Mat> masks, IList<Mat?>? sharp, double sharpLambda, CancellationToken ct)
    {
        var all = new List<(int I, int J, Rect Roi)>();
        for (var i = 0; i < imgs.Count; i++)
        {
            for (var j = i + 1; j < imgs.Count; j++)
            {
                var roi = new Rect(corners[i], imgs[i].Size()) & new Rect(corners[j], imgs[j].Size());
                if (roi.Width > 0 && roi.Height > 0)
                {
                    all.Add((i, j, roi));
                }
            }
        }

        // Runden: gierige Kantenfärbung in OpenCV-Reihenfolge (gleiches Ergebnis wie nacheinander)
        var rounds = new List<List<(int I, int J, Rect Roi)>>();
        var busy = new List<HashSet<int>>();
        foreach (var p in all)
        {
            var r = 0;
            while (r < rounds.Count && (busy[r].Contains(p.I) || busy[r].Contains(p.J)))
            {
                r++;
            }

            if (r == rounds.Count)
            {
                rounds.Add([]);
                busy.Add([]);
            }

            rounds[r].Add(p);
            busy[r].Add(p.I);
            busy[r].Add(p.J);
        }

        var sh = sharp is not null && sharpLambda > 0 ? sharp : null;
        var options = new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count, CancellationToken = ct };
        foreach (var round in rounds)
        {
            ct.ThrowIfCancellationRequested();
            Parallel.For(0, round.Count, options, k =>
            {
                var (i, j, roi) = round[k];
                FindInPair(imgs[i], imgs[j], masks[i], masks[j], corners[i], corners[j], roi, sh?[i], sh?[j], sharpLambda);
            });
        }
    }

    private static void FindInPair(Mat img1, Mat img2, Mat mask1, Mat mask2, Point tl1, Point tl2, Rect roi, Mat? sharp1, Mat? sharp2, double sharpLambda)
    {
        const int gap = 10;
        int w = roi.Width + 2 * gap, h = roi.Height + 2 * gap;
        var s1 = new float[w * h * 3];
        var s2 = new float[w * h * 3];
        var m1 = new byte[w * h];
        var m2 = new byte[w * h];
        var e1 = sharp1 is not null ? new float[w * h] : null;
        var e2 = sharp2 is not null ? new float[w * h] : null;
        var anyBoth = false;
        for (var y = -gap; y < roi.Height + gap; y++)
        {
            for (var x = -gap; x < roi.Width + gap; x++)
            {
                var o = (y + gap) * w + (x + gap);
                int y1 = roi.Y - tl1.Y + y, x1 = roi.X - tl1.X + x;
                if (y1 >= 0 && x1 >= 0 && y1 < img1.Rows && x1 < img1.Cols)
                {
                    var v = img1.At<Vec3f>(y1, x1);
                    s1[o * 3] = v.Item0;
                    s1[o * 3 + 1] = v.Item1;
                    s1[o * 3 + 2] = v.Item2;
                    m1[o] = mask1.At<byte>(y1, x1);
                    if (e1 is not null)
                    {
                        e1[o] = sharp1!.At<float>(y1, x1);
                    }
                }

                int y2 = roi.Y - tl2.Y + y, x2 = roi.X - tl2.X + x;
                if (y2 >= 0 && x2 >= 0 && y2 < img2.Rows && x2 < img2.Cols)
                {
                    var v = img2.At<Vec3f>(y2, x2);
                    s2[o * 3] = v.Item0;
                    s2[o * 3 + 1] = v.Item1;
                    s2[o * 3 + 2] = v.Item2;
                    m2[o] = mask2.At<byte>(y2, x2);
                    if (e2 is not null)
                    {
                        e2[o] = sharp2!.At<float>(y2, x2);
                    }
                }

                if (m1[o] != 0 && m2[o] != 0)
                {
                    anyBoth = true;
                }
            }
        }

        if (!anyBoth)
        {
            return;
        }

        var g = new GCGraph(w * h, w * h * 4 + 2);
        float Diff(int o)
        {
            float a = s1[o * 3] - s2[o * 3], b = s1[o * 3 + 1] - s2[o * 3 + 1], c = s1[o * 3 + 2] - s2[o * 3 + 2];
            return a * a + b * b + c * c;
        }

        for (var o = 0; o < w * h; o++)
        {
            double sw = m1[o] != 0 ? TerminalCost : 0, tw = m2[o] != 0 ? TerminalCost : 0;
            if (m1[o] != 0 && m2[o] != 0 && e1 is not null && e2 is not null)
            {
                // Quelle = Bild 1: höheres Quellgewicht = teurer, das Pixel Bild 2 zu geben
                double de = e1[o] - e2[o];
                if (de > SharpDeadZone)
                {
                    sw += sharpLambda * (de - SharpDeadZone);
                }
                else if (de < -SharpDeadZone)
                {
                    tw += sharpLambda * (-de - SharpDeadZone);
                }
            }

            g.AddTermWeights(o, sw, tw);
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = y * w + x;
                if (x < w - 1)
                {
                    double wgt = Diff(o) + Diff(o + 1) + WeightEps;
                    if (m1[o] == 0 || m1[o + 1] == 0 || m2[o] == 0 || m2[o + 1] == 0)
                    {
                        wgt += BadRegionPenalty;
                    }

                    g.AddEdge(o, o + 1, wgt, wgt);
                }

                if (y < h - 1)
                {
                    double wgt = Diff(o) + Diff(o + w) + WeightEps;
                    if (m1[o] == 0 || m1[o + w] == 0 || m2[o] == 0 || m2[o + w] == 0)
                    {
                        wgt += BadRegionPenalty;
                    }

                    g.AddEdge(o, o + w, wgt, wgt);
                }
            }
        }

        g.MaxFlow();
        for (var y = 0; y < roi.Height; y++)
        {
            for (var x = 0; x < roi.Width; x++)
            {
                var o = (y + gap) * w + (x + gap);
                int y1 = roi.Y - tl1.Y + y, x1 = roi.X - tl1.X + x, y2 = roi.Y - tl2.Y + y, x2 = roi.X - tl2.X + x;
                if (g.InSourceSegment(o))
                {
                    if (mask1.At<byte>(y1, x1) != 0)
                    {
                        mask2.Set<byte>(y2, x2, 0);
                    }
                }
                else if (mask2.At<byte>(y2, x2) != 0)
                {
                    mask1.Set<byte>(y1, x1, 0);
                }
            }
        }
    }
}

/// <summary>
/// Max-Flow nach Boykov–Kolmogorov, Port von OpenCVs cv::detail::GCGraph (gcgraph.hpp) auf Felder statt Zeiger (Spike 20).
/// Kanten paarweise (e, e^1), Index 0/1 frei („keine Kante“), Eltern: 0 = frei, −1 = Endknoten, −2 = Waise.
/// </summary>
internal sealed class GCGraph
{
    private const int Terminal = -1, Orphan = -2;
    private readonly int _n;
    private readonly int[] _vFirst, _vNext, _vParent, _vDist, _vTs;
    private readonly byte[] _vT;
    private readonly double[] _vW;
    private int[] _eDst, _eNext;
    private double[] _eW;
    private int _m = 2;
    private double _flow;

    public GCGraph(int vertices, int edgeHint)
    {
        _n = vertices;
        _vFirst = new int[_n];
        _vNext = new int[_n + 1];
        _vParent = new int[_n];
        _vDist = new int[_n];
        _vTs = new int[_n];
        _vT = new byte[_n];
        _vW = new double[_n];
        _eDst = new int[edgeHint * 2 + 2];
        _eNext = new int[edgeHint * 2 + 2];
        _eW = new double[edgeHint * 2 + 2];
    }

    public void AddTermWeights(int i, double sourceW, double sinkW)
    {
        var dw = _vW[i];
        if (dw > 0)
        {
            sourceW += dw;
        }
        else
        {
            sinkW -= dw;
        }

        _flow += Math.Min(sourceW, sinkW);
        _vW[i] = sourceW - sinkW;
    }

    public void AddEdge(int i, int j, double w, double revw)
    {
        if (_m + 2 > _eDst.Length)
        {
            var nl = _eDst.Length * 2;
            Array.Resize(ref _eDst, nl);
            Array.Resize(ref _eNext, nl);
            Array.Resize(ref _eW, nl);
        }

        _eDst[_m] = j;
        _eNext[_m] = _vFirst[i];
        _eW[_m] = w;
        _vFirst[i] = _m++;
        _eDst[_m] = i;
        _eNext[_m] = _vFirst[j];
        _eW[_m] = revw;
        _vFirst[j] = _m++;
    }

    public bool InSourceSegment(int i) => _vT[i] == 0;

    public double MaxFlow()
    {
        var nil = _n;
        const int notInList = -1;
        Array.Fill(_vNext, notInList);
        int first, last = nil;
        _vNext[nil] = nil;
        var currTs = 0;
        for (var i = 0; i < _n; i++)
        {
            _vTs[i] = 0;
            if (_vW[i] != 0)
            {
                _vNext[last] = i;
                last = i;
                _vDist[i] = 1;
                _vParent[i] = Terminal;
                _vT[i] = (byte)(_vW[i] < 0 ? 1 : 0);
            }
            else
            {
                _vParent[i] = 0;
            }
        }

        first = _vNext[nil];
        _vNext[last] = nil;
        _vNext[nil] = notInList;
        var orphans = new Stack<int>();

        for (; ; )
        {
            int v, u, e0 = -1, ei = 0, ej;
            double minWeight, weight;
            byte vt;
            while (first != nil)
            {
                v = first;
                if (_vParent[v] != 0)
                {
                    vt = _vT[v];
                    for (ei = _vFirst[v]; ei != 0; ei = _eNext[ei])
                    {
                        if (_eW[ei ^ vt] == 0)
                        {
                            continue;
                        }

                        u = _eDst[ei];
                        if (_vParent[u] == 0)
                        {
                            _vT[u] = vt;
                            _vParent[u] = ei ^ 1;
                            _vTs[u] = _vTs[v];
                            _vDist[u] = _vDist[v] + 1;
                            if (_vNext[u] == notInList)
                            {
                                _vNext[u] = nil;
                                _vNext[last] = u;
                                last = u;
                            }

                            continue;
                        }

                        if (_vT[u] != vt)
                        {
                            e0 = ei ^ vt;
                            break;
                        }

                        if (_vDist[u] > _vDist[v] + 1 && _vTs[u] <= _vTs[v])
                        {
                            _vParent[u] = ei ^ 1;
                            _vTs[u] = _vTs[v];
                            _vDist[u] = _vDist[v] + 1;
                        }
                    }

                    if (e0 > 0)
                    {
                        break;
                    }
                }

                first = _vNext[first];
                _vNext[v] = notInList;
            }

            if (e0 <= 0)
            {
                break;
            }

            minWeight = _eW[e0];
            for (var k = 1; k >= 0; k--)
            {
                for (v = _eDst[e0 ^ k]; ; v = _eDst[ei])
                {
                    if ((ei = _vParent[v]) < 0)
                    {
                        break;
                    }

                    weight = _eW[ei ^ k];
                    minWeight = Math.Min(minWeight, weight);
                }

                weight = Math.Abs(_vW[v]);
                minWeight = Math.Min(minWeight, weight);
            }

            _eW[e0] -= minWeight;
            _eW[e0 ^ 1] += minWeight;
            _flow += minWeight;
            for (var k = 1; k >= 0; k--)
            {
                for (v = _eDst[e0 ^ k]; ; v = _eDst[ei])
                {
                    if ((ei = _vParent[v]) < 0)
                    {
                        break;
                    }

                    _eW[ei ^ (k ^ 1)] += minWeight;
                    if ((_eW[ei ^ k] -= minWeight) <= 1e-12)
                    {
                        _eW[ei ^ k] = 0;
                        orphans.Push(v);
                        _vParent[v] = Orphan;
                    }
                }

                _vW[v] = _vW[v] + minWeight * (1 - k * 2);
                if (Math.Abs(_vW[v]) <= 1e-12)
                {
                    _vW[v] = 0;
                    orphans.Push(v);
                    _vParent[v] = Orphan;
                }
            }

            currTs++;
            while (orphans.Count > 0)
            {
                var v2 = orphans.Pop();
                int d, minDist = int.MaxValue;
                e0 = 0;
                vt = _vT[v2];
                for (ei = _vFirst[v2]; ei != 0; ei = _eNext[ei])
                {
                    if (_eW[ei ^ (vt ^ 1)] == 0)
                    {
                        continue;
                    }

                    u = _eDst[ei];
                    if (_vT[u] != vt || _vParent[u] == 0)
                    {
                        continue;
                    }

                    for (d = 0; ;)
                    {
                        if (_vTs[u] == currTs)
                        {
                            d += _vDist[u];
                            break;
                        }

                        ej = _vParent[u];
                        d++;
                        if (ej < 0)
                        {
                            if (ej == Orphan)
                            {
                                d = int.MaxValue - 1;
                            }
                            else
                            {
                                _vTs[u] = currTs;
                                _vDist[u] = 1;
                            }

                            break;
                        }

                        u = _eDst[ej];
                    }

                    if (++d < int.MaxValue)
                    {
                        if (d < minDist)
                        {
                            minDist = d;
                            e0 = ei;
                        }

                        for (u = _eDst[ei]; _vTs[u] != currTs; u = _eDst[_vParent[u]])
                        {
                            _vTs[u] = currTs;
                            _vDist[u] = --d;
                        }
                    }
                }

                if ((_vParent[v2] = e0) > 0)
                {
                    _vTs[v2] = currTs;
                    _vDist[v2] = minDist;
                    continue;
                }

                _vTs[v2] = 0;
                for (ei = _vFirst[v2]; ei != 0; ei = _eNext[ei])
                {
                    u = _eDst[ei];
                    ej = _vParent[u];
                    if (_vT[u] != vt || ej == 0)
                    {
                        continue;
                    }

                    if (_eW[ei ^ (vt ^ 1)] != 0 && _vNext[u] == notInList)
                    {
                        _vNext[u] = nil;
                        _vNext[last] = u;
                        last = u;
                    }

                    if (ej > 0 && _eDst[ej] == v2)
                    {
                        orphans.Push(u);
                        _vParent[u] = Orphan;
                    }
                }
            }
        }

        return _flow;
    }
}
