using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LibVLCSharp.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Lightbox.Controls;
using Lumora.Lightbox.Imaging;
using Lumora.Services.Archive;
using Lumora.Services.Catalog;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Gallery.Categories;
using Lumora.Gallery.Tags;
using Lumora.Services.Categories;
using Lumora.Services.Tags;

namespace Lumora.Lightbox;

/// <summary>
/// ViewModel eines Lightbox-Fensters: Sequenz + aktueller Eintrag, Bild-Decode (Hintergrund,
/// Prefetch ±1), Video-Wiedergabe (ein MediaPlayer pro Fenster, Media pro Datei), Favorit/Bewertung
/// über <see cref="ICatalogUpdateService"/>, Papierkorb über <see cref="IFileOperationService"/>,
/// Info-Panel-Daten und Katalog-Sync via <see cref="FilesChangedMessage"/>.
/// Läuft auf dem UI-Thread; Worker-Ereignisse (LibVLC, Messenger) werden dispatcht.
/// </summary>
public sealed partial class LightboxViewModel : ObservableObject, IDisposable
{
    private const int PrefetchRadius = 1;
    private const int PageStep = 10;
    private const int StatusDisplayMs = 4000;
    private const int DbChunkSize = 500;

    private readonly LibVlcHost _vlc;
    private readonly ILibraryContextFactory _contextFactory;
    private readonly ICatalogUpdateService? _catalog;
    private readonly IFileOperationService? _fileOperations;
    private readonly IArchiveService? _archives;
    private readonly IArchiveMoveService? _archiveMove;
    private readonly ILogger<LightboxViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly LightboxImageCache _cache = new(capacity: 4);
    private readonly List<LightboxItem> _items = new();
    private readonly Dictionary<int, LightboxItem> _itemsById = new();

    private MediaPlayer? _player;
    private Media? _media;
    private long _lengthMs;
    private bool _isSeeking;
    private double? _pendingSeekFraction;
    private int _showVersion;
    private CancellationTokenSource? _statusCts;
    private string? _videoUnavailableReason;
    private string? _currentDimensions;
    private bool _disposed;

    public LightboxViewModel(
        LibVlcHost vlc,
        ILibraryContextFactory contextFactory,
        ILogger<LightboxViewModel> logger,
        ICatalogUpdateService? catalog = null,
        IFileOperationService? fileOperations = null,
        IArchiveService? archives = null,
        IArchiveMoveService? archiveMove = null,
        ITagService? tags = null,
        ITagPickerService? tagPicker = null,
        ICategoryService? categories = null,
        ICategoryPickerService? categoryPicker = null,
        Lumora.Services.Faces.Assignment.IFaceRecognitionService? faceRecognition = null,
        Lumora.Shell.IDialogService? dialogs = null,
        Gallery.ListView.IGalleryViewSettingsStore? viewSettings = null,
        Lumora.Services.Places.IGeoCatalogReader? geoReader = null,
        Lumora.Services.Motifs.IMotifTermService? motifs = null,
        Lumora.Slideshow.ISlideshowService? slideshow = null,
        Lumora.Places.Suggestions.PlacesSuggestionContext? geoSuggestions = null,
        Lumora.Marks.MarkSettingsState? markSettings = null)
    {
        InitMarks(markSettings); // Leiste unten mit Tags/Kategorien (Mockup 16, LightboxViewModel.Marks.cs)
        _slideshow = slideshow; // „Diashow ab hier“ (ADR-0059, LightboxViewModel.Slideshow.cs)
        _vlc = vlc;
        _contextFactory = contextFactory;
        _logger = logger;
        _catalog = catalog;
        _fileOperations = fileOperations;
        _archives = archives;
        _archiveMove = archiveMove;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _volume = Math.Clamp(vlc.LastVolume, 0, 100);
        _isMuted = vlc.LastMuted;

        WeakReferenceMessenger.Default.Register<FilesChangedMessage>(
            this,
            static (recipient, message) => ((LightboxViewModel)recipient).OnFilesChanged(message));
        InitTags(tags, tagPicker); // Tags anzeigen/bearbeiten (LightboxViewModel.Tags.cs, ADR-0021)
        InitCategories(categories, categoryPicker); // Kategorie-Werte anzeigen/zuweisen (LightboxViewModel.Categories.cs, ADR-0027)
        InitFaces(faceRecognition, dialogs, viewSettings); // Gesichtsrahmen, Taste F gemerkt (LightboxViewModel.Faces.cs, 0.2.38/0.2.39)
        InitCaptureDate(); // Zeile „Aufgenommen“ (LightboxViewModel.CaptureDate.cs, ADR-0046)
        InitPlaces(geoReader); // Zeile „Ort“ (LightboxViewModel.Places.cs, ADR-0050,
        InitMotifs(motifs); // Info-Panel „Motive“ (LightboxViewModel.Motifs.cs, ADR-0052)
        InitGeoHint(geoSuggestions); // Hinweis „Position geschätzt“ mit Vorschlag (LightboxViewModel.GeoAccuracy.cs, ADR-0062)
    }

    // ----- Anbindung an das Fenster (View-Mechanik) -----

    /// <summary>Zoom-Ziel (ZoomPanControl), vom Fenster gesetzt.</summary>
    public IZoomPanTarget? ZoomTarget { get; set; }

    /// <summary>Bestätigungsdialog (Titel, Text, Primär-Button) → true bei Bestätigung. Vom Fenster gesetzt.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    public event EventHandler? CloseRequested;

    public event EventHandler? ToggleFullscreenRequested;

    /// <summary>Aktueller MediaPlayer für das VideoView-Binding (null, bis das erste Video gezeigt wird).</summary>
    public MediaPlayer? Player => _player;

    public IReadOnlyList<LightboxItem> Items => _items;

    public int Count => _items.Count;

    public bool CanGoPrevious => CurrentIndex > 0;

    public bool CanGoNext => CurrentIndex >= 0 && CurrentIndex < _items.Count - 1;

    public ObservableCollection<LightboxInfoRow> FileInfoRows { get; } = new();

    public ObservableCollection<LightboxInfoRow> MediaInfoRows { get; } = new();

    // ----- Zustand -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious), nameof(CanGoNext))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    private int _currentIndex = -1;

    [ObservableProperty] private LightboxItem? _currentItem;
    [ObservableProperty] private BitmapSource? _imageSource;
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] private bool _isVideo;

    // Archiv-Inhaltsansicht (ADR-0022): statt Bild/Video die Einträge des Archivs.
    [ObservableProperty] private bool _isArchive;
    [ObservableProperty] private string _archiveSummary = string.Empty;
    [ObservableProperty] private string? _archiveError;
    public ObservableCollection<ArchiveEntryRow> ArchiveEntries { get; } = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isChromeVisible = true;
    [ObservableProperty] private bool _isInfoVisible;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private double _zoomPercent = 100;
    [ObservableProperty] private bool _isZoomFit = true;
    [ObservableProperty] private string _title = "Lightbox";
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _counterText = string.Empty;
    [ObservableProperty] private string _mediaInfoHeader = "Aufnahme";

    /// <summary>Decode-Limit in Pixeln (Breite); das Fenster setzt 2× Bildschirmbreite.</summary>
    [ObservableProperty] private int _decodePixelLimit = 3840;

    // Video
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isMediaEnded;
    [ObservableProperty] private double _positionFraction;
    [ObservableProperty] private string _timeText = "00:00 / 00:00";
    [ObservableProperty] private int _volume;
    [ObservableProperty] private bool _isMuted;

    public string ZoomText => IsZoomFit ? $"Fit · {ZoomPercent:0.#} %" : $"{ZoomPercent:0.#} %";

    partial void OnZoomPercentChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    partial void OnIsZoomFitChanged(bool value) => OnPropertyChanged(nameof(ZoomText));

    partial void OnVolumeChanged(int value)
    {
        _vlc.LastVolume = value;
        TryPlayer(p => p.Volume = value);
    }

    partial void OnIsMutedChanged(bool value)
    {
        _vlc.LastMuted = value;
        TryPlayer(p => p.Mute = value);
    }

    // ----- Laden / Navigation -----

    /// <summary>Übernimmt eine neue Sequenz und zeigt entries[startIndex]. UI-Thread.</summary>
    public void Load(IReadOnlyList<LightboxEntry> entries, int startIndex)
    {
        _items.Clear();
        _itemsById.Clear();
        _cache.Clear();
        foreach (var entry in entries)
        {
            var item = new LightboxItem(entry);
            _items.Add(item);
            _itemsById[item.FileId] = item;
        }

        OnPropertyChanged(nameof(Count));
        if (_items.Count == 0)
        {
            CurrentIndex = -1;
            CurrentItem = null;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        NavigateTo(Math.Clamp(startIndex, 0, _items.Count - 1), force: true);
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => NavigateTo(CurrentIndex + 1);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous() => NavigateTo(CurrentIndex - 1);

    [RelayCommand]
    private void First() => NavigateTo(0);

    [RelayCommand]
    private void Last() => NavigateTo(_items.Count - 1);

    [RelayCommand]
    private void PageForward() => NavigateTo(CurrentIndex + PageStep);

    [RelayCommand]
    private void PageBack() => NavigateTo(CurrentIndex - PageStep);

    [RelayCommand]
    private void ToggleInfo() => IsInfoVisible = !IsInfoVisible;

    [RelayCommand]
    private void ToggleFullscreen() => ToggleFullscreenRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ZoomIn() => ZoomTarget?.ZoomIn();

    [RelayCommand]
    private void ZoomOut() => ZoomTarget?.ZoomOut();

    [RelayCommand]
    private void ZoomFit() => ZoomTarget?.ZoomToFit();

    [RelayCommand]
    private void ZoomActual() => ZoomTarget?.ZoomToActualSize();

    private void NavigateTo(int index, bool force = false)
    {
        if (_disposed || _items.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _items.Count - 1);
        if (!force && index == CurrentIndex)
        {
            return;
        }

        CurrentIndex = index;
        CurrentItem = _items[index];
        _currentDimensions = null;
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        UpdateHeader();
        _ = LoadCurrentTagsAsync();
        _ = LoadCurrentCategoriesAsync();
        _ = LoadCurrentMotifsAsync(); // ADR-0052
        UpdateFacesForCurrent(); // 0.2.38
        _ = ShowCurrentAsync();
    }

    private void UpdateHeader()
    {
        var item = CurrentItem;
        if (item is null)
        {
            Title = "Lightbox";
            Subtitle = string.Empty;
            CounterText = string.Empty;
            return;
        }

        Title = item.Name;
        CounterText = $"{CurrentIndex + 1} / {_items.Count}";
        var subtitle = $"{FormatSize(item.Size)} · {item.ModifiedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}";
        Subtitle = _currentDimensions is null ? subtitle : $"{_currentDimensions} · {subtitle}";
    }

    private async Task ShowCurrentAsync()
    {
        var item = CurrentItem;
        if (item is null)
        {
            return;
        }

        var version = ++_showVersion;
        ErrorMessage = null;

        if (item.IsArchive)
        {
            StopVideo();
            ImageSource = null;
            IsImage = false;
            IsVideo = false;
            IsArchive = true;
            await ShowArchiveAsync(item, version).ConfigureAwait(true);
            return;
        }

        IsArchive = false;
        if (item.IsVideo)
        {
            ImageSource = null;
            IsImage = false;
            IsVideo = true;
            await ShowVideoAsync(item, version).ConfigureAwait(true);
        }
        else
        {
            StopVideo();
            IsVideo = false;
            IsImage = true;
            await ShowImageAsync(item, version).ConfigureAwait(true);
        }
    }

    // ----- Archive (ADR-0022) -----

    /// <summary>Liest das Inhaltsverzeichnis über IArchiveService (Thread-Pool) und füllt die Liste; Fehler (Passwort, kaputt) als Text.</summary>
    private async Task ShowArchiveAsync(LightboxItem item, int version)
    {
        IsLoading = true;
        ArchiveError = null;
        ArchiveEntries.Clear();
        ArchiveSummary = "Inhalt wird gelesen…";
        BuildFileInfo(item);
        MediaInfoHeader = "Archiv";
        MediaInfoRows.Clear();
        try
        {
            if (_archives is null)
            {
                throw new InvalidOperationException("Archiv-Dienst nicht verfügbar.");
            }

            var listing = await _archives.ListAsync(item.Path).ConfigureAwait(true);
            if (version != _showVersion)
            {
                return;
            }

            foreach (var entry in listing.Entries)
            {
                ArchiveEntries.Add(new ArchiveEntryRow(entry));
            }

            ArchiveSummary = $"{listing.FileCount:N0} Datei(en) · {listing.MediaCount:N0} Fotos/Videos · {FormatSize(listing.TotalSize)} unkomprimiert"
                             + (listing.NestedArchiveCount > 0 ? $" · {listing.NestedArchiveCount:N0} Archiv(e) darin" : string.Empty)
                             + (listing.HasEncryptedEntries ? " · passwortgeschützt" : string.Empty);
            MediaInfoRows.Add(new LightboxInfoRow("Dateien", listing.FileCount.ToString("N0", CultureInfo.CurrentCulture)));
            MediaInfoRows.Add(new LightboxInfoRow("Fotos/Videos", listing.MediaCount.ToString("N0", CultureInfo.CurrentCulture)));
            MediaInfoRows.Add(new LightboxInfoRow("Unkomprimiert", FormatSize(listing.TotalSize)));
            if (listing.HasEncryptedEntries)
            {
                ArchiveError = "Das Archiv ist passwortgeschützt — es kann angezeigt, aber nicht entpackt werden.";
            }
        }
        catch (Exception ex)
        {
            if (version == _showVersion)
            {
                ArchiveSummary = "Inhalt nicht lesbar";
                ArchiveError = ex.Message;
                _logger.LogWarning(ex, "Archiv-Inhalt nicht lesbar: {Path}", item.Path);
            }
        }
        finally
        {
            if (version == _showVersion)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>„Entpacken…“: die Galerie öffnet den Dialog (Modul-Entkopplung über den Messenger, ADR-0017).</summary>
    [RelayCommand]
    private void ExtractArchive()
    {
        if (CurrentItem is { IsArchive: true } item)
        {
            WeakReferenceMessenger.Default.Send(new ExtractArchiveRequestedMessage(item.FileId, item.Path, item.Name));
        }
    }

    // ----- Bilder -----

    private async Task ShowImageAsync(LightboxItem item, int version)
    {
        IsLoading = true;
        BuildFileInfo(item);
        MediaInfoHeader = "Aufnahme";
        MediaInfoRows.Clear();

        try
        {
            var sw = Stopwatch.StartNew();
            var decoded = await _cache.GetOrStartAsync(item.FileId, item.Path, DecodePixelLimit).ConfigureAwait(true);
            if (version != _showVersion)
            {
                return;
            }

            ImageSource = decoded.Bitmap;
            BuildImageInfo(item, decoded);
            _logger.LogDebug(
                "Lightbox-Bild gezeigt: {Name} {W}x{H} (decodiert {DW}x{DH}, {Ms} ms)",
                item.Name, decoded.DisplayWidth, decoded.DisplayHeight,
                decoded.Bitmap.PixelWidth, decoded.Bitmap.PixelHeight, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Navigation hat den Decode überholt — nichts zu tun.
        }
        catch (Exception ex)
        {
            if (version == _showVersion)
            {
                ImageSource = null;
                ErrorMessage = $"Bild kann nicht angezeigt werden:\n{ex.Message}";
                _logger.LogWarning(ex, "Lightbox-Decode fehlgeschlagen: {Path}", item.Path);
            }
        }
        finally
        {
            if (version == _showVersion)
            {
                IsLoading = false;
            }
        }

        if (version == _showVersion)
        {
            Prefetch(CurrentIndex);
        }
    }

    private void Prefetch(int center)
    {
        for (var offset = 1; offset <= PrefetchRadius; offset++)
        {
            StartPrefetch(center + offset);
            StartPrefetch(center - offset);
        }
    }

    private void StartPrefetch(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        var item = _items[index];
        if (!item.IsImage)
        {
            return;
        }

        try
        {
            var task = _cache.GetOrStartAsync(item.FileId, item.Path, DecodePixelLimit);
            _ = task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            // Fenster wird gerade geschlossen.
        }
    }

    // ----- Video -----

    private bool EnsurePlayer()
    {
        if (_player is not null)
        {
            return true;
        }

        try
        {
            var player = _vlc.CreatePlayer();
            player.Playing += OnPlayerPlaying;
            player.Paused += OnPlayerPaused;
            player.Stopped += OnPlayerStopped;
            player.EndReached += OnPlayerEndReached;
            player.EncounteredError += OnPlayerEncounteredError;
            player.LengthChanged += OnPlayerLengthChanged;
            player.TimeChanged += OnPlayerTimeChanged;
            _player = player;
            _videoUnavailableReason = null;
            OnPropertyChanged(nameof(Player));
            return true;
        }
        catch (Exception ex)
        {
            _videoUnavailableReason = ex.Message;
            _logger.LogError(ex, "LibVLC-MediaPlayer konnte nicht erstellt werden");
            return false;
        }
    }

    private async Task ShowVideoAsync(LightboxItem item, int version)
    {
        IsLoading = true;
        BuildFileInfo(item);
        MediaInfoHeader = "Video";
        MediaInfoRows.Clear();
        StopVideo();

        if (!EnsurePlayer())
        {
            IsLoading = false;
            ErrorMessage = $"Videowiedergabe nicht verfügbar:\n{_videoUnavailableReason}";
            return;
        }

        Media? media = null;
        try
        {
            if (!File.Exists(item.Path))
            {
                throw new FileNotFoundException("Datei nicht gefunden.", item.Path);
            }

            media = new Media(_vlc.GetLibVlc(), item.Path, FromType.FromPath);
            _media = media;

            // Tracks/Dauer für das Info-Panel (lokal, mit Timeout) — Abbruch bei Navigation.
            var parseStatus = await media.Parse(MediaParseOptions.ParseLocal, timeout: 3000).ConfigureAwait(true);
            if (version != _showVersion || !ReferenceEquals(_media, media))
            {
                return;
            }

            BuildVideoInfo(media, parseStatus);

            // VideoView muss gelayoutet sein (Hwnd gesetzt), sonst öffnet VLC ein eigenes Fenster.
            await WaitForVideoSurfaceAsync().ConfigureAwait(true);
            if (version != _showVersion || !ReferenceEquals(_media, media) || _player is null)
            {
                return;
            }

            _pendingSeekFraction = null;
            IsMediaEnded = false;
            if (!_player.Play(media))
            {
                throw new InvalidOperationException("LibVLC hat die Wiedergabe abgelehnt.");
            }

            _logger.LogDebug("Lightbox-Video gestartet: {Name}", item.Name);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version == _showVersion)
            {
                ErrorMessage = $"Video kann nicht abgespielt werden:\n{ex.Message}";
                _logger.LogWarning(ex, "Lightbox-Video fehlgeschlagen: {Path}", item.Path);
            }
        }
        finally
        {
            if (version == _showVersion)
            {
                IsLoading = false;
            }
        }
    }

    private async Task WaitForVideoSurfaceAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            if (_player is null || _player.Hwnd != IntPtr.Zero)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(true);
        }

        _logger.LogWarning("VideoView hat innerhalb von 500 ms kein Hwnd geliefert — Wiedergabe startet trotzdem");
    }

    /// <summary>Stoppt die Wiedergabe und gibt das aktuelle Media frei (Player bleibt für die Session).</summary>
    public void StopVideo()
    {
        var media = _media;
        _media = null;
        if (_player is not null)
        {
            try
            {
                _player.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MediaPlayer.Stop fehlgeschlagen");
            }
        }

        media?.Dispose();
        IsPlaying = false;
        IsMediaEnded = false;
        _lengthMs = 0;
        _pendingSeekFraction = null;
        if (!_isSeeking)
        {
            PositionFraction = 0;
        }

        TimeText = "00:00 / 00:00";
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_player is null || _media is null)
        {
            return;
        }

        try
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
            }
            else if (IsMediaEnded || _player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
            {
                IsMediaEnded = false;
                _player.Play(_media);
            }
            else
            {
                _player.Play();
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Wiedergabe-Fehler: {ex.Message}");
            _logger.LogWarning(ex, "PlayPause fehlgeschlagen");
        }
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>Beginn einer Slider-Interaktion: Positions-Updates vom Player werden ausgesetzt.</summary>
    public void BeginSeek() => _isSeeking = true;

    /// <summary>Ende einer Slider-Interaktion: Zielposition (0–1) an den Player geben.</summary>
    public void EndSeek(double fraction)
    {
        _isSeeking = false;
        SeekTo(fraction);
    }

    public void SeekTo(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        PositionFraction = fraction;
        UpdateTimeText((long)(fraction * _lengthMs));
        if (_player is null || _media is null)
        {
            return;
        }

        try
        {
            if (IsMediaEnded || _player.State is VLCState.Ended or VLCState.Stopped)
            {
                _pendingSeekFraction = fraction;
                IsMediaEnded = false;
                _player.Play(_media);
                return;
            }

            _player.Position = (float)fraction;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Seek fehlgeschlagen");
        }
    }

    private void TryPlayer(Action<MediaPlayer> action)
    {
        var player = _player;
        if (player is null)
        {
            return;
        }

        try
        {
            action(player);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MediaPlayer-Aufruf fehlgeschlagen");
        }
    }

    private void OnPlayerPlaying(object? sender, EventArgs e) => RunOnUi(() =>
    {
        IsPlaying = true;
        IsMediaEnded = false;
        IsLoading = false;
        TryPlayer(p =>
        {
            p.Volume = Volume;
            p.Mute = IsMuted;
            if (_pendingSeekFraction is { } pending)
            {
                _pendingSeekFraction = null;
                p.Position = (float)pending;
            }
        });
    });

    private void OnPlayerPaused(object? sender, EventArgs e) => RunOnUi(() => IsPlaying = false);

    private void OnPlayerStopped(object? sender, EventArgs e) => RunOnUi(() => IsPlaying = false);

    private void OnPlayerEndReached(object? sender, EventArgs e) => RunOnUi(() =>
    {
        IsPlaying = false;
        IsMediaEnded = true;
        if (!_isSeeking)
        {
            PositionFraction = 1;
            UpdateTimeText(_lengthMs);
        }
    });

    private void OnPlayerEncounteredError(object? sender, EventArgs e) => RunOnUi(() =>
    {
        IsPlaying = false;
        IsLoading = false;
        ErrorMessage = "Video kann nicht abgespielt werden (LibVLC-Fehler).";
    });

    private void OnPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) => RunOnUi(() =>
    {
        _lengthMs = Math.Max(0, e.Length);
        UpdateTimeText((long)(PositionFraction * _lengthMs));
    });

    private void OnPlayerTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        var time = e.Time;
        RunOnUi(() =>
        {
            if (_isSeeking)
            {
                return;
            }

            if (_lengthMs > 0)
            {
                PositionFraction = Math.Clamp((double)time / _lengthMs, 0, 1);
            }

            UpdateTimeText(time);
        });
    }

    private void UpdateTimeText(long positionMs)
        => TimeText = $"{FormatTime(positionMs)} / {FormatTime(_lengthMs)}";

    // ----- Favorit / Bewertung / Löschen / Shell -----

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        var item = CurrentItem;
        if (item is null)
        {
            return;
        }

        if (_catalog is null)
        {
            ShowStatus("Katalog-Dienst nicht verfügbar — Favorit kann nicht gespeichert werden.");
            return;
        }

        var newValue = !item.IsFavorite;
        item.IsFavorite = newValue;
        try
        {
            await _catalog.SetFavoriteAsync(new[] { item.FileId }, newValue).ConfigureAwait(true);
            ShowStatus(newValue ? "Als Favorit markiert." : "Favorit entfernt.");
        }
        catch (Exception ex)
        {
            item.IsFavorite = !newValue;
            ShowStatus($"Favorit konnte nicht gespeichert werden: {ex.Message}");
            _logger.LogWarning(ex, "SetFavoriteAsync fehlgeschlagen für File {FileId}", item.FileId);
        }
    }

    /// <summary>Bewertung setzen; Parameter „1“–„5“, „0“ oder null löscht. Erneuter Klick auf den aktuellen Stern löscht ebenfalls.</summary>
    [RelayCommand]
    private async Task SetRatingAsync(object? parameter)
    {
        var item = CurrentItem;
        if (item is null)
        {
            return;
        }

        byte? rating = parameter switch
        {
            byte b => b,
            int i => (byte)Math.Clamp(i, 0, 5),
            string s when byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        if (rating is 0)
        {
            rating = null;
        }

        if (rating is not null && item.Rating == rating)
        {
            rating = null; // Toggle: gleicher Stern nochmal → Bewertung löschen
        }

        if (_catalog is null)
        {
            ShowStatus("Katalog-Dienst nicht verfügbar — Bewertung kann nicht gespeichert werden.");
            return;
        }

        var previous = item.Rating;
        item.Rating = rating;
        try
        {
            await _catalog.SetRatingAsync(new[] { item.FileId }, rating).ConfigureAwait(true);
            ShowStatus(rating is null ? "Bewertung entfernt." : $"Bewertung: {rating} von 5.");
        }
        catch (Exception ex)
        {
            item.Rating = previous;
            ShowStatus($"Bewertung konnte nicht gespeichert werden: {ex.Message}");
            _logger.LogWarning(ex, "SetRatingAsync fehlgeschlagen für File {FileId}", item.FileId);
        }
    }

    [RelayCommand]
    private Task ClearRatingAsync() => SetRatingAsync(null);

    [RelayCommand]
    private async Task DeleteCurrentAsync()
    {
        var item = CurrentItem;
        if (item is null)
        {
            return;
        }

        if (_fileOperations is null)
        {
            ShowStatus("Datei-Dienst nicht verfügbar — Löschen nicht möglich.");
            return;
        }

        var confirmed = ConfirmAsync is null
            || await ConfirmAsync(
                "In den Papierkorb verschieben?",
                $"„{item.Name}“ wird in den Windows-Papierkorb verschoben und aus dem Katalog entfernt.",
                "In den Papierkorb").ConfigureAwait(true);
        if (!confirmed || !ReferenceEquals(CurrentItem, item))
        {
            return;
        }

        if (item.IsVideo)
        {
            StopVideo(); // Datei-Handle von VLC freigeben
        }

        try
        {
            var result = await _fileOperations.RecycleAsync(new[] { item.FileId }).ConfigureAwait(true);
            var itemResult = result.Items.FirstOrDefault(r => r.FileId == item.FileId);
            var success = itemResult?.Success ?? result.Succeeded > 0;
            if (success)
            {
                RemoveItems(new[] { item.FileId });
                ShowStatus($"„{item.Name}“ wurde in den Papierkorb verschoben.");
            }
            else
            {
                ShowStatus($"Löschen fehlgeschlagen: {itemResult?.Error ?? "unbekannter Fehler"}");
                if (item.IsVideo && ReferenceEquals(CurrentItem, item))
                {
                    _ = ShowCurrentAsync();
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Löschen fehlgeschlagen: {ex.Message}");
            _logger.LogWarning(ex, "RecycleAsync fehlgeschlagen für File {FileId}", item.FileId);
        }
    }

    /// <summary>
    /// Strg+M: aktuelle Datei „Ins Archiv…“ (ADR-0020) über den WPF-freien <see cref="IArchiveMoveService"/> — der
    /// Zieldialog gehört zur Galerie, die Lightbox kennt sie nicht. Der neue Pfad kommt per FilesChangedMessage(Moved).
    /// </summary>
    [RelayCommand]
    private async Task ArchiveCurrentAsync()
    {
        var item = CurrentItem;
        if (item is null)
        {
            return;
        }

        if (_archiveMove is null)
        {
            ShowStatus("Archiv-Dienst nicht verfügbar — Verschieben nicht möglich.");
            return;
        }

        if (item.IsVideo)
        {
            StopVideo(); // Datei-Handle von VLC freigeben
        }

        try
        {
            var result = await _archiveMove.ArchiveAsync(new[] { item.FileId }).ConfigureAwait(true);
            if (result is null)
            {
                if (item.IsVideo && ReferenceEquals(CurrentItem, item))
                {
                    _ = ShowCurrentAsync();
                }

                return; // abgebrochen
            }

            var itemResult = result.Items.FirstOrDefault(r => r.FileId == item.FileId);
            var success = itemResult?.Success ?? result.Succeeded > 0;
            if (success)
            {
                ShowStatus($"„{item.Name}“ in die Bibliothek verschoben.");
            }
            else
            {
                ShowStatus($"In die Bibliothek fehlgeschlagen: {itemResult?.Error ?? "unbekannter Fehler"}");
                if (item.IsVideo && ReferenceEquals(CurrentItem, item))
                {
                    _ = ShowCurrentAsync();
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"In die Bibliothek fehlgeschlagen: {ex.Message}");
            _logger.LogWarning(ex, "ArchiveAsync fehlgeschlagen für File {FileId}", item.FileId);
        }
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        var path = CurrentItem?.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Lumora.Shell.HiddenLibraries.HiddenSessionPrivacy.ThrowIfShellBlocked(); // versteckte Sitzung: keine Shell-Spuren (ADR-0049)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus($"Explorer konnte nicht geöffnet werden: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenExternal()
    {
        var path = CurrentItem?.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Lumora.Shell.HiddenLibraries.HiddenSessionPrivacy.ThrowIfShellBlocked(); // versteckte Sitzung: keine Shell-Spuren (ADR-0049)
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus($"Externes Öffnen fehlgeschlagen: {ex.Message}");
        }
    }

    // ----- Katalog-Sync -----

    private void OnFilesChanged(FilesChangedMessage message)
    {
        if (_disposed)
        {
            return;
        }

        RunOnUi(() => HandleFilesChanged(message));
    }

    private void HandleFilesChanged(FilesChangedMessage message)
    {
        if (_disposed || _items.Count == 0)
        {
            return;
        }

        var relevant = message.FileIds.Where(_itemsById.ContainsKey).Distinct().ToList();
        if (relevant.Count == 0)
        {
            return;
        }

        switch (message.Kind)
        {
            case FilesChangeKind.Removed:
                RemoveItems(relevant);
                break;
            case FilesChangeKind.Metadata:
            case FilesChangeKind.Moved:
                _ = RefreshItemsAsync(relevant, message.Kind);
                if (CurrentItem is { } current && relevant.Contains(current.FileId))
                {
                    _ = LoadCurrentTagsAsync(); // Tag-Zuweisungen melden sich als Metadata
                    _ = LoadCurrentCategoriesAsync(); // Kategorie-Zuweisungen ebenso
                }

                break;
        }
    }

    private async Task RefreshItemsAsync(List<int> fileIds, FilesChangeKind kind)
    {
        try
        {
            var rows = await Task.Run(async () =>
            {
                var result = new List<CatalogRow>(fileIds.Count);
                await using var ctx = _contextFactory.Create();
                foreach (var chunk in fileIds.Chunk(DbChunkSize))
                {
                    var ids = chunk.ToList();
                    var part = await (
                        from f in ctx.Files.AsNoTracking()
                        join d in ctx.Folders.AsNoTracking() on f.FolderId equals d.Id
                        where ids.Contains(f.Id)
                        select new CatalogRow(f.Id, f.Name, d.Path, f.IsFavorite, f.Rating, f.Size, f.ModifiedAtUtc))
                        .ToListAsync()
                        .ConfigureAwait(false);
                    result.AddRange(part);
                }

                return result;
            }).ConfigureAwait(true);

            if (_disposed)
            {
                return;
            }

            var currentChanged = false;
            foreach (var row in rows)
            {
                if (!_itemsById.TryGetValue(row.Id, out var item))
                {
                    continue;
                }

                item.IsFavorite = row.IsFavorite;
                item.Rating = row.Rating;
                if (kind == FilesChangeKind.Moved)
                {
                    var newPath = Path.Combine(row.FolderPath, row.Name);
                    if (!string.Equals(newPath, item.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Path = newPath;
                        item.Name = row.Name;
                        item.Size = row.Size;
                        item.ModifiedAtUtc = row.ModifiedAtUtc;
                        _cache.Invalidate(row.Id);
                        if (ReferenceEquals(item, CurrentItem))
                        {
                            currentChanged = true;
                        }
                    }
                }
            }

            if (currentChanged)
            {
                UpdateHeader();
                BuildFileInfo(CurrentItem!);
                if (CurrentItem!.IsVideo)
                {
                    _ = ShowCurrentAsync(); // VLC hält den alten Pfad → neu öffnen
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lightbox-Sync ({Kind}) fehlgeschlagen", kind);
        }
    }

    private sealed record CatalogRow(int Id, string Name, string FolderPath, bool IsFavorite, byte? Rating, long Size, DateTime ModifiedAtUtc);

    private void RemoveItems(IReadOnlyList<int> fileIds)
    {
        var oldIndex = CurrentIndex;
        var removedCurrent = false;
        foreach (var id in fileIds)
        {
            if (!_itemsById.Remove(id, out var item))
            {
                continue;
            }

            var index = _items.IndexOf(item);
            if (index < 0)
            {
                continue;
            }

            _items.RemoveAt(index);
            _cache.Invalidate(id);
            if (index < oldIndex)
            {
                oldIndex--;
            }
            else if (index == oldIndex)
            {
                removedCurrent = true;
            }
        }

        OnPropertyChanged(nameof(Count));
        if (_items.Count == 0)
        {
            StopVideo();
            ImageSource = null;
            CurrentIndex = -1;
            CurrentItem = null;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var newIndex = Math.Clamp(oldIndex, 0, _items.Count - 1);
        if (removedCurrent)
        {
            NavigateTo(newIndex, force: true);
        }
        else
        {
            CurrentIndex = newIndex;
            NextCommand.NotifyCanExecuteChanged();
            PreviousCommand.NotifyCanExecuteChanged();
            UpdateHeader();
        }
    }

    // ----- Info-Panel -----

    private void BuildFileInfo(LightboxItem item)
    {
        FileInfoRows.Clear();
        FileInfoRows.Add(new LightboxInfoRow("Name", item.Name));
        FileInfoRows.Add(new LightboxInfoRow("Ordner", Path.GetDirectoryName(item.Path) ?? string.Empty));
        FileInfoRows.Add(new LightboxInfoRow("Typ", item.IsArchive ? "Archiv" : item.IsVideo ? "Video" : "Bild"));
        FileInfoRows.Add(new LightboxInfoRow("Größe", FormatSize(item.Size)));
        FileInfoRows.Add(new LightboxInfoRow("Geändert", item.ModifiedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)));
        QueueCaptureDateRow(item); // „Aufgenommen“ (ADR-0046, LightboxViewModel.CaptureDate.cs)
        QueuePlaceRow(item); // „Ort“ (ADR-0050, LightboxViewModel.Places.cs)
    }

    private void BuildImageInfo(LightboxItem item, DecodedImage decoded)
    {
        var dimensions = $"{decoded.DisplayWidth} × {decoded.DisplayHeight}";
        FileInfoRows.Add(new LightboxInfoRow("Pixelmaße", dimensions));
        _currentDimensions = dimensions;
        UpdateHeader();

        MediaInfoRows.Clear();
        var exif = decoded.Exif;
        if (!exif.HasAny)
        {
            MediaInfoRows.Add(new LightboxInfoRow("EXIF", "Keine Aufnahmedaten"));
            return;
        }

        if (exif.Camera is not null)
        {
            MediaInfoRows.Add(new LightboxInfoRow("Kamera", exif.Camera));
        }

        if (exif.Lens is not null)
        {
            MediaInfoRows.Add(new LightboxInfoRow("Objektiv", exif.Lens));
        }

        if (exif.DateTaken is { } taken)
        {
            MediaInfoRows.Add(new LightboxInfoRow("Aufnahme", taken.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture)));
        }

        if (exif.ExposureSeconds is { } exposure && exposure > 0)
        {
            MediaInfoRows.Add(new LightboxInfoRow("Belichtung", FormatExposure(exposure)));
        }

        if (exif.FNumber is { } f && f > 0)
        {
            MediaInfoRows.Add(new LightboxInfoRow("Blende", $"ƒ/{f.ToString("0.#", CultureInfo.CurrentCulture)}"));
        }

        if (exif.Iso is { } iso)
        {
            MediaInfoRows.Add(new LightboxInfoRow("ISO", iso.ToString(CultureInfo.CurrentCulture)));
        }

        if (exif.FocalLengthMm is { } focal && focal > 0)
        {
            MediaInfoRows.Add(new LightboxInfoRow("Brennweite", $"{focal.ToString("0.#", CultureInfo.CurrentCulture)} mm"));
        }
    }

    private void BuildVideoInfo(Media media, MediaParsedStatus status)
    {
        MediaInfoRows.Clear();
        if (status != MediaParsedStatus.Done)
        {
            MediaInfoRows.Add(new LightboxInfoRow("Analyse", status switch
            {
                MediaParsedStatus.Timeout => "Zeitüberschreitung",
                MediaParsedStatus.Failed => "Fehlgeschlagen",
                _ => "Übersprungen",
            }));
        }

        try
        {
            var duration = media.Duration;
            if (duration > 0)
            {
                _lengthMs = duration;
                UpdateTimeText(0);
                MediaInfoRows.Add(new LightboxInfoRow("Dauer", FormatTime(duration)));
            }

            foreach (var track in media.Tracks)
            {
                switch (track.TrackType)
                {
                    case TrackType.Video:
                    {
                        var video = track.Data.Video;
                        if (video.Width > 0 && video.Height > 0)
                        {
                            var resolution = $"{video.Width} × {video.Height}";
                            MediaInfoRows.Add(new LightboxInfoRow("Auflösung", resolution));
                            _currentDimensions = resolution;
                            UpdateHeader();
                        }

                        MediaInfoRows.Add(new LightboxInfoRow("Codec", DescribeCodec(media, track)));
                        if (video.FrameRateDen > 0 && video.FrameRateNum > 0)
                        {
                            var fps = (double)video.FrameRateNum / video.FrameRateDen;
                            MediaInfoRows.Add(new LightboxInfoRow("Bildrate", $"{fps.ToString("0.##", CultureInfo.CurrentCulture)} fps"));
                        }

                        break;
                    }

                    case TrackType.Audio:
                    {
                        var audio = track.Data.Audio;
                        var parts = new List<string> { DescribeCodec(media, track) };
                        if (audio.Channels > 0)
                        {
                            parts.Add(audio.Channels switch { 1 => "Mono", 2 => "Stereo", _ => $"{audio.Channels} Kanäle" });
                        }

                        if (audio.Rate > 0)
                        {
                            parts.Add($"{audio.Rate / 1000.0:0.#} kHz");
                        }

                        MediaInfoRows.Add(new LightboxInfoRow("Audio", string.Join(" · ", parts)));
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Video-Tracks konnten nicht gelesen werden");
        }
    }

    private static string DescribeCodec(Media media, MediaTrack track)
    {
        try
        {
            var description = media.CodecDescription(track.TrackType, track.Codec);
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }
        catch
        {
            // Fallback FourCC
        }

        return FourCc(track.Codec);
    }

    private static string FourCc(uint codec)
    {
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var c = (char)((codec >> (8 * i)) & 0xFF);
            chars[i] = char.IsLetterOrDigit(c) ? c : '?';
        }

        return new string(chars);
    }

    // ----- Hilfen -----

    private void ShowStatus(string text)
    {
        StatusText = text;
        _statusCts?.Cancel();
        _statusCts = new CancellationTokenSource();
        var token = _statusCts.Token;
        _ = _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(StatusDisplayMs, token).ConfigureAwait(true);
                if (!token.IsCancellationRequested)
                {
                    StatusText = null;
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void RunOnUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            });
        }
    }

    public static string FormatSize(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        var culture = CultureInfo.CurrentCulture;
        return bytes switch
        {
            >= (long)gb => $"{(bytes / gb).ToString("0.##", culture)} GB",
            >= (long)mb => $"{(bytes / mb).ToString("0.#", culture)} MB",
            >= (long)kb => $"{(bytes / kb).ToString("0", culture)} KB",
            _ => $"{bytes} B",
        };
    }

    public static string FormatTime(long milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string FormatExposure(double seconds)
    {
        if (seconds >= 1)
        {
            return $"{seconds.ToString("0.#", CultureInfo.CurrentCulture)} s";
        }

        var denominator = Math.Round(1 / seconds);
        return $"1/{denominator.ToString("0", CultureInfo.CurrentCulture)} s";
    }

    // ----- Lebenszyklus -----

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _showVersion++;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        Faces.Dispose(); // 0.2.38
        _statusCts?.Cancel();

        var media = _media;
        _media = null;
        if (_player is not null)
        {
            var player = _player;
            player.Playing -= OnPlayerPlaying;
            player.Paused -= OnPlayerPaused;
            player.Stopped -= OnPlayerStopped;
            player.EndReached -= OnPlayerEndReached;
            player.EncounteredError -= OnPlayerEncounteredError;
            player.LengthChanged -= OnPlayerLengthChanged;
            player.TimeChanged -= OnPlayerTimeChanged;
            try
            {
                player.Stop();
                player.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MediaPlayer.Dispose fehlgeschlagen");
            }

            _player = null;
            OnPropertyChanged(nameof(Player));
        }

        media?.Dispose();
        _cache.Dispose();
        ImageSource = null;
    }
}
