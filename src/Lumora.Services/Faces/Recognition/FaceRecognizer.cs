using System.Collections.Frozen;

namespace Lumora.Services.Faces.Recognition;

/// <summary>
/// Lernen aus bestätigten Gesichtern wie digiKam 8.6/8.7 (ADR-0036):
/// <list type="bullet">
/// <item><b>Nur bestätigte Gesichter</b> (<see cref="FaceSample.ConfirmedPersonId"/>) mit Qualität ≥ Mindestqualität
/// werden Lernbeispiele — eigene Vorschläge fließen nie ein.</item>
/// <item><b>k-NN je Person</b>: Mittel der k ähnlichsten Beispiele — ein einzelnes falsches Beispiel zieht den Mittelwert
/// nicht über die Schwelle (k = 3).</item>
/// <item><b>Linearer Klassifikator je Person</b> (<see cref="LinearPersonClassifier"/>); Score = Mittel beider Anteile.</item>
/// <item><b>Distanzprüfung</b>: ist die k-NN-Ähnlichkeit des besten Kandidaten zu gering → „unbekannt“.</item>
/// <item><b>Ablehnungen</b>: abgelehnte Personen werden für dieses Gesicht übersprungen → nächstbester Kandidat.</item>
/// <item><b>Neu lernen</b> ist immer ein vollständiger, schneller Neuaufbau aus den übergebenen Beispielen
/// (10 000 Beispiele / 200 Personen deutlich unter 2 s, siehe docs/spikes/10-gesichter-lernen.md).</item>
/// </list>
/// </summary>
public sealed class FaceRecognizer : IFaceRecognizer
{
    /// <summary>Für die Personen-Statistik (typische k-NN-Ähnlichkeit) höchstens so viele Beispiele je Person.</summary>
    private const int MaxStatisticsSamples = 64;

    /// <summary>Nächste Personen (nach Zentrum) als Quelle schwieriger Gegenbeispiele.</summary>
    private const int NearestPersonsForNegatives = 8;

    public IFaceModel Train(
        IReadOnlyList<FaceSample> confirmed,
        IReadOnlyCollection<FaceRejection> rejections,
        FaceRecognizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(rejections);
        options ??= FaceRecognizerOptions.Default;
        Validate(options);

        // Reihenfolge: Person, dann FaceId — Beispiele einer Person liegen zusammen; unabhängig von der Eingabe-Reihenfolge.
        var candidates = new List<int>(confirmed.Count);
        for (var i = 0; i < confirmed.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(confirmed[i], nameof(confirmed));
            if (confirmed[i].ConfirmedPersonId is not null)
            {
                candidates.Add(i);
            }
        }

        candidates.Sort((a, b) =>
        {
            var c = confirmed[a].ConfirmedPersonId!.Value.CompareTo(confirmed[b].ConfirmedPersonId!.Value);
            if (c != 0)
            {
                return c;
            }

            c = confirmed[a].FaceId.CompareTo(confirmed[b].FaceId);
            return c != 0 ? c : a.CompareTo(b);
        });

        var dimension = EmbeddingRows.ResolveDimension(confirmed);
        var seenFaces = new HashSet<long>();
        var rows = EmbeddingRows.Build(
            confirmed, candidates, dimension,
            s => s.Quality >= options.MinQuality && seenFaces.Add(s.FaceId),
            out var accepted, out var ignored);

        var faceIds = new long[rows.Count];
        var personIds = new List<int>();
        var personStart = new List<int>();
        var personCount = new List<int>();
        for (var row = 0; row < rows.Count; row++)
        {
            var sample = confirmed[accepted[row]];
            faceIds[row] = sample.FaceId;
            var personId = sample.ConfirmedPersonId!.Value;
            if (personIds.Count == 0 || personIds[^1] != personId)
            {
                personIds.Add(personId);
                personStart.Add(row);
                personCount.Add(0);
            }

            personCount[^1]++;
        }

        var rejectionMap = rejections
            .GroupBy(r => r.FaceId)
            .ToFrozenDictionary(g => g.Key, g => g.Select(r => r.PersonId).Distinct().Order().ToArray());

        var persons = personIds.Count;
        var starts = personStart.ToArray();
        var counts = personCount.ToArray();
        var centroids = new float[persons * dimension];
        var weights = new float[persons * dimension];
        var bias = new float[persons];
        var hasLinear = new bool[persons];
        var typical = new float[persons];
        var mad = new float[persons];
        var parallel = Background.BackgroundCpu.Options(cancellationToken); // Training im Hintergrund: höchstens die Hälfte der Kerne

        Parallel.For(0, persons, parallel, p =>
        {
            var centroid = centroids.AsSpan(p * dimension, dimension);
            for (var row = starts[p]; row < starts[p] + counts[p]; row++)
            {
                VectorMath.AddScaled(centroid, rows.Row(row), 1f);
            }

            VectorMath.NormalizeInPlace(centroid);
        });

        var nearest = NearestPersons(centroids, persons, dimension, parallel);
        var model = new FaceModel(
            options, rows, faceIds, personIds.ToArray(), starts, counts, centroids, weights, bias, hasLinear, typical, mad,
            rejectionMap, ignored.Count);

        Parallel.For(0, persons, parallel, p =>
        {
            if (options.UseLinearClassifier)
            {
                hasLinear[p] = LinearPersonClassifier.Train(
                    rows, p, starts, counts, nearest[p], options, personIds[p],
                    weights.AsSpan(p * dimension, dimension), out bias[p]);
            }

            (typical[p], mad[p]) = PersonStatistics(model, rows, starts[p], counts[p], p);
        });

        // Das Modell teilt die jetzt gefüllten Arrays (Gewichte, Statistik) und ist damit fertig.
        return model;
    }

    public OutlierCheck CheckOutlier(FaceSample face, int personId, IFaceModel model)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(model);
        if (model is not FaceModel faceModel)
        {
            throw new ArgumentException("Modell stammt nicht von FaceRecognizer.Train.", nameof(model));
        }

        var options = faceModel.Options;
        if (faceModel.PersonCount > 0 && face.Embedding.Length != faceModel.Dimension)
        {
            return new OutlierCheck(true, 0f, 0f, OutlierReason.InvalidEmbedding,
                "Der Fingerabdruck dieses Gesichts ist ungültig (falsche Länge).");
        }

        var query = new float[face.Embedding.Length];
        if (!VectorMath.TryNormalize(face.Embedding.Span, query))
        {
            return new OutlierCheck(true, 0f, 0f, OutlierReason.InvalidEmbedding,
                "Der Fingerabdruck dieses Gesichts ist ungültig (leer).");
        }

        var lowQuality = face.Quality < options.MinQuality;
        if (!faceModel.TryGetPersonIndex(personId, out var personIndex)
            || faceModel.ExampleCountExcluding(personIndex, face.FaceId) == 0)
        {
            return lowQuality
                ? new OutlierCheck(false, 0f, 0f, OutlierReason.LowQuality, LowQualityText)
                : new OutlierCheck(false, 0f, 0f, OutlierReason.FirstExample,
                    "Erstes Beispiel dieser Person — nichts zu vergleichen.");
        }

        var selfRow = faceModel.SelfRow(face.FaceId);
        var similarity = faceModel.KnnSimilarity(query, personIndex, selfRow);
        var typicalSimilarity = faceModel.TypicalSimilarity(personIndex);
        var examples = faceModel.ExampleCountExcluding(personIndex, face.FaceId);

        // Passt eine andere Person deutlich besser? (gleiche Vorauswahl wie beim Vorschlag)
        int? closer = null;
        var closerSimilarity = float.NegativeInfinity;
        foreach (var other in faceModel.SelectCandidates(query, [personId], null))
        {
            var otherSimilarity = faceModel.KnnSimilarity(query, other, selfRow);
            if (otherSimilarity > closerSimilarity)
            {
                closerSimilarity = otherSimilarity;
                closer = faceModel.PersonIdAt(other);
            }
        }

        if (closer is not null && closerSimilarity >= options.UnknownSimilarity
            && closerSimilarity >= similarity + options.CloserPersonMargin)
        {
            return new OutlierCheck(true, similarity, typicalSimilarity, OutlierReason.CloserToOtherPerson,
                $"Dieses Gesicht ähnelt einer anderen Person deutlich mehr ({closerSimilarity:0.00} statt {similarity:0.00}). Wirklich zuordnen?",
                closer);
        }

        if (similarity < options.OutlierMinSimilarity)
        {
            return new OutlierCheck(true, similarity, typicalSimilarity, OutlierReason.FarFromExamples,
                $"Dieses Gesicht ähnelt den bisherigen Beispielen der Person kaum ({similarity:0.00}). Wirklich zuordnen?");
        }

        if (examples >= 5)
        {
            var z = (typicalSimilarity - similarity) / (1.4826f * faceModel.SimilarityMad(personIndex) + 0.02f);
            if (z >= options.OutlierRobustZ)
            {
                return new OutlierCheck(true, similarity, typicalSimilarity, OutlierReason.UnusualForPerson,
                    $"Dieses Gesicht passt deutlich schlechter zu der Person als ihre bisherigen Beispiele ({similarity:0.00}, üblich {typicalSimilarity:0.00}). Wirklich zuordnen?");
            }
        }

        return lowQuality
            ? new OutlierCheck(false, similarity, typicalSimilarity, OutlierReason.LowQuality, LowQualityText)
            : new OutlierCheck(false, similarity, typicalSimilarity, OutlierReason.Fits, "Passt zu den bisherigen Beispielen.");
    }

    private const string LowQualityText =
        "Die Qualität dieses Gesichts ist gering — es wird zugeordnet, aber nicht als Lernbeispiel genutzt.";

    private static void Validate(FaceRecognizerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.NeighborCount, 1, nameof(options.NeighborCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.NeighborCount, 64, nameof(options.NeighborCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Epochs, 1, nameof(options.Epochs));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CandidatePersons, 1, nameof(options.CandidatePersons));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxAlternatives, nameof(options.MaxAlternatives));
    }

    /// <summary>Je Person die anderen Personen absteigend nach Zentrums-Ähnlichkeit (höchstens einige).</summary>
    private static int[][] NearestPersons(float[] centroids, int persons, int dimension, ParallelOptions parallel)
    {
        var result = new int[persons][];
        Parallel.For(0, persons, parallel, p =>
        {
            var own = new ReadOnlySpan<float>(centroids, p * dimension, dimension);
            var scores = new (float Score, int Index)[Math.Max(0, persons - 1)];
            var w = 0;
            for (var q = 0; q < persons; q++)
            {
                if (q != p)
                {
                    scores[w++] = (VectorMath.Dot(own, new ReadOnlySpan<float>(centroids, q * dimension, dimension)), q);
                }
            }

            Array.Sort(scores, (a, b) =>
            {
                var c = b.Score.CompareTo(a.Score);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            });
            result[p] = scores.Take(NearestPersonsForNegatives).Select(s => s.Index).ToArray();
        });
        return result;
    }

    /// <summary>
    /// Typische Leave-one-out-k-NN-Ähnlichkeit der Beispiele einer Person (Median) und ihre Streuung (MAD) — Maßstab der
    /// Ausreißer-Prüfung. Bei sehr vielen Beispielen auf einer festen Stichprobe.
    /// </summary>
    private static (float Median, float Mad) PersonStatistics(FaceModel model, EmbeddingRows rows, int start, int count, int personIndex)
    {
        if (count < 2)
        {
            return (0f, 0f);
        }

        var samples = Math.Min(count, MaxStatisticsSamples);
        var values = new float[samples];
        for (var s = 0; s < samples; s++)
        {
            var row = start + (int)((long)s * count / samples);
            values[s] = model.KnnSimilarity(rows.Row(row), personIndex, row);
        }

        var median = VectorMath.Median(values);
        return (median, VectorMath.MedianAbsoluteDeviation(values, median));
    }
}
