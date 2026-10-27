using System.Collections.Frozen;

namespace Lumora.Services.Faces.Recognition;

/// <summary>
/// Unveränderliches Ergebnis von <see cref="FaceRecognizer.Train"/>. Beispiele liegen nach Person gruppiert in einer
/// zusammenhängenden Matrix; alle Lesezugriffe nutzen nur lokale Puffer und sind damit thread-sicher.
/// </summary>
internal sealed class FaceModel : IFaceModel
{
    /// <summary>Unter dieser k-NN-Ähnlichkeit zählt der k-NN-Anteil des Scores 0 (verschiedene Personen: typisch ≤ 0,2).</summary>
    internal const float SimilarityFloor = 0.2f;

    /// <summary>Ab dieser k-NN-Ähnlichkeit zählt der k-NN-Anteil 1 (gleiche Person: typisch 0,6–0,8).</summary>
    internal const float SimilarityCeiling = 0.7f;

    private static readonly int[] NoPersons = [];

    private readonly EmbeddingRows _rows;
    private readonly long[] _faceIds;
    private readonly int[] _personIds;
    private readonly int[] _personStart;
    private readonly int[] _personCount;
    private readonly FrozenDictionary<int, int> _personIndex;
    private readonly FrozenDictionary<long, int> _exampleByFaceId;
    private readonly float[] _centroids;
    private readonly float[] _weights;
    private readonly float[] _bias;
    private readonly bool[] _hasLinear;
    private readonly float[] _typicalSimilarity;
    private readonly float[] _similarityMad;
    private readonly FrozenDictionary<long, int[]> _rejections;

    internal FaceModel(
        FaceRecognizerOptions options,
        EmbeddingRows rows,
        long[] faceIds,
        int[] personIds,
        int[] personStart,
        int[] personCount,
        float[] centroids,
        float[] weights,
        float[] bias,
        bool[] hasLinear,
        float[] typicalSimilarity,
        float[] similarityMad,
        FrozenDictionary<long, int[]> rejections,
        int ignoredCount)
    {
        Options = options;
        _rows = rows;
        _faceIds = faceIds;
        _personIds = personIds;
        _personStart = personStart;
        _personCount = personCount;
        _centroids = centroids;
        _weights = weights;
        _bias = bias;
        _hasLinear = hasLinear;
        _typicalSimilarity = typicalSimilarity;
        _similarityMad = similarityMad;
        _rejections = rejections;
        IgnoredCount = ignoredCount;
        _personIndex = personIds.Select((id, index) => (id, index)).ToFrozenDictionary(p => p.id, p => p.index);
        _exampleByFaceId = faceIds.Select((id, index) => (id, index)).ToFrozenDictionary(p => p.id, p => p.index);
    }

    public FaceRecognizerOptions Options { get; }

    public int Dimension => _rows.Dimension;

    public IReadOnlyCollection<int> PersonIds => _personIds;

    public int ExampleCount => _rows.Count;

    public int IgnoredCount { get; }

    internal int PersonCount => _personIds.Length;

    public int ExampleCountOf(int personId) =>
        _personIndex.TryGetValue(personId, out var index) ? _personCount[index] : 0;

    internal bool TryGetPersonIndex(int personId, out int index) => _personIndex.TryGetValue(personId, out index);

    internal int PersonIdAt(int index) => _personIds[index];

    internal float TypicalSimilarity(int personIndex) => _typicalSimilarity[personIndex];

    internal float SimilarityMad(int personIndex) => _similarityMad[personIndex];

    /// <summary>Anzahl Beispiele der Person ohne das Gesicht <paramref name="faceId"/> selbst.</summary>
    internal int ExampleCountExcluding(int personIndex, long faceId)
    {
        var count = _personCount[personIndex];
        if (_exampleByFaceId.TryGetValue(faceId, out var row) && IsRowOfPerson(row, personIndex))
        {
            count--;
        }

        return count;
    }

    public FaceSuggestion Suggest(FaceSample face, IReadOnlyCollection<int>? excludedPersons = null)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (_personIds.Length == 0 || face.Embedding.Length != Dimension)
        {
            return FaceSuggestion.None;
        }

        var query = new float[Dimension];
        if (!VectorMath.TryNormalize(face.Embedding.Span, query))
        {
            return FaceSuggestion.None;
        }

        var excluded = _rejections.TryGetValue(face.FaceId, out var rejected) ? rejected : NoPersons;
        var candidates = SelectCandidates(query, excluded, excludedPersons);
        if (candidates.Count == 0)
        {
            return FaceSuggestion.None;
        }

        var selfRow = _exampleByFaceId.TryGetValue(face.FaceId, out var r) ? r : -1;
        var scored = new List<(int PersonIndex, float Score, float Similarity)>(candidates.Count);
        var qualityFactor = face.Quality < Options.MinQuality ? Options.LowQualityScoreFactor : 1f;
        foreach (var personIndex in candidates)
        {
            var similarity = KnnSimilarity(query, personIndex, selfRow);
            if (float.IsNaN(similarity))
            {
                continue; // einziges Beispiel war das Gesicht selbst
            }

            scored.Add((personIndex, CombinedScore(query, personIndex, similarity) * qualityFactor, similarity));
        }

        if (scored.Count == 0)
        {
            return FaceSuggestion.None;
        }

        scored.Sort((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : _personIds[a.PersonIndex].CompareTo(_personIds[b.PersonIndex]);
        });

        var best = scored[0];
        var unknown = best.Similarity < Options.UnknownSimilarity || best.Score < Options.MinScore;
        var alternatives = new List<PersonScore>(Options.MaxAlternatives);
        for (var i = unknown ? 0 : 1; i < scored.Count && alternatives.Count < Options.MaxAlternatives; i++)
        {
            alternatives.Add(new PersonScore(_personIds[scored[i].PersonIndex], scored[i].Score));
        }

        return new FaceSuggestion(
            unknown ? null : _personIds[best.PersonIndex],
            best.Score,
            best.Similarity,
            alternatives,
            unknown);
    }

    public IReadOnlyList<FaceSuggestion> SuggestMany(IReadOnlyList<FaceSample> faces, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(faces);
        var result = new FaceSuggestion[faces.Count];
        Parallel.For(0, faces.Count, Background.BackgroundCpu.Options(cancellationToken), i =>
            result[i] = Suggest(faces[i]));
        return result;
    }

    /// <summary>
    /// Mittel der k ähnlichsten Beispiele der Person (k = min(<see cref="FaceRecognizerOptions.NeighborCount"/>,
    /// Beispiele)); die Zeile <paramref name="skipRow"/> (das Gesicht selbst) zählt nicht. NaN ohne Beispiele.
    /// </summary>
    internal float KnnSimilarity(ReadOnlySpan<float> query, int personIndex, int skipRow)
    {
        var start = _personStart[personIndex];
        var end = start + _personCount[personIndex];
        var available = _personCount[personIndex] - (skipRow >= start && skipRow < end ? 1 : 0);
        var k = Math.Min(Options.NeighborCount, available);
        if (k <= 0)
        {
            return float.NaN;
        }

        Span<float> top = stackalloc float[k];
        top.Fill(float.NegativeInfinity);
        for (var row = start; row < end; row++)
        {
            if (row == skipRow)
            {
                continue;
            }

            var similarity = VectorMath.Dot(query, _rows.Row(row));
            if (similarity <= top[k - 1])
            {
                continue;
            }

            // Einfügen in absteigend sortierte Liste (k ist klein).
            var position = k - 1;
            while (position > 0 && top[position - 1] < similarity)
            {
                top[position] = top[position - 1];
                position--;
            }

            top[position] = similarity;
        }

        var sum = 0f;
        foreach (var value in top)
        {
            sum += value;
        }

        return sum / k;
    }

    /// <summary>Ensemble: Mittel aus k-NN-Anteil (0…1 zwischen Floor und Ceiling) und Wahrscheinlichkeit des linearen Klassifikators.</summary>
    internal float CombinedScore(ReadOnlySpan<float> query, int personIndex, float knnSimilarity)
    {
        var knn = Math.Clamp((knnSimilarity - SimilarityFloor) / (SimilarityCeiling - SimilarityFloor), 0f, 1f);
        if (!Options.UseLinearClassifier || !_hasLinear[personIndex])
        {
            return knn;
        }

        return 0.5f * knn + 0.5f * LinearProbability(query, personIndex);
    }

    internal float LinearProbability(ReadOnlySpan<float> query, int personIndex) =>
        VectorMath.Sigmoid(VectorMath.Dot(query, new ReadOnlySpan<float>(_weights, personIndex * Dimension, Dimension)) + _bias[personIndex]);

    /// <summary>Kandidaten-Personen: alle erlaubten oder (bei vielen Personen) die nächsten Zentren plus beste lineare Scores.</summary>
    internal List<int> SelectCandidates(ReadOnlySpan<float> query, int[] rejected, IReadOnlyCollection<int>? excludedPersons)
    {
        var personCount = _personIds.Length;
        var result = new List<int>(Math.Min(personCount, Options.CandidatePersons + 8));
        bool Allowed(int index) =>
            Array.IndexOf(rejected, _personIds[index]) < 0
            && (excludedPersons is null || !excludedPersons.Contains(_personIds[index]));

        if (personCount <= Options.CandidatePersons)
        {
            for (var i = 0; i < personCount; i++)
            {
                if (Allowed(i))
                {
                    result.Add(i);
                }
            }

            return result;
        }

        var byCentroid = new (float Score, int Index)[personCount];
        var byLinear = Options.UseLinearClassifier ? new (float Score, int Index)[personCount] : null;
        for (var i = 0; i < personCount; i++)
        {
            byCentroid[i] = (Allowed(i) ? VectorMath.Dot(query, new ReadOnlySpan<float>(_centroids, i * Dimension, Dimension)) : float.NegativeInfinity, i);
            if (byLinear is not null)
            {
                byLinear[i] = (Allowed(i) && _hasLinear[i] ? LinearProbability(query, i) : float.NegativeInfinity, i);
            }
        }

        Comparison<(float Score, int Index)> descending = (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        };
        Array.Sort(byCentroid, descending);
        var seen = new HashSet<int>();
        foreach (var (score, index) in byCentroid.Take(Options.CandidatePersons))
        {
            if (!float.IsNegativeInfinity(score) && seen.Add(index))
            {
                result.Add(index);
            }
        }

        if (byLinear is not null)
        {
            Array.Sort(byLinear, descending);
            foreach (var (score, index) in byLinear.Take(Math.Max(1, Options.CandidatePersons / 3)))
            {
                if (!float.IsNegativeInfinity(score) && seen.Add(index))
                {
                    result.Add(index);
                }
            }
        }

        return result;
    }

    internal int SelfRow(long faceId) => _exampleByFaceId.TryGetValue(faceId, out var row) ? row : -1;

    private bool IsRowOfPerson(int row, int personIndex) =>
        row >= _personStart[personIndex] && row < _personStart[personIndex] + _personCount[personIndex];
}
