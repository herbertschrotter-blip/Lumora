using System.Diagnostics;
using OpenCvSharp;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Worker.Decoding;
using Lumora.Imaging.Worker.Output;
using Lumora.Imaging.Worker.Panorama;

namespace Lumora.Imaging.Worker;

/// <summary>
/// Arbeiten des Hilfsprozesses für Panoramen. Die Vorschau-Sitzung behält die verkleinert gelesenen Quellen und die letzte
/// Ausrichtung im Speicher: Umschalten der Aufnahmeart setzt nur neu zusammen, Ändern von Tonwert-Angleich oder
/// Farbverstärkung tont nur das letzte Bild neu (≈ 0,1 s statt Sekunden). „Rendern“ liest die Quellen frisch in der
/// nötigen Größe und rechnet mit der Ausrichtung aus dem Rezept.
/// </summary>
internal sealed class PanoramaJobs : IDisposable
{
    /// <summary>So oft wird bei einer gekippten Ausrichtung mit einem anderen festen Startwert neu ausgerichtet.</summary>
    public const int MaxAttempts = 3;

    private List<SourceImage>? _sources;
    private AlignmentOutcome? _alignment;
    private LookCache? _look;

    public EstimateResult Estimate(EstimateRequest request, Action<WorkerStep, int, int> progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        DisposeLook();
        DisposeSources();
        _alignment = null;
        _sources = ReadSources(request.Sources, request.DecodeEdge, progress, ct);
        AlignmentOutcome? best = null;
        var attempts = 0;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            var outcome = PanoAligner.Align(_sources, request.WorkMegapixels, request.Seed + attempt, progress, ct);
            if (best is null || Score(outcome) < Score(best))
            {
                best = outcome;
            }

            if (outcome.Problems.Count == 0)
            {
                break;
            }
        }

        _alignment = best!;
        var notes = new List<string>(best!.Notes);
        if (attempts > 1)
        {
            notes.Add("retried:" + (attempts - 1));
        }

        return new EstimateResult
        {
            Id = request.Id,
            Alignment = ToSpec(best, _sources),
            SuggestedProjection = PanoComposer.Suggest(_sources, best),
            Notes = notes,
            Seconds = clock.Elapsed.TotalSeconds,
            PeakBytes = PeakBytes(),
        };
    }

    public ComposeResult Compose(ComposeRequest request, Action<WorkerStep, int, int> progress, CancellationToken ct)
    {
        if (_sources is null || _alignment is null)
        {
            throw new WorkerException(WorkerErrorCode.BadRequest, "Erst ausrichten, dann zusammensetzen.");
        }

        var clock = Stopwatch.StartNew();
        var key = LookCache.KeyOf(request.Settings);
        if (_look is { } look && look.Key == key)
        {
            // nur Tonwert-Angleich bzw. Farbverstärkung geändert: das gemerkte Bild neu tonen, nicht neu zusammensetzen
            progress(WorkerStep.Finish, 0, 2);
            using var image = look.Untoned.Clone();
            var notes = look.BaseNotes.ToList();
            PanoComposer.ApplyLook(image, look.Reference, request.Settings, notes, ct);
            progress(WorkerStep.Write, 0, 1);
            JpegOutput.Write(image, request.OutputPath, request.JpegQuality);
            return new ComposeResult
            {
                Id = request.Id,
                OutputPath = request.OutputPath,
                Width = image.Width,
                Height = image.Height,
                FullWidth = look.FullWidth,
                FullHeight = look.FullHeight,
                Scale = look.Scale,
                FullPeakBytes = look.FullPeakBytes,
                Check = look.Check,
                Notes = notes,
                Projection = look.Projection,
                Seconds = clock.Elapsed.TotalSeconds,
                PeakBytes = PeakBytes(),
            };
        }

        DisposeLook();
        using var outcome = PanoComposer.Compose(_sources, _alignment, request.Settings, fullQuality: false, ownsSources: false, 0, WorkerLimits.MaxJpegEdge, progress, ct, keepUntoned: true);
        progress(WorkerStep.Write, 0, 1);
        JpegOutput.Write(outcome.Image, request.OutputPath, request.JpegQuality);
        if (outcome.Untoned is { } untoned)
        {
            outcome.Untoned = null; // gehört jetzt dem Merker
            _look = new LookCache(key, untoned, outcome);
        }

        return Result(request.Id, request.OutputPath, outcome, clock);
    }

    public ComposeResult Render(RenderRequest request, Action<WorkerStep, int, int> progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        // Sitzung freigeben: volle Auflösung braucht den Speicher
        DisposeLook();
        DisposeSources();
        _alignment = null;
        var alignment = FromSpec(request.Alignment, request.Sources);
        var full = Math.Min(1.0, request.Settings.Scale > 0 ? request.Settings.Scale : 1.0);
        var decodeEdge = 0;
        if (full < 0.999)
        {
            // nur so groß lesen wie nötig (JPEG dekodiert verkleinert schnell)
            var maxEdge = alignment.Cameras.Max(c => Math.Max(c.Size.Width, c.Size.Height));
            decodeEdge = (int)Math.Ceiling(maxEdge * Math.Min(1.0, full * 1.05));
        }

        var used = alignment.Cameras.Select(c => c.Source).ToHashSet();
        var sources = ReadSources(request.Sources, decodeEdge, progress, ct, used);
        try
        {
            using var outcome = PanoComposer.Compose(sources, alignment, request.Settings, fullQuality: true, ownsSources: true, request.MaxBytes, request.MaxEdge, progress, ct);
            progress(WorkerStep.Write, 0, 1);
            JpegOutput.Write(outcome.Image, request.OutputPath, request.JpegQuality);
            return Result(request.Id, request.OutputPath, outcome, clock);
        }
        finally
        {
            foreach (var s in sources)
            {
                s.Dispose();
            }
        }
    }

    private static ComposeResult Result(int id, string path, ComposeOutcome outcome, Stopwatch clock) => new()
    {
        Id = id,
        OutputPath = path,
        Width = outcome.Image.Width,
        Height = outcome.Image.Height,
        FullWidth = outcome.FullWidth,
        FullHeight = outcome.FullHeight,
        Scale = outcome.Scale,
        FullPeakBytes = outcome.FullPeakBytes,
        Check = outcome.Check,
        Notes = outcome.Notes,
        Projection = outcome.Projection,
        Seconds = clock.Elapsed.TotalSeconds,
        PeakBytes = PeakBytes(),
    };

    private static double Score(AlignmentOutcome a) => a.Problems.Count * 1e6 + (double.IsFinite(a.ResidualMedianPx) ? a.ResidualMedianPx : 1e5);

    /// <summary>Quellen lesen (parallel, höchstens 4 zugleich); <paramref name="only"/>: nur diese Stellen (Rendern), übrige leer.</summary>
    private static List<SourceImage> ReadSources(IReadOnlyList<SourceSpec> specs, int decodeEdge, Action<WorkerStep, int, int> progress, CancellationToken ct, HashSet<int>? only = null)
    {
        var result = new SourceImage?[specs.Count];
        var done = 0;
        try
        {
            Parallel.For(0, specs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, WorkerThreads.Count), CancellationToken = ct }, i =>
            {
                var spec = specs[i];
                if (only is not null && !only.Contains(i))
                {
                    return;
                }

                var hints = new SourceMeta { Focal35 = spec.Focal35, GimbalYaw = spec.GimbalYaw, GimbalPitch = spec.GimbalPitch, GimbalRoll = spec.GimbalRoll };
                result[i] = WicSourceDecoder.Read(spec.Index, spec.Path, decodeEdge, hints);
                progress(WorkerStep.Load, Interlocked.Increment(ref done), only?.Count ?? specs.Count);
            });
        }
        catch
        {
            foreach (var s in result)
            {
                s?.Dispose();
            }

            throw;
        }

        // nicht benötigte Stellen (Rendern ohne verworfene Quellen) bekommen ein leeres Bild, damit die Indizes stimmen
        return result.Select((s, i) => s ?? new SourceImage { Index = specs[i].Index, Image = new Mat(), FullSize = new OpenCvSharp.Size(1, 1), Meta = new SourceMeta() }).ToList();
    }

    private static AlignmentSpec ToSpec(AlignmentOutcome a, IReadOnlyList<SourceImage> sources) => new()
    {
        Cameras = a.Cameras.Select(c => new CameraSpec
        {
            Source = sources[c.Source].Index,
            Width = c.Size.Width,
            Height = c.Size.Height,
            F = c.F,
            K1 = c.K1,
            K2 = c.K2,
            Ppx = c.Ppx,
            Ppy = c.Ppy,
            R = c.R.ToArray(),
        }).ToList(),
        Dropped = a.Dropped.Select(i => sources[i].Index).ToList(),
        Is360 = a.Is360,
        UsedGimbal = a.UsedGimbal,
        ResidualMedianPx = a.ResidualMedianPx,
        ResidualP90Px = a.ResidualP90Px,
        Photometric = a.Photometric is { E.Length: > 0 } p
            ? new PhotometricSpec { E = p.E, Wr = p.Wr, Wb = p.Wb, B1 = p.B1, B2 = p.B2, B3 = p.B3, Applied = p.Applicable, RmsBefore = p.RmsBefore, RmsAfter = p.RmsAfter }
            : null,
        Similarity = a.Similarity?.Select((s, k) => new SimilaritySpec { Source = sources[a.Cameras[k].Source].Index, A = s.A, B = s.B, Tx = s.Tx, Ty = s.Ty }).ToList(),
        Seed = a.Seed,
    };

    /// <summary>Ausrichtung aus dem Rezept; Quellen werden über ihre Stelle in der Auswahl (SourceSpec.Index) zugeordnet.</summary>
    internal static AlignmentOutcome FromSpec(AlignmentSpec spec, IReadOnlyList<SourceSpec> sources)
    {
        var byIndex = sources.Select((s, i) => (s.Index, i)).ToDictionary(t => t.Index, t => t.i);
        var cams = new List<PanoCamera>();
        foreach (var c in spec.Cameras)
        {
            if (!byIndex.TryGetValue(c.Source, out var local))
            {
                throw new WorkerException(WorkerErrorCode.BadRequest, "Rezept passt nicht zu den Quellen.");
            }

            cams.Add(new PanoCamera { Source = local, Size = new OpenCvSharp.Size(c.Width, c.Height), F = c.F, K1 = c.K1, K2 = c.K2, Ppx = c.Ppx, Ppy = c.Ppy, R = M3.From(c.R) });
        }

        if (cams.Count < 2)
        {
            throw new WorkerException(WorkerErrorCode.NotEnoughOverlap, "Rezept mit weniger als zwei Kameras.");
        }

        Photometric? photo = null;
        if (spec.Photometric is { } p && p.E.Length == cams.Count)
        {
            photo = new Photometric { E = p.E, Wr = p.Wr, Wb = p.Wb, B1 = p.B1, B2 = p.B2, B3 = p.B3, RmsBefore = p.RmsBefore, RmsAfter = p.Applied ? Math.Min(p.RmsAfter, Photometric.MaxRmsToApply / 2) : double.PositiveInfinity };
        }

        List<(double, double, double, double)>? sim = null;
        if (spec.Similarity is { } s && s.Count == cams.Count)
        {
            sim = s.Select(x => (x.A, x.B, x.Tx, x.Ty)).ToList();
        }

        return new AlignmentOutcome
        {
            Cameras = cams,
            Dropped = spec.Dropped.Where(byIndex.ContainsKey).Select(i => byIndex[i]).ToArray(),
            Is360 = spec.Is360,
            UsedGimbal = spec.UsedGimbal,
            ResidualMedianPx = spec.ResidualMedianPx,
            ResidualP90Px = spec.ResidualP90Px,
            Photometric = photo,
            Similarity = sim,
            Seed = spec.Seed,
        };
    }

    private static long PeakBytes()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PeakWorkingSet64;
    }

    private void DisposeSources()
    {
        if (_sources is null)
        {
            return;
        }

        foreach (var s in _sources)
        {
            s.Dispose();
        }

        _sources = null;
        GC.Collect();
    }

    private void DisposeLook()
    {
        _look?.Dispose();
        _look = null;
    }

    public void Dispose()
    {
        DisposeLook();
        DisposeSources();
    }

    /// <summary>Letzte Vorschau vor Tonwert-Angleich und Farbverstärkung, mit allem, was das Neu-Tonen braucht.</summary>
    private sealed class LookCache(ComposeSpec key, Mat untoned, ComposeOutcome outcome) : IDisposable
    {
        public ComposeSpec Key { get; } = key;

        public Mat Untoned { get; } = untoned;

        public ToneReference? Reference { get; } = outcome.ToneReference;

        public List<string> BaseNotes { get; } = outcome.BaseNotes.ToList();

        public int FullWidth { get; } = outcome.FullWidth;

        public int FullHeight { get; } = outcome.FullHeight;

        public double Scale { get; } = outcome.Scale;

        public long FullPeakBytes { get; } = outcome.FullPeakBytes;

        public QualityCheck Check { get; } = outcome.Check;

        public WireProjection Projection { get; } = outcome.Projection;

        /// <summary>Alles außer dem „Look“ muss gleich sein, damit das gemerkte Bild passt.</summary>
        public static ComposeSpec KeyOf(ComposeSpec spec) => spec with { ToneMatch = 0, ColorBoost = 0 };

        public void Dispose() => Untoned.Dispose();
    }
}
