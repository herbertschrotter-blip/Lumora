using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;

namespace Lumora.Slideshow;

/// <summary>Eine angezeigte Folie: Datei, Bild (null bei Video), Zoom-Plan und Anzeigedauer.</summary>
public sealed record SlideshowSlide(int Index, SlideshowItemInfo Info, BitmapSource? Image, KenBurnsPlan Plan, TimeSpan Duration)
{
    public bool IsVideo => Image is null && Info.MediaType == MediaType.Video;
}

/// <summary>
/// Ablauf einer Diashow (ADR-0059), ohne Fenster testbar: Reihenfolge aus einer <see cref="ISlideshowSequence"/> (Ids
/// blockweise, Datei-Infos stapelweise über <see cref="ISlideshowItemLoader"/>), Weiter/Zurück/Anfang/Ende mit Umlauf,
/// Pause, Überspringen fehlender/kaputter/nicht abspielbarer Dateien (in Blätterrichtung), Vorladen des nächsten Bildes
/// (höchstens drei dekodierte Bilder im Speicher), Videos höchstens <see cref="SlideshowSettings.MaxVideoSeconds"/> lang,
/// Leiste/Mauszeiger blenden nach 2 s ohne Bewegung aus. Zeitgeber über <see cref="TimeProvider"/>; ihre Rückrufe gehen
/// über <c>post</c> auf den UI-Thread. Überblendung und Zoom-Animation macht das Fenster (<see cref="SlideChanged"/>).
/// </summary>
public sealed partial class SlideshowViewModel : ObservableObject, IDisposable
{
    /// <summary>Ids/Infos je Block (eine Abfrage je Block).</summary>
    internal const int BlockSize = 50;

    private const int CachedBlocks = 6;
    private const int ImageCacheSize = 3;

    /// <summary>Leiste und Mauszeiger blenden nach dieser Zeit ohne Bewegung aus.</summary>
    public static readonly TimeSpan ChromeHideDelay = TimeSpan.FromSeconds(2);

    private readonly ISlideshowSequence _sequence;
    private readonly ISlideshowItemLoader _loader;
    private readonly ISlideshowImageDecoder _decoder;
    private readonly ISlideshowVideoPlayer? _video;
    private readonly ISlideshowSettingsStore? _store;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly int _startIndex;
    private readonly Dictionary<int, Block> _blocks = new();
    private readonly LinkedList<int> _blockLru = new();
    private readonly Dictionary<int, ImageEntry> _images = new();
    private readonly LinkedList<int> _imageLru = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private bool _videoEndedWhilePaused;

    private SlideshowSettings _settings;
    private CancellationTokenSource? _showCts;
    private ITimer? _slideTimer;
    private ITimer? _chromeTimer;
    private long _slideStarted;
    private TimeSpan _remaining;
    private int _version;
    private int _targetIndex;
    private bool _zoomInNext = true;
    private bool _disposed;
    private bool _suppressPersist;

    public SlideshowViewModel(
        ISlideshowSequence sequence,
        int startIndex,
        string? title,
        SlideshowSettings settings,
        ISlideshowItemLoader loader,
        ISlideshowImageDecoder decoder,
        ISlideshowVideoPlayer? video,
        TimeProvider time,
        Action<Action> post,
        ILogger logger,
        ISlideshowSettingsStore? store = null)
    {
        _sequence = sequence;
        _loader = loader;
        _decoder = decoder;
        _video = video;
        _store = store;
        _time = time;
        _post = post;
        _logger = logger;
        _settings = settings.Normalized();
        Count = sequence.Count;
        _startIndex = Count == 0 ? 0 : Math.Clamp(startIndex, 0, Count - 1);
        _targetIndex = _startIndex;
        _title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();

        _suppressPersist = true;
        SlideSeconds = _settings.SlideSeconds;
        KenBurnsEnabled = _settings.KenBurns;
        ShowCaption = _settings.ShowCaption;
        _suppressPersist = false;

        if (_video is not null)
        {
            _video.Ended += OnVideoEnded;
        }
    }

    /// <summary>Neue Folie (UI-Thread): das Fenster blendet über und startet Zoom und Fortschrittsbalken.</summary>
    public event EventHandler<SlideshowSlide>? SlideChanged;

    /// <summary>Esc / Beenden.</summary>
    public event EventHandler? CloseRequested;

    public int Count { get; }

    /// <summary>Ziel-Pixelgröße fürs Dekodieren (Bildschirm × Skalierung × Zoom-Reserve); das Fenster setzt sie.</summary>
    public int DecodeWidth { get; set; } = 1920;

    public int DecodeHeight { get; set; } = 1080;

    /// <summary>Wirksame Einstellungen (inkl. Änderungen über die Leiste).</summary>
    public SlideshowSettings Settings => _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTitle))]
    private string? _title;

    public bool HasTitle => !string.IsNullOrEmpty(Title);

    [ObservableProperty] private int _currentIndex = -1;
    [ObservableProperty] private string _positionText = string.Empty;
    [ObservableProperty] private SlideshowSlide? _currentSlide;
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] private bool _isVideo;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isChromeVisible = true;
    [ObservableProperty] private string? _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDate), nameof(IsCaptionVisible))]
    private string? _dateText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlace), nameof(IsCaptionVisible))]
    private string? _placeText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDuration3), nameof(IsDuration5), nameof(IsDuration8))]
    private int _slideSeconds = 5;

    [ObservableProperty] private bool _kenBurnsEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCaptionVisible))]
    private bool _showCaption = true;

    /// <summary>Übersprungene Dateien (fehlend, kaputt, Archiv, Video ohne Wiedergabe) seit dem Start.</summary>
    public int SkippedCount { get; private set; }

    /// <summary>Die Maus steht über der Leiste: nicht ausblenden.</summary>
    public bool IsChromeHovered { get; set; }

    public bool HasDate => !string.IsNullOrEmpty(DateText);

    public bool HasPlace => !string.IsNullOrEmpty(PlaceText);

    public bool IsCaptionVisible => ShowCaption && (HasDate || HasPlace);

    public bool IsDuration3
    {
        get => SlideSeconds == 3;
        set
        {
            if (value)
            {
                SlideSeconds = 3;
            }
        }
    }

    public bool IsDuration5
    {
        get => SlideSeconds == 5;
        set
        {
            if (value)
            {
                SlideSeconds = 5;
            }
        }
    }

    public bool IsDuration8
    {
        get => SlideSeconds == 8;
        set
        {
            if (value)
            {
                SlideSeconds = 8;
            }
        }
    }

    /// <summary>Verbleibende Zeit der aktuellen Folie (auch während der Pause).</summary>
    public TimeSpan Remaining => IsPaused || _slideTimer is null ? _remaining : _remaining - _time.GetElapsedTime(_slideStarted);

    partial void OnSlideSecondsChanged(int value)
    {
        _settings = _settings with { SlideSeconds = SlideshowSettings.NearestDuration(value) };
        Persist();
    }

    partial void OnKenBurnsEnabledChanged(bool value)
    {
        _settings = _settings with { KenBurns = value };
        Persist();
    }

    partial void OnShowCaptionChanged(bool value)
    {
        _settings = _settings with { ShowCaption = value };
        Persist();
    }

    // ----- Ablauf -----

    /// <summary>Erste Folie ab dem Startindex zeigen und die Leiste einblenden (Ausblenden nach 2 s).</summary>
    public Task StartAsync()
    {
        NotifyUserActivity();
        if (Count == 0)
        {
            StatusText = "Keine Fotos oder Videos für die Diashow.";
            return Task.CompletedTask;
        }

        return ShowAsync(_startIndex, +1);
    }

    public Task NextAsync() => Count == 0 ? Task.CompletedTask : ShowAsync(Wrap(_targetIndex + 1), +1);

    public Task PreviousAsync() => Count == 0 ? Task.CompletedTask : ShowAsync(Wrap(_targetIndex - 1), -1);

    public Task FirstAsync() => Count == 0 ? Task.CompletedTask : ShowAsync(0, +1);

    public Task LastAsync() => Count == 0 ? Task.CompletedTask : ShowAsync(Count - 1, -1);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Next() => RunSafeAsync(NextAsync);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Previous() => RunSafeAsync(PreviousAsync);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task First() => RunSafeAsync(FirstAsync);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Last() => RunSafeAsync(LastAsync);

    /// <summary>Pause / Weiter (Leertaste): Restzeit merken bzw. weiterlaufen lassen; Videos pausieren mit.</summary>
    [RelayCommand]
    public void TogglePause()
    {
        if (_disposed)
        {
            return;
        }

        if (!IsPaused)
        {
            _remaining = Remaining;
            if (_remaining < TimeSpan.Zero)
            {
                _remaining = TimeSpan.Zero;
            }

            StopSlideTimer();
            IsPaused = true;
            if (IsVideo)
            {
                _video?.Pause();
            }
        }
        else
        {
            IsPaused = false;
            if (_videoEndedWhilePaused && IsVideo)
            {
                // Das Video lief während der Pause zu Ende — nicht auf den Zeitgeber warten (sonst bliebe es schwarz).
                _videoEndedWhilePaused = false;
                _ = RunSafeAsync(NextAsync);
                return;
            }

            if (CurrentSlide is not null)
            {
                StartSlideTimer(_remaining, _version);
            }

            if (IsVideo)
            {
                _video?.Resume();
            }
        }
    }

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Maus bewegt oder Taste gedrückt: Leiste und Mauszeiger zeigen, nach 2 s Ruhe wieder ausblenden.</summary>
    public void NotifyUserActivity()
    {
        if (_disposed)
        {
            return;
        }

        IsChromeVisible = true;
        if (_chromeTimer is null)
        {
            _chromeTimer = _time.CreateTimer(static s => ((SlideshowViewModel)s!).OnChromeTimer(), this, ChromeHideDelay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _chromeTimer.Change(ChromeHideDelay, Timeout.InfiniteTimeSpan); // ein Zeitgeber, bei jeder Bewegung neu gestellt
        }
    }

    private void OnChromeTimer() => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        if (IsChromeHovered)
        {
            NotifyUserActivity();
            return;
        }

        IsChromeVisible = false;
    });

    private async Task ShowAsync(int index, int direction)
    {
        if (_disposed)
        {
            return;
        }

        var version = ++_version;
        _videoEndedWhilePaused = false;
        _showCts?.Cancel();
        _showCts?.Dispose();
        _showCts = new CancellationTokenSource();
        var ct = _showCts.Token;
        StopSlideTimer();
        _targetIndex = index;
        IsLoading = true;
        try
        {
            for (var attempt = 0; attempt < Count; attempt++)
            {
                var prepared = await PrepareAsync(index, ct).ConfigureAwait(true);
                if (version != _version || _disposed)
                {
                    return;
                }

                if (prepared is not null && await ApplyAsync(prepared, index, version, ct).ConfigureAwait(true))
                {
                    Prefetch(Wrap(index + direction));
                    return;
                }

                if (version != _version || _disposed)
                {
                    return;
                }

                SkippedCount++;
                index = Wrap(index + direction);
                _targetIndex = index;
            }

            ShowNothing("Keine Fotos oder Videos zum Anzeigen — alle Dateien fehlen oder lassen sich nicht öffnen.");
        }
        catch (OperationCanceledException)
        {
            // überholt (Blättern) oder geschlossen
        }
        catch (Exception ex)
        {
            if (version == _version && !_disposed)
            {
                _logger.LogWarning(ex, "Diashow: Reihenfolge oder Katalog nicht lesbar");
                ShowNothing($"Die Diashow kann nicht weiterlesen: {ex.Message}");
            }
        }
        finally
        {
            if (version == _version && !_disposed)
            {
                IsLoading = false;
            }
        }
    }

    private void ShowNothing(string message)
    {
        if (IsVideo)
        {
            _video?.Stop();
        }

        IsVideo = false;
        IsImage = false;
        CurrentSlide = null;
        DateText = null;
        PlaceText = null;
        StatusText = message;
    }

    /// <summary>Datei an <paramref name="index"/> vorbereiten (Infos, Bild dekodiert); null = überspringen.</summary>
    private async Task<Prepared?> PrepareAsync(int index, CancellationToken ct)
    {
        var info = await GetInfoAsync(index, ct).ConfigureAwait(true);
        if (info is null || info.Missing)
        {
            return null;
        }

        switch (info.MediaType)
        {
            case MediaType.Image:
                try
                {
                    var image = await GetImageAsync(info).WaitAsync(ct).ConfigureAwait(true);
                    return new Prepared(info, image);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // fehlende/kaputte Datei: überspringen (Pfad nur im Debug-Protokoll, ADR-0049)
                    _logger.LogDebug(ex, "Diashow: Bild übersprungen (FileId {FileId})", info.FileId);
                    InvalidateImage(info.FileId);
                    return null;
                }

            case MediaType.Video when _settings.PlayVideos && _video is not null:
                return new Prepared(info, null);

            default:
                return null; // Archive, Videos ohne Wiedergabe
        }
    }

    /// <summary>Folie übernehmen; bei Videos erst nach gelungenem Start. False = überspringen.</summary>
    private async Task<bool> ApplyAsync(Prepared prepared, int index, int version, CancellationToken ct)
    {
        var info = prepared.Info;
        KenBurnsPlan plan;
        TimeSpan duration;
        if (prepared.Image is { } image)
        {
            if (IsVideo)
            {
                _video?.Stop();
            }

            IsVideo = false;
            IsImage = true;
            plan = KenBurnsEnabled ? KenBurnsPlanner.Plan(info.Faces, _zoomInNext) : KenBurnsPlan.None;
            _zoomInNext = !_zoomInNext;
            duration = TimeSpan.FromSeconds(_settings.SlideSeconds);
            Commit(new SlideshowSlide(index, info, image.Bitmap, plan, duration));
            if (!IsPaused)
            {
                StartSlideTimer(duration, version);
            }
            else
            {
                _remaining = duration;
            }

            return true;
        }

        // Video: Ansicht umschalten, dann starten; gelingt der Start nicht, weiter mit der nächsten Datei.
        IsImage = false;
        IsVideo = true;
        var max = TimeSpan.FromSeconds(_settings.MaxVideoSeconds);
        duration = VideoDuration(info.DurationMs, max);
        var started = await _video!.PlayAsync(info.Path, ct).ConfigureAwait(true);
        if (version != _version || _disposed)
        {
            return true; // überholt — die neue Folie räumt auf
        }

        if (!started)
        {
            _logger.LogDebug("Diashow: Video übersprungen (FileId {FileId})", info.FileId);
            return false;
        }

        Commit(new SlideshowSlide(index, info, null, KenBurnsPlan.None, duration));
        if (IsPaused)
        {
            _video.Pause();
            _remaining = duration;
        }
        else
        {
            StartSlideTimer(duration, version);
        }

        return true;
    }

    /// <summary>Anzeigedauer eines Videos: seine Länge (+ 0,5 s Puffer), höchstens <paramref name="max"/>.</summary>
    internal static TimeSpan VideoDuration(int? durationMs, TimeSpan max)
    {
        if (durationMs is not > 0)
        {
            return max;
        }

        var length = TimeSpan.FromMilliseconds(durationMs.Value + 500);
        return length < max ? length : max;
    }

    /// <summary>Nutzungs-Signale (ADR-0072): Anzeigezeit je Folie, nur solange das Fenster vorne ist, gedämpft.</summary>
    public Lumora.Services.Usage.UsageViewTracker? UsageTracker { get; init; }

    /// <summary>Fenster vorne bzw. im Hintergrund (vom Fenster gemeldet).</summary>
    public void SetUsageActive(bool active) => UsageTracker?.SetActive(active);

    private void Commit(SlideshowSlide slide)
    {
        UsageTracker?.Show(slide.Info.FileId); // ADR-0072
        CurrentIndex = slide.Index;
        PositionText = $"{slide.Index + 1:N0} / {Count:N0}";
        var caption = SlideshowCaption.For(slide.Info.CaptureDate, slide.Info.PlaceName);
        DateText = caption.DateText;
        PlaceText = caption.PlaceText;
        StatusText = null;
        CurrentSlide = slide;
        SlideChanged?.Invoke(this, slide);
    }

    private void StartSlideTimer(TimeSpan duration, int version)
    {
        StopSlideTimer();
        _remaining = duration;
        _slideStarted = _time.GetTimestamp();
        _slideTimer = _time.CreateTimer(
            static s =>
            {
                var (vm, v) = ((SlideshowViewModel, int))s!;
                vm._post(() => vm.OnSlideTimer(v));
            },
            (this, version),
            duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
            Timeout.InfiniteTimeSpan);
    }

    private void StopSlideTimer()
    {
        _slideTimer?.Dispose();
        _slideTimer = null;
    }

    private void OnSlideTimer(int version)
    {
        if (_disposed || version != _version || IsPaused)
        {
            return;
        }

        _ = RunSafeAsync(NextAsync);
    }

    private void OnVideoEnded(object? sender, EventArgs e)
    {
        var version = _version;
        _post(() =>
        {
            if (_disposed || version != _version || !IsVideo || CurrentSlide is not { IsVideo: true })
            {
                return;
            }

            if (IsPaused)
            {
                _videoEndedWhilePaused = true; // beim Fortsetzen gleich weiter
                return;
            }

            _ = RunSafeAsync(NextAsync);
        });
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diashow: Blättern fehlgeschlagen");
        }
    }

    private int Wrap(int index) => Count == 0 ? 0 : ((index % Count) + Count) % Count;

    // ----- Ids und Datei-Infos (blockweise) -----

    private async Task<SlideshowItemInfo?> GetInfoAsync(int index, CancellationToken ct)
    {
        var block = GetBlock(index / BlockSize);
        var ids = await block.Ids.WaitAsync(ct).ConfigureAwait(true);
        var offset = index % BlockSize;
        if (offset >= ids.Count || ids[offset] <= 0)
        {
            return null;
        }

        block.Infos ??= LoadInfosAsync(block, ids);
        var infos = await block.Infos.WaitAsync(ct).ConfigureAwait(true);
        return infos.TryGetValue(ids[offset], out var info) ? info : null;
    }

    private Task<IReadOnlyDictionary<int, SlideshowItemInfo>> LoadInfosAsync(Block block, IReadOnlyList<int> ids)
    {
        var task = _loader.LoadAsync(ids.Where(id => id > 0).ToList(), CancellationToken.None);
        ForgetOnFault(task, block);
        return task;
    }

    private Block GetBlock(int number)
    {
        if (_blocks.TryGetValue(number, out var existing))
        {
            _blockLru.Remove(number);
            _blockLru.AddFirst(number);
            return existing;
        }

        var block = new Block(number);
        block.Ids = _sequence.GetRangeAsync(number * BlockSize, BlockSize, CancellationToken.None);
        ForgetOnFault(block.Ids, block);
        _blocks[number] = block;
        _blockLru.AddFirst(number);
        while (_blocks.Count > CachedBlocks && _blockLru.Last is { } last)
        {
            _blocks.Remove(last.Value);
            _blockLru.RemoveLast();
        }

        return block;
    }

    /// <summary>Ein fehlgeschlagener Block wird beim nächsten Zugriff neu gelesen.</summary>
    private void ForgetOnFault(Task task, Block block)
        => task.ContinueWith(
            _ => _post(() =>
            {
                if (_blocks.TryGetValue(block.Number, out var current) && ReferenceEquals(current, block))
                {
                    _blocks.Remove(block.Number);
                    _blockLru.Remove(block.Number);
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // ----- Bilder (aktuelles, nächstes, vorheriges) -----

    private Task<SlideshowImage> GetImageAsync(SlideshowItemInfo info)
    {
        if (_images.TryGetValue(info.FileId, out var entry))
        {
            if (string.Equals(entry.Path, info.Path, StringComparison.OrdinalIgnoreCase) && !entry.Task.IsFaulted && !entry.Task.IsCanceled)
            {
                _imageLru.Remove(info.FileId);
                _imageLru.AddFirst(info.FileId);
                return entry.Task;
            }

            InvalidateImage(info.FileId);
        }

        var cts = new CancellationTokenSource();
        var task = _decoder.DecodeAsync(info.Path, DecodeWidth, DecodeHeight, cts.Token);
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _images[info.FileId] = new ImageEntry(info.Path, task, cts);
        _imageLru.AddFirst(info.FileId);
        while (_images.Count > ImageCacheSize && _imageLru.Last is { } last)
        {
            InvalidateImage(last.Value);
        }

        return task;
    }

    private void InvalidateImage(int fileId)
    {
        if (_images.Remove(fileId, out var entry))
        {
            _imageLru.Remove(fileId);
            if (!entry.Task.IsCompleted)
            {
                // Laufende Dekodierung abbrechen; die CTS erst freigeben, wenn sie fertig ist (Token noch in Gebrauch).
                entry.Cts.Cancel();
                var cts = entry.Cts;
                _ = entry.Task.ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                entry.Cts.Dispose();
            }
        }
    }

    /// <summary>Zahl der vorgehaltenen Bilder (Tests: Speicher bleibt begrenzt).</summary>
    internal int CachedImageCount => _images.Count;

    /// <summary>Nächste Datei im Hintergrund vorbereiten (Ids, Infos, Bild dekodiert) — kein Ruckeln beim Wechsel.</summary>
    private void Prefetch(int index)
    {
        if (Count < 2)
        {
            return;
        }

        _ = PrefetchAsync(index);
    }

    private async Task PrefetchAsync(int index)
    {
        try
        {
            var info = await GetInfoAsync(index, CancellationToken.None).ConfigureAwait(true);
            if (!_disposed && info is { Missing: false, MediaType: MediaType.Image })
            {
                _ = GetImageAsync(info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow: Vorladen fehlgeschlagen");
        }
    }

    // ----- Einstellungen -----

    private void Persist()
    {
        if (_suppressPersist || _store is null || _disposed)
        {
            return;
        }

        var snapshot = _settings;
        _ = PersistAsync(snapshot);
    }

    /// <summary>
    /// Nur die Werte der Leiste (Dauer, Zoom, Datum &amp; Ort) in die gespeicherten Einstellungen übernehmen — was inzwischen
    /// in den Einstellungen geändert wurde (Reihenfolge, Videos), bleibt. Speichervorgänge laufen nacheinander.
    /// </summary>
    private async Task PersistAsync(SlideshowSettings snapshot)
    {
        await _persistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var stored = await _store!.LoadAsync().ConfigureAwait(false);
            await _store.SaveAsync(stored with { SlideSeconds = snapshot.SlideSeconds, KenBurns = snapshot.KenBurns, ShowCaption = snapshot.ShowCaption })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diashow-Einstellungen konnten nicht gespeichert werden");
        }
        finally
        {
            _persistGate.Release();
        }
    }

    /// <summary>Wartet, bis alle angestoßenen Speichervorgänge der Leiste fertig sind (Tests).</summary>
    internal async Task FlushPersistAsync()
    {
        await _persistGate.WaitAsync().ConfigureAwait(false);
        _persistGate.Release();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UsageTracker?.Stop(); // letzte Folie zählen (ADR-0072)
        _version++;
        _showCts?.Cancel();
        _showCts?.Dispose();
        _showCts = null;
        StopSlideTimer();
        _chromeTimer?.Dispose();
        _chromeTimer = null;
        if (_video is not null)
        {
            _video.Ended -= OnVideoEnded;
            _video.Stop();
        }

        foreach (var id in _images.Keys.ToList())
        {
            InvalidateImage(id);
        }

        _blocks.Clear();
        _blockLru.Clear();
        CurrentSlide = null;
    }

    private sealed record Prepared(SlideshowItemInfo Info, SlideshowImage? Image);

    private sealed record ImageEntry(string Path, Task<SlideshowImage> Task, CancellationTokenSource Cts);

    private sealed class Block(int number)
    {
        public int Number { get; } = number;

        public Task<IReadOnlyList<int>> Ids { get; set; } = null!;

        public Task<IReadOnlyDictionary<int, SlideshowItemInfo>>? Infos { get; set; }
    }
}
