using OpenCvSharp;
using Lumora.Imaging.Protocol;
using Lumora.Imaging.Worker.Decoding;

namespace Lumora.Imaging.Worker.Panorama;

/// <summary>Ergebnis von Phase 2 (vor dem Schreiben).</summary>
internal sealed class ComposeOutcome : IDisposable
{
    public required Mat Image { get; init; }

    public int FullWidth { get; init; }

    public int FullHeight { get; init; }

    public double Scale { get; init; }

    public long FullPeakBytes { get; init; }

    public required QualityCheck Check { get; init; }

    public List<string> Notes { get; init; } = [];

    public WireProjection Projection { get; init; }

    /// <summary>Vorschau-Sitzung: Bild vor Tonwert-Angleich und Farbverstärkung (zum schnellen Neu-Tonen); sonst null.</summary>
    public Mat? Untoned { get; set; }

    /// <summary>Tonwert-Statistik der Quellen (für das Neu-Tonen), wenn gemessen.</summary>
    public ToneReference? ToneReference { get; init; }

    /// <summary>Hinweise ohne die von Tonwert-Angleich und Farbverstärkung.</summary>
    public List<string> BaseNotes { get; init; } = [];

    /// <summary>
    /// Pixel im Zuschnitt, die kein Einzelbild deckt (gemessen an der echten Deckungsmaske nach dem Überblenden, vor jedem
    /// Füllen). „Gerade Kanten“ garantiert 0; „Lücken füllen“ füllt genau diese Pixel, „Ungeschnitten“ lässt sie schwarz.
    /// </summary>
    public long UncoveredInCrop { get; init; }

    /// <summary>Vorschau-Sitzung bei „Gerade Kanten“: ungeschnittenes Panorama mit schraffierten Lücken (vor dem Look); sonst null.</summary>
    public Mat? Frame { get; set; }

    /// <summary>Lage des Zuschnitts in <see cref="Frame"/>.</summary>
    public Rect FrameCrop { get; init; }

    public void Dispose()
    {
        Image.Dispose();
        Untoned?.Dispose();
        Frame?.Dispose();
    }
}

/// <summary>
/// Phase 2 der eigenen Kette (Port aus Spike 20 Runde 2): Leinwand je Aufnahmeart, Belichtungsausgleich in Blöcken je Kanal,
/// Graph-Cut-Naht mit Schärfe-Term, Entgeisterung im Nahtband, Mehrband-Überblendung, Zuschnitt (gerade Kanten, Lücken
/// füllen, ungeschnitten; Kugel als Horizontstreifen oder 2 : 1), Tonwert-Angleich an die Quellen, Farbverstärkung und die Prüfung vor dem
/// Speichern. Rechnet nur mit der übergebenen Ausrichtung — Umschalten der Aufnahmeart richtet nicht neu aus.
/// </summary>
internal sealed class PanoComposer
{
    /// <summary>Stufen für „größte passende Größe“ (Anteil der vollen Auflösung).</summary>
    public static readonly double[] SizeSteps = [1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.42, 0.35, 0.3, 0.25, 0.2, 0.15, 0.1];

    private const double SharpLambda = 10;
    private const int SeamDilate = 2;
    private const double FillFraction = 0.03;
    private const double DeghostBandPx = 96, FlowScale = 0.5;

    /// <summary>Höchste Höhe des Horizontstreifens der Kugel (Grad): 360° / 80° = 4,5 : 1.</summary>
    private const double MaxBandDeg = 80;

    private readonly IReadOnlyList<SourceImage> _sources;
    private readonly AlignmentOutcome _alignment;
    private readonly ComposeSpec _spec;
    private readonly bool _fullQuality;
    private readonly bool _ownsSources;
    private readonly bool _keepUntoned;
    private readonly long _maxBytes;
    private readonly int _maxEdge;
    private readonly Action<WorkerStep, int, int> _progress;
    private readonly CancellationToken _ct;
    private readonly List<string> _notes = [];
    private List<PanoCamera> _cams = [];
    private Proj _proj;
    private bool _is360;
    private bool _full360;
    private double _bandTop = double.NaN, _bandBottom = double.NaN;
    private List<(int Cam, int Shift, bool Pole)> _specs = [];

    /// <param name="ownsSources">Rendern: Quellen nach dem letzten Teil freigeben (Speicher); Vorschau-Sitzung: behalten.</param>
    /// <param name="keepUntoned">Vorschau-Sitzung: Bild vor Tonwert-Angleich und Farbverstärkung mitliefern (Neu-Tonen).</param>
    private PanoComposer(IReadOnlyList<SourceImage> sources, AlignmentOutcome alignment, ComposeSpec spec, bool fullQuality, bool ownsSources,
        long maxBytes, int maxEdge, Action<WorkerStep, int, int> progress, CancellationToken ct, bool keepUntoned)
    {
        _sources = sources;
        _alignment = alignment;
        _spec = spec;
        _fullQuality = fullQuality;
        _ownsSources = ownsSources;
        _keepUntoned = keepUntoned;
        _maxBytes = maxBytes;
        _maxEdge = maxEdge;
        _progress = progress;
        _ct = ct;
    }

    public static ComposeOutcome Compose(IReadOnlyList<SourceImage> sources, AlignmentOutcome alignment, ComposeSpec spec, bool fullQuality, bool ownsSources,
        long maxBytes, int maxEdge, Action<WorkerStep, int, int> progress, CancellationToken ct, bool keepUntoned = false)
        => new PanoComposer(sources, alignment, spec, fullQuality, ownsSources, maxBytes, maxEdge, progress, ct, keepUntoned).Run();

    /// <summary>
    /// „Look“ nach dem Zuschnitt: Tonwert-Angleich an die Quellen, dann Farbverstärkung (in place). Dieselbe Folge rechnet die
    /// Vorschau-Sitzung beim Neu-Tonen auf dem gemerkten Bild.
    /// </summary>
    public static void ApplyLook(Mat pano, ToneReference? reference, ComposeSpec spec, List<string> notes, CancellationToken ct)
    {
        if (reference is not null && spec.ToneMatch > 0 && ToneMatcher.Match(pano, reference, spec.ToneMatch) is { } tone)
        {
            notes.Add(tone);
        }

        ct.ThrowIfCancellationRequested();
        if (ColorBooster.Apply(pano, spec.ColorBoost, null, ct) is { } color)
        {
            notes.Add(color);
        }
    }

    // ------------------------------------------------------------------ Aufnahmeart

    /// <summary>Vorschlag der Aufnahmeart (ADR-0078 Punkt 6) aus Gimbal, Abdeckung und Bildwinkel.</summary>
    public static WireProjection Suggest(IReadOnlyList<SourceImage> all, AlignmentOutcome a)
    {
        var sources = a.Cameras.Select(c => all[c.Source]).ToList();
        if (sources.All(s => s.Meta.HasGimbal))
        {
            var yaws = sources.Select(s => s.Meta.GimbalYaw!.Value).ToList();
            var pitches = sources.Select(s => s.Meta.GimbalPitch!.Value).ToList();
            var yawSpan = CircularSpanDeg(yaws);
            var pitchSpan = pitches.Max() - pitches.Min();
            if (yawSpan >= 300 && pitches.Min() <= -70)
            {
                return WireProjection.Spherical360;
            }

            if (yawSpan is >= 120 and < 300)
            {
                return WireProjection.Cylindrical;
            }

            if (yawSpan < 15 && pitchSpan >= 30)
            {
                return WireProjection.Vertical;
            }

            return yawSpan >= 300 ? WireProjection.Cylindrical : WireProjection.Perspective;
        }

        if (!sources.Any(s => s.Meta.HasCamera || s.Meta.Focal35 is not null) && a.Similarity is not null
            && double.IsFinite(a.SimilarityResidualPx) && a.SimilarityResidualPx <= Math.Max(2.0, a.ResidualMedianPx * 1.2))
        {
            return WireProjection.ShiftOnly; // Scans: ohne Kamera im EXIF und flach besser erklärt
        }

        // Spannen aus den Blickrichtungen der Bildmitten plus einem Bildwinkel (Grad)
        var yawsDeg = a.Cameras.Select(c => Math.Atan2(c.R.Col(2).X, c.R.Col(2).Z) * 180 / Math.PI).ToList();
        var pitchesDeg = a.Cameras.Select(c => -Math.Asin(Math.Clamp(c.R.Col(2).Y, -1, 1)) * 180 / Math.PI).ToList();
        var hFov = a.Cameras.Select(c => 2 * Math.Atan(c.Size.Width / 2.0 / c.F) * 180 / Math.PI).OrderBy(v => v).ElementAt(a.Cameras.Count / 2);
        var vFov = a.Cameras.Select(c => 2 * Math.Atan(c.Size.Height / 2.0 / c.F) * 180 / Math.PI).OrderBy(v => v).ElementAt(a.Cameras.Count / 2);
        var hSpan = Math.Min(360, CircularSpanDeg(yawsDeg) + hFov);
        var vSpan = pitchesDeg.Max() - pitchesDeg.Min() + vFov;
        var top = pitchesDeg.Max() + vFov / 2;
        var bottom = pitchesDeg.Min() - vFov / 2;
        if (a.Is360)
        {
            return top > 60 || bottom < -60 ? WireProjection.Spherical360 : WireProjection.Cylindrical;
        }

        if (vSpan > 1.3 * hSpan && hSpan < 80)
        {
            return WireProjection.Vertical;
        }

        return hSpan > 100 || vSpan > 100 ? WireProjection.Cylindrical : WireProjection.Perspective;
    }

    private static double CircularSpanDeg(List<double> anglesDeg)
    {
        var a = anglesDeg.Select(v => ((v % 360) + 360) % 360).OrderBy(v => v).ToList();
        var maxGap = 360 - a[^1] + a[0];
        for (var i = 1; i < a.Count; i++)
        {
            maxGap = Math.Max(maxGap, a[i] - a[i - 1]);
        }

        return 360 - maxGap;
    }

    /// <summary>Waagrechte Spanne und senkrechte Lage (Breite oben/unten, Radiant) der Bilder.</summary>
    private static (double HSpan, double VTop, double VBottom) Extent(IReadOnlyList<PanoCamera> cams)
    {
        double a = double.MaxValue, b = double.MinValue, vt = double.MaxValue, vb = double.MinValue;
        foreach (var c in cams)
        {
            var r = ImageRange(c, Proj.Spherical);
            a = Math.Min(a, r.A);
            b = Math.Max(b, r.B);
            vt = Math.Min(vt, r.Vt);
            vb = Math.Max(vb, r.Vb);
        }

        return (Math.Min(2 * Math.PI, b - a), vt, vb);
    }

    // ------------------------------------------------------------------ Ablauf

    private ComposeOutcome Run()
    {
        var projection = _spec.Projection == WireProjection.Auto ? Suggest(_sources, _alignment) : _spec.Projection;
        _cams = _alignment.Cameras.Select(c => c.Clone()).ToList();
        _is360 = _alignment.Is360;
        SetupProjection(projection);
        _specs = PartSpecs();
        if (_specs.Count == 0)
        {
            throw new WorkerException(WorkerErrorCode.NotEnoughOverlap, "Keine Bilder in der Leinwand.");
        }

        // Größe: voll (1) bzw. so groß wie die gelesenen Bilder erlauben; Obergrenzen Pixel, Kante, Speicher
        var full = MakeLayout(1.0);
        var maxEdgeOfImages = _cams.Max(c => Math.Max(c.Size.Width, c.Size.Height));
        if (_proj == Proj.Plane && Math.Max(full.W, full.H) > 8 * maxEdgeOfImages * Math.Sqrt(_cams.Count))
        {
            // Bildwinkel nahe 180°: die Ebene wird unendlich groß
            throw new WorkerException(WorkerErrorCode.BadRequest, "Für diesen Bildwinkel passt „Perspektive“ nicht — „Zylinder“ oder „Kugel 360°“ wählen.");
        }

        if ((double)full.W * full.H > 4e9)
        {
            throw new WorkerException(WorkerErrorCode.BadRequest, "Die Leinwand würde zu groß — eine andere Aufnahmeart wählen.");
        }

        var maxSourceScale = _cams.Max(c => SourceOf(c).Scale);
        var cs = _spec.Scale > 0 ? Math.Min(_spec.Scale, 1.0) : Math.Min(1.0, maxSourceScale);
        if (_spec.MaxOutputPixels > 0 && (double)full.W * full.H * cs * cs > _spec.MaxOutputPixels)
        {
            cs = Math.Sqrt(_spec.MaxOutputPixels / ((double)full.W * full.H));
        }

        var fullPeak = PeakBytes(1.0, full);
        var wanted = cs;
        if (_maxEdge > 0 && Math.Max(full.W, full.H) * cs > _maxEdge)
        {
            cs = StepBelow(_maxEdge / (double)Math.Max(full.W, full.H));
            _notes.Add("size-reduced:jpeg-edge");
        }

        if (_maxBytes > 0)
        {
            var fit = cs;
            while (PeakBytes(fit, null, full) > _maxBytes && fit > SizeSteps[^1])
            {
                fit = StepBelow(fit - 1e-9);
            }

            if (PeakBytes(fit, null, full) > _maxBytes)
            {
                throw new WorkerException(WorkerErrorCode.OutOfMemory, "Zu wenig Arbeitsspeicher.");
            }

            if (fit < cs)
            {
                cs = fit;
                _notes.Add("size-reduced:memory");
            }
        }

        if (cs < wanted - 1e-9)
        {
            _notes.Add("size:" + Math.Round(cs, 3).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Quellen je Kamera (mit photometrischem Modell, wenn es die Überlappungen erklärt)
        var photo = _spec.Exposure && _alignment.Photometric is { Applicable: true } ? _alignment.Photometric : null;
        var images = new Mat?[_cams.Count];
        var imageScale = new double[_cams.Count];
        var ownImage = new bool[_cams.Count];
        try
        {
            for (var k = 0; k < _cams.Count; k++)
            {
                var src = SourceOf(_cams[k]);
                imageScale[k] = src.Scale;
                if (photo is not null)
                {
                    if (_ownsSources)
                    {
                        photo.Apply(src.Image, k);
                        images[k] = src.Image;
                    }
                    else
                    {
                        var copy = src.Image.Clone();
                        photo.Apply(copy, k);
                        images[k] = copy;
                        ownImage[k] = true;
                    }
                }
                else
                {
                    images[k] = src.Image;
                }
            }

            if (photo is not null)
            {
                _notes.Add("photometric");
            }

            return ComposeCore(cs, full, fullPeak, images, imageScale, ownImage, projection);
        }
        finally
        {
            for (var k = 0; k < images.Length; k++)
            {
                if (ownImage[k])
                {
                    images[k]?.Dispose();
                }
            }
        }
    }

    private SourceImage SourceOf(PanoCamera c) => _sources[c.Source];

    private static double StepBelow(double s)
    {
        foreach (var step in SizeSteps)
        {
            if (step <= s + 1e-9)
            {
                return step;
            }
        }

        return SizeSteps[^1];
    }

    /// <summary>
    /// Geschätzte Speicherspitze bei Maßstab <paramref name="s"/> (Spike 20: Drohne 161 MP → 5,0 GB, Handy-2 70 MP → 2,9 GB,
    /// Handy-3 87 MP → 4,0 GB): Quellen bei s, Mehrband-Pyramide (16SC3 + 32F, ×4/3) und ein Viertel Reserve.
    /// </summary>
    private long PeakBytes(double s, PanoLayout? layout, PanoLayout? full = null)
    {
        var canvas = layout is not null ? (double)layout.W * layout.H : (double)full!.W * full.H * s * s;
        var sources = _cams.Sum(c => (double)c.Size.Width * c.Size.Height * 3 * Math.Min(1.0, s * 1.05) * Math.Min(1.0, s * 1.05));
        return (long)(1.25 * (sources + canvas * 13.4) + 300e6);
    }

    private void SetupProjection(WireProjection projection)
    {
        switch (projection)
        {
            case WireProjection.ShiftOnly when _alignment.Similarity is not null:
                _proj = Proj.Flat;
                _is360 = false;
                for (var k = 0; k < _cams.Count; k++)
                {
                    _cams[k].Similarity = _alignment.Similarity[k];
                }

                break;
            case WireProjection.ShiftOnly:
                _notes.Add("shift-unavailable");
                _proj = Proj.Plane;
                _is360 = false;
                CenterForPerspective();
                break;
            case WireProjection.Perspective:
                _proj = Proj.Plane;
                _is360 = false;
                CenterForPerspective();
                break;
            case WireProjection.Vertical:
                _proj = Proj.VerticalCylinder;
                _is360 = false;
                OrientForVertical();
                break;
            case WireProjection.Cylindrical:
            {
                // hohe Schwenks (über ±45°) strecken im echten Zylinder stark → Mercator-Teilung (Spike 20: Handy-2/3)
                var (_, vt, vb) = Extent(_cams);
                _proj = vt < -45 * Math.PI / 180 || vb > 45 * Math.PI / 180 ? Proj.Mercator : Proj.Cylindrical;
                break;
            }

            default:
                _proj = Proj.Spherical;
                if (_is360 && _spec.SphereForm == WireSphereForm.Full)
                {
                    _full360 = true;
                }
                else if (_is360)
                {
                    (_bandTop, _bandBottom) = FindBand();
                }
                else if (_spec.SphereForm == WireSphereForm.Full)
                {
                    _notes.Add("sphere-not-closed");
                }

                break;
        }
    }

    /// <summary>Perspektive: Welt so drehen, dass das mittlere Bild geradeaus blickt, Horizont waagrecht.</summary>
    private void CenterForPerspective()
    {
        var mid = _cams[_cams.Count / 2].R.Col(2);
        var down0 = new V3(0, 1, 0);
        var fwd = mid.Normalized();
        var right = down0.Cross(fwd);
        if (right.Norm < 1e-6)
        {
            return; // Blick senkrecht nach oben/unten: so lassen
        }

        right = right.Normalized();
        var down = fwd.Cross(right).Normalized();
        var g = M3.FromRows(right, down, fwd);
        foreach (var c in _cams)
        {
            c.R = g * c.R;
        }
    }

    /// <summary>Senkrechter Schwenk: Welt-x = gemeinsame Drehachse (mittlere Kamera-x-Achse), mittleres Bild geradeaus.</summary>
    private void OrientForVertical()
    {
        var right = _cams.Aggregate(new V3(0, 0, 0), (acc, c) => acc + c.R.Col(0)).Normalized();
        var mid = _cams[_cams.Count / 2].R.Col(2);
        var fwd = (mid - right * mid.Dot(right)).Normalized();
        var down = fwd.Cross(right).Normalized();
        var g = M3.FromRows(right, down, fwd);
        foreach (var c in _cams)
        {
            c.R = g * c.R;
        }
    }

    /// <summary>Kugel als Horizontstreifen: längstes Band voll gedeckter Breiten (≥ 97 % je Zeile, 0,5°-Raster).</summary>
    private (double Top, double Bottom) FindBand()
    {
        const int cols = 720, rows = 360;
        var full = new bool[rows];
        Parallel.For(0, rows, new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count }, y =>
        {
            var lat = -Math.PI / 2 + (y + 0.5) * Math.PI / rows;
            var covered = 0;
            for (var x = 0; x < cols; x++)
            {
                var lon = -Math.PI + (x + 0.5) * 2 * Math.PI / cols;
                Projections.Dir(Proj.Spherical, lon, lat, out var dx, out var dy, out var dz);
                var d = new V3(dx, dy, dz);
                foreach (var c in _cams)
                {
                    if (c.Contains(d))
                    {
                        covered++;
                        break;
                    }
                }
            }

            full[y] = covered >= cols * (1 - FillFraction);
        });
        int best = 0, bestStart = 0, cur = 0, curStart = 0;
        for (var y = 0; y < rows; y++)
        {
            if (full[y])
            {
                if (cur == 0)
                {
                    curStart = y;
                }

                cur++;
                if (cur > best)
                {
                    best = cur;
                    bestStart = curStart;
                }
            }
            else
            {
                cur = 0;
            }
        }

        if (best == 0)
        {
            return (double.NaN, double.NaN);
        }

        // Horizontstreifen wie die bisherigen Photoshop-Ergebnisse (≈ 5 : 1): höchstens 80° hoch, oben beginnend — sonst
        // reichte das Band bei Drohnen-Kugeln mit Nadir-Bildern bis senkrecht nach unten
        var top = -Math.PI / 2 + bestStart * Math.PI / rows;
        var bottom = Math.Min(-Math.PI / 2 + (bestStart + best) * Math.PI / rows, top + MaxBandDeg * Math.PI / 180);
        var margin = 2 * Math.PI / 180;
        return (top - margin, bottom + margin);
    }

    private bool Banded => _proj == Proj.Spherical && !double.IsNaN(_bandTop);

    /// <summary>Bereich eines Bildes in Projektionskoordinaten, Länge ausgerollt um die Bildmitte.</summary>
    internal static (double A, double B, double Vt, double Vb) ImageRange(PanoCamera c, Proj proj)
    {
        double w = c.Size.Width, hh = c.Size.Height;
        var dirs = new List<V3>(260);
        const int n = 64;
        for (var t = 0; t <= n; t++)
        {
            var u = (double)t / n;
            dirs.Add(c.Ray(u * (w - 1), 0));
            dirs.Add(c.Ray(u * (w - 1), hh - 1));
            dirs.Add(c.Ray(0, u * (hh - 1)));
            dirs.Add(c.Ray(w - 1, u * (hh - 1)));
        }

        var center = c.Ray((w - 1) / 2, (hh - 1) / 2);
        var lc = proj == Proj.VerticalCylinder ? Math.Atan2(center.Y, center.Z) : Math.Atan2(center.X, center.Z);
        double a = double.MaxValue, b = double.MinValue, vt = double.MaxValue, vb = double.MinValue;
        foreach (var d in dirs)
        {
            Projections.ToProj(proj, d, lc, out var u, out var v, out var ok);
            if (!ok)
            {
                continue;
            }

            a = Math.Min(a, u);
            b = Math.Max(b, u);
            vt = Math.Min(vt, v);
            vb = Math.Max(vb, v);
        }

        if (Projections.Wraps(proj))
        {
            bool zen = c.Contains(new V3(0, -1, 0)), nad = c.Contains(new V3(0, 1, 0));
            if (zen || nad)
            {
                a = lc - Math.PI;
                b = lc + Math.PI;
            }

            if (proj == Proj.Spherical)
            {
                if (zen)
                {
                    vt = -Math.PI / 2;
                }

                if (nad)
                {
                    vb = Math.PI / 2;
                }
            }
        }

        return (a, b, vt, vb);
    }

    private List<(int Cam, int Shift, bool Pole)> PartSpecs()
    {
        var res = new List<(int, int, bool)>();
        for (var k = 0; k < _cams.Count; k++)
        {
            if (_proj == Proj.Flat)
            {
                res.Add((k, 0, false));
                continue;
            }

            var (a, b, vt, vb) = ImageRange(_cams[k], _proj);
            if (a == double.MaxValue)
            {
                continue; // ganz außerhalb der Ebene
            }

            if (Banded && (vb < _bandTop || vt > _bandBottom))
            {
                continue; // ganz außerhalb des Horizontstreifens
            }

            if (!_is360)
            {
                res.Add((k, 0, false));
                continue;
            }

            if (b - a >= 2 * Math.PI - 1e-9)
            {
                res.Add((k, 0, true));
                continue;
            }

            foreach (var shift in new[] { 0, 1, -1 })
            {
                double lo = Math.Max(a + shift * 2 * Math.PI, -Math.PI), hi = Math.Min(b + shift * 2 * Math.PI, Math.PI);
                if (hi - lo > 0.003)
                {
                    res.Add((k, shift, false));
                }
            }
        }

        return res;
    }

    private PanoLayout MakeLayout(double scale)
    {
        if (_proj == Proj.Flat)
        {
            return MakeFlatLayout(scale);
        }

        var s = _cams.Select(c => c.F).OrderBy(v => v).ElementAt(_cams.Count / 2) * scale;
        var lay = new PanoLayout { S = s, C = scale, Is360 = _is360 };
        var rng = _cams.Select(c => ImageRange(c, _proj)).ToArray();
        if (_is360)
        {
            lay.W = (int)Math.Round(2 * Math.PI * s);
            lay.U0 = -lay.W / 2;
        }
        else
        {
            lay.U0 = _specs.Min(p => (int)Math.Floor(s * rng[p.Cam].A) - 2);
            lay.W = _specs.Max(p => (int)Math.Ceiling(s * rng[p.Cam].B) + 2) - lay.U0;
        }

        foreach (var (cam, shift, pole) in _specs)
        {
            var (a, b, vt, vb) = rng[cam];
            int va = (int)Math.Floor(s * vt) - 2, vbp = (int)Math.Ceiling(s * vb) + 2;
            if (Banded)
            {
                va = Math.Max(va, (int)Math.Floor(s * _bandTop));
                vbp = Math.Min(vbp, (int)Math.Ceiling(s * _bandBottom));
            }

            if (_full360)
            {
                va = Math.Max(va, (int)Math.Floor(-s * Math.PI / 2));
                vbp = Math.Min(vbp, (int)Math.Ceiling(s * Math.PI / 2));
            }

            int lo, hi;
            if (pole)
            {
                lo = lay.U0;
                hi = lay.U0 + lay.W;
            }
            else
            {
                lo = (int)Math.Floor(s * (a + shift * 2 * Math.PI)) - 2;
                hi = (int)Math.Ceiling(s * (b + shift * 2 * Math.PI)) + 2;
                if (_is360)
                {
                    lo = Math.Max(lo, lay.U0);
                    hi = Math.Min(hi, lay.U0 + lay.W);
                }
            }

            lay.Parts.Add(new PartLayout { Cam = cam, Roi = new Rect(lo - lay.U0, va, Math.Max(1, hi - lo), Math.Max(1, vbp - va)) });
        }

        if (_full360)
        {
            // 2 : 1 — die ganze Kugel, auch wo kein Bild liegt (Zenit/Nadir werden gefüllt)
            lay.V0 = (int)Math.Floor(-s * Math.PI / 2);
            lay.H = (int)Math.Round(lay.W / 2.0);
        }
        else
        {
            lay.V0 = lay.Parts.Min(p => p.Roi.Y);
            lay.H = lay.Parts.Max(p => p.Roi.Bottom) - lay.V0;
        }

        foreach (var p in lay.Parts)
        {
            var y = Math.Max(0, p.Roi.Y - lay.V0);
            var bottom = Math.Min(lay.H, p.Roi.Bottom - lay.V0);
            p.Roi = new Rect(p.Roi.X, y, p.Roi.Width, Math.Max(1, bottom - y));
        }

        return lay;
    }

    private PanoLayout MakeFlatLayout(double scale)
    {
        var boxes = _cams.Select(c =>
        {
            var (a, b, tx, ty) = c.Similarity!.Value;
            var pts = new[] { (0.0, 0.0), (c.Size.Width - 1.0, 0.0), (0.0, c.Size.Height - 1.0), (c.Size.Width - 1.0, c.Size.Height - 1.0) }
                .Select(p => (X: a * p.Item1 + b * p.Item2 + tx, Y: -b * p.Item1 + a * p.Item2 + ty)).ToList();
            return (MinX: pts.Min(p => p.X), MaxX: pts.Max(p => p.X), MinY: pts.Min(p => p.Y), MaxY: pts.Max(p => p.Y));
        }).ToList();
        var lay = new PanoLayout { S = scale, C = scale, Is360 = false };
        lay.U0 = (int)Math.Floor(boxes.Min(b => b.MinX) * scale) - 2;
        lay.V0 = (int)Math.Floor(boxes.Min(b => b.MinY) * scale) - 2;
        lay.W = (int)Math.Ceiling(boxes.Max(b => b.MaxX) * scale) + 2 - lay.U0;
        lay.H = (int)Math.Ceiling(boxes.Max(b => b.MaxY) * scale) + 2 - lay.V0;
        foreach (var (cam, _, _) in _specs)
        {
            var b = boxes[cam];
            int x0 = (int)Math.Floor(b.MinX * scale) - 2 - lay.U0, y0 = (int)Math.Floor(b.MinY * scale) - 2 - lay.V0;
            int x1 = (int)Math.Ceiling(b.MaxX * scale) + 2 - lay.U0, y1 = (int)Math.Ceiling(b.MaxY * scale) + 2 - lay.V0;
            lay.Parts.Add(new PartLayout { Cam = cam, Roi = new Rect(Math.Max(0, x0), Math.Max(0, y0), Math.Max(1, Math.Min(lay.W, x1) - Math.Max(0, x0)), Math.Max(1, Math.Min(lay.H, y1) - Math.Max(0, y0))) });
        }

        return lay;
    }

    // ------------------------------------------------------------------ Zusammensetzen

    private ComposeOutcome ComposeCore(double cs, PanoLayout fullLayout, long fullPeak, Mat?[] images, double[] imageScale, bool[] ownImage, WireProjection projection)
    {
        var interp = _fullQuality ? InterpolationFlags.Lanczos4 : InterpolationFlags.Linear;
        // Tonwert der Quellen vorher messen: beim Rendern werden die Quellen nach ihrem letzten Teil freigegeben
        var toneReference = _spec.ToneMatch > 0 || _keepUntoned ? ToneMatcher.Reference(images.Select(m => m!).ToList()) : null;
        var refArea = _cams.Max(c => (double)c.Size.Width * c.Size.Height);
        var seamScale = Math.Min(cs, Math.Sqrt(_spec.SeamMegapixels * 1e6 / refArea));
        var ls = MakeLayout(seamScale);
        var np = ls.Parts.Count;
        _progress(WorkerStep.Exposure, 0, 2);

        // 1 Naht-Maßstab: verkleinerte Quellen, Schärfe, Verzerren
        var sImgs = new Mat[np];
        var sMasks = new Mat[np];
        var sSharp = new Mat?[np];
        var sCorners = ls.Parts.Select(p => p.Roi.TopLeft).ToList();
        var smallCache = new Mat[_cams.Count];
        var sharpCache = new Mat?[_cams.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(_fullQuality ? 2 : 4, WorkerThreads.Count)), CancellationToken = _ct };
        Mat[]? gainMaps = null;
        try
        {
            Parallel.For(0, _cams.Count, options, k =>
            {
                var src = images[k]!;
                var small = new Mat();
                var f = seamScale / imageScale[k];
                Cv2.Resize(src, small, new Size(Math.Max(1, (int)Math.Round(_cams[k].Size.Width * seamScale)), Math.Max(1, (int)Math.Round(_cams[k].Size.Height * seamScale))), 0, 0, f < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
                smallCache[k] = small;
                if (_spec.Seams)
                {
                    sharpCache[k] = SharpnessMap(src, small.Size());
                }
            });
            Parallel.For(0, np, new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count, CancellationToken = _ct }, k =>
            {
                var part = ls.Parts[k];
                var cam = _cams[part.Cam];
                var (wi, wm) = Warper.Warp(smallCache[part.Cam], seamScale, cam, ls, part.Roi, _proj, InterpolationFlags.Linear, threads: 1);
                sImgs[k] = wi;
                sMasks[k] = wm;
                if (sharpCache[part.Cam] is { } sharp)
                {
                    var (si, sm) = Warper.Warp(sharp, seamScale, cam, ls, part.Roi, _proj, InterpolationFlags.Linear, threads: 1);
                    sm.Dispose();
                    sSharp[k] = si;
                }
            });
            foreach (var s in smallCache)
            {
                s?.Dispose();
            }

            foreach (var s in sharpCache)
            {
                s?.Dispose();
            }

            // 2 Belichtung in Blöcken je Kanal, gain-kompensiert in den Graph-Cut
            if (_spec.Exposure)
            {
                gainMaps = BlocksGain.Compute(sCorners, sImgs, sMasks, perChannel: true, 32, 32, out var range);
                _notes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"gain:{range.Min:0.00}-{range.Max:0.00}"));
                for (var k = 0; k < np; k++)
                {
                    BlocksGain.Apply(sImgs[k], gainMaps[k]);
                }
            }

            _progress(WorkerStep.Exposure, 2, 2);
            _ct.ThrowIfCancellationRequested();
            if (_spec.Seams)
            {
                _progress(WorkerStep.Seams, 0, 1);
                var sF = new List<Mat>(np);
                try
                {
                    foreach (var img in sImgs)
                    {
                        var f = new Mat();
                        img.ConvertTo(f, MatType.CV_32FC3);
                        sF.Add(f);
                    }

                    var orig = sMasks.Select(m => m.Clone()).ToList();
                    try
                    {
                        GraphCutSeam.Find(sF, sCorners, sMasks, sSharp, SharpLambda, _ct);
                        FillSeamHoles(ls, orig, sMasks, sCorners);
                    }
                    finally
                    {
                        foreach (var m in orig)
                        {
                            m.Dispose();
                        }
                    }
                }
                finally
                {
                    foreach (var f in sF)
                    {
                        f.Dispose();
                    }
                }

                _progress(WorkerStep.Seams, 1, 1);
            }

            foreach (var m in sImgs)
            {
                m.Dispose();
            }

            foreach (var m in sSharp)
            {
                m?.Dispose();
            }

            // 3 Entgeisterung (volle Qualität) bzw. nur Nahtgüte messen (Vorschau)
            var lc = MakeLayout(cs);
            List<FlowPatch>[]? flowPatches = null;
            var seamNcc = double.NaN;
            if (_spec.Seams && np > 1)
            {
                _progress(WorkerStep.Deghost, 0, 1);
                var deghost = _spec.Deghost && _fullQuality;
                var lf = MakeLayout(cs * FlowScale);
                var (patches, stats) = Deghost.Run(ls, sMasks, sCorners, lf, lc, _cams, k => (images[k]!, imageScale[k]), _proj, DeghostBandPx * cs, FlowScale, !deghost, _ct);
                seamNcc = Deghost.MedianNcc(stats, after: deghost);
                if (deghost)
                {
                    flowPatches = patches;
                }

                _progress(WorkerStep.Deghost, 1, 1);
            }

            // 4 Verzerren und Mehrband-Überblendung (zwei Arbeiter in Teil-Reihenfolge, höchstens drei Teile Vorlauf)
            var bands = MultiBandBlender.BandsFor(lc.W, lc.H);
            Mat pano, panoMask;
            using (var blender = new MultiBandBlender(new Rect(0, 0, lc.W, lc.H), bands))
            {
                var parts = lc.Parts.Count;
                var ready = new (Mat? Wi, Mat? Wm)[parts];
                var done = Enumerable.Range(0, parts).Select(_ => new SemaphoreSlim(0)).ToArray();
                var slots = new SemaphoreSlim(3);
                var remaining = new int[_cams.Count];
                foreach (var p in lc.Parts)
                {
                    remaining[p.Cam]++;
                }

                var next = -1;
                Exception? prodErr = null;
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                {
                    while (true)
                    {
                        try
                        {
                            slots.Wait(stop.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        var k = Interlocked.Increment(ref next);
                        if (k >= parts)
                        {
                            slots.Release();
                            return;
                        }

                        try
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            var part = lc.Parts[k];
                            var cam = _cams[part.Cam];
                            var img = images[part.Cam]!;
                            Mat? scaled = null;
                            var srcScale = imageScale[part.Cam];
                            if (cs < srcScale * 0.95)
                            {
                                scaled = new Mat();
                                Cv2.Resize(img, scaled, new Size(Math.Max(1, (int)Math.Round(cam.Size.Width * cs)), Math.Max(1, (int)Math.Round(cam.Size.Height * cs))), 0, 0, InterpolationFlags.Area);
                                img = scaled;
                                srcScale = cs;
                            }

                            var (wi, wm) = Warper.Warp(img, srcScale, cam, lc, part.Roi, _proj, interp, flowPatches?[k]);
                            scaled?.Dispose();
                            if (Interlocked.Decrement(ref remaining[part.Cam]) == 0 && _ownsSources)
                            {
                                // letzter Teil dieses Bildes: Quelle freigeben (Speicher beim Rendern)
                                SourceOf(cam).Image.Dispose();
                            }

                            if (gainMaps is not null)
                            {
                                BlocksGain.Apply(wi, gainMaps[k]);
                            }

                            using (var dil = new Mat())
                            using (var seamUp = new Mat())
                            {
                                Cv2.Dilate(sMasks[k], dil, null, null, SeamDilate);
                                Cv2.Resize(dil, seamUp, wm.Size(), 0, 0, InterpolationFlags.LinearExact);
                                Cv2.BitwiseAnd(seamUp, wm, wm);
                            }

                            ready[k] = (wi, wm);
                        }
                        catch (Exception ex)
                        {
                            prodErr ??= ex;
                            stop.Cancel();
                        }
                        finally
                        {
                            done[k].Release();
                        }
                    }
                })).ToArray();
                try
                {
                    for (var k = 0; k < parts; k++)
                    {
                        done[k].Wait(_ct);
                        if (prodErr is not null)
                        {
                            break;
                        }

                        var (wi, wm) = ready[k];
                        blender.Feed(wi!, wm!, lc.Parts[k].Roi.TopLeft);
                        wi!.Dispose();
                        wm!.Dispose();
                        ready[k] = default;
                        slots.Release();
                        _progress(WorkerStep.Warp, k + 1, parts);
                    }
                }
                finally
                {
                    stop.Cancel();
                    try
                    {
                        Task.WaitAll(workers);
                    }
                    catch (AggregateException)
                    {
                        // Fehler steht in prodErr
                    }

                    foreach (var (wi, wm) in ready)
                    {
                        wi?.Dispose();
                        wm?.Dispose();
                    }

                    if (flowPatches is not null)
                    {
                        foreach (var l in flowPatches)
                        {
                            foreach (var p in l)
                            {
                                p.Dispose();
                            }
                        }
                    }
                }

                if (prodErr is not null)
                {
                    if (prodErr is OperationCanceledException)
                    {
                        _ct.ThrowIfCancellationRequested();
                    }

                    throw new WorkerException(WorkerErrorCode.OpenCv, "Verzerren fehlgeschlagen: " + prodErr.GetType().Name, prodErr);
                }

                _ct.ThrowIfCancellationRequested();
                _progress(WorkerStep.Blend, 0, 1);
                (pano, panoMask) = blender.Blend();
            }

            _progress(WorkerStep.Finish, 0, 2);
            Mat? untoned = null;
            try
            {
                var crop = CropAndFill(pano, panoMask, cs, out var cropFraction, out var uncovered);
                Mat result;
                using (var view = new Mat(pano, crop))
                {
                    result = view.Clone();
                }

                pano.Dispose();
                pano = result;
                _ct.ThrowIfCancellationRequested();
                var baseNotes = _notes.ToList();
                untoned = _keepUntoned ? pano.Clone() : null;
                ApplyLook(pano, toneReference, _spec, _notes, _ct);
                _progress(WorkerStep.Finish, 2, 2);
                var check = Check(cropFraction, seamNcc, uncovered);
                return new ComposeOutcome
                {
                    Image = pano,
                    FullWidth = (int)Math.Round(crop.Width / cs),
                    FullHeight = (int)Math.Round(crop.Height / cs),
                    Scale = cs,
                    FullPeakBytes = fullPeak,
                    Check = check,
                    Notes = _notes,
                    Projection = projection,
                    Untoned = untoned,
                    ToneReference = toneReference,
                    BaseNotes = baseNotes,
                    UncoveredInCrop = uncovered,
                    Frame = _frame,
                    FrameCrop = _frameCrop,
                };
            }
            catch
            {
                pano.Dispose();
                untoned?.Dispose();
                _frame?.Dispose();
                throw;
            }
            finally
            {
                panoMask.Dispose();
            }
        }
        finally
        {
            foreach (var m in sMasks)
            {
                m?.Dispose();
            }

            if (gainMaps is not null)
            {
                foreach (var g in gainMaps)
                {
                    g.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Lokale Schärfe, kontrastnormiert (Spike 20): log(Laplace² fein (σ 0,8) + 4) − log(Laplace² grob (σ 2,0) + 1),
    /// flächengemittelt auf die Naht-Größe. Dunst senkt beide Bänder gleich, Unschärfe nur das feine.
    /// </summary>
    private static Mat SharpnessMap(Mat bgr, Size target)
    {
        using var g = new Mat();
        Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY);
        using var f = new Mat();
        g.ConvertTo(f, MatType.CV_32F);
        Mat Band(double sigma, double eps)
        {
            using var b = new Mat();
            Cv2.GaussianBlur(f, b, new Size(0, 0), sigma);
            using var l = new Mat();
            Cv2.Laplacian(b, l, MatType.CV_32F);
            using var l2 = new Mat();
            Cv2.Multiply(l, l, l2);
            var s = new Mat();
            Cv2.Resize(l2, s, target, 0, 0, InterpolationFlags.Area);
            Cv2.Add(s, new Scalar(eps), s);
            Cv2.Log(s, s);
            return s;
        }

        using var fine = Band(0.8, 4);
        using var coarse = Band(2.0, 1);
        var res = new Mat();
        Cv2.Subtract(fine, coarse, res);
        return res;
    }

    /// <summary>Lücken zwischen den Nahtmasken (kein Teil deckt das Pixel mehr) dem Teil geben, in dem es am tiefsten liegt.</summary>
    private static void FillSeamHoles(PanoLayout ls, IList<Mat> orig, IList<Mat> seam, IList<Point> corners)
    {
        var cover = new int[ls.W * ls.H];
        for (var k = 0; k < seam.Count; k++)
        {
            var m = seam[k];
            var c = corners[k];
            int rows = m.Rows, cols = m.Cols;
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < cols; x++)
                {
                    int xx = c.X + x, yy = c.Y + y;
                    if (xx < 0 || yy < 0 || xx >= ls.W || yy >= ls.H)
                    {
                        continue;
                    }

                    if (m.At<byte>(y, x) != 0)
                    {
                        cover[yy * ls.W + xx]++;
                    }
                }
            }
        }

        var best = new float[ls.W * ls.H];
        var owner = Enumerable.Repeat(-1, ls.W * ls.H).ToArray();
        for (var k = 0; k < orig.Count; k++)
        {
            using var dist = new Mat();
            Cv2.DistanceTransform(orig[k], dist, DistanceTypes.L2, DistanceTransformMasks.Mask3);
            var c = corners[k];
            int rows = dist.Rows, cols = dist.Cols;
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < cols; x++)
                {
                    int xx = c.X + x, yy = c.Y + y;
                    if (xx < 0 || yy < 0 || xx >= ls.W || yy >= ls.H)
                    {
                        continue;
                    }

                    var o = yy * ls.W + xx;
                    if (cover[o] != 0)
                    {
                        continue;
                    }

                    var d = dist.At<float>(y, x);
                    if (d > best[o])
                    {
                        best[o] = d;
                        owner[o] = k;
                    }
                }
            }
        }

        for (var o = 0; o < owner.Length; o++)
        {
            var k = owner[o];
            if (k < 0)
            {
                continue;
            }

            int xx = o % ls.W, yy = o / ls.W;
            seam[k].Set<byte>(yy - corners[k].Y, xx - corners[k].X, 255);
        }
    }

    // ------------------------------------------------------------------ Zuschnitt und Füllen

    /// <summary>
    /// Zuschnitt nach Einstellung; füllt Lücken im Ausschnitt nur bei „Lücken füllen“ (und den Polen der 2 : 1-Kugel); liefert
    /// den Ausschnitt, seine Höhe gegenüber der Bildhöhe und die Zahl der ungedeckten Pixel darin. „Gerade Kanten“ bestimmt das
    /// Rechteck aus der echten Deckungsmaske nach dem Überblenden (ohne Schließen, ohne Dehnen), abzüglich eines kleinen
    /// Sicherheitsrands für Interpolation und Überblendung — der Ausschnitt enthält danach kein einziges gefülltes Pixel.
    /// (Bis 0.5.46 dehnte der Zuschnitt die Maske um 3 % der Höhe und füllte die Ränder per Inpaint: helle Flecken an den Ecken.)
    /// </summary>
    private Rect CropAndFill(Mat pano, Mat mask, double cs, out double cropFraction, out long uncovered)
    {
        using var raw = new Mat();
        Cv2.Threshold(mask, raw, 0, 255, ThresholdTypes.Binary);
        Rect crop;
        if (_full360)
        {
            Close(mask);
            crop = new Rect(0, 0, pano.Width, pano.Height);
            uncovered = CountUncovered(raw, crop);
            FillPoles(pano, mask);
        }
        else if (_spec.Crop == WireCrop.Straight)
        {
            var wraps = _is360 && Projections.Wraps(_proj);
            using var safe = SafeCoverage(raw, CropMargin(pano), wraps);
            crop = TrimToCovered(safe, wraps ? Crop360(safe) : CropInner(safe));
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                // nichts voll gedeckt (gekippte Kette): ungeschnitten zeigen, die Prüfung meldet „zu wenig Bildhöhe“
                crop = Cv2.BoundingRect(raw);
                crop = crop.Width > 0 && crop.Height > 0 ? crop : new Rect(0, 0, pano.Width, pano.Height);
                cropFraction = 0;
                _cropAreaFraction = 0;
                uncovered = CountUncovered(raw, crop);
                return crop;
            }

            uncovered = CountUncovered(raw, crop);
            if (_keepUntoned)
            {
                // Bühne des Fensters: ungeschnittenes Panorama mit schraffierten Lücken, Zuschnitt als Rahmen darin
                var frameBounds = Cv2.BoundingRect(raw);
                if (frameBounds.Width > 0 && frameBounds.Height > 0)
                {
                    _frame = new Mat(pano, frameBounds).Clone();
                    using (var coverage = new Mat(raw, frameBounds))
                    {
                        HatchGaps(_frame, coverage);
                    }

                    _frameCrop = new Rect(crop.X - frameBounds.X, crop.Y - frameBounds.Y, crop.Width, crop.Height);
                }
            }
        }
        else
        {
            Close(mask);
            crop = Cv2.BoundingRect(mask);
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                crop = new Rect(0, 0, pano.Width, pano.Height);
            }

            uncovered = CountUncovered(raw, crop);
            if (_spec.Crop == WireCrop.Fill)
            {
                FillBorders(pano, mask, crop);
            }
            else if (_keepUntoned)
            {
                // „Ungeschnitten“ in der Vorschau: Lücken schraffiert statt schwarz (gespeichert wird ohne Schraffur)
                using var view = new Mat(pano, crop);
                using var coverage = new Mat(raw, crop);
                HatchGaps(view, coverage);
            }
        }

        // Zuschnitthöhe quer zum Schwenk gegenüber der mittleren Bildhöhe auf der Leinwand (gekippte Ketten und Wellen
        // verlieren viel davon); beim senkrechten Schwenk zählt die Breite
        var vertical = _proj == Proj.VerticalCylinder;
        var extents = _cams.Select(c => (double)(vertical ? c.Size.Width : c.Size.Height)).OrderBy(v => v).ToList();
        var medianExtent = extents[extents.Count / 2] * cs;
        cropFraction = (vertical ? crop.Width : crop.Height) / Math.Max(1.0, medianExtent);
        // Anteil des Zuschnitts an der gedeckten Fläche: eine gekippte Kette kann die volle Höhe behalten, verliert dann
        // aber einen großen Teil des Schwenks
        var bounds = Cv2.BoundingRect(raw);
        _cropAreaFraction = bounds.Width > 0 && bounds.Height > 0 ? crop.Width * (double)crop.Height / (bounds.Width * (double)bounds.Height) : 1;
        return crop;
    }

    private double _cropAreaFraction = 1;
    private Mat? _frame;
    private Rect _frameCrop;

    /// <summary>Ungedeckte Pixel als graue Schraffur (Bühne des Fensters; nie im gespeicherten Ergebnis).</summary>
    internal static void HatchGaps(Mat image, Mat coverage)
    {
        using var gaps = new Mat();
        Cv2.Threshold(coverage, gaps, 0, 255, ThresholdTypes.BinaryInv);
        if (Cv2.CountNonZero(gaps) == 0)
        {
            return;
        }

        int width = image.Width, height = image.Height;
        using var hatch = new Mat(image.Size(), image.Type(), new Scalar(44, 44, 44));
        var step = Math.Max(8, Math.Min(width, height) / 50);
        var thickness = Math.Max(2, step / 3);
        for (var d = -height; d < width; d += step)
        {
            Cv2.Line(hatch, new Point(d, height), new Point(d + height, 0), new Scalar(92, 92, 92), thickness);
        }

        hatch.CopyTo(image, gaps);
    }

    /// <summary>Kleine Löcher und Kerben der Maske schließen (nur „Lücken füllen“, „Ungeschnitten“ und 2 : 1-Kugel).</summary>
    private static void Close(Mat mask)
    {
        using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, k5);
    }

    /// <summary>Sicherheitsrand für „Gerade Kanten“ (Interpolation, Überblendung): 0,05 % der kurzen Seite, mindestens 2 px.</summary>
    internal static int CropMargin(Mat pano) => Math.Max(2, (int)Math.Round(Math.Min(pano.Width, pano.Height) * 0.0005));

    /// <summary>
    /// Deckung abzüglich Sicherheitsrand: Erosion mit ungedecktem Rand (auch an den Leinwandkanten); bei der 360°-Kugel laufen
    /// linker und rechter Rand ineinander und werden dort nicht beschnitten.
    /// </summary>
    private static Mat SafeCoverage(Mat raw, int margin, bool wraps)
    {
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * margin + 1, 2 * margin + 1));
        var safe = new Mat();
        if (!wraps)
        {
            Cv2.Erode(raw, safe, kernel, null, 1, BorderTypes.Constant, Scalar.All(0));
            return safe;
        }

        using var padded = new Mat();
        Cv2.CopyMakeBorder(raw, padded, 0, 0, margin, margin, BorderTypes.Wrap);
        using var eroded = new Mat();
        Cv2.Erode(padded, eroded, kernel, null, 1, BorderTypes.Constant, Scalar.All(0));
        using (var view = new Mat(eroded, new Rect(margin, 0, raw.Cols, raw.Rows)))
        {
            view.CopyTo(safe);
        }

        return safe;
    }

    /// <summary>Ungedeckte Pixel der Maske im Ausschnitt.</summary>
    private static long CountUncovered(Mat raw, Rect crop)
    {
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            return 0;
        }

        using var view = new Mat(raw, crop);
        return crop.Width * (long)crop.Height - Cv2.CountNonZero(view);
    }

    /// <summary>360°: das längste Band an Zeilen, die über die ganze Breite gedeckt sind.</summary>
    private static Rect Crop360(Mat mask)
    {
        int best = 0, bestStart = 0, cur = 0, curStart = 0;
        int rows = mask.Rows, cols = mask.Cols;
        using var rowSums = new Mat();
        using (var bin = new Mat())
        {
            Cv2.Threshold(mask, bin, 0, 1, ThresholdTypes.Binary);
            Cv2.Reduce(bin, rowSums, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32S);
        }

        for (var y = 0; y < rows; y++)
        {
            if (rowSums.At<int>(y, 0) >= cols)
            {
                if (cur == 0)
                {
                    curStart = y;
                }

                cur++;
                if (cur > best)
                {
                    best = cur;
                    bestStart = curStart;
                }
            }
            else
            {
                cur = 0;
            }
        }

        return best == 0 ? new Rect(0, 0, 0, 0) : new Rect(0, bestStart, cols, best);
    }

    /// <summary>
    /// Größtes achsparalleles Rechteck innerhalb der Maske (auf ≤ 1/8 verkleinert, nur voll gedeckte Blöcke, Histogramm-Stapel);
    /// die Kanten zieht danach <see cref="TrimToCovered"/> pixelgenau nach.
    /// </summary>
    private static Rect CropInner(Mat mask)
    {
        var f = Math.Max(1, Math.Min(8, Math.Min(mask.Cols, mask.Rows) / 64));
        using var small = new Mat();
        Cv2.Resize(mask, small, new Size(Math.Max(1, mask.Cols / f), Math.Max(1, mask.Rows / f)), 0, 0, InterpolationFlags.Area);
        using var bin = new Mat();
        Cv2.Threshold(small, bin, 253, 255, ThresholdTypes.Binary);
        int rows = bin.Rows, cols = bin.Cols;
        var hgt = new int[cols];
        long bestA = 0;
        var best = new Rect(0, 0, 0, 0);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                hgt[x] = bin.At<byte>(y, x) != 0 ? hgt[x] + 1 : 0;
            }

            var st = new Stack<int>();
            for (var x = 0; x <= cols; x++)
            {
                var hcur = x == cols ? 0 : hgt[x];
                while (st.Count > 0 && hgt[st.Peek()] >= hcur)
                {
                    var top = st.Pop();
                    var hh = hgt[top];
                    var left = st.Count == 0 ? 0 : st.Peek() + 1;
                    var area = (long)hh * (x - left);
                    if (area > bestA)
                    {
                        bestA = area;
                        best = new Rect(left, y - hh + 1, x - left, hh);
                    }
                }

                st.Push(x);
            }
        }

        if (bestA == 0)
        {
            return new Rect(0, 0, 0, 0);
        }

        // Blöcke zurück auf volle Größe (der verkleinerte Raster ist bei nicht teilbaren Maßen leicht gestreckt: die Kanten
        // prüft TrimToCovered pixelgenau)
        double sx = mask.Cols / (double)cols, sy = mask.Rows / (double)rows;
        var x0 = (int)Math.Ceiling(best.X * sx);
        var y0 = (int)Math.Ceiling(best.Y * sy);
        var x1 = (int)Math.Floor(best.Right * sx);
        var y1 = (int)Math.Floor(best.Bottom * sy);
        var r = new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        return r & new Rect(0, 0, mask.Cols, mask.Rows);
    }

    /// <summary>
    /// Zieht die Kanten des Rechtecks nach innen, bis kein ungedecktes Pixel mehr darin liegt (Kante mit dem größten Anteil an
    /// Lücken zuerst; ein Loch im Inneren schneidet die kleinere Seite um dieses Loch ab). Leeres Rechteck, wenn nichts übrig bleibt.
    /// </summary>
    internal static Rect TrimToCovered(Mat mask, Rect r)
    {
        r &= new Rect(0, 0, mask.Cols, mask.Rows);
        while (r.Width > 0 && r.Height > 0)
        {
            using var view = new Mat(mask, r);
            using var topRow = new Mat(mask, new Rect(r.X, r.Y, r.Width, 1));
            using var bottomRow = new Mat(mask, new Rect(r.X, r.Bottom - 1, r.Width, 1));
            using var leftCol = new Mat(mask, new Rect(r.X, r.Y, 1, r.Height));
            using var rightCol = new Mat(mask, new Rect(r.Right - 1, r.Y, 1, r.Height));
            var top = 1 - Cv2.CountNonZero(topRow) / (double)r.Width;
            var bottom = 1 - Cv2.CountNonZero(bottomRow) / (double)r.Width;
            var left = 1 - Cv2.CountNonZero(leftCol) / (double)r.Height;
            var right = 1 - Cv2.CountNonZero(rightCol) / (double)r.Height;
            var worst = Math.Max(Math.Max(top, bottom), Math.Max(left, right));
            if (worst > 0)
            {
                // die Kante mit dem größten Lückenanteil wandert eine Zeile bzw. Spalte nach innen
                r = worst == top ? new Rect(r.X, r.Y + 1, r.Width, r.Height - 1)
                    : worst == bottom ? new Rect(r.X, r.Y, r.Width, r.Height - 1)
                    : worst == left ? new Rect(r.X + 1, r.Y, r.Width - 1, r.Height)
                    : new Rect(r.X, r.Y, r.Width - 1, r.Height);
                continue;
            }

            if (Cv2.CountNonZero(view) == r.Width * (long)r.Height)
            {
                return r;
            }

            // Loch im Inneren (Kanten sauber): um EIN Loch (erste Zusammenhangskomponente) die größte der vier Teilflächen
            // behalten — mehrere verstreute Löcher werden so einzeln abgeschnitten, nicht ihr gemeinsames Rechteck
            using var inverted = new Mat();
            Cv2.Threshold(view, inverted, 0, 255, ThresholdTypes.BinaryInv);
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var hole = Cv2.ConnectedComponentsWithStats(inverted, labels, stats, centroids, PixelConnectivity.Connectivity8) > 1
                ? new Rect(stats.At<int>(1, 0), stats.At<int>(1, 1), stats.At<int>(1, 2), stats.At<int>(1, 3))
                : Cv2.BoundingRect(inverted);
            Rect[] parts =
            [
                new Rect(r.X, r.Y, r.Width, hole.Y),
                new Rect(r.X, r.Y + hole.Bottom, r.Width, r.Height - hole.Bottom),
                new Rect(r.X, r.Y, hole.X, r.Height),
                new Rect(r.X + hole.Right, r.Y, r.Width - hole.Right, r.Height),
            ];
            r = parts.OrderByDescending(p => p.Width * (long)p.Height).First();
        }

        return new Rect(r.X, r.Y, 0, 0);
    }

    /// <summary>Ungedeckte Pixel im Ausschnitt: Inpaint (Telea) auf ¼ Maßstab, hochskaliert + leichtes Rauschen (fester Startwert).</summary>
    private static void FillBorders(Mat pano, Mat mask, Rect crop)
    {
        using var mc = new Mat(mask, crop);
        using var holes = new Mat();
        Cv2.Threshold(mc, holes, 0, 255, ThresholdTypes.BinaryInv);
        if (Cv2.CountNonZero(holes) == 0)
        {
            return;
        }

        using var view = new Mat(pano, crop);
        var sw = Math.Max(1, crop.Width / 4);
        var sh = Math.Max(1, crop.Height / 4);
        using var small = new Mat();
        Cv2.Resize(view, small, new Size(sw, sh), 0, 0, InterpolationFlags.Area);
        using var hs = new Mat();
        Cv2.Resize(holes, hs, small.Size(), 0, 0, InterpolationFlags.Area);
        Cv2.Threshold(hs, hs, 0, 255, ThresholdTypes.Binary);
        using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5)))
        {
            Cv2.Dilate(hs, hs, k);
        }

        using var inp = new Mat();
        Cv2.Inpaint(small, hs, inp, 6, InpaintTypes.Telea);
        using var up = new Mat();
        Cv2.Resize(inp, up, crop.Size, 0, 0, InterpolationFlags.Cubic);
        using var up16 = new Mat();
        up.ConvertTo(up16, MatType.CV_16SC3);
        using var noise = new Mat(up.Size(), MatType.CV_16SC3);
        Cv2.SetTheRNG(4711);
        Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(2, 2, 2));
        Cv2.Add(up16, noise, up16);
        using var up8 = new Mat();
        up16.ConvertTo(up8, MatType.CV_8UC3);
        up8.CopyTo(view, holes);
    }

    /// <summary>
    /// 2 : 1-Kugel: Zenit und Nadir einfach füllen (Stufe 1) — je Spalte der Wert der äußersten gedeckten Zeile, danach weich
    /// verwischt und mit der Umgebung verblendet; kleine Lücken wie bei <see cref="FillBorders"/>.
    /// </summary>
    private static unsafe void FillPoles(Mat pano, Mat mask)
    {
        int w = pano.Cols, h = pano.Rows;
        var top = new int[w];
        var bottom = new int[w];
        for (var x = 0; x < w; x++)
        {
            top[x] = -1;
            bottom[x] = -1;
        }

        var mp = (byte*)mask.Data;
        var ms = mask.Step();
        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++)
            {
                if (mp[y * ms + x] != 0)
                {
                    top[x] = y;
                    break;
                }
            }

            for (var y = h - 1; y >= 0; y--)
            {
                if (mp[y * ms + x] != 0)
                {
                    bottom[x] = y;
                    break;
                }
            }
        }

        using var filled = pano.Clone();
        var fp = (byte*)filled.Data;
        var fs = filled.Step();
        for (var x = 0; x < w; x++)
        {
            if (top[x] < 0)
            {
                continue;
            }

            var srcTop = fp + top[x] * fs + x * 3;
            for (var y = 0; y < top[x]; y++)
            {
                var d = fp + y * fs + x * 3;
                d[0] = srcTop[0];
                d[1] = srcTop[1];
                d[2] = srcTop[2];
            }

            var srcBottom = fp + bottom[x] * fs + x * 3;
            for (var y = bottom[x] + 1; y < h; y++)
            {
                var d = fp + y * fs + x * 3;
                d[0] = srcBottom[0];
                d[1] = srcBottom[1];
                d[2] = srcBottom[2];
            }
        }

        // Spalten ohne jedes Pixel (sehr selten): Nachbarn übernehmen bleibt dem Verwischen überlassen
        using var blurred = new Mat();
        var sigma = Math.Max(2.0, w / 120.0);
        Cv2.GaussianBlur(filled, blurred, new Size(0, 0), sigma, sigma / 3, BorderTypes.Reflect);
        using var holes = new Mat();
        Cv2.Threshold(mask, holes, 0, 255, ThresholdTypes.BinaryInv);
        using var soft = new Mat();
        holes.ConvertTo(soft, MatType.CV_32F, 1 / 255.0);
        Cv2.GaussianBlur(soft, soft, new Size(0, 0), Math.Max(2.0, h / 200.0));
        Cv2.Max(soft, new Scalar(0), soft);
        // weiche Überblendung: Lücken = verwischt, Rand = Mischung, Rest = Bild
        var bp = (byte*)blurred.Data;
        var bs = blurred.Step();
        var pp = (byte*)pano.Data;
        var ps = pano.Step();
        var sp = (float*)soft.Data;
        var ss = soft.Step() / 4;
        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads.Count }, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var a = mp[y * ms + x] == 0 ? 1f : Math.Min(1f, sp[y * ss + x] * 2f);
                if (a <= 0)
                {
                    continue;
                }

                var dst = pp + y * ps + x * 3;
                var src = bp + y * bs + x * 3;
                for (var c = 0; c < 3; c++)
                {
                    dst[c] = (byte)Math.Clamp(dst[c] * (1 - a) + src[c] * a + 0.5f, 0, 255);
                }
            }
        });
    }

    // ------------------------------------------------------------------ Prüfung

    private QualityCheck Check(double cropFraction, double seamNcc, long uncovered)
    {
        var problems = new List<string>(_alignment.Problems);
        var total = _sources.Count;
        var used = _cams.Count;
        if (used < total)
        {
            _notes.Add("dropped:" + (total - used));
            if (used < Math.Ceiling(total * 2.0 / 3.0))
            {
                problems.Add("images-dropped");
            }
        }

        // gekippte Kette bzw. Wellen: der gerade Zuschnitt verliert den Großteil der Bildhöhe
        if (!_full360 && _spec.Crop == WireCrop.Straight && cropFraction < 0.45)
        {
            problems.Add("crop-height");
        }

        // … oder behält die Höhe und verliert dafür einen großen Teil des Schwenks
        if (!_full360 && _spec.Crop == WireCrop.Straight && _cropAreaFraction < 0.55)
        {
            problems.Add("crop-size");
        }

        if (double.IsFinite(seamNcc) && seamNcc < 0.0)
        {
            problems.Add("seams");
        }

        return new QualityCheck
        {
            Passed = problems.Count == 0,
            Problems = problems.Distinct().ToList(),
            ResidualMedianPx = _alignment.ResidualMedianPx,
            ResidualP90Px = _alignment.ResidualP90Px,
            SeamNcc = seamNcc,
            CropHeightFraction = Math.Round(cropFraction, 3),
            CropAreaFraction = Math.Round(_cropAreaFraction, 3),
            UncoveredPixels = uncovered,
            UsedImages = used,
            TotalImages = total,
        };
    }
}
