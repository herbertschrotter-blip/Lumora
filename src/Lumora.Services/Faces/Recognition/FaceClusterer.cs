namespace Lumora.Services.Faces.Recognition;

/// <summary>
/// Strenge Gruppierung unbekannter Gesichter (ADR-0036).
/// <list type="number">
/// <item>Qualitätsfilter: Gesichter unter <see cref="FaceClusterOptions.MinQuality"/> und ungültige Fingerabdrücke werden
/// nicht gruppiert (verrauschte Vektoren sind die typischen Brücken zwischen Personen).</item>
/// <item>k-NN-Graph: je Gesicht die <see cref="FaceClusterOptions.NeighborCount"/> ähnlichsten anderen ab
/// <see cref="FaceClusterOptions.EdgeSimilarity"/> (Brute Force, SIMD, parallel in Zeilenblöcken).</item>
/// <item>Agglomerativ mit Average Linkage entlang der Graph-Kanten: verschmolzen wird immer das Paar mit der höchsten
/// mittleren Ähnlichkeit ALLER Paare zwischen beiden Gruppen, solange sie ≥ <see cref="FaceClusterOptions.MergeSimilarity"/>
/// ist. Exakt und billig über Summenvektoren: mittlere Ähnlichkeit = (S_A · S_B) / (|A|·|B|). Anders als bei
/// DBSCAN/Single Linkage verbindet eine einzelne ähnliche Brücke zwei Personen nicht.</item>
/// <item>Nachkontrolle: Mitglieder mit Ähnlichkeit zum Zentrum unter <see cref="FaceClusterOptions.MinCentroidSimilarity"/>
/// fliegen raus; Gruppen unter <see cref="FaceClusterOptions.MinGroupSize"/> werden Rauschen.</item>
/// <item>Mitglieder nach Nähe zum Zentrum sortiert; zweifelhafte (robuste Abweichung oder zu geringe Zentrums-Ähnlichkeit)
/// am Ende mit <see cref="FaceGroupMember.IsOutlier"/>.</item>
/// </list>
/// Deterministisch: Reihenfolge nach FaceId, feste Gleichstands-Regeln, keine Zufallszahlen.
/// </summary>
public sealed class FaceClusterer : IFaceClusterer
{
    private const int RowBlock = 16;
    private const int MaxEjectionPasses = 3;

    public FaceClusterResult Cluster(
        IReadOnlyList<FaceSample> unassigned,
        FaceClusterOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unassigned);
        options ??= FaceClusterOptions.Default;
        Validate(options);
        if (unassigned.Count == 0)
        {
            return FaceClusterResult.Empty;
        }

        var order = EmbeddingRows.OrderByFaceId(unassigned);
        var dimension = EmbeddingRows.ResolveDimension(unassigned);
        var seen = new HashSet<long>();
        var rows = EmbeddingRows.Build(
            unassigned, order, dimension,
            s => s.Quality >= options.MinQuality && seen.Add(s.FaceId),
            out var accepted, out var rejected);
        var skipped = rejected.Select(i => unassigned[i].FaceId).Distinct().Order().ToList();
        if (rows.Count == 0)
        {
            return new FaceClusterResult([], [], skipped);
        }

        var faceIds = accepted.Select(i => unassigned[i].FaceId).ToArray();
        var adjacency = BuildNeighborGraph(rows, options, cancellationToken);
        var clusters = MergeAverageLinkage(rows, adjacency, options.MergeSimilarity, cancellationToken);

        var groups = new List<FaceGroup>();
        var noise = new List<long>();
        foreach (var members in clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = Finish(rows, faceIds, members, options, noise);
            if (group is not null)
            {
                groups.Add(group);
            }
        }

        groups.Sort((a, b) =>
        {
            var c = b.Count.CompareTo(a.Count);
            return c != 0 ? c : a.Members.Min(m => m.FaceId).CompareTo(b.Members.Min(m => m.FaceId));
        });
        noise.Sort();
        return new FaceClusterResult(groups, noise, skipped);
    }

    private static void Validate(FaceClusterOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.NeighborCount, 1, nameof(options.NeighborCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinGroupSize, 2, nameof(options.MinGroupSize));
    }

    /// <summary>Symmetrischer k-NN-Graph (Kante, wenn j unter den k nächsten von i ist oder umgekehrt).</summary>
    private static HashSet<int>[] BuildNeighborGraph(EmbeddingRows rows, FaceClusterOptions options, CancellationToken ct)
    {
        var n = rows.Count;
        var k = Math.Min(options.NeighborCount, n - 1);
        var topIndex = new int[n][];
        var topSim = new float[n][];
        var blocks = (n + RowBlock - 1) / RowBlock;
        // halbe Kerne wie alle Hintergrund-Arbeit: sonst stehen Vorschaubilder und Galerie still (2026-10-30, 449 000 Dateien)
        Parallel.For(0, blocks, Background.BackgroundCpu.Options(ct), block =>
        {
            var start = block * RowBlock;
            var end = Math.Min(n, start + RowBlock);
            var heaps = new TopK[end - start];
            for (var r = 0; r < heaps.Length; r++)
            {
                heaps[r] = new TopK(k, options.EdgeSimilarity);
            }

            // Zeile j einmal laden und gegen den ganzen Block rechnen (Block bleibt im L1/L2-Cache).
            for (var j = 0; j < n; j++)
            {
                var other = rows.Row(j);
                for (var i = start; i < end; i++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    heaps[i - start].Offer(j, VectorMath.Dot(rows.Row(i), other));
                }
            }

            for (var i = start; i < end; i++)
            {
                heaps[i - start].Export(out topIndex[i], out topSim[i]);
            }
        });

        var adjacency = new HashSet<int>[n];
        for (var i = 0; i < n; i++)
        {
            adjacency[i] = [];
        }

        for (var i = 0; i < n; i++)
        {
            foreach (var j in topIndex[i])
            {
                adjacency[i].Add(j);
                adjacency[j].Add(i);
            }
        }

        return adjacency;
    }

    /// <summary>Agglomeratives Verschmelzen (Average Linkage) entlang der Kanten; liefert Mitgliederlisten.</summary>
    private static List<List<int>> MergeAverageLinkage(
        EmbeddingRows rows, HashSet<int>[] adjacency, float mergeSimilarity, CancellationToken ct)
    {
        var n = rows.Count;
        var sums = new float[n][];
        var sizes = new int[n];
        var versions = new int[n];
        var alive = new bool[n];
        var members = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            sizes[i] = 1;
            alive[i] = true;
            members[i] = [i];
        }

        ReadOnlySpan<float> Sum(int c) => sums[c] is { } s ? s : rows.Row(c);

        float Linkage(int a, int b) => VectorMath.Dot(Sum(a), Sum(b)) / ((float)sizes[a] * sizes[b]);

        var queue = new PriorityQueue<(int A, int B, int VersionA, int VersionB), (float Linkage, int A, int B)>(
            Comparer<(float Linkage, int A, int B)>.Create((x, y) =>
            {
                var c = y.Linkage.CompareTo(x.Linkage); // höchste Ähnlichkeit zuerst
                if (c != 0)
                {
                    return c;
                }

                c = x.A.CompareTo(y.A);
                return c != 0 ? c : x.B.CompareTo(y.B);
            }));

        void Push(int a, int b)
        {
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            var linkage = Linkage(lo, hi);
            if (linkage >= mergeSimilarity)
            {
                queue.Enqueue((lo, hi, versions[lo], versions[hi]), (linkage, lo, hi));
            }
        }

        for (var i = 0; i < n; i++)
        {
            foreach (var j in adjacency[i])
            {
                if (j > i)
                {
                    Push(i, j);
                }
            }
        }

        var merges = 0;
        while (queue.TryDequeue(out var pair, out _))
        {
            if (!alive[pair.A] || !alive[pair.B] || versions[pair.A] != pair.VersionA || versions[pair.B] != pair.VersionB)
            {
                continue; // veralteter Eintrag
            }

            if ((++merges & 255) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            // Größere Gruppe überlebt (Gleichstand: kleinerer Index) — weniger Kopieren.
            var (keep, drop) = sizes[pair.A] >= sizes[pair.B] ? (pair.A, pair.B) : (pair.B, pair.A);
            if (sums[keep] is null)
            {
                sums[keep] = rows.Row(keep).ToArray();
            }

            VectorMath.AddScaled(sums[keep], Sum(drop), 1f);
            sizes[keep] += sizes[drop];
            members[keep].AddRange(members[drop]);
            members[drop] = [];
            sums[drop] = null!;
            alive[drop] = false;
            versions[keep]++;

            adjacency[keep].UnionWith(adjacency[drop]);
            adjacency[keep].Remove(keep);
            adjacency[keep].Remove(drop);
            foreach (var other in adjacency[drop])
            {
                if (other != keep)
                {
                    adjacency[other].Remove(drop);
                    adjacency[other].Add(keep);
                }
            }

            adjacency[drop].Clear();
            foreach (var other in adjacency[keep])
            {
                Push(keep, other);
            }
        }

        var result = new List<List<int>>();
        for (var i = 0; i < n; i++)
        {
            if (alive[i])
            {
                result.Add(members[i]);
            }
        }

        return result;
    }

    /// <summary>Nachkontrolle einer Rohgruppe: Ausschluss ferner Mitglieder, Mindestgröße, Sortierung, Ausreißer.</summary>
    private static FaceGroup? Finish(
        EmbeddingRows rows, long[] faceIds, List<int> members, FaceClusterOptions options, List<long> noise)
    {
        var current = members;
        float[] centroid = [];
        float[] similarities = [];
        for (var pass = 0; pass < MaxEjectionPasses; pass++)
        {
            if (current.Count < options.MinGroupSize)
            {
                break;
            }

            centroid = Centroid(rows, current, out _);
            similarities = new float[current.Count];
            var kept = new List<int>(current.Count);
            for (var m = 0; m < current.Count; m++)
            {
                similarities[m] = VectorMath.Dot(rows.Row(current[m]), centroid);
                if (similarities[m] >= options.MinCentroidSimilarity)
                {
                    kept.Add(current[m]);
                }
                else
                {
                    noise.Add(faceIds[current[m]]);
                }
            }

            if (kept.Count == current.Count)
            {
                break;
            }

            current = kept;
            similarities = [];
        }

        if (current.Count < options.MinGroupSize)
        {
            foreach (var m in current)
            {
                noise.Add(faceIds[m]);
            }

            return null;
        }

        if (similarities.Length != current.Count)
        {
            centroid = Centroid(rows, current, out _);
            similarities = current.Select(m => VectorMath.Dot(rows.Row(m), centroid)).ToArray();
        }

        Centroid(rows, current, out var sumSquared);
        var count = current.Count;
        var cohesion = (sumSquared - count) / ((float)count * (count - 1));

        var distances = similarities.Select(s => 1f - s).ToArray();
        var median = VectorMath.Median(distances);
        var mad = VectorMath.MedianAbsoluteDeviation(distances, median);
        var useRobust = count >= 5;
        var result = new FaceGroupMember[count];
        for (var m = 0; m < count; m++)
        {
            var z = useRobust ? MathF.Max(0f, (distances[m] - median) / (1.4826f * mad + 0.01f)) : 0f;
            var outlier = (useRobust && z >= options.OutlierRobustZ) || similarities[m] < options.OutlierCentroidSimilarity;
            result[m] = new FaceGroupMember(faceIds[current[m]], distances[m], z, outlier);
        }

        Array.Sort(result, (a, b) =>
        {
            var c = a.IsOutlier.CompareTo(b.IsOutlier); // Ausreißer ans Ende
            if (c != 0)
            {
                return c;
            }

            c = a.Distance.CompareTo(b.Distance);
            return c != 0 ? c : a.FaceId.CompareTo(b.FaceId);
        });
        return new FaceGroup(result, Math.Clamp(cohesion, -1f, 1f), centroid);
    }

    private static float[] Centroid(EmbeddingRows rows, List<int> members, out float sumSquared)
    {
        var sum = new float[rows.Dimension];
        foreach (var m in members)
        {
            VectorMath.AddScaled(sum, rows.Row(m), 1f);
        }

        sumSquared = VectorMath.Dot(sum, sum);
        VectorMath.NormalizeInPlace(sum);
        return sum;
    }

    /// <summary>Kleine Bestenliste (k ≤ ~50): ersetzt das schwächste Element, nur echte Verbesserungen.</summary>
    private sealed class TopK(int capacity, float minimum)
    {
        private readonly int[] _index = new int[Math.Max(capacity, 0)];
        private readonly float[] _sim = new float[Math.Max(capacity, 0)];
        private int _count;
        private int _weakest;

        public void Offer(int index, float similarity)
        {
            if (similarity < minimum || _index.Length == 0)
            {
                return;
            }

            if (_count < _index.Length)
            {
                _index[_count] = index;
                _sim[_count] = similarity;
                _count++;
                if (_count == _index.Length)
                {
                    FindWeakest();
                }

                return;
            }

            if (similarity <= _sim[_weakest])
            {
                return; // Gleichstand: der frühere (kleinere Index) bleibt
            }

            _index[_weakest] = index;
            _sim[_weakest] = similarity;
            FindWeakest();
        }

        public void Export(out int[] indices, out float[] similarities)
        {
            indices = _index[.._count];
            similarities = _sim[.._count];
        }

        private void FindWeakest()
        {
            _weakest = 0;
            for (var i = 1; i < _count; i++)
            {
                if (_sim[i] < _sim[_weakest] || (_sim[i] == _sim[_weakest] && _index[i] > _index[_weakest]))
                {
                    _weakest = i;
                }
            }
        }
    }
}
