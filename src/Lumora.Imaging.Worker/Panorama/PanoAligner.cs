using OpenCvSharp;
using OpenCvSharp.Detail;
using OpenCvSharp.Features2D;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Worker.Decoding;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>Ergebnis der Ausrichtung (Phase 1).</summary>
internal sealed class AlignmentOutcome
{
    /// <summary>Kameras der eingebundenen Quellen; <see cref="PanoCamera.Source"/> = Stelle in der Quellenliste der Sitzung.</summary>
    public required List<PanoCamera> Cameras { get; init; }

    /// <summary>Nicht eingebundene Quellen (Stelle in der Quellenliste).</summary>
    public required int[] Dropped { get; init; }

    public bool Is360 { get; init; }

    public bool UsedGimbal { get; init; }

    public double ResidualMedianPx { get; init; }

    public double ResidualP90Px { get; init; }

    public Photometric? Photometric { get; init; }

    /// <summary>„Nur verschieben“: Ähnlichkeit je Kamera (gleiche Reihenfolge wie <see cref="Cameras"/>), null ohne.</summary>
    public List<(double A, double B, double Tx, double Ty)>? Similarity { get; init; }

    public double SimilarityResidualPx { get; init; } = double.NaN;

    /// <summary>Probleme der Ausrichtung (Codes wie <see cref="QualityCheck.Problems"/>).</summary>
    public List<string> Problems { get; init; } = [];

    public List<string> Notes { get; init; } = [];

    public int Seed { get; init; }
}

/// <summary>
/// Phase 1 der eigenen Kette (Port aus Spike 20 Runde 2, nach dem Muster von OpenCV stitching_detailed): SIFT-Merkmale,
/// Paarabgleich mit BestOf2NearestMatcher (je Paar fester RANSAC-Startwert), größte zusammenhängende Gruppe, Startwerte aus
/// Gimbal bzw. Spannbaum der Homographien, Bündelausgleich (Strahlfehler, Levenberg-Marquardt, Huber, k1/k2, Gimbal als
/// weiche Vorgabe), Ausreißer je Treffer und je Paar, Wellenkorrektur bzw. Ausrichtung am Gimbal, 360°-Erkennung,
/// photometrisches Modell und für „Nur verschieben“ eine Ähnlichkeits-Abbildung.
/// </summary>
internal sealed class PanoAligner
{
    private const double ConfThresh = 1.0;
    private const int MaxFeatures = 6000;
    private const double PairMaxAngleDeg = 75;
    private const int Range = 4;
    private const double MatchOutlierFactor = 3;

    private readonly IReadOnlyList<SourceImage> _sources;
    private readonly double _workMp;
    private readonly int _seed;
    private readonly Action<WorkerStep, int, int> _progress;
    private readonly CancellationToken _ct;
    private readonly HashSet<long> _weakPairs = [], _rejectedPairs = [], _badPairs = [], _removedMatches = [], _matchOutliers = [];
    private List<PanoCamera> _cams = [];
    private int[] _group = [];
    private double _pairRejectMin = 15, _pairRejectFactor = 6;

    /// <summary>
    /// Restverzeichnung k1/k2 schätzen — nur mit Brennweite aus dem EXIF. Ohne sie tauschen Brennweite und Verzeichnung
    /// gegeneinander (ein waagrechter Schwenk legt beide nicht fest) und die Bilder „blähen“ sich auf.
    /// </summary>
    private bool _estimateDistortion = true;

    private PanoAligner(IReadOnlyList<SourceImage> sources, double workMp, int seed, Action<WorkerStep, int, int> progress, CancellationToken ct)
    {
        _sources = sources;
        _workMp = workMp;
        _seed = seed;
        _progress = progress;
        _ct = ct;
    }

    private static double Conf(MatchesInfo m) => m.Matches.Count == 0 ? 0 : m.NumInliers / (8 + 0.3 * m.Matches.Count);

    public static AlignmentOutcome Align(IReadOnlyList<SourceImage> sources, double workMp, int seed, Action<WorkerStep, int, int> progress, CancellationToken ct)
        => new PanoAligner(sources, workMp, seed, progress, ct).Run();

    private AlignmentOutcome Run()
    {
        var n = _sources.Count;
        if (n < 2)
        {
            throw new WorkerException(WorkerErrorCode.NotEnoughOverlap, "Weniger als zwei Quellen.");
        }

        // 1 Arbeitsbilder: ein gemeinsamer Maßstab gegenüber voller Auflösung (größtes Bild ≈ WorkMP)
        var maxArea = _sources.Max(s => (double)s.FullSize.Width * s.FullSize.Height);
        var workScale = Math.Min(1.0, Math.Sqrt(_workMp * 1e6 / maxArea));
        workScale = Math.Min(workScale, _sources.Min(s => s.Scale));
        var works = new Mat[n];
        var options = new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count, CancellationToken = _ct };
        Parallel.For(0, n, options, i =>
        {
            var f = workScale / _sources[i].Scale;
            var r = new Mat();
            if (Math.Abs(f - 1) < 1e-6)
            {
                _sources[i].Image.CopyTo(r);
            }
            else
            {
                Cv2.Resize(_sources[i].Image, r, new Size(Math.Max(1, (int)Math.Round(_sources[i].FullSize.Width * workScale)), Math.Max(1, (int)Math.Round(_sources[i].FullSize.Height * workScale))), 0, 0, InterpolationFlags.Area);
            }

            works[i] = r;
        });

        var feats = new ImageFeatures[n];
        var pairs = new MatchesInfo?[n * n];
        try
        {
            // 2 Merkmale
            var done = 0;
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, WorkerThreads.Count), CancellationToken = _ct }, i =>
            {
                feats[i] = Detect(works[i], i, low: false);
                _progress(WorkerStep.Features, Interlocked.Increment(ref done), n);
            });
            for (var i = 0; i < n; i++)
            {
                if (feats[i].Keypoints.Count >= 1000)
                {
                    continue;
                }

                // Sonne, Himmel, Dunst: CLAHE + niedrigere Kontrastschwelle (0,01 statt 0,04)
                var more = Detect(works[i], i, low: true);
                if (more.Keypoints.Count > feats[i].Keypoints.Count)
                {
                    feats[i].Dispose();
                    feats[i] = more;
                }
                else
                {
                    more.Dispose();
                }
            }

            _ct.ThrowIfCancellationRequested();

            // 3 Paare
            var gimbal = _sources.All(s => s.Meta.HasGimbal)
                ? _sources.Select(s => GimbalR(s.Meta.GimbalYaw!.Value, s.Meta.GimbalPitch!.Value)).ToArray()
                : null;
            var todo = new List<(int I, int J)>();
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    var ok = gimbal is not null
                        ? Math.Acos(Math.Clamp(gimbal[i].Col(2).Dot(gimbal[j].Col(2)), -1, 1)) * 180 / Math.PI < PairMaxAngleDeg
                        : n <= 30 || Math.Abs(i - j) <= Range || n - Math.Abs(i - j) <= Range;
                    if (ok)
                    {
                        todo.Add((i, j));
                    }
                }
            }

            var matched = 0;
            Parallel.ForEach(
                todo,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(6, WorkerThreads.Count), CancellationToken = _ct },
                () => new BestOf2NearestMatcher(false, 0.3f, 6, 6),
                (pr, _, matcher) =>
                {
                    // fester RANSAC-Startwert je Paar (theRNG ist je Thread): gleiche Eingabe, gleiches Ergebnis
                    Cv2.SetTheRNG(unchecked((ulong)(uint)_seed * 7919UL + (ulong)pr.I * 1_000_003UL + (ulong)pr.J + 1UL));
                    pairs[pr.I * n + pr.J] = matcher.Apply(feats[pr.I], feats[pr.J]);
                    _progress(WorkerStep.Match, Interlocked.Increment(ref matched), todo.Count);
                    return matcher;
                },
                matcher => matcher.Dispose());
            MatchesInfo P(int i, int j) => pairs[Math.Min(i, j) * n + Math.Max(i, j)] ?? Empty;

            // 4 Größte Komponente
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x)
            {
                while (parent[x] != x)
                {
                    x = parent[x] = parent[parent[x]];
                }

                return x;
            }

            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    if (Conf(P(i, j)) > ConfThresh)
                    {
                        parent[Find(i)] = Find(j);
                    }
                }
            }

            var comp = Enumerable.Range(0, n).GroupBy(Find).OrderByDescending(g => g.Count()).ThenBy(g => g.Min()).First().OrderBy(x => x).ToArray();
            var dropped = Enumerable.Range(0, n).Except(comp).ToArray();
            var notes = new List<string>();
            if (gimbal is not null && dropped.Length > 0)
            {
                var rescued = new List<int>();
                foreach (var d in dropped)
                {
                    var links = comp.Where(j =>
                    {
                        var m = P(d, j);
                        return Conf(m) > 0.5 && m.NumInliers >= 10;
                    }).ToList();
                    rescued.Add(d); // mit schwachen Paaren oder allein über die Gimbal-Vorgabe (σ 1°/2°) statt eines Lochs
                    foreach (var j in links)
                    {
                        _weakPairs.Add(((long)Math.Min(d, j) << 32) | (uint)Math.Max(d, j));
                    }
                }

                comp = comp.Concat(rescued).OrderBy(x => x).ToArray();
                dropped = dropped.Except(rescued).ToArray();
                notes.Add("gimbal-rescued:" + rescued.Count);
            }

            if (comp.Length < 2)
            {
                throw new WorkerException(WorkerErrorCode.NotEnoughOverlap, "Keine überlappenden Quellen gefunden.");
            }

            // 5 Startwerte: Brennweite aus EXIF (KB über die Diagonale) bzw. aus den Homographien, Gruppen je Bildgröße
            var sizes = comp.Select(i => _sources[i].FullSize).Distinct().ToList();
            _group = comp.Select(i => sizes.IndexOf(_sources[i].FullSize)).ToArray();
            var fEst = EstimateFocal(pairs, n, comp) / workScale;
            _cams = comp.Select(i =>
            {
                var s = _sources[i];
                var diag = Math.Sqrt((double)s.FullSize.Width * s.FullSize.Width + (double)s.FullSize.Height * s.FullSize.Height);
                double f;
                if (s.Meta.Focal35 is { } f35)
                {
                    f = f35 / 43.2666 * diag;
                    if (double.IsFinite(fEst) && Math.Abs(fEst / f - 1) < 0.25)
                    {
                        f = fEst; // Handy-EXIF ist oft gerundet; die Homographien sind genauer, solange sie passen
                    }
                }
                else
                {
                    f = double.IsFinite(fEst) && fEst > 0.2 * diag && fEst < 20 * diag ? fEst : Math.Max(s.FullSize.Width, s.FullSize.Height);
                }

                return new PanoCamera { Source = i, Size = s.FullSize, F = f, R = M3.I };
            }).ToList();
            if (_sources.All(s => s.Meta.Focal35 is null))
            {
                notes.Add("focal-estimated");
            }

            _estimateDistortion = _sources.All(s => s.Meta.Focal35 is not null);

            // gemeinsame Brennweite je Gruppe (Median)
            for (var g = 0; g < sizes.Count; g++)
            {
                var fs = _cams.Where((_, k) => _group[k] == g).Select(c => c.F).OrderBy(v => v).ToList();
                var fm = fs[fs.Count / 2];
                for (var k = 0; k < _cams.Count; k++)
                {
                    if (_group[k] == g)
                    {
                        _cams[k].F = fm;
                    }
                }
            }

            var useGimbal = gimbal is not null;
            if (useGimbal)
            {
                for (var k = 0; k < _cams.Count; k++)
                {
                    _cams[k].R = gimbal![_cams[k].Source];
                }

                _pairRejectMin = 8;
                _pairRejectFactor = 4;
                notes.Add("gimbal");
            }
            else
            {
                SpanningTreeInit(pairs, n, comp, workScale);
            }

            _ct.ThrowIfCancellationRequested();

            // 6 Bündelausgleich (Strahl + Verzeichnung + Gimbal-Vorgabe), Ausreißer je Treffer und je Paar
            _progress(WorkerStep.Bundle, 0, 3);
            var priorG = useGimbal ? gimbal : null;
            var ba = BundleAdjust(pairs, feats, n, comp, workScale, priorG, collectMatchOutliers: true);
            if (_matchOutliers.Count > 0)
            {
                foreach (var k in _matchOutliers)
                {
                    _removedMatches.Add(k);
                }

                _matchOutliers.Clear();
                ba = BundleAdjust(pairs, feats, n, comp, workScale, priorG, false);
            }

            _progress(WorkerStep.Bundle, 1, 3);
            for (var round = 0; round < 3 && _badPairs.Count > 0; round++)
            {
                foreach (var k in _badPairs)
                {
                    _rejectedPairs.Add(k);
                }

                _badPairs.Clear();
                ba = BundleAdjust(pairs, feats, n, comp, workScale, priorG, false);
            }

            // Verzeichnung unplausibel groß (Brennweite und Verzeichnung haben getauscht): ohne Verzeichnung neu ausgleichen
            if (_estimateDistortion && (Math.Abs(_cams[0].K1) > 0.3 || Math.Abs(_cams[0].K2) > 0.3))
            {
                _estimateDistortion = false;
                foreach (var c in _cams)
                {
                    c.K1 = 0;
                    c.K2 = 0;
                }

                ba = BundleAdjust(pairs, feats, n, comp, workScale, priorG, false);
                notes.Add("distortion-reset");
            }

            _progress(WorkerStep.Bundle, 2, 3);

            // 7 Ausrichtung: am Gimbal (Nordrichtung, Horizont) bzw. Wellenkorrektur, 360° erkennen, Mitte drehen
            if (gimbal is not null)
            {
                var s = M3.Zero;
                foreach (var c in _cams)
                {
                    s += gimbal[c.Source] * c.R.T;
                }

                var g = s.Orthonormalized();
                foreach (var c in _cams)
                {
                    c.R = g * c.R;
                }
            }
            else
            {
                WaveCorrect();
            }

            var is360 = CenterOrDetect360();
            var problems = new List<string>();
            var diagFull = Math.Sqrt(_cams.Average(c => (double)c.Size.Width * c.Size.Width + (double)c.Size.Height * c.Size.Height));
            if (ba.MedianFull > 0.0035 * diagFull || ba.P90Full > 0.012 * diagFull)
            {
                problems.Add("residual");
            }

            if (CountUnmatchedOverlaps(pairs, n, comp, feats) is var (bad, expected) && bad >= 2 && bad > 0.25 * expected)
            {
                problems.Add("tilted");
            }

            // 7b Photometrie (nur angewandt, wenn das Modell die Überlappungen erklärt)
            var camWorks = _cams.Select(c => works[c.Source]).ToList();
            Photometric? photo = null;
            try
            {
                photo = Photometric.Fit(_cams, camWorks, workScale, _ct);
            }
            catch (Exception ex) when (ex is OpenCVException or InvalidOperationException)
            {
                notes.Add("photometric-failed");
            }

            // 7c Ähnlichkeit für „Nur verschieben“ (Scans) aus denselben Treffern
            var (sim, simRes) = FitSimilarity(pairs, feats, n, comp, workScale);
            _progress(WorkerStep.Bundle, 3, 3);
            return new AlignmentOutcome
            {
                Cameras = _cams,
                Dropped = dropped,
                Is360 = is360,
                UsedGimbal = useGimbal,
                ResidualMedianPx = ba.MedianFull,
                ResidualP90Px = ba.P90Full,
                Photometric = photo,
                Similarity = sim,
                SimilarityResidualPx = simRes,
                Problems = problems,
                Notes = notes,
                Seed = _seed,
            };
        }
        finally
        {
            foreach (var w in works)
            {
                w?.Dispose();
            }

            foreach (var p in pairs)
            {
                if (p is not null && !ReferenceEquals(p, Empty))
                {
                    p.Dispose();
                }
            }

            foreach (var f in feats)
            {
                f?.Dispose();
            }
        }
    }

    private static readonly MatchesInfo Empty = new(-1, -1, Array.Empty<DMatch>(), Array.Empty<byte>(), 0, new Mat(), 0);

    private static ImageFeatures Detect(Mat work, int index, bool low)
    {
        using var g = new Mat();
        Cv2.CvtColor(work, g, ColorConversionCodes.BGR2GRAY);
        Mat input = g;
        using var enhanced = new Mat();
        if (low)
        {
            using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
            clahe.Apply(g, enhanced);
            input = enhanced;
        }

        using var sift = low ? SIFT.Create(MaxFeatures, 3, 0.01) : SIFT.Create(MaxFeatures);
        var desc = new Mat();
        sift.DetectAndCompute(input, null, out KeyPoint[] kps, desc);
        return new ImageFeatures(index, work.Size(), kps, desc);
    }

    /// <summary>DJI: Gierwinkel im Uhrzeigersinn ab Nord, Nickwinkel positiv nach oben, Rollen 0.</summary>
    internal static M3 GimbalR(double yawDeg, double pitchDeg)
    {
        double y = yawDeg * Math.PI / 180, p = pitchDeg * Math.PI / 180;
        var z = new V3(Math.Sin(y) * Math.Cos(p), -Math.Sin(p), Math.Cos(y) * Math.Cos(p));
        var x = new V3(Math.Cos(y), 0, -Math.Sin(y));
        var yy = z.Cross(x);
        return M3.FromCols(x, yy, z);
    }

    private static double EstimateFocal(MatchesInfo?[] pairs, int n, int[] comp)
    {
        var est = new List<double>();
        foreach (var i in comp)
        {
            foreach (var j in comp)
            {
                if (i >= j)
                {
                    continue;
                }

                var m = pairs[i * n + j];
                if (m is null || Conf(m) <= ConfThresh || m.H.Empty())
                {
                    continue;
                }

                using var hinv = m.H.Inv();
                using var hm = hinv.ToMat();
                hm.GetArray(out double[] h);
                if (FocalsFromHomography(h, out var fa, out var fb))
                {
                    est.Add(Math.Sqrt(fa * fb));
                }
            }
        }

        if (est.Count == 0)
        {
            return double.NaN;
        }

        est.Sort();
        return est[est.Count / 2];
    }

    private static bool FocalsFromHomography(double[] h, out double f0, out double f1)
    {
        f0 = f1 = 0;
        bool ok1 = true, ok0 = true;
        double d1 = h[6] * h[7], d2 = (h[7] - h[6]) * (h[7] + h[6]);
        double v1 = -(h[0] * h[1] + h[3] * h[4]) / d1, v2 = (h[0] * h[0] + h[3] * h[3] - h[1] * h[1] - h[4] * h[4]) / d2;
        if (v1 < v2)
        {
            (v1, v2) = (v2, v1);
        }

        if (v1 > 0 && v2 > 0)
        {
            f1 = Math.Sqrt(Math.Abs(d1) > Math.Abs(d2) ? v1 : v2);
        }
        else if (v1 > 0)
        {
            f1 = Math.Sqrt(v1);
        }
        else
        {
            ok1 = false;
        }

        d1 = h[0] * h[3] + h[1] * h[4];
        d2 = h[0] * h[0] + h[1] * h[1] - h[3] * h[3] - h[4] * h[4];
        v1 = -h[2] * h[5] / d1;
        v2 = (h[5] * h[5] - h[2] * h[2]) / d2;
        if (v1 < v2)
        {
            (v1, v2) = (v2, v1);
        }

        if (v1 > 0 && v2 > 0)
        {
            f0 = Math.Sqrt(Math.Abs(d1) > Math.Abs(d2) ? v1 : v2);
        }
        else if (v1 > 0)
        {
            f0 = Math.Sqrt(v1);
        }
        else
        {
            ok0 = false;
        }

        return ok0 && ok1 && double.IsFinite(f0) && double.IsFinite(f1);
    }

    private void SpanningTreeInit(MatchesInfo?[] pairs, int n, int[] comp, double workScale)
    {
        var idx = comp.Select((src, k) => (src, k)).ToDictionary(t => t.src, t => t.k);
        var edges = new List<(double C, int A, int B)>();
        foreach (var i in comp)
        {
            foreach (var j in comp)
            {
                if (i < j && pairs[i * n + j] is { } m && Conf(m) > ConfThresh && !_rejectedPairs.Contains(((long)i << 32) | (uint)j))
                {
                    edges.Add((Conf(m), idx[i], idx[j]));
                }
            }
        }

        edges.Sort((x, y) => y.C != x.C ? y.C.CompareTo(x.C) : x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        var nc = comp.Length;
        var par = Enumerable.Range(0, nc).ToArray();
        int F(int x)
        {
            while (par[x] != x)
            {
                x = par[x] = par[par[x]];
            }

            return x;
        }

        var adj = Enumerable.Range(0, nc).Select(_ => new List<int>()).ToArray();
        foreach (var (_, a, b) in edges)
        {
            if (F(a) != F(b))
            {
                par[F(a)] = F(b);
                adj[a].Add(b);
                adj[b].Add(a);
            }
        }

        var root = nc / 2;
        var seen = new bool[nc];
        seen[root] = true;
        _cams[root].R = M3.I;
        var q = new Queue<int>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var a = q.Dequeue();
            foreach (var b in adj[a])
            {
                if (seen[b])
                {
                    continue;
                }

                seen[b] = true;
                double fa = _cams[a].F * workScale, fb = _cams[b].F * workScale;
                M3 hinv;
                if (comp[a] < comp[b])
                {
                    using var hinvMat = pairs[comp[a] * n + comp[b]]!.H.Inv();
                    using var hi = hinvMat.ToMat();
                    hi.GetArray(out double[] hiA);
                    hinv = M3.From(hiA);
                }
                else
                {
                    pairs[comp[b] * n + comp[a]]!.H.GetArray(out double[] hA);
                    hinv = M3.From(hA);
                }

                var kainv = new M3(1 / fa, 0, 0, 0, 1 / fa, 0, 0, 0, 1);
                var kb = new M3(fb, 0, 0, 0, fb, 0, 0, 0, 1);
                var rrel = (kainv * hinv * kb).Orthonormalized();
                _cams[b].R = (_cams[a].R * rrel).Orthonormalized();
                q.Enqueue(b);
            }
        }
    }

    private readonly record struct Obs(int A, int B, double X1, double Y1, double X2, double Y2, long Key);

    /// <summary>
    /// Bündelausgleich „Ray“ (wie BundleAdjusterRay, Spike 20 Runde 2): Abstand der Sehstrahlen beider Punkte × Brennweite.
    /// Parameter: Rodrigues-Vektor je Kamera, Brennweite je Bildgröße, gemeinsame Restverzeichnung k1/k2; mit Gimbal-Vorgabe
    /// zusätzlich ein Gier-Versatz ψ und weiche Terme (σ 1° Nick/Roll, 2° Gier) je Kamera statt einer festgehaltenen
    /// Referenzkamera. Levenberg-Marquardt, Huber, numerische Ableitungen.
    /// </summary>
    private (double MedianFull, double P90Full) BundleAdjust(MatchesInfo?[] pairs, ImageFeatures[] feats, int n, int[] comp, double workScale, M3[]? gimbal, bool collectMatchOutliers)
    {
        var idx = comp.Select((src, k) => (src, k)).ToDictionary(t => t.src, t => t.k);
        var obs = new List<Obs>();
        foreach (var i in comp)
        {
            foreach (var j in comp)
            {
                if (i >= j || pairs[i * n + j] is not { } m)
                {
                    continue;
                }

                var key = ((long)i << 32) | (uint)j;
                if (_rejectedPairs.Contains(key) || (Conf(m) <= ConfThresh && !_weakPairs.Contains(key)))
                {
                    continue;
                }

                var inl = new List<int>();
                for (var k = 0; k < m.Matches.Count; k++)
                {
                    if (m.InliersMask[k] != 0)
                    {
                        inl.Add(k);
                    }
                }

                var stride = Math.Max(1, inl.Count / 300);
                var s1 = feats[i].ImgSize;
                var s2 = feats[j].ImgSize;
                for (var t = 0; t < inl.Count; t += stride)
                {
                    var mk = key * 8192 + inl[t];
                    if (_removedMatches.Contains(mk))
                    {
                        continue;
                    }

                    var dm = m.Matches[inl[t]];
                    var p1 = feats[i].Keypoints[dm.QueryIdx].Pt;
                    var p2 = feats[j].Keypoints[dm.TrainIdx].Pt;
                    obs.Add(new Obs(idx[i], idx[j], p1.X - (s1.Width - 1) / 2.0, p1.Y - (s1.Height - 1) / 2.0, p2.X - (s2.Width - 1) / 2.0, p2.Y - (s2.Height - 1) / 2.0, mk));
                }
            }
        }

        if (obs.Count == 0)
        {
            return (double.NaN, double.NaN);
        }

        var nc = comp.Length;
        var ng = _group.Max() + 1;
        var prior = gimbal is not null;
        int iF = 3 * nc, iK = iF + ng, iPsi = iK + 2, p = iPsi + (prior ? 1 : 0);
        var x = new double[p];
        for (var k = 0; k < nc; k++)
        {
            var w = _cams[k].R.ToRodrigues();
            x[3 * k] = w.X;
            x[3 * k + 1] = w.Y;
            x[3 * k + 2] = w.Z;
        }

        for (var g = 0; g < ng; g++)
        {
            x[iF + g] = _cams[Array.IndexOf(_group, g)].F * workScale;
        }

        x[iK] = _cams[0].K1;
        x[iK + 1] = _cams[0].K2;
        var refCam = nc / 2;
        const double huberK = 2.0; // Pixel (Arbeitsmaßstab)
        const double priorW = 4.47; // ≈ √20: eine σ-Abweichung kostet wie 20 Treffer mit 1 px
        var kSigma = _estimateDistortion ? 0.2 : 1e-4; // ohne Schätzung bleibt k1/k2 praktisch 0
        double sRp = 1.0 * Math.PI / 180, sY = 2.0 * Math.PI / 180;
        var gk = prior ? comp.Select(src => gimbal![src]).ToArray() : null;
        var group = _group;

        V3 Ray(M3 r, int k, double px, double py, double[] pv)
        {
            double f = pv[iF + group[k]], k1 = pv[iK], k2 = pv[iK + 1];
            double xx = px / f, yy = py / f;
            PanoCamera.Undistort(ref xx, ref yy, k1, k2);
            return (r * new V3(xx, yy, 1)).Normalized();
        }

        M3 Rot(double[] pv, int k) => M3.Rodrigues(new V3(pv[3 * k], pv[3 * k + 1], pv[3 * k + 2]));
        double FScale(double[] pv, Obs o) => 0.5 * (pv[iF + group[o.A]] + pv[iF + group[o.B]]);
        V3 ObsRes(double[] pv, M3 ra, M3 rb, Obs o) => (Ray(ra, o.A, o.X1, o.Y1, pv) - Ray(rb, o.B, o.X2, o.Y2, pv)) * FScale(pv, o);
        V3 PriorRes(double[] pv, M3 rk, int k)
        {
            var g = M3.RotY(pv[iPsi]) * gk![k];
            var d = (rk * g.T).ToRodrigues();
            return new V3(d.X / sRp * priorW, d.Y / sY * priorW, d.Z / sRp * priorW);
        }

        double Cost(double[] pv)
        {
            var r = Enumerable.Range(0, nc).Select(k => Rot(pv, k)).ToArray();
            double c = 0;
            foreach (var o in obs)
            {
                var e = ObsRes(pv, r[o.A], r[o.B], o).Norm;
                c += e <= huberK ? e * e : 2 * huberK * e - huberK * huberK;
            }

            if (prior)
            {
                for (var k = 0; k < nc; k++)
                {
                    var pr = PriorRes(pv, r[k], k);
                    c += pr.Dot(pr);
                }
            }

            c += Math.Pow(pv[iK] / kSigma * priorW, 2) + Math.Pow(pv[iK + 1] / kSigma * priorW, 2);
            return c;
        }

        var lambda = 1e-3;
        var cost = Cost(x);
        const double h = 1e-6;
        var shared = new List<int>();
        for (var g = 0; g < ng; g++)
        {
            shared.Add(iF + g);
        }

        shared.Add(iK);
        shared.Add(iK + 1);
        var ns = shared.Count;
        for (var it = 0; it < 200; it++)
        {
            _ct.ThrowIfCancellationRequested();
            var r = Enumerable.Range(0, nc).Select(k => Rot(x, k)).ToArray();
            var rp = new M3[nc, 3];
            for (var k = 0; k < nc; k++)
            {
                for (var d = 0; d < 3; d++)
                {
                    var w = new V3(x[3 * k] + (d == 0 ? h : 0), x[3 * k + 1] + (d == 1 ? h : 0), x[3 * k + 2] + (d == 2 ? h : 0));
                    rp[k, d] = M3.Rodrigues(w);
                }
            }

            var jtj = new double[p, p];
            var jtr = new double[p];
            var nj = 6 + ns;
            var jac = new double[nj][];
            for (var q = 0; q < nj; q++)
            {
                jac[q] = new double[3];
            }

            var gi = new int[nj];
            var xs = new double[ns][];
            for (var q = 0; q < ns; q++)
            {
                xs[q] = (double[])x.Clone();
                var hh = Math.Max(1e-7, Math.Abs(x[shared[q]]) * 1e-6);
                xs[q][shared[q]] += hh;
            }

            foreach (var o in obs)
            {
                var fsc = FScale(x, o);
                var ra = Ray(r[o.A], o.A, o.X1, o.Y1, x);
                var rb = Ray(r[o.B], o.B, o.X2, o.Y2, x);
                var r0 = (ra - rb) * fsc;
                double e = r0.Norm, wgt = e <= huberK ? 1 : huberK / e;
                for (var d = 0; d < 3; d++)
                {
                    var r1 = (Ray(rp[o.A, d], o.A, o.X1, o.Y1, x) - rb) * fsc;
                    var r2 = (ra - Ray(rp[o.B, d], o.B, o.X2, o.Y2, x)) * fsc;
                    jac[d][0] = (r1.X - r0.X) / h;
                    jac[d][1] = (r1.Y - r0.Y) / h;
                    jac[d][2] = (r1.Z - r0.Z) / h;
                    jac[3 + d][0] = (r2.X - r0.X) / h;
                    jac[3 + d][1] = (r2.Y - r0.Y) / h;
                    jac[3 + d][2] = (r2.Z - r0.Z) / h;
                    gi[d] = 3 * o.A + d;
                    gi[3 + d] = 3 * o.B + d;
                }

                for (var q = 0; q < ns; q++)
                {
                    var hh = xs[q][shared[q]] - x[shared[q]];
                    var rq = ObsRes(xs[q], r[o.A], r[o.B], o);
                    jac[6 + q][0] = (rq.X - r0.X) / hh;
                    jac[6 + q][1] = (rq.Y - r0.Y) / hh;
                    jac[6 + q][2] = (rq.Z - r0.Z) / hh;
                    gi[6 + q] = shared[q];
                }

                for (var a = 0; a < nj; a++)
                {
                    jtr[gi[a]] += wgt * (jac[a][0] * r0.X + jac[a][1] * r0.Y + jac[a][2] * r0.Z);
                    for (var b = 0; b < nj; b++)
                    {
                        jtj[gi[a], gi[b]] += wgt * (jac[a][0] * jac[b][0] + jac[a][1] * jac[b][1] + jac[a][2] * jac[b][2]);
                    }
                }
            }

            if (prior)
            {
                var xp = (double[])x.Clone();
                xp[iPsi] += h;
                for (var k = 0; k < nc; k++)
                {
                    var r0 = PriorRes(x, r[k], k);
                    var jp = new V3[4];
                    var gp = new int[4];
                    for (var d = 0; d < 3; d++)
                    {
                        var r1 = PriorRes(x, rp[k, d], k);
                        jp[d] = (r1 - r0) * (1 / h);
                        gp[d] = 3 * k + d;
                    }

                    {
                        var r1 = PriorRes(xp, r[k], k);
                        jp[3] = (r1 - r0) * (1 / h);
                        gp[3] = iPsi;
                    }

                    for (var a = 0; a < 4; a++)
                    {
                        jtr[gp[a]] += jp[a].Dot(r0);
                        for (var b = 0; b < 4; b++)
                        {
                            jtj[gp[a], gp[b]] += jp[a].Dot(jp[b]);
                        }
                    }
                }
            }

            for (var q = 0; q < 2; q++)
            {
                var s = priorW / kSigma;
                jtj[iK + q, iK + q] += s * s;
                jtr[iK + q] += s * s * x[iK + q];
            }

            if (!prior)
            {
                for (var d = 0; d < 3; d++)
                {
                    var g = 3 * refCam + d;
                    for (var k = 0; k < p; k++)
                    {
                        jtj[g, k] = 0;
                        jtj[k, g] = 0;
                    }

                    jtj[g, g] = 1;
                    jtr[g] = 0;
                }
            }

            var improved = false;
            for (var tries = 0; tries < 12; tries++)
            {
                using var am = new Mat(p, p, MatType.CV_64FC1);
                for (var a = 0; a < p; a++)
                {
                    for (var b = 0; b < p; b++)
                    {
                        am.Set(a, b, jtj[a, b] + (a == b ? lambda * Math.Max(jtj[a, a], 1e-9) : 0));
                    }
                }

                using var bv = new Mat(p, 1, MatType.CV_64FC1);
                for (var a = 0; a < p; a++)
                {
                    bv.Set(a, 0, -jtr[a]);
                }

                using var dx = new Mat();
                if (!Cv2.Solve(am, bv, dx, DecompTypes.Cholesky))
                {
                    Cv2.Solve(am, bv, dx, DecompTypes.SVD);
                }

                var xn = (double[])x.Clone();
                for (var a = 0; a < p; a++)
                {
                    xn[a] += dx.At<double>(a, 0);
                }

                var cn = Cost(xn);
                if (cn < cost)
                {
                    var rel = (cost - cn) / cost;
                    x = xn;
                    cost = cn;
                    lambda = Math.Max(lambda / 10, 1e-12);
                    improved = true;
                    if (rel < 1e-8)
                    {
                        it = 1000;
                    }

                    break;
                }

                lambda *= 10;
            }

            if (!improved)
            {
                break;
            }
        }

        var rd = Enumerable.Range(0, nc).Select(k => Rot(x, k)).ToArray();
        var err = obs.Select(o => ObsRes(x, rd[o.A], rd[o.B], o).Norm).ToArray();
        var sorted = err.OrderBy(v => v).ToArray();
        double med = sorted[sorted.Length / 2], p90 = sorted[(int)(sorted.Length * 0.9)];
        if (collectMatchOutliers)
        {
            for (var i = 0; i < obs.Count; i++)
            {
                if (err[i] > MatchOutlierFactor * med)
                {
                    _matchOutliers.Add(obs[i].Key);
                }
            }
        }

        // Ausreißer-Paare: Median eines Paares > max(Min, Faktor × Gesamtmedian) im Arbeitsmaßstab (nie das letzte Paar eines Bildes)
        var per = obs.Select((o, i) => (o, e: err[i])).GroupBy(t => (t.o.A, t.o.B))
            .Select(g2 => (g2.Key, med: g2.Select(t => t.e).OrderBy(v => v).ElementAt(g2.Count() / 2)))
            .OrderByDescending(t => t.med).ThenBy(t => t.Key.A).ThenBy(t => t.Key.B).ToList();
        var lim = Math.Max(_pairRejectMin, _pairRejectFactor * med);
        var deg = new int[comp.Length];
        foreach (var t in per)
        {
            deg[t.Key.A]++;
            deg[t.Key.B]++;
        }

        foreach (var t in per.Where(t => t.med > lim))
        {
            if (deg[t.Key.A] <= 1 || deg[t.Key.B] <= 1)
            {
                continue;
            }

            deg[t.Key.A]--;
            deg[t.Key.B]--;
            _badPairs.Add(((long)comp[t.Key.A] << 32) | (uint)comp[t.Key.B]);
        }

        for (var k = 0; k < nc; k++)
        {
            _cams[k].R = rd[k];
            _cams[k].F = x[iF + group[k]] / workScale;
            _cams[k].K1 = x[iK];
            _cams[k].K2 = x[iK + 1];
        }

        return (med / workScale, p90 / workScale);
    }

    /// <summary>Wellenkorrektur waagrecht: Hochachse = kleinster Eigenvektor von Σ x·xᵀ der Kamera-x-Achsen; Vorzeichen über die y-Achsen.</summary>
    private void WaveCorrect()
    {
        using var mom = new Mat(3, 3, MatType.CV_64FC1, Scalar.All(0));
        var sumY = new V3(0, 0, 0);
        foreach (var c in _cams)
        {
            var x = c.R.Col(0);
            double[] v = [x.X, x.Y, x.Z];
            for (var a = 0; a < 3; a++)
            {
                for (var b = 0; b < 3; b++)
                {
                    mom.Set(a, b, mom.At<double>(a, b) + v[a] * v[b]);
                }
            }

            sumY += c.R.Col(1);
        }

        using var evals = new Mat();
        using var evecs = new Mat();
        Cv2.Eigen(mom, evals, evecs);
        var down = new V3(evecs.At<double>(2, 0), evecs.At<double>(2, 1), evecs.At<double>(2, 2)).Normalized();
        // senkrechter Schwenk: alle x-Achsen gleich → kleinster Eigenvektor unbestimmt; dann die mittlere y-Achse
        if (evals.At<double>(1, 0) < 0.05 * _cams.Count)
        {
            var my = sumY.Normalized();
            var mx = _cams.Aggregate(new V3(0, 0, 0), (acc, c) => acc + c.R.Col(0)).Normalized();
            var fwd0 = mx.Cross(my).Normalized();
            down = fwd0.Cross(mx).Normalized();
        }

        if (down.Dot(sumY) < 0)
        {
            down *= -1;
        }

        var mid = _cams[_cams.Count / 2].R.Col(2);
        var fwd = (mid - down * mid.Dot(down)).Normalized();
        var right = down.Cross(fwd);
        var g = M3.FromRows(right, down, fwd);
        foreach (var c in _cams)
        {
            c.R = g * c.R;
        }
    }

    /// <summary>360° rundum erkennen; sonst die Lücke nach hinten drehen (Mitte auf Länge 0).</summary>
    private bool CenterOrDetect360()
    {
        const int bins = 720;
        var cov = new bool[bins];
        foreach (var c in _cams)
        {
            var r = PanoComposer.ImageRange(c, Proj.Spherical);
            for (var l = r.A; l <= r.B; l += Math.PI / bins)
            {
                var bi = (int)Math.Floor((Projections.Wrap(l) + Math.PI) / (2 * Math.PI) * bins) % bins;
                cov[(bi + bins) % bins] = true;
            }
        }

        if (cov.All(v => v))
        {
            return true;
        }

        int bestStart = 0, bestLen = 0;
        for (var s = 0; s < bins; s++)
        {
            if (cov[s] || !cov[(s - 1 + bins) % bins])
            {
                continue;
            }

            var len = 0;
            while (len < bins && !cov[(s + len) % bins])
            {
                len++;
            }

            if (len > bestLen)
            {
                bestLen = len;
                bestStart = s;
            }
        }

        var gapMid = -Math.PI + (bestStart + bestLen / 2.0) / bins * 2 * Math.PI;
        var center = Projections.Wrap(gapMid + Math.PI);
        var rot = M3.RotY(-center);
        foreach (var c in _cams)
        {
            c.R = rot * c.R;
        }

        return false;
    }

    /// <summary>
    /// Prüfung „gekippt“: Paare, die laut Ausrichtung stark überlappen (≥ 35 % der Fläche), deren Bilder Merkmale haben, die
    /// aber im Abgleich fast keine Treffer hatten — Zeichen einer falsch geschlossenen Kette (z. B. „zwei Sonnen“).
    /// </summary>
    private (int Bad, int Expected) CountUnmatchedOverlaps(MatchesInfo?[] pairs, int n, int[] comp, ImageFeatures[] feats)
    {
        int bad = 0, expected = 0;
        for (var a = 0; a < _cams.Count; a++)
        {
            for (var b = a + 1; b < _cams.Count; b++)
            {
                var ca = _cams[a];
                var cb = _cams[b];
                if (Math.Acos(Math.Clamp(ca.R.Col(2).Dot(cb.R.Col(2)), -1, 1)) > ca.HalfDiagonalFov + cb.HalfDiagonalFov)
                {
                    continue;
                }

                var inside = 0;
                const int grid = 9;
                for (var gy = 0; gy < grid; gy++)
                {
                    for (var gx = 0; gx < grid; gx++)
                    {
                        var ray = ca.Ray((gx + 0.5) / grid * (ca.Size.Width - 1), (gy + 0.5) / grid * (ca.Size.Height - 1));
                        if (cb.Contains(ray))
                        {
                            inside++;
                        }
                    }
                }

                if (inside < 0.35 * grid * grid)
                {
                    continue;
                }

                int i = comp[a], j = comp[b];
                if (feats[i].Keypoints.Count < 300 || feats[j].Keypoints.Count < 300)
                {
                    continue;
                }

                expected++;
                var m = pairs[Math.Min(i, j) * n + Math.Max(i, j)];
                if (m is not null && Conf(m) < 0.3)
                {
                    bad++;
                }
            }
        }

        return (bad, expected);
    }

    /// <summary>
    /// Ähnlichkeits-Abbildung je Bild für „Nur verschieben“: lineare kleinste Quadrate über die Treffer der eingebundenen
    /// Paare (x' = a·x + b·y + tx, y' = −b·x + a·y + ty), Referenz = mittleres Bild. Liefert Abbildungen und Median-Restfehler
    /// (volle Auflösung).
    /// </summary>
    private (List<(double A, double B, double Tx, double Ty)>? Sim, double ResidualPx) FitSimilarity(MatchesInfo?[] pairs, ImageFeatures[] feats, int n, int[] comp, double workScale)
    {
        var nc = comp.Length;
        var idx = comp.Select((src, k) => (src, k)).ToDictionary(t => t.src, t => t.k);
        var rows = new List<(int A, int B, double X1, double Y1, double X2, double Y2)>();
        foreach (var i in comp)
        {
            foreach (var j in comp)
            {
                if (i >= j || pairs[i * n + j] is not { } m || Conf(m) <= ConfThresh)
                {
                    continue;
                }

                var inl = new List<int>();
                for (var k = 0; k < m.Matches.Count; k++)
                {
                    if (m.InliersMask[k] != 0)
                    {
                        inl.Add(k);
                    }
                }

                var stride = Math.Max(1, inl.Count / 100);
                for (var t = 0; t < inl.Count; t += stride)
                {
                    var dm = m.Matches[inl[t]];
                    var p1 = feats[i].Keypoints[dm.QueryIdx].Pt;
                    var p2 = feats[j].Keypoints[dm.TrainIdx].Pt;
                    rows.Add((idx[i], idx[j], p1.X / workScale, p1.Y / workScale, p2.X / workScale, p2.Y / workScale));
                }
            }
        }

        if (rows.Count < 8 || nc > 200)
        {
            return (null, double.NaN);
        }

        var u = 4 * nc;
        var reference = nc / 2;
        var a = new double[u, u];
        var bvec = new double[u];
        void Add(Span<(int Index, double Value)> terms, double rhs)
        {
            foreach (var (pi, pv) in terms)
            {
                bvec[pi] += pv * rhs;
                foreach (var (qi, qv) in terms)
                {
                    a[pi, qi] += pv * qv;
                }
            }
        }

        Span<(int, double)> buf = stackalloc (int, double)[8];
        foreach (var (ia, ib, x1, y1, x2, y2) in rows)
        {
            // T_a(p1) − T_b(p2) = 0, je Koordinate eine Gleichung
            int pa = 4 * ia, pb = 4 * ib;
            buf[0] = (pa, x1);
            buf[1] = (pa + 1, y1);
            buf[2] = (pa + 2, 1);
            buf[3] = (pb, -x2);
            buf[4] = (pb + 1, -y2);
            buf[5] = (pb + 2, -1);
            Add(buf[..6], 0);
            buf[0] = (pa, y1);
            buf[1] = (pa + 1, -x1);
            buf[2] = (pa + 3, 1);
            buf[3] = (pb, -y2);
            buf[4] = (pb + 1, x2);
            buf[5] = (pb + 3, -1);
            Add(buf[..6], 0);
        }

        // Referenz festhalten: a = 1, b = 0, t = 0
        const double fix = 1e8;
        a[4 * reference, 4 * reference] += fix;
        bvec[4 * reference] += fix;
        for (var q = 1; q < 4; q++)
        {
            a[4 * reference + q, 4 * reference + q] += fix;
        }

        for (var q = 0; q < u; q++)
        {
            a[q, q] += 1e-9;
        }

        using var am = new Mat(u, u, MatType.CV_64FC1);
        using var bm = new Mat(u, 1, MatType.CV_64FC1);
        for (var p = 0; p < u; p++)
        {
            bm.Set(p, 0, bvec[p]);
            for (var q = 0; q < u; q++)
            {
                am.Set(p, q, a[p, q]);
            }
        }

        using var xm = new Mat();
        if (!Cv2.Solve(am, bm, xm, DecompTypes.Cholesky) && !Cv2.Solve(am, bm, xm, DecompTypes.SVD))
        {
            return (null, double.NaN);
        }

        var sim = new List<(double, double, double, double)>(nc);
        for (var k = 0; k < nc; k++)
        {
            sim.Add((xm.At<double>(4 * k, 0), xm.At<double>(4 * k + 1, 0), xm.At<double>(4 * k + 2, 0), xm.At<double>(4 * k + 3, 0)));
        }

        var errs = rows.Select(r =>
        {
            var (aa, ab, atx, aty) = sim[r.A];
            var (ba, bb, btx, bty) = sim[r.B];
            double ux = aa * r.X1 + ab * r.Y1 + atx, uy = -ab * r.X1 + aa * r.Y1 + aty;
            double vx = ba * r.X2 + bb * r.Y2 + btx, vy = -bb * r.X2 + ba * r.Y2 + bty;
            return Math.Sqrt((ux - vx) * (ux - vx) + (uy - vy) * (uy - vy));
        }).OrderBy(v => v).ToArray();
        return (sim, errs[errs.Length / 2]);
    }
}
