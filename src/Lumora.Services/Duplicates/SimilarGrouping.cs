using System.Numerics;

namespace Lumora.Services.Duplicates;

/// <summary>
/// Gruppen ähnlicher Bilder aus 64-Bit-Fingerabdrücken (ADR-0053).
/// <para>
/// Suche: Mehrfach-Index-Hashing (Norouzi u. a.) — der Fingerabdruck wird in vier 16-Bit-Bänder geteilt, je Band ein
/// Bucket-Index (Zählsortierung, Feld statt Wörterbuch). Unterscheiden sich zwei Fingerabdrücke in höchstens r Bits, dann
/// in mindestens einem Band in höchstens ⌊r/4⌋ Bits (Schubfachprinzip); gesucht werden je Band alle Schlüssel in diesem
/// Abstand (r ≤ 7: 17, r ≤ 11: 137, r ≤ 15: 697 Schlüssel), Kandidaten werden mit dem vollen Abstand geprüft. Doppelte
/// Kandidaten aus mehreren Bändern fallen ohne Merk-Feld weg (nur das erste passende Band zählt).
/// </para>
/// <para>
/// Gruppen: Stern-Verfahren statt Ketten — der Reihe nach wird jedes noch freie Bild Mittelpunkt, alle freien Bilder im
/// Abstand ≤ r gehören zu seiner Gruppe. So liegen alle Bilder einer Gruppe nah am ersten (keine langen Ketten A~B~C~…,
/// in denen Anfang und Ende nichts mehr gemein haben). Die Reihenfolge gibt der Aufrufer vor (ältere Dateien zuerst).
/// Die Nachbarn der Mittelpunkte werden blockweise parallel gesucht, die Zuordnung danach der Reihe nach.
/// </para>
/// </summary>
public static class SimilarGrouping
{
    private const int Bands = 4;
    private const int BandBits = 16;
    private const int BlockSize = 1024;

    /// <summary>Höchstens so viele Nachbarn je Mittelpunkt (Schutz vor sehr großen Gruppen gleicher Bilder).</summary>
    public const int MaxGroupSize = 5000;

    private static readonly ushort[][] s_masks = BuildMasks(3);

    /// <summary>
    /// Gruppen (mindestens zwei Einträge, Mittelpunkt zuerst, übrige nach Index) als Indizes in <paramref name="hashes"/>.
    /// </summary>
    public static IReadOnlyList<int[]> Group(IReadOnlyList<ulong> hashes, int maxDistance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        if (maxDistance < 0 || maxDistance >= Bands * (s_masks.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), maxDistance, $"Abstand 0–{(Bands * s_masks.Length) - 1}.");
        }

        var n = hashes.Count;
        var groups = new List<int[]>();
        if (n < 2)
        {
            return groups;
        }

        var index = new HammingIndex(hashes);
        var assigned = new bool[n];
        var blockNeighbors = new List<int>?[BlockSize];
        for (var blockStart = 0; blockStart < n; blockStart += BlockSize)
        {
            ct.ThrowIfCancellationRequested();
            var blockEnd = Math.Min(n, blockStart + BlockSize);
            Array.Clear(blockNeighbors);

            // Nachbarn aller noch freien Bilder des Blocks parallel suchen (liest „assigned“ nur)
            Parallel.For(
                blockStart,
                blockEnd,
                Background.BackgroundCpu.Options(ct),
                i =>
                {
                    if (!assigned[i])
                    {
                        blockNeighbors[i - blockStart] = index.Query(i, maxDistance, assigned);
                    }
                });

            // der Reihe nach zuordnen: wer inzwischen vergeben ist, fällt weg
            for (var i = blockStart; i < blockEnd; i++)
            {
                if (assigned[i] || blockNeighbors[i - blockStart] is not { Count: > 0 } neighbors)
                {
                    continue;
                }

                neighbors.Sort();
                var members = new List<int>(neighbors.Count + 1) { i };
                foreach (var j in neighbors)
                {
                    if (!assigned[j] && j != i)
                    {
                        members.Add(j);
                    }
                }

                if (members.Count < 2)
                {
                    continue;
                }

                foreach (var m in members)
                {
                    assigned[m] = true;
                }

                groups.Add(members.ToArray());
            }
        }

        return groups;
    }

    /// <summary>Alle Paare (i &lt; j) mit Abstand ≤ r — nur für Tests und Messungen (Vergleich mit dem Vollvergleich).</summary>
    public static IReadOnlyList<(int A, int B)> FindPairs(IReadOnlyList<ulong> hashes, int maxDistance)
    {
        var index = new HammingIndex(hashes);
        var pairs = new List<(int, int)>();
        var none = new bool[hashes.Count];
        for (var i = 0; i < hashes.Count; i++)
        {
            foreach (var j in index.Query(i, maxDistance, none) ?? [])
            {
                if (j > i)
                {
                    pairs.Add((i, j));
                }
            }
        }

        return pairs;
    }

    private static ushort[][] BuildMasks(int maxRadius)
    {
        var byRadius = new ushort[maxRadius + 1][];
        for (var s = 0; s <= maxRadius; s++)
        {
            var list = new List<ushort>();
            for (var m = 0; m <= ushort.MaxValue; m++)
            {
                if (BitOperations.PopCount((uint)m) <= s)
                {
                    list.Add((ushort)m);
                }
            }

            byRadius[s] = list.ToArray();
        }

        return byRadius;
    }

    /// <summary>Bucket-Index je 16-Bit-Band (CSR: Startpositionen + Einträge).</summary>
    private sealed class HammingIndex
    {
        private readonly ulong[] _hashes;
        private readonly int[][] _starts = new int[Bands][];
        private readonly int[][] _items = new int[Bands][];

        public HammingIndex(IReadOnlyList<ulong> hashes)
        {
            _hashes = hashes.ToArray();
            var n = _hashes.Length;
            for (var b = 0; b < Bands; b++)
            {
                var starts = new int[(1 << BandBits) + 1];
                foreach (var h in _hashes)
                {
                    starts[Band(h, b) + 1]++;
                }

                for (var k = 1; k < starts.Length; k++)
                {
                    starts[k] += starts[k - 1];
                }

                var fill = (int[])starts.Clone();
                var items = new int[n];
                for (var i = 0; i < n; i++)
                {
                    items[fill[Band(_hashes[i], b)]++] = i;
                }

                _starts[b] = starts;
                _items[b] = items;
            }
        }

        private static int Band(ulong hash, int band) => (int)((hash >> (band * BandBits)) & 0xFFFF);

        /// <summary>Freie Bilder j ≠ i mit Abstand ≤ r (höchstens <see cref="MaxGroupSize"/>); null ohne Treffer.</summary>
        public List<int>? Query(int i, int r, bool[] assigned)
        {
            var h = _hashes[i];
            var s = r / Bands;
            var masks = s_masks[s];
            List<int>? result = null;
            for (var b = 0; b < Bands; b++)
            {
                var key = Band(h, b);
                var starts = _starts[b];
                var items = _items[b];
                foreach (var mask in masks)
                {
                    var probe = key ^ mask;
                    for (var k = starts[probe]; k < starts[probe + 1]; k++)
                    {
                        var j = items[k];
                        if (j == i || assigned[j])
                        {
                            continue;
                        }

                        var other = _hashes[j];
                        if (BitOperations.PopCount(h ^ other) > r || MatchedInEarlierBand(h, other, b, s))
                        {
                            continue;
                        }

                        result ??= new List<int>();
                        result.Add(j);
                        if (result.Count >= MaxGroupSize)
                        {
                            return result;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>true, wenn j schon über ein früheres Band gefunden wurde (dort Bandabstand ≤ s).</summary>
        private static bool MatchedInEarlierBand(ulong a, ulong b, int band, int s)
        {
            for (var e = 0; e < band; e++)
            {
                if (BitOperations.PopCount((uint)(Band(a, e) ^ Band(b, e))) <= s)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
