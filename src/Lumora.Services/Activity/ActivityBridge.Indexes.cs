using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Duplicates;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Services.Motifs;
using Lumora.Services.Places;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Tracks;
using Lumora.Services.Quality;
using Lumora.Services.Stories;

namespace Lumora.Services.Activity;

/// <summary>
/// Hintergrund-Indizes in der Aktivitäts-Anzeige (ADR-0067): Vorschaubilder, Doppelte, Gesichter, Motive, Aufnahmedatum,
/// Orte, Genauigkeit, Foto-Güte, Storys, GPS-Spuren. Ohne kurze Meldung und — außer bei Fehlern bzw. beim vom Benutzer
/// angestoßenen Einlesen der GPS-Spuren — ohne Eintrag unter „Zuletzt fertig“. Ein Zustand „wartet“ der Quelle (z. B.
/// „wartet auf den Katalog“) wird zu „Wartet“ mit diesem Grund. Gesichter und Motive teilen sich die Grafikkarte Foto um
/// Foto (<c>IGpuGate</c>, ADR-0052) — keiner wartet dabei auf den anderen; die zweite Zeile nennt das.
/// </summary>
public sealed partial class ActivityBridge
{
    private const string FacesKey = "faces";
    private const string MotifsKey = "motifs";
    private const string ThumbnailsKey = "thumbnails";

    private readonly Thumbnails.IThumbnailService? _thumbnails;
    private readonly IFaceIndexService? _faces;
    private readonly IMotifIndexService? _motifs;
    private readonly IFileQualityIndexService? _quality;
    private readonly IGpsTrackImportService? _gpsTracks;
    private readonly TimeProvider _time;
    private ITimer? _thumbnailTimer;
    private int _thumbnailPeak;
    private volatile bool _facesOnGpu;
    private volatile bool _motifsOnGpu;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastIndexErrors = new();

    private void StartIndexes()
    {
        _messenger.Register<ActivityBridge, FaceIndexStatusMessage>(this, static (r, m) => r.OnFaces(m.Status));
        _messenger.Register<ActivityBridge, MotifIndexStatusMessage>(this, static (r, m) => r.OnMotifs(m.Status));
        _messenger.Register<ActivityBridge, DuplicateIndexStatusMessage>(this, static (r, m) => r.OnSimpleIndex(
            "duplicates", ActivityKind.Duplicates, "Doppelte suchen", m.Status.IsRunning, m.Status.Done, m.Status.Total, m.Status.Message, m.Status.Phase));
        _messenger.Register<ActivityBridge, CaptureDateBackfillStatusMessage>(this, static (r, m) => r.OnSimpleIndex(
            "capture-dates", ActivityKind.CaptureDates, "Aufnahmedatum nachtragen", m.Status.IsRunning, m.Status.Done, m.Status.Total, m.Status.Message, "aus EXIF, Video, Dateiname"));
        _messenger.Register<ActivityBridge, GeoBackfillStatusMessage>(this, static (r, m) => r.OnSimpleIndex(
            "geo-backfill", ActivityKind.Places, "Orte nachtragen", m.Status.IsRunning, m.Status.Done, m.Status.Total, m.Status.Message, "aus EXIF, XMP, Video"));
        _messenger.Register<ActivityBridge, GeoAccuracyStatusMessage>(this, static (r, m) => r.OnSimpleIndex(
            "geo-accuracy", ActivityKind.Places, "Orte prüfen", m.Status.IsRunning, m.Status.Done, m.Status.Total, m.Status.Message, "Genauigkeit der Koordinaten"));
        _messenger.Register<ActivityBridge, FileQualityIndexStatusMessage>(this, static (r, m) => r.OnQuality(m.Status));
        _messenger.Register<ActivityBridge, StoryIndexStatusMessage>(this, static (r, m) => r.OnStories(m.Status));
        _messenger.Register<ActivityBridge, GpsTrackImportStatusMessage>(this, static (r, m) => r.OnGpsTracks(m.Status));

        if (_thumbnails is not null)
        {
            _thumbnailTimer = _time.CreateTimer(_ => PollThumbnails(), null, ThumbnailPollInterval, ThumbnailPollInterval);
        }

        // Schon laufende Indizes (Bridge startet vor den Diensten — nur zur Sicherheit)
        if (_faces is { Status.IsActive: true })
        {
            OnFaces(_faces.Status);
        }

        if (_motifs is { Status.IsActive: true })
        {
            OnMotifs(_motifs.Status);
        }
    }

    private void StopIndexes()
    {
        _thumbnailTimer?.Dispose();
        _thumbnailTimer = null;
    }

    /// <summary>Warteschlange der Vorschaubilder: Fortschritt = höchster Stand seit Beginn minus Rest.</summary>
    internal void PollThumbnails()
    {
        var pending = _thumbnails?.PendingCount ?? 0;
        if (pending <= 0)
        {
            _thumbnailPeak = 0;
            _center.Find(ThumbnailsKey)?.Dismiss();
            return;
        }

        _thumbnailPeak = Math.Max(_thumbnailPeak, pending);
        _center.Start(new ActivityStartInfo(ActivityKind.Thumbnails, "Vorschaubilder erstellen", KeepInRecent: false, Key: ThumbnailsKey))
            .Report(_thumbnailPeak - pending, _thumbnailPeak, $"{pending:N0} in Warteschlange");
    }

    private void OnSimpleIndex(string key, ActivityKind kind, string title, bool isRunning, int done, int total, string? message, string? detail)
    {
        if (isRunning)
        {
            _center.Start(new ActivityStartInfo(kind, title, KeepInRecent: false, Key: key)).Report(done, total, detail is { Length: > 0 } ? $"im Hintergrund · {detail}" : "im Hintergrund");
        }
        else if (!string.IsNullOrWhiteSpace(message))
        {
            _center.Start(new ActivityStartInfo(kind, title, KeepInRecent: false, Key: key)).Wait(message);
        }
        else
        {
            _center.Find(key)?.Dismiss();
        }
    }

    private void OnQuality(FileQualityIndexStatus status)
    {
        const string key = "quality";
        if (status.IsRunning)
        {
            var quality = _quality;
            _center.Start(new ActivityStartInfo(ActivityKind.Quality, "Foto-Güte bewerten", KeepInRecent: false, Key: key, Cancel: quality is null ? null : quality.CancelPass))
                .Report(status.Done, status.Total, status.Phase == FileQualityIndexStatus.AestheticPhase ? "im Hintergrund · Bildwirkung" : "im Hintergrund · Schärfe und Belichtung");
            return;
        }

        OnSimpleIndex(key, ActivityKind.Quality, "Foto-Güte bewerten", false, 0, 0, status.Message, null);
    }

    private void OnStories(StoryIndexStatus status)
    {
        const string key = "stories";
        if (status.IsRunning)
        {
            _center.Start(new ActivityStartInfo(ActivityKind.Stories, "Storys berechnen", KeepInRecent: false, Key: key)).Report(0, 0, "im Hintergrund");
        }
        else if (!string.IsNullOrWhiteSpace(status.Text) && status.Text != StoryIndexStatus.Idle.Text)
        {
            _center.Start(new ActivityStartInfo(ActivityKind.Stories, "Storys berechnen", KeepInRecent: false, Key: key)).Wait(status.Text);
        }
        else
        {
            _center.Find(key)?.Dismiss();
        }
    }

    /// <summary>Einlesen der GPS-Spuren (vom Benutzer bzw. der erkannten Uhr angestoßen): Ergebnis unter „Zuletzt fertig“.</summary>
    private void OnGpsTracks(GpsTrackImportStatus status)
    {
        const string key = "gps-tracks";
        if (status.IsRunning)
        {
            var tracks = _gpsTracks;
            _center.Start(new ActivityStartInfo(ActivityKind.GpsTracks, "GPS-Spuren einlesen", Key: key, Cancel: tracks is null ? null : tracks.Cancel))
                .Report(status.FilesRead, status.FilesFound, $"{status.Imported:N0} Aktivitäten eingelesen");
            return;
        }

        if (_center.Find(key) is { } handle)
        {
            var text = $"{status.Imported:N0} {(status.Imported == 1 ? "Aktivität" : "Aktivitäten")} eingelesen";
            if (status.Duplicates > 0)
            {
                text += $", {status.Duplicates:N0} schon vorhanden";
            }

            handle.Complete(new ActivityCompletion(text));
        }
    }

    private void OnFaces(FaceIndexStatus status)
    {
        _facesOnGpu = status.IsActive && string.Equals(status.ExecutionProvider, "DirectML", StringComparison.Ordinal);
        var faces = _faces;
        OnGpuIndex(
            FacesKey,
            ActivityKind.Faces,
            "Gesichter erkennen",
            status.State switch
            {
                FaceIndexState.Running => IndexPhase.Running,
                FaceIndexState.Paused => IndexPhase.Paused,
                FaceIndexState.Waiting => IndexPhase.Waiting,
                FaceIndexState.Error => IndexPhase.Error,
                _ => IndexPhase.Off,
            },
            status.Done,
            status.Total,
            status.Message,
            GpuDetail(status.ExecutionProvider, _motifsOnGpu ? "Motive" : null),
            faces is null ? null : () => { faces.CancelRun(); faces.Resume(); },
            faces is null ? null : faces.Pause,
            faces is null ? null : faces.Resume);
    }

    private void OnMotifs(MotifIndexStatus status)
    {
        _motifsOnGpu = status.IsActive && string.Equals(status.ExecutionProvider, "DirectML", StringComparison.Ordinal);
        var motifs = _motifs;
        OnGpuIndex(
            MotifsKey,
            ActivityKind.Motifs,
            "Motive erkennen",
            status.State switch
            {
                MotifIndexState.Running => IndexPhase.Running,
                MotifIndexState.Paused => IndexPhase.Paused,
                MotifIndexState.Waiting => IndexPhase.Waiting,
                MotifIndexState.Error => IndexPhase.Error,
                _ => IndexPhase.Off,
            },
            status.Done,
            status.Total,
            status.Message,
            GpuDetail(status.ExecutionProvider, _facesOnGpu ? "Gesichter" : null),
            motifs is null ? null : () => { motifs.CancelRun(); motifs.Resume(); },
            motifs is null ? null : motifs.Pause,
            motifs is null ? null : motifs.Resume);
    }

    private enum IndexPhase { Off, Running, Paused, Waiting, Error }

    /// <summary>Gesichter/Motive: Pause/Fortsetzen und Abbrechen über die Dienste (Abbrechen hebt eine Pause mit auf, wie bisher).</summary>
    private void OnGpuIndex(string key, ActivityKind kind, string title, IndexPhase phase, int done, int total, string? message, string detail,
        Action? cancel, Action? pause, Action? resume)
    {
        if (phase != IndexPhase.Error)
        {
            _lastIndexErrors.TryRemove(key, out _);
        }
        else if (_lastIndexErrors.TryGetValue(key, out var last) && last == (message ?? string.Empty))
        {
            return; // derselbe Fehler wird wiederholt gemeldet — ein Eintrag genügt
        }
        else
        {
            _lastIndexErrors[key] = message ?? string.Empty;
        }

        if (phase == IndexPhase.Off)
        {
            _center.Find(key)?.Dismiss();
            return;
        }

        var handle = _center.Start(new ActivityStartInfo(kind, title, KeepInRecent: false, Key: key, Cancel: cancel, Pause: pause, Resume: resume));
        switch (phase)
        {
            case IndexPhase.Running:
                handle.SetPaused(false);
                handle.Report(done, total, detail);
                break;
            case IndexPhase.Paused:
                handle.Report(done, total, detail);
                handle.SetPaused(true);
                break;
            case IndexPhase.Waiting:
                handle.Wait(message ?? "wartet");
                break;
            default:
                handle.Fail($"{title}: {message ?? "Fehler"}", message);
                break;
        }
    }

    /// <summary>„im Hintergrund · Grafikkarte“ bzw. „… · Prozessor“, bei gleichzeitigem zweiten GPU-Index mit Hinweis auf das Teilen.</summary>
    internal static string GpuDetail(string? provider, string? sharingWith)
    {
        var device = provider is null ? null : string.Equals(provider, "DirectML", StringComparison.Ordinal) ? "Grafikkarte" : "Prozessor";
        var text = device is null ? "im Hintergrund" : $"im Hintergrund · {device}";
        return device == "Grafikkarte" && sharingWith is not null ? $"{text} (abwechselnd mit „{sharingWith}“)" : text;
    }
}
