using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Imaging.Merge.Panorama;
using Lumora.Imaging.Worker;
using Lumora.Services.Activity;
using Lumora.Services.Albums;
using Lumora.Services.Background;
using Lumora.Services.Cloud;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Operations;

namespace Lumora.Imaging.Merge;

/// <summary>
/// Merge-Dienst (ADR-0078, Bau Stufe 1 Teil A): Analyse der Auswahl aus dem Katalog, Vorschau-Sitzungen über den Hilfsprozess,
/// Einstellungen. Das Rechnen und Speichern steckt in <see cref="MergePreviewSession"/>. Kennt seine offenen Sitzungen: Ende der
/// Katalog-Sitzung (<see cref="DisposeAsync"/>, vor dem Entsorgen der Dienste bzw. dem Aushängen) bricht alle ab — auch ein
/// laufendes Speichern — und wartet, bis ihre Hilfsprozesse beendet sind (0.5.48).
/// </summary>
public sealed class MergeService : IMergeService, IAsyncDisposable, IDisposable
{
    /// <summary>Zeit-Nachbarn: gleicher Ordner, gleiche Kamera, ± 2 min (ADR-0078 Punkt 7).</summary>
    public static readonly TimeSpan NeighborWindow = TimeSpan.FromMinutes(2);

    private static readonly string[] JpegSiblings = [".jpg", ".jpeg", ".JPG", ".JPEG"];

    private readonly ILibraryContextFactory _factory;
    private readonly IImagingWorkerHost _host;
    private readonly IPanoramaStitcher _stitcher;
    private readonly IFileOperationService _fileOps;
    private readonly IMergeSettingsStore _settings;
    private readonly ICatalogStoragePaths _storage;
    private readonly IAlbumService? _albums;
    private readonly IActivityCenter? _activities;
    private readonly IForegroundWork? _foreground;
    private readonly IMessenger _messenger;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _availabilityLock = new(1, 1);
    private readonly Lock _sessionsLock = new();
    private readonly HashSet<MergePreviewSession> _sessions = [];
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MergeAvailability? _availability;
    private int _tempCleaned;
    private int _disposing;

    public MergeService(
        ILibraryContextFactory factory,
        IImagingWorkerHost host,
        IPanoramaStitcher stitcher,
        IFileOperationService fileOps,
        IMergeSettingsStore settings,
        ICatalogStoragePaths storage,
        IAlbumService? albums = null,
        IActivityCenter? activities = null,
        IForegroundWork? foreground = null,
        IMessenger? messenger = null,
        ILogger<MergeService>? logger = null)
    {
        _factory = factory;
        _host = host;
        _stitcher = stitcher;
        _fileOps = fileOps;
        _settings = settings;
        _storage = storage;
        _albums = albums;
        _activities = activities;
        _foreground = foreground;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    internal ILibraryContextFactory Factory => _factory;

    internal IImagingWorkerHost Host => _host;

    internal IPanoramaStitcher Stitcher => _stitcher;

    internal IFileOperationService FileOps => _fileOps;

    internal IMergeSettingsStore SettingsStore => _settings;

    internal ICatalogStoragePaths Storage => _storage;

    internal IAlbumService? Albums => _albums;

    internal IActivityCenter? Activities => _activities;

    internal IForegroundWork? Foreground => _foreground;

    internal IMessenger Messenger => _messenger;

    internal ILogger Logger => _logger;

    /// <summary>
    /// Verfügbarkeit; gemerkt wird nur, was sich in dieser Sitzung nicht mehr ändert (Programmdatei fehlt bzw. OpenCV geladen) —
    /// ein einmaliger Startfehler (Virenscanner, zu wenig Speicher, Zeitgrenze) wird beim nächsten Versuch neu geprüft (0.5.48).
    /// </summary>
    public async Task<MergeAvailability> GetAvailabilityAsync(CancellationToken ct = default)
    {
        if (_availability is { } known)
        {
            return known;
        }

        await _availabilityLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_availability is { } again)
            {
                return again;
            }

            if (!_host.IsInstalled)
            {
                return _availability = new MergeAvailability(false, "Der Rechen-Prozess für „Zusammenfügen“ fehlt im Programmordner.", null);
            }

            try
            {
                await using var probe = await _host.StartAsync(1, ct).ConfigureAwait(false);
                return _availability = new MergeAvailability(true, null, probe.OpenCvVersion);
            }
            catch (ImagingWorkerException ex) when (ex.Failure == ImagingFailure.NotFound)
            {
                return _availability = new MergeAvailability(false, ex.Message, null);
            }
            catch (ImagingWorkerException ex)
            {
                _logger.LogWarning("Zusammenfügen: OpenCV nicht verfügbar ({Failure})", ex.Failure);
                return new MergeAvailability(false, "OpenCV ließ sich nicht laden (VC-Laufzeit oder Virenscanner?) — „Zusammenfügen“ ist gerade nicht verfügbar.", null, Transient: true);
            }
        }
        finally
        {
            _availabilityLock.Release();
        }
    }

    public Task<MergeSettings> LoadSettingsAsync(CancellationToken ct = default) => _settings.LoadAsync(ct);

    public Task SaveSettingsAsync(MergeSettings settings, CancellationToken ct = default) => _settings.SaveAsync(settings, ct);

    public Task RememberColorBoostAsync(int percent, CancellationToken ct = default) => _settings.RememberColorBoostAsync(percent, ct);

    public Task RememberSphereFormAsync(MergeSphereForm form, CancellationToken ct = default) => _settings.RememberSphereFormAsync(form, ct);

    public IMergePreviewSession OpenPreviewSession(MergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanStaleTempOnce();
        var session = new MergePreviewSession(this, request);
        lock (_sessionsLock)
        {
            // unter der Sperre: DisposeAsync setzt das Ende vor seinem Schnappschuss — keine Sitzung rutscht durch
            if (Volatile.Read(ref _disposing) != 0)
            {
                throw new MergeException("„Zusammenfügen“ ist beendet — die Bibliothek wird gerade geschlossen.");
            }

            _sessions.Add(session);
        }

        return session;
    }

    // ------------------------------------------------------------------ Sitzungen und Ende

    /// <summary>true, solange eine Sitzung speichert (auch nach „Im Hintergrund weiterrechnen“, Fenster zu).</summary>
    public bool IsSaving
    {
        get
        {
            lock (_sessionsLock)
            {
                return _sessions.Any(s => s.IsSaving);
            }
        }
    }

    /// <summary>Bricht laufende Speichern ab (Rückfrage „Abbrechen und …“ beim Zusperren, Wechseln, Beenden).</summary>
    public void CancelSaves()
    {
        foreach (var session in Snapshot().Where(s => s.IsSaving))
        {
            session.CancelSave();
        }
    }

    internal void Forget(MergePreviewSession session)
    {
        lock (_sessionsLock)
        {
            _sessions.Remove(session);
        }
    }

    private List<MergePreviewSession> Snapshot()
    {
        lock (_sessionsLock)
        {
            return [.. _sessions];
        }
    }

    /// <summary>
    /// Ende der Katalog-Sitzung: keine neuen Sitzungen, alle offenen abbrechen (auch ein Speichern) und warten, bis sie
    /// aufgeräumt und ihre Hilfsprozesse beendet sind. Mehrfach aufrufbar.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            await _disposed.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            var sessions = Snapshot();
            if (sessions.Count > 0)
            {
                _logger.LogInformation("Zusammenfügen: Sitzungsende — {Count} offene Sitzung(en) werden abgebrochen", sessions.Count);
            }

            foreach (var session in sessions)
            {
                session.Abandon();
            }

            await Task.WhenAll(sessions.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Zusammenfügen: Sitzungsende nicht sauber ({Error})", ex.GetType().Name);
        }
        finally
        {
            _disposed.TrySetResult();
        }
    }

    /// <summary>Synchron für den DI-Container (Beenden): wie <see cref="DisposeAsync"/>, höchstens 20 s.</summary>
    public void Dispose()
    {
        if (_disposed.Task.IsCompleted)
        {
            return;
        }

        if (!DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(20)))
        {
            _logger.LogWarning("Zusammenfügen: Sitzungsende dauerte zu lange");
        }
    }

    // ------------------------------------------------------------------ Analyse

    internal sealed record SourceRow(int Id, int LibraryId, int FolderId, string FolderPath, string Name, string Extension, MediaType MediaType, bool Missing,
        long Size, int? Width, int? Height, DateTime? CapturedAt, DateTime ModifiedAtUtc, string? CameraModel)
    {
        public string FullPath => Path.Combine(FolderPath, Name);

        public DateTime SortTime => CapturedAt ?? ModifiedAtUtc;
    }

    public async Task<MergeAnalysis> AnalyzeAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToList();
        var rows = await LoadRowsAsync(ids, ct).ConfigureAwait(false);
        var notes = new List<MergeNote>();
        var libraries = rows.Select(r => r.LibraryId).Distinct().ToList();
        var mixed = libraries.Count > 1;
        if (mixed)
        {
            notes.Add(new MergeNote("mixed-libraries", "Die Fotos stammen aus verschiedenen Bibliotheken — Zusammenfügen geht nur innerhalb einer Bibliothek."));
        }

        // RAW (Stufe 3): JPEG-Geschwister im selben Ordner nehmen, wenn es im Katalog ist und nicht schon gewählt
        var chosen = new HashSet<int>(rows.Select(r => r.Id));
        var siblings = await RawSiblingsAsync(rows.Where(r => IsRaw(r.Extension)).ToList(), ct).ConfigureAwait(false);
        var effective = new List<(SourceRow Row, int? ReplacesRaw)>();
        int rawReplaced = 0, rawExcluded = 0;
        foreach (var row in rows)
        {
            if (IsRaw(row.Extension) && siblings.TryGetValue(row.Id, out var sibling) && !chosen.Contains(sibling.Id))
            {
                chosen.Add(sibling.Id);
                effective.Add((sibling, row.Id));
                rawReplaced++;
                continue;
            }

            if (IsRaw(row.Extension) && siblings.ContainsKey(row.Id))
            {
                continue; // Geschwister ist schon gewählt
            }

            if (IsRaw(row.Extension))
            {
                rawExcluded++;
            }

            effective.Add((row, null));
        }

        var ordered = effective.OrderBy(e => e.Row.SortTime).ThenBy(e => e.Row.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var sources = new List<MergeSourceInfo>(ordered.Count);
        int onlineOnly = 0;
        long onlineBytes = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var (row, replaces) = ordered[i];
            var state = StateOf(row);
            if (state == MergeSourceState.OnlineOnly)
            {
                onlineOnly++;
                onlineBytes += row.Size;
            }

            sources.Add(new MergeSourceInfo(row.Id, i, row.Name, row.CapturedAt, row.CameraModel, row.Width, row.Height, row.Size, state, replaces));
        }

        void Count(MergeSourceState state, string code, string singular, string plural)
        {
            var n = sources.Count(s => s.State == state);
            if (n > 0)
            {
                notes.Add(new MergeNote(code + ":" + n.ToString(CultureInfo.InvariantCulture), n == 1 ? singular : string.Create(CultureInfo.GetCultureInfo("de-DE"), $"{n:N0} {plural}")));
            }
        }

        Count(MergeSourceState.Video, "videos", "1 Video wird nicht verwendet.", "Videos werden nicht verwendet.");
        Count(MergeSourceState.Archive, "archives", "1 Archiv wird nicht verwendet.", "Archive werden nicht verwendet.");
        Count(MergeSourceState.Missing, "missing", "1 Foto fehlt und wird nicht verwendet.", "Fotos fehlen und werden nicht verwendet.");
        Count(MergeSourceState.Offline, "offline", "1 Foto liegt auf einem nicht erreichbaren Laufwerk.", "Fotos liegen auf einem nicht erreichbaren Laufwerk.");
        Count(MergeSourceState.Unsupported, "unsupported", "1 Datei hat ein Format, das Stufe 1 nicht liest.", "Dateien haben ein Format, das Stufe 1 nicht liest.");
        if (rawReplaced > 0)
        {
            notes.Add(new MergeNote("raw-replaced:" + rawReplaced, $"RAW-Fotos: die JPEG-Geschwister werden verwendet ({rawReplaced}) — RAW/DNG kommt mit Stufe 3."));
        }

        if (rawExcluded > 0)
        {
            notes.Add(new MergeNote("raw-excluded:" + rawExcluded, $"{rawExcluded} RAW-Foto(s) ohne JPEG-Geschwister werden nicht verwendet — RAW/DNG kommt mit Stufe 3."));
        }

        if (onlineOnly > 0)
        {
            notes.Add(new MergeNote("online-only:" + onlineOnly, string.Create(CultureInfo.GetCultureInfo("de-DE"),
                $"{onlineOnly} von {sources.Count} Fotos liegen nur in OneDrive (≈ {Math.Max(1, onlineBytes / 1_000_000):N0} MB) — zum Zusammenfügen herunterladen?")));
        }

        var usable = sources.Count(s => s.IsUsable) + onlineOnly;
        if (usable > MergeLimits.MaxPanoramaSources)
        {
            notes.Add(new MergeNote("too-many", $"Panorama: höchstens {MergeLimits.MaxPanoramaSources} Fotos."));
        }

        var neighbors = Array.Empty<MergeSourceInfo>() as IReadOnlyList<MergeSourceInfo>;
        if (sources.Count(s => s.IsUsable) == 1 && rows.Count >= 1)
        {
            var single = ordered.First(e => StateOf(e.Row) == MergeSourceState.Usable).Row;
            neighbors = await NeighborsAsync(single, ct).ConfigureAwait(false);
        }

        var suits = !mixed && usable >= MergeLimits.MinSources && usable <= MergeLimits.MaxPanoramaSources;
        var reason = mixed ? "Fotos aus verschiedenen Bibliotheken"
            : usable < MergeLimits.MinSources ? (usable == 1 ? "nur 1 Foto angehakt" : "keine lesbaren Fotos")
            : usable > MergeLimits.MaxPanoramaSources ? $"höchstens {MergeLimits.MaxPanoramaSources} Fotos"
            : "überlappende Fotos einer Szene";
        var methods = Enum.GetValues<MergeMethod>().Select(m => m == MergeMethod.Panorama
            ? new MergeMethodInfo(m, MergeTexts.Method(m), true, suits, suits ? "passt · " + reason : "eher nicht · " + reason)
            : new MergeMethodInfo(m, MergeTexts.Method(m), false, false, "kommt später")).ToList();
        return new MergeAnalysis(sources, neighbors, mixed ? null : libraries.FirstOrDefault(), mixed, methods, MergeMethod.Panorama, PanoramaProjection.Auto, notes, onlineOnly, onlineBytes);
    }

    internal static bool IsRaw(string extension) => MergeLimits.RawExtensions.Contains(extension);

    internal static MergeSourceState StateOf(SourceRow row)
    {
        if (row.MediaType == MediaType.Video)
        {
            return MergeSourceState.Video;
        }

        if (row.MediaType == MediaType.Archive)
        {
            return MergeSourceState.Archive;
        }

        if (IsRaw(row.Extension))
        {
            return MergeSourceState.Raw;
        }

        if (!MergeLimits.ReadableExtensions.Contains(row.Extension))
        {
            return MergeSourceState.Unsupported;
        }

        var attributes = CloudFiles.TryGetAttributes(row.FullPath);
        if (attributes is null)
        {
            // fehlt — oder Laufwerk nicht erreichbar (nie als „fehlt“ werten, PLAN Kap. 12)
            var root = Path.GetPathRoot(row.FolderPath);
            return !string.IsNullOrEmpty(root) && !Directory.Exists(root) ? MergeSourceState.Offline : MergeSourceState.Missing;
        }

        return CloudFiles.IsOnlineOnly(attributes.Value) ? MergeSourceState.OnlineOnly : MergeSourceState.Usable;
    }

    internal async Task<List<SourceRow>> LoadRowsAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var list = new List<SourceRow>(ids.Count);
        if (ids.Count == 0)
        {
            return list;
        }

        await using var ctx = _factory.Create();
        foreach (var chunk in ids.Chunk(400))
        {
            var rows = await ctx.Files.AsNoTracking()
                .Where(f => chunk.Contains(f.Id))
                .Join(ctx.Folders, f => f.FolderId, d => d.Id, (f, d) => new { f.Id, f.LibraryId, f.FolderId, d.Path, f.Name, f.Extension, f.MediaType, f.Missing, f.Size, f.Width, f.Height, f.CapturedAt, f.ModifiedAtUtc, f.CameraModel })
                .ToListAsync(ct).ConfigureAwait(false);
            list.AddRange(rows.Select(r => new SourceRow(r.Id, r.LibraryId, r.FolderId, r.Path, r.Name, r.Extension, r.MediaType, r.Missing, r.Size, r.Width, r.Height, r.CapturedAt, r.ModifiedAtUtc, r.CameraModel)));
        }

        return list;
    }

    private async Task<Dictionary<int, SourceRow>> RawSiblingsAsync(List<SourceRow> raws, CancellationToken ct)
    {
        var result = new Dictionary<int, SourceRow>();
        if (raws.Count == 0)
        {
            return result;
        }

        await using var ctx = _factory.Create();
        foreach (var raw in raws)
        {
            var stem = Path.GetFileNameWithoutExtension(raw.Name);
            var names = JpegSiblings.Select(e => stem + e).ToList();
            var sibling = await ctx.Files.AsNoTracking()
                .Where(f => f.FolderId == raw.FolderId && names.Contains(f.Name) && f.MediaType == MediaType.Image)
                .OrderBy(f => f.Id)
                .Select(f => new SourceRow(f.Id, f.LibraryId, f.FolderId, raw.FolderPath, f.Name, f.Extension, f.MediaType, f.Missing, f.Size, f.Width, f.Height, f.CapturedAt, f.ModifiedAtUtc, f.CameraModel))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (sibling is not null)
            {
                result[raw.Id] = sibling;
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<MergeSourceInfo>> NeighborsAsync(SourceRow single, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var from = single.SortTime - NeighborWindow;
        var to = single.SortTime + NeighborWindow;
        var camera = single.CameraModel;
        var candidates = await ctx.Files.AsNoTracking()
            .Where(f => f.FolderId == single.FolderId && f.Id != single.Id && f.MediaType == MediaType.Image && !f.Missing && f.CameraModel == camera)
            .Select(f => new { f.Id, f.Name, f.Extension, f.Size, f.Width, f.Height, f.CapturedAt, f.ModifiedAtUtc, f.CameraModel, f.LibraryId, f.FolderId, f.MediaType, f.Missing })
            .ToListAsync(ct).ConfigureAwait(false);
        return candidates
            .Select(c => new SourceRow(c.Id, c.LibraryId, c.FolderId, single.FolderPath, c.Name, c.Extension, c.MediaType, c.Missing, c.Size, c.Width, c.Height, c.CapturedAt, c.ModifiedAtUtc, c.CameraModel))
            .Where(r => r.SortTime >= from && r.SortTime <= to && !IsRaw(r.Extension))
            .OrderBy(r => r.SortTime).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MergeLimits.MaxPanoramaSources)
            .Select((r, i) => new MergeSourceInfo(r.Id, i, r.Name, r.CapturedAt, r.CameraModel, r.Width, r.Height, r.Size, StateOf(r)))
            .ToList();
    }

    // ------------------------------------------------------------------ Temp-Ordner

    /// <summary>Reste abgestürzter Sitzungen (älter als ein Tag) im Arbeitsordner wegräumen — einmal je Katalog-Sitzung.</summary>
    private void CleanStaleTempOnce()
    {
        if (Interlocked.Exchange(ref _tempCleaned, 1) != 0)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(_host.WorkRoot))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(_host.WorkRoot))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Zusammenfügen: alte Zwischenstände nicht entfernt");
        }
    }
}
