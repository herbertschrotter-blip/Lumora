using System.Globalization;
using System.Numerics;

namespace Lumora.Services.Quality;

/// <summary>Was die Highlight-Auswahl über eine Datei weiß (aus Files, FileQuality, FileFingerprints, Faces).</summary>
/// <param name="Position">Stelle in der Eingabe (Aufnahme-Reihenfolge der Story).</param>
/// <param name="Time">Aufnahmezeit (sonst Änderungszeit) in Unix-Sekunden.</param>
/// <param name="Sharpness">Schärfe 0..1, null = noch nicht bewertet.</param>
/// <param name="Exposure">Belichtung 0..1, null = noch nicht bewertet.</param>
/// <param name="Aesthetic">Bildwirkung 0..1, null = ohne Motiv-Vektor.</param>
/// <param name="ConfirmedFaces">Bestätigte Gesichter (Faces.State = 2).</param>
/// <param name="FaceQuality">Mittlere Qualität dieser Gesichter (0..1).</param>
/// <param name="ImageHash">Gültiger pHash (ADR-0053), null ohne.</param>
/// <param name="LowDetail">Fast einfarbiges Bild (pHash ist Rauschen, zählt nie als Serie).</param>
/// <param name="ContentHash">Gültige Inhalts-Prüfsumme (SHA-256), null ohne.</param>
/// <param name="Usage">Gelernte Nutzung (ADR-0072), null ohne bzw. wenn „Nutzung lernen“ aus ist — nie ein Abzug.</param>
public sealed record HighlightCandidate(
    int FileId,
    int Position,
    long Time,
    bool IsVideo,
    double? Sharpness,
    double? Exposure,
    double? Aesthetic,
    int ConfirmedFaces,
    double FaceQuality,
    bool IsFavorite,
    int Rating,
    ulong? ImageHash,
    bool LowDetail,
    byte[]? ContentHash,
    Usage.UsageTotals? Usage = null);

/// <summary>
/// Rechenkern der Highlight-Auswahl (ADR-0057), ohne Datenbank (Tests, Messung):
/// <list type="number">
/// <item>Wert je Datei: Güte (Schärfe halb relativ zur Story, halb absolut; Belichtung; Bildwirkung, falls vorhanden) +
/// bestätigte Personen (mehr und gut sichtbare Gesichter heben stärker) + Favorit + Bewertung + kleiner Video-Bonus
/// (Abwechslung) − Abschlag für fast einfarbige Bilder + gedämpfter Zuschlag aus der gelernten Nutzung (ADR-0072, höchstens
/// <see cref="Usage.UsageScoring.MaxBonus"/>, ohne Nutzung 0).</item>
/// <item>Gruppen: gleiche Inhalts-Prüfsumme immer zusammen; Serie = pHash-Abstand ≤ Schwelle UND Aufnahmezeit-Abstand ≤
/// <see cref="SeriesSeconds"/>, eine Serie umfasst höchstens <see cref="SeriesSpanSeconds"/>. Je Gruppe zählt nur die
/// beste Datei, der Rest wird <c>SimilarCount</c>.</item>
/// <item>Anzahl k = Anteil × Dateien, begrenzt auf [Min, Max] und auf die Zahl der Gruppen.</item>
/// <item>Verteilung: je Stunde der Aufnahme höchstens ihr Anteil an k (aufgerundet, mindestens 1); erst die besten unter
/// dieser Quote, dann nach Wert auffüllen.</item>
/// </list>
/// </summary>
public static class HighlightPlanner
{
    /// <summary>Höchster Zeitabstand zweier Fotos einer Serie.</summary>
    public const int SeriesSeconds = 60;

    /// <summary>Längste Dauer einer Serie (erste bis letzte Aufnahme) — Intervallaufnahmen werden nicht zu einer Kette.</summary>
    public const int SeriesSpanSeconds = 300;

    /// <summary>So viele Nachfolger (nach Zeit) werden je Foto auf „Serie“ geprüft (begrenzt den Aufwand bei Dauerfeuer).</summary>
    private const int SeriesWindow = 64;

    private const double FavoriteBonus = 0.15;
    private const double RatingStep = 0.03;
    private const double VideoBonus = 0.05;
    private const double LowDetailPenalty = 0.25;

    /// <summary>Gruppe aus der Auswahl: bestes Mitglied, Größe, Art.</summary>
    private sealed record Group(int Best, int Size, bool IsSeries, int Shots);

    /// <summary>Highlights in Eingabe-Reihenfolge.</summary>
    public static IReadOnlyList<HighlightPick> Select(IReadOnlyList<HighlightCandidate> candidates, HighlightOptions options, int similarThreshold)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (candidates.Count == 0)
        {
            return [];
        }

        var scored = Score(candidates);
        var groups = BuildGroups(candidates, scored.Total, similarThreshold);
        var k = TargetCount(candidates.Count, groups.Count, options);
        if (k == 0)
        {
            return [];
        }

        var chosen = Distribute(candidates, groups, scored.Total, k);
        return chosen
            .OrderBy(g => candidates[g.Best].Position)
            .Select(g => new HighlightPick(
                candidates[g.Best].FileId,
                Math.Round(scored.Total[g.Best], 4),
                Reason(candidates, scored, g),
                g.Size - 1))
            .ToList();
    }

    /// <summary>Titelbild: bestes Foto, das weder zu den unschärfsten noch zu den dunkelsten gehört, solange es eine Alternative gibt.</summary>
    public static int? PickCover(IReadOnlyList<HighlightCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var scored = Score(candidates);
        var pool = Enumerable.Range(0, candidates.Count).ToList();
        if (pool.Any(i => !candidates[i].IsVideo))
        {
            pool = pool.Where(i => !candidates[i].IsVideo).ToList(); // Fotos vor Videos
        }

        var rated = pool.Where(i => candidates[i].Sharpness is not null && candidates[i].Exposure is not null).ToList();
        var lowSharp = LowSet(rated, i => candidates[i].Sharpness!.Value, absoluteFloor: 0.35);
        var lowExposure = LowSet(rated, i => candidates[i].Exposure!.Value, absoluteFloor: 0.45);
        var good = rated.Where(i => !lowSharp.Contains(i) && !lowExposure.Contains(i) && !candidates[i].LowDetail).ToList();
        if (good.Count == 0)
        {
            // Alle im unteren Bereich: dann wenigstens nie das unschärfste bzw. dunkelste Foto.
            var worstSharp = Worst(rated, i => candidates[i].Sharpness!.Value);
            var worstExposure = Worst(rated, i => candidates[i].Exposure!.Value);
            good = rated.Where(i => !worstSharp.Contains(i) && !worstExposure.Contains(i)).ToList();
        }

        var choice = good.Count > 0 ? good : pool;
        var best = choice
            .OrderByDescending(i => scored.Total[i] + (0.15 * (candidates[i].Aesthetic ?? 0.5)))
            .ThenBy(i => candidates[i].Position)
            .First();
        return candidates[best].FileId;
    }

    /// <summary>Zahl der Highlights: Anteil × Dateien in [Min, Max], höchstens so viele wie Gruppen.</summary>
    public static int TargetCount(int files, int groups, HighlightOptions options)
    {
        var share = double.IsNaN(options.Share) ? 0 : Math.Clamp(options.Share, 0, 1);
        var max = Math.Max(0, options.Max);
        var min = Math.Clamp(options.Min, 0, max);
        var wanted = Math.Clamp((int)Math.Round(share * files, MidpointRounding.AwayFromZero), min, max);
        return Math.Min(wanted, groups);
    }

    // ----- Wert -----

    private sealed record Scores(double[] Total, double[] SharpRank, double[] AestheticRank);

    private static Scores Score(IReadOnlyList<HighlightCandidate> c)
    {
        var sharpRank = Ranks(c, x => x.Sharpness);
        var aestheticRank = Ranks(c, x => x.Aesthetic);
        var total = new double[c.Count];
        for (var i = 0; i < c.Count; i++)
        {
            var x = c[i];
            var sharp = x.Sharpness is { } s ? (0.5 * sharpRank[i]) + (0.5 * s) : 0.5;
            var exposure = x.Exposure ?? 0.6;
            var quality = x.Aesthetic is { } a
                ? (0.40 * sharp) + (0.25 * exposure) + (0.35 * ((0.5 * aestheticRank[i]) + (0.5 * a)))
                : (0.60 * sharp) + (0.40 * exposure);
            var people = x.ConfirmedFaces > 0
                ? (0.10 + (0.04 * Math.Min(x.ConfirmedFaces - 1, 3))) * (0.6 + (0.4 * Math.Clamp(x.FaceQuality, 0, 1)))
                : 0;
            var rating = x.Rating > 1 ? RatingStep * (Math.Min(x.Rating, 5) - 1) : 0;
            total[i] = quality + people + (x.IsFavorite ? FavoriteBonus : 0) + rating
                       + (x.IsVideo ? VideoBonus : 0) - (x.LowDetail ? LowDetailPenalty : 0)
                       + Usage.UsageScoring.Bonus(x.Usage); // gelernte Nutzung (ADR-0072): gedämpft, höchstens 0,12, nie negativ
        }

        return new Scores(total, sharpRank, aestheticRank);
    }

    /// <summary>Rang 0..1 innerhalb der Story (gleiche Werte gleicher Rang); ohne Wert 0,5.</summary>
    private static double[] Ranks(IReadOnlyList<HighlightCandidate> c, Func<HighlightCandidate, double?> value)
    {
        var ranks = new double[c.Count];
        Array.Fill(ranks, 0.5);
        var known = Enumerable.Range(0, c.Count).Where(i => value(c[i]) is not null).OrderBy(i => value(c[i])!.Value).ToList();
        if (known.Count < 2)
        {
            return ranks;
        }

        var start = 0;
        while (start < known.Count)
        {
            var end = start;
            while (end + 1 < known.Count && value(c[known[end + 1]]) == value(c[known[start]]))
            {
                end++;
            }

            var rank = ((start + end) / 2.0) / (known.Count - 1);
            for (var k = start; k <= end; k++)
            {
                ranks[known[k]] = rank;
            }

            start = end + 1;
        }

        return ranks;
    }

    // ----- Gruppen (Serien und Doppelte) -----

    private static List<Group> BuildGroups(IReadOnlyList<HighlightCandidate> c, double[] total, int threshold)
    {
        var parent = Enumerable.Range(0, c.Count).ToArray();
        var series = new bool[c.Count];
        var first = c.Select(x => x.Time).ToArray(); // je Wurzel: früheste und späteste Aufnahme der Gruppe
        var last = c.Select(x => x.Time).ToArray();

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        bool Union(int a, int b, bool limitSpan)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb)
            {
                return true;
            }

            var from = Math.Min(first[ra], first[rb]);
            var to = Math.Max(last[ra], last[rb]);
            if (limitSpan && to - from > SeriesSpanSeconds)
            {
                return false; // keine endlose Kette (Intervallaufnahmen): eine Serie umfasst höchstens wenige Minuten
            }

            var root = Math.Min(ra, rb);
            parent[Math.Max(ra, rb)] = root;
            first[root] = from;
            last[root] = to;
            return true;
        }

        // gleiche Inhalts-Prüfsumme = dieselbe Datei
        var byContent = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < c.Count; i++)
        {
            if (c[i].ContentHash is { Length: > 0 } hash)
            {
                var key = Convert.ToHexString(hash);
                if (byContent.TryGetValue(key, out var original))
                {
                    Union(original, i, limitSpan: false);
                }
                else
                {
                    byContent[key] = i;
                }
            }
        }

        // Serien: ähnlicher pHash und kurz nacheinander aufgenommen
        var byTime = Enumerable.Range(0, c.Count).Where(i => c[i].ImageHash is not null && !c[i].LowDetail)
            .OrderBy(i => c[i].Time).ThenBy(i => c[i].Position).ToList();
        for (var a = 0; a < byTime.Count; a++)
        {
            var i = byTime[a];
            for (var b = a + 1; b < byTime.Count && b <= a + SeriesWindow; b++)
            {
                var j = byTime[b];
                if (c[j].Time - c[i].Time > SeriesSeconds)
                {
                    break;
                }

                if (BitOperations.PopCount(c[i].ImageHash!.Value ^ c[j].ImageHash!.Value) <= threshold && Union(i, j, limitSpan: true))
                {
                    series[i] = series[j] = true;
                }
            }
        }

        var members = new Dictionary<int, List<int>>();
        for (var i = 0; i < c.Count; i++)
        {
            var root = Find(i);
            if (!members.TryGetValue(root, out var list))
            {
                members[root] = list = [];
            }

            list.Add(i);
        }

        return members.Values
            .Select(list => new Group(
                list.OrderByDescending(i => total[i]).ThenBy(i => c[i].Position).First(),
                list.Count,
                list.Any(i => series[i]),
                DistinctShots(c, list)))
            .ToList();
    }

    /// <summary>Verschiedene Aufnahmen einer Gruppe (byte-gleiche Kopien zählen einmal).</summary>
    private static int DistinctShots(IReadOnlyList<HighlightCandidate> c, List<int> members)
        => members.Where(i => c[i].ContentHash is not { Length: > 0 }).Count()
           + members.Where(i => c[i].ContentHash is { Length: > 0 }).Select(i => Convert.ToHexString(c[i].ContentHash!)).Distinct().Count();

    // ----- Verteilung über die Zeit -----

    private static List<Group> Distribute(IReadOnlyList<HighlightCandidate> c, List<Group> groups, double[] total, int k)
    {
        var ordered = groups.OrderByDescending(g => total[g.Best]).ThenBy(g => c[g.Best].Position).ToList();
        if (k >= ordered.Count)
        {
            return ordered;
        }

        static long Hour(long time) => (long)Math.Floor(time / 3600.0);
        var perHour = ordered.GroupBy(g => Hour(c[g.Best].Time)).ToDictionary(x => x.Key, x => x.Count());
        var quota = perHour.ToDictionary(x => x.Key, x => Math.Max(1, (int)Math.Ceiling(k * (double)x.Value / ordered.Count)));
        var taken = perHour.Keys.ToDictionary(h => h, _ => 0);
        var chosen = new List<Group>(k);
        var rest = new List<Group>();
        foreach (var g in ordered)
        {
            var hour = Hour(c[g.Best].Time);
            if (chosen.Count < k && taken[hour] < quota[hour])
            {
                chosen.Add(g);
                taken[hour]++;
            }
            else
            {
                rest.Add(g);
            }
        }

        chosen.AddRange(rest.Take(k - chosen.Count));
        return chosen;
    }

    // ----- „warum ausgewählt“ -----

    private static string Reason(IReadOnlyList<HighlightCandidate> c, Scores s, Group g)
    {
        var x = c[g.Best];
        var index = g.Best;
        var parts = new List<string>(4);
        if (g.Size > 1)
        {
            parts.Add(g.IsSeries && g.Shots > 1
                ? $"beste aus einer Serie von {g.Shots.ToString(CultureInfo.InvariantCulture)}"
                : $"beste von {g.Size.ToString(CultureInfo.InvariantCulture)} gleichen");
        }

        if (x.Sharpness is { } sharp && sharp >= 0.55 && s.SharpRank[index] >= 0.6)
        {
            parts.Add("scharf");
        }

        if (Usage.UsageScoring.Reason(x.Usage) is { } usage)
        {
            parts.Add(usage); // „oft angesehen“, „2× geteilt“ (ADR-0072) — vor Personen/Favorit, damit es im Tooltip steht
        }

        if (x.ConfirmedFaces == 1)
        {
            parts.Add(x.FaceQuality >= 0.7 ? "Gesicht gut sichtbar" : "1 Person");
        }
        else if (x.ConfirmedFaces > 1)
        {
            parts.Add($"{x.ConfirmedFaces.ToString(CultureInfo.InvariantCulture)} Personen");
        }

        if (x.IsFavorite)
        {
            parts.Add("Favorit");
        }

        if (x.Rating >= 3)
        {
            parts.Add($"{Math.Min(x.Rating, 5).ToString(CultureInfo.InvariantCulture)} Sterne");
        }

        if (x.Aesthetic is { } a && a >= 0.6 && s.AestheticRank[index] >= 0.75)
        {
            parts.Add("starke Bildwirkung");
        }

        if (x.Exposure is { } e && e >= 0.85)
        {
            parts.Add("gut belichtet");
        }

        if (x.IsVideo)
        {
            parts.Add("Video");
        }

        return parts.Count == 0 ? "gute Gesamtwertung" : string.Join(" · ", parts.Take(3));
    }

    /// <summary>Kandidaten im unteren Viertel (unter dem 25-%-Wert, nur wenn es Unterschiede gibt) oder unter der festen Grenze.</summary>
    private static HashSet<int> LowSet(List<int> items, Func<int, double> value, double absoluteFloor)
    {
        var set = new HashSet<int>();
        if (items.Count == 0)
        {
            return set;
        }

        var sorted = items.Select(value).Order().ToList();
        var cutoff = sorted[(int)Math.Floor((sorted.Count - 1) * 0.25)];
        var max = sorted[^1];
        foreach (var i in items)
        {
            var v = value(i);
            if (v < absoluteFloor || (v <= cutoff && v < max))
            {
                set.Add(i);
            }
        }

        return set;
    }

    /// <summary>Die Kandidaten mit dem kleinsten Wert (leer, wenn alle gleich sind).</summary>
    private static HashSet<int> Worst(List<int> items, Func<int, double> value)
    {
        if (items.Count < 2)
        {
            return [];
        }

        var min = items.Min(value);
        var max = items.Max(value);
        return min < max ? items.Where(i => value(i) == min).ToHashSet() : [];
    }
}
