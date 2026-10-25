using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Gallery.Controls;
using Lumora.Lightbox;
using Lumora.Services.Messaging;
using Lumora.Services.Quality;
using Lumora.Services.Stories;
using Lumora.Shell;
using Wpf.Ui.Controls;

namespace Lumora.Stories;

/// <summary>
/// Story-Ansicht (ADR-0060, Mockup 09): Titelbild mit sachlicher Überschrift (Art, Titel, Orte, Zähler, Personen), fixierte
/// Leiste (Highlights/Alle Fotos mit Zählern, Tages-Chips, Diashow), Tagesabschnitte mit Collage-Blöcken
/// (<see cref="StoryLayoutEngine"/>) bzw. dichter Collage (<see cref="JustifiedRowLayout"/>), aufklappbare Tage, lange
/// Reisen ab dem vierten Tag eingeklappt, Ende-Kasten. Das „…“-Menü: Titel ändern (nur eigener Text), Titelbild wählen,
/// Fotos hinzufügen/entfernen, Anheften, Als Album speichern, In der Galerie zeigen, Ausblenden, Neu berechnen, Löschen
/// (eigene/Rückblicke) und „Als Collage-Bild speichern…“ (Collage-Dialog, ADR-0063). Öffnen markiert als gesehen.
/// Alle Zeilen sind Daten einer virtualisierten Liste; Vorschaubilder kommen, sobald eine Zeile sichtbar wird.
/// </summary>
public sealed partial class StoryViewModel : ObservableObject, IDisposable
{
    /// <summary>Bis zu so vielen Tagen alle offen; darüber die ersten <see cref="OpenDays"/>, der Rest eingeklappt.</summary>
    public const int MaxOpenDays = 4;

    public const int OpenDays = 3;

    /// <summary>Zielhöhe der dichten Collage (Mockup: 120 px) und Fuge.</summary>
    public const double DenseRowHeight = 132;

    public const double DenseSpacing = 4;

    /// <summary>Lightbox: höchstens so viele Fotos um das geöffnete herum.</summary>
    public const int LightboxLimit = 5000;

    private readonly StoryPageServices _services;
    private readonly Action _close;
    private readonly ILogger<StoryViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly Dictionary<int, List<StoryTileViewModel>> _tiles = new();
    private readonly HashSet<int> _expandedDays = new();
    private readonly Dictionary<int, int> _dayRows = new();
    private Dictionary<int, HighlightPick> _highlightMap = new();
    private IReadOnlyDictionary<int, StoryMediaInfo> _media = new Dictionary<int, StoryMediaInfo>();
    private CancellationTokenSource? _coverCts;
    private int? _coverFileId;
    private DateTime _lastPickAt;
    private int _reloadScheduled;
    private bool _disposed;

    public StoryViewModel(StoryPageServices services, int storyId, Action close, ILogger<StoryViewModel> logger, IMessenger? messenger = null)
    {
        _services = services;
        StoryId = storyId;
        _close = close;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _messenger.Register<StoryViewModel, StoriesChangedMessage>(this, static (r, m) => r.OnStoriesChanged(m));
    }

    public int StoryId { get; }

    public StoryDetails? Details { get; private set; }

    public StoryCard? Card => Details?.Card;

    public IReadOnlyList<HighlightPick> Highlights { get; private set; } = [];

    public IReadOnlyList<StoryDayModel> Days { get; private set; } = [];

    public ObservableCollection<StoryRow> Rows { get; } = new();

    public ObservableCollection<StoryDayChip> DayChips { get; } = new();

    public ObservableCollection<StoryAvatarViewModel> Avatars { get; } = new();

    /// <summary>Die Story gibt es nicht (mehr) bzw. sie gehört zu einer anderen Gruppe.</summary>
    public bool NotFound { get; private set; }

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private string? _statusText;

    [ObservableProperty] private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlightsMode), nameof(IsAllMode))]
    private StoryViewMode _mode = StoryViewMode.Highlights;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPicking), nameof(PickHint), nameof(IsRemoving), nameof(IsPickingCover))]
    private StoryPickMode _pickMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickHint))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmRemoveCommand))]
    private int _markedCount;

    [ObservableProperty] private ImageSource? _coverImage;

    [ObservableProperty] private bool _showAllDays;

    public bool IsHighlightsMode => Mode == StoryViewMode.Highlights;

    public bool IsAllMode => Mode == StoryViewMode.All;

    public bool IsPicking => PickMode != StoryPickMode.None;

    public bool IsRemoving => PickMode == StoryPickMode.Remove;

    public bool IsPickingCover => PickMode == StoryPickMode.Cover;

    public string PickHint => PickMode switch
    {
        StoryPickMode.Cover => "Titelbild wählen: Klick auf ein Foto der Story.",
        StoryPickMode.Remove => MarkedCount == 0
            ? "Fotos entfernen: Fotos anklicken, die nicht zur Story gehören."
            : $"{MarkedCount:N0} {(MarkedCount == 1 ? "Foto" : "Fotos")} markiert — sie bleiben in der Galerie, nur nicht mehr in dieser Story.",
        _ => string.Empty,
    };

    // ---- Kopf ------------------------------------------------------------------------------------------------------

    public string Title => Card?.Title ?? string.Empty;

    public string AutoTitle => Card?.AutoTitle ?? string.Empty;

    /// <summary>„Reise · 8 Tage“, „Heute vor 3 Jahren“.</summary>
    public string Kicker => Details?.Kicker ?? string.Empty;

    public SymbolRegular KindSymbol => Card is { } c ? StoryKindVisuals.Symbol(c.Kind, c.IsAway) : SymbolRegular.BookOpen24;

    /// <summary>Brotkrumen: „Reisen &amp; Ereignisse“.</summary>
    public string Section => Card is { } c ? StoryKindVisuals.Section(c.Kind) : string.Empty;

    /// <summary>Orte der Story bzw. Untertitel der Karte.</summary>
    public string? PlacesText => Details is { } d
        ? (d.Places.Count > 0 && d.Card.Kind is not (StoryKind.Person or StoryKind.Place) ? d.Card.Subtitle ?? string.Join(StoryTitleBuilder.Dot, d.Places.Select(p => p.Name)) : d.Card.Subtitle)
        : null;

    public string CountsText => Card?.CountsText ?? string.Empty;

    /// <summary>„Anna, Max, Oma“ bzw. „Anna, Max und 3 weitere“.</summary>
    public string? PersonsText => Details is { Persons.Count: > 0 } d
        ? d.Persons.Count <= 3
            ? string.Join(", ", d.Persons.Select(p => p.Name))
            : string.Join(", ", d.Persons.Take(2).Select(p => p.Name)) + $" und {d.Persons.Count - 2:N0} weitere"
        : null;

    public bool IsPinned => Card?.IsPinned == true;

    public string PinText => IsPinned ? "Lösen" : "Anheften";

    public bool HasCustomTitle => Card?.HasCustomTitle == true;

    /// <summary>Löschen nur für eigene Storys und gespeicherte Rückblicke (automatische lassen sich ausblenden).</summary>
    public bool CanDelete => Card?.Kind is StoryKind.Custom or StoryKind.Memory;

    public int HighlightCount => Highlights.Count;

    public int AllCount => Details?.FileIds.Count ?? 0;

    public bool HasSlideshow => _services.Slideshow is not null;

    /// <summary>„Als Collage-Bild speichern…“ (Welle 6b, ADR-0063) — aktiv, sobald der Collage-Dialog da ist.</summary>
    public bool CanSaveCollage => _services.Collage is not null;

    /// <summary>Früher „Welle 6b“ am deaktivierten Menüpunkt; jetzt ohne Zusatz.</summary>
    public string? CollageHint => _services.Collage is null ? "nicht verfügbar" : null;

    /// <summary>Breite der Zeilen in DIP (setzt die Ansicht).</summary>
    public double LayoutWidth { get; private set; } = 1000;

    /// <summary>Zuletzt angefragter Sprung (Zeilen-Index) — die Ansicht scrollt dorthin.</summary>
    public int? LastScrollRow { get; private set; }

    /// <summary>Die Ansicht soll zu dieser Zeile scrollen (Tages-Sprung).</summary>
    public event Action<int>? ScrollRequested;

    // ---- Laden -----------------------------------------------------------------------------------------------------

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            ErrorText = null;
            var details = await _services.Stories.GetDetailsAsync(StoryId);
            if (details is null)
            {
                NotFound = true;
                return;
            }

            var highlights = await _services.Stories.GetHighlightsAsync(StoryId);
            var media = await _services.Media.LoadAsync(details.FileIds);
            if (_disposed)
            {
                return;
            }

            Details = details;
            Highlights = highlights;
            _highlightMap = highlights.GroupBy(h => h.FileId).ToDictionary(g => g.Key, g => g.First());
            _media = media;
            Days = BuildDays(details, media);
            RefreshHeader();
            RebuildChips();
            RebuildRows();
            _ = LoadCoverAsync();
            _ = LoadAvatarsAsync();
            if (!details.Card.IsSeen)
            {
                await _services.Stories.MarkSeenAsync([StoryId]);
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"Die Story konnte nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Storys: Story {Id} nicht geladen", StoryId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RefreshHeader()
    {
        foreach (var name in new[]
                 {
                     nameof(Card), nameof(Details), nameof(Title), nameof(AutoTitle), nameof(Kicker), nameof(KindSymbol), nameof(Section),
                     nameof(PlacesText), nameof(CountsText), nameof(PersonsText), nameof(IsPinned), nameof(PinText), nameof(HasCustomTitle),
                     nameof(CanDelete), nameof(HighlightCount), nameof(AllCount),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>
    /// Tagesabschnitte: wie vom Dienst; eine Story über genau einen Tag wird nach Tageszeit geteilt (Rückblick „Vormittag ·
    /// Prater“, „Abend · Innere Stadt“), wenn mindestens zwei Tageszeiten belegt sind.
    /// </summary>
    internal List<StoryDayModel> BuildDays(StoryDetails details, IReadOnlyDictionary<int, StoryMediaInfo> media)
    {
        var days = new List<StoryDayModel>();
        if (details.Days.Count == 1)
        {
            var day = details.Days[0];
            var sections = StoryDaySections.Split(day.FileIds.Select(id => (id, media.TryGetValue(id, out var m) ? m.LocalTime : null)).ToList());
            if (sections.Count >= 2)
            {
                for (var i = 0; i < sections.Count; i++)
                {
                    var section = sections[i];
                    var places = PlacesOf(section.FileIds, media) ?? day.Places;
                    var videos = section.FileIds.Count(id => media.TryGetValue(id, out var m) && m.IsVideo);
                    var firstPlace = places?.Split(StoryTitleBuilder.Dot)[0];
                    days.Add(new StoryDayModel(i + 1, section.Label, day.Title, places,
                        firstPlace is { Length: > 0 } ? section.Label + StoryTitleBuilder.Dot + firstPlace : section.Label,
                        day.Date, section.FileIds, section.FileIds.Count - videos, videos));
                }

                return days;
            }
        }

        foreach (var day in details.Days)
        {
            days.Add(new StoryDayModel(day.Index, "Tag " + day.Index.ToString(CultureInfo.InvariantCulture), day.Title, day.Places, day.ShortTitle, day.Date, day.FileIds, day.PhotoCount, day.VideoCount));
        }

        return days;
    }

    private string? PlacesOf(IReadOnlyList<int> fileIds, IReadOnlyDictionary<int, StoryMediaInfo> media)
    {
        if (_services.Places is not { } places)
        {
            return null;
        }

        var names = fileIds.Select(id => media.TryGetValue(id, out var m) ? m.PlaceId : null).OfType<int>()
            .GroupBy(id => id).OrderByDescending(g => g.Count()).Take(2)
            .Select(g => places.GetPlace(g.Key)?.Name).OfType<string>().ToList();
        return StoryTitleBuilder.Places(names);
    }

    private void RebuildChips()
    {
        var selected = DayChips.FirstOrDefault(c => c.IsSelected)?.Index ?? 1;
        DayChips.Clear();
        foreach (var day in Days)
        {
            DayChips.Add(new StoryDayChip(day.Index, day.Chip) { IsSelected = day.Index == selected, Owner = this });
        }
    }

    private async Task LoadCoverAsync()
    {
        _coverCts?.Cancel();
        var cts = _coverCts = new CancellationTokenSource();
        if (Card?.CoverFileId is not { } coverId || !_media.TryGetValue(coverId, out var cover))
        {
            // ohne (vorhandenes) Titelbild: das erste Foto der Story
            _coverFileId = Details?.FileIds.Count > 0 && _media.TryGetValue(Details.FileIds[0], out var first) ? first.FileId : null;
            CoverImage = _coverFileId is { } fallback ? Request(_media[fallback]) : null;
            return;
        }

        _coverFileId = coverId;
        CoverImage = Request(cover);
        if (_services.Decoder is null || cover.IsVideo)
        {
            return;
        }

        try
        {
            // scharf aus dem Original (wie die Diashow), auf Bannergröße — das Vorschaubild hat nur 400 px kurze Kante
            var image = await _services.Decoder.DecodeAsync(cover.Path, 1800, 760, cts.Token);
            if (!cts.IsCancellationRequested && !_disposed && Card?.CoverFileId == coverId)
            {
                CoverImage = image.Bitmap;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Storys: Titelbild nicht aus dem Original dekodiert");
        }
        catch (OperationCanceledException)
        {
        }

        ImageSource? Request(StoryMediaInfo info)
        {
            _services.Images.Request([info], large: true);
            return _services.Images.TryGet(info.FileId, large: true);
        }
    }

    private async Task LoadAvatarsAsync()
    {
        try
        {
            var persons = await _services.Media.LoadPersonsAsync([StoryId], 5);
            Avatars.Clear();
            foreach (var info in persons.GetValueOrDefault(StoryId) ?? [])
            {
                Avatars.Add(new StoryAvatarViewModel(info) { Image = info.FaceId is { } f ? _services.Faces?.TryGet(f) : null });
            }

            if (_services.Faces is { } faces)
            {
                foreach (var group in Avatars.Select(a => a.Info).Where(a => a.FaceId is not null).GroupBy(a => a.LibraryId))
                {
                    faces.Request(group.Key, group.Select(a => (long)a.FaceId!.Value).ToList());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Storys: Personen der Story nicht geladen");
        }
    }

    private void OnStoriesChanged(StoriesChangedMessage message)
    {
        // Hintergrund-Erkennung bzw. Löschen: neu lesen. Eigene Aktionen laden selbst nach.
        if (message.Kind is not (StoriesChangeKind.Detected or StoriesChangeKind.Deleted)
            || (message.StoryIds.Count > 0 && !message.StoryIds.Contains(StoryId))
            || Interlocked.Exchange(ref _reloadScheduled, 1) != 0)
        {
            return;
        }

        UiThread.Post(async () =>
        {
            await Task.Delay(300);
            Volatile.Write(ref _reloadScheduled, 0);
            if (!_disposed)
            {
                await LoadAsync();
                if (NotFound)
                {
                    _close();
                }
            }
        });
    }

    // ---- Zeilen ----------------------------------------------------------------------------------------------------

    public void SetLayoutWidth(double width)
    {
        if (!double.IsFinite(width) || width < 200 || Math.Abs(width - LayoutWidth) < 1)
        {
            return;
        }

        LayoutWidth = Math.Floor(width);
        if (Details is not null)
        {
            RebuildRows();
        }
    }

    partial void OnModeChanged(StoryViewMode value)
    {
        _expandedDays.Clear();
        RebuildRows();
    }

    partial void OnShowAllDaysChanged(bool value) => RebuildRows();

    /// <summary>Alle Zeilen neu (Modus, Breite, auf-/zugeklappte Tage) — rein aus den geladenen Daten.</summary>
    public void RebuildRows()
    {
        _tiles.Clear();
        _dayRows.Clear();
        var rows = new List<StoryRow> { new StoryCoverRow(this), new StoryBarSpacerRow() };
        if (Details is null)
        {
            ReplaceRows(rows);
            return;
        }

        var width = LayoutWidth;
        var visible = ShowAllDays || Days.Count <= MaxOpenDays ? Days : Days.Take(OpenDays).ToList();
        foreach (var day in visible)
        {
            var picks = day.FileIds.Where(_highlightMap.ContainsKey).ToList();
            var counts = StoryTitleBuilder.Counts(day.FileIds.Count, day.VideoCount) + StoryTitleBuilder.Dot + picks.Count.ToString("N0", CultureInfo.CurrentCulture) + " ausgewählt";
            _dayRows[day.Index] = rows.Count;
            rows.Add(new StoryDayHeaderRow(day, counts));
            var expanded = _expandedDays.Contains(day.Index);
            if (Mode == StoryViewMode.Highlights && !expanded)
            {
                var photos = picks.Select(id => new StoryLayoutPhoto(id, Aspect(id), IsVideo(id), _highlightMap[id].Score)).ToList();
                foreach (var block in StoryLayoutEngine.Layout(photos, width, StoryLayoutEngine.DefaultGap, seed: day.Index))
                {
                    var tiles = block.Tiles.Select(t => Tile(t.FileId, t.X, t.Y, t.Width, t.Height, t.IsLarge, pale: false)).ToList();
                    rows.Add(new StoryTilesRow(block.Height + StoryLayoutEngine.DefaultGap, width, tiles, dense: false));
                }

                rows.Add(new StoryDayFooterRow(day, $"Alle {day.FileIds.Count:N0} {(day.FileIds.Count == 1 ? "Foto" : "Fotos")} dieses Abschnitts", expanded: false,
                    picks.Count == 0 ? "Keine Highlights in diesem Abschnitt." : null));
                continue;
            }

            rows.AddRange(DenseRows(day.FileIds, width));
            if (Mode == StoryViewMode.Highlights)
            {
                rows.Add(new StoryDayFooterRow(day, "Nur Highlights zeigen", expanded: true));
            }
        }

        if (visible.Count < Days.Count)
        {
            var rest = Days.Skip(visible.Count).ToList();
            var places = rest.Select(d => d.Places).Where(p => !string.IsNullOrEmpty(p)).SelectMany(p => p!.Split(StoryTitleBuilder.Dot))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            var range = rest[0].Date == rest[^1].Date ? rest[0].Chip : rest[0].Chip + " – " + rest[^1].Chip;
            rows.Add(new StoryCollapsedRow(
                $"{rest.Count:N0} weitere {(rest.Count == 1 ? "Tag" : "Tage")}",
                range + (places.Count > 0 ? StoryTitleBuilder.Dot + string.Join(", ", places) : string.Empty)));
        }

        rows.Add(new StoryEndRow(this, EndText));
        ReplaceRows(rows);
    }

    /// <summary>„64 von 421 Fotos als Highlights · 37 ähnliche weggelassen“ bzw. Hinweis zu blassen Kacheln.</summary>
    public string EndText
    {
        get
        {
            if (Mode == StoryViewMode.All)
            {
                return "Blasse Kacheln sind nicht in den Highlights.";
            }

            var similar = Highlights.Sum(h => h.SimilarCount);
            var text = $"{HighlightCount:N0} von {AllCount:N0} {(AllCount == 1 ? "Foto" : "Fotos")} als Highlights";
            return similar > 0 ? text + StoryTitleBuilder.Dot + $"{similar:N0} ähnliche weggelassen" : text;
        }
    }

    private IEnumerable<StoryRow> DenseRows(IReadOnlyList<int> fileIds, double width)
    {
        if (fileIds.Count == 0)
        {
            yield break;
        }

        var scale = Math.Clamp(width / StoryLayoutEngine.ReferenceWidth, 0.8, 1.4);
        var layout = new JustifiedRowLayout();
        var aspects = fileIds.Select(Aspect).ToArray();
        layout.Compute(aspects, width + DenseSpacing, DenseRowHeight * scale, DenseSpacing);
        for (var row = 0; row < layout.RowCount; row++)
        {
            var tiles = new List<StoryTileViewModel>();
            for (var i = layout.RowFirst(row); i <= layout.RowLast(row); i++)
            {
                var rect = layout.ItemRect(i);
                var id = fileIds[i];
                tiles.Add(Tile(id, rect.X, 0, Math.Max(1, rect.Width - DenseSpacing), Math.Max(1, rect.Height - DenseSpacing), false, pale: !_highlightMap.ContainsKey(id)));
            }

            yield return new StoryTilesRow(layout.RowHeight(row), width, tiles, dense: true);
        }
    }

    private StoryTileViewModel Tile(int fileId, double x, double y, double width, double height, bool large, bool pale)
    {
        _media.TryGetValue(fileId, out var media);
        var pick = _highlightMap.GetValueOrDefault(fileId);
        var tile = new StoryTileViewModel(fileId, x, y, width, height, large, media, pick?.Reason, pick?.SimilarCount ?? 0, pick is not null, pale)
        {
            Image = _services.Images.TryGet(fileId, large),
            Owner = this,
        };
        if (!_tiles.TryGetValue(fileId, out var list))
        {
            _tiles[fileId] = list = new List<StoryTileViewModel>(1);
        }

        list.Add(tile);
        return tile;
    }

    private double Aspect(int fileId) => _media.TryGetValue(fileId, out var m) ? m.Aspect : StoryLayoutEngine.DefaultAspect;

    private bool IsVideo(int fileId) => _media.TryGetValue(fileId, out var m) && m.IsVideo;

    private void ReplaceRows(List<StoryRow> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            row.Owner = this;
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(EndText));
    }

    // ---- Bilder ----------------------------------------------------------------------------------------------------

    /// <summary>Eine Zeile wird sichtbar: ihre Vorschaubilder anfordern (große Plätze in größerer Auflösung).</summary>
    public void RequestRowImages(StoryTilesRow row)
    {
        // große Plätze immer anfragen: ein schon geladenes kleines Bild (Karte, dichte Collage) wird dann ersetzt
        var large = row.Tiles.Where(t => t.IsLarge && t.Media is not null).Select(t => t.Media!).ToList();
        var small = row.Tiles.Where(t => !t.IsLarge && t.Media is not null && t.Image is null).Select(t => t.Media!).ToList();
        if (large.Count > 0)
        {
            _services.Images.Request(large, large: true);
        }

        if (small.Count > 0)
        {
            _services.Images.Request(small);
        }

        foreach (var tile in row.Tiles.Where(t => t.Image is null))
        {
            tile.Image = _services.Images.TryGet(tile.FileId, tile.IsLarge);
        }
    }

    internal void OnImagesReady(IReadOnlySet<int> fileIds)
    {
        foreach (var id in fileIds)
        {
            if (_tiles.TryGetValue(id, out var tiles))
            {
                var image = _services.Images.TryGet(id);
                foreach (var tile in tiles)
                {
                    tile.Image = image ?? tile.Image;
                }
            }
        }

        if (CoverImage is null && _coverFileId is { } cover && fileIds.Contains(cover))
        {
            CoverImage = _services.Images.TryGet(cover, large: true);
        }
    }

    internal void OnFacesReady(IReadOnlyDictionary<long, ImageSource> faces)
    {
        foreach (var avatar in Avatars)
        {
            if (avatar.Info.FaceId is { } f && faces.TryGetValue(f, out var image))
            {
                avatar.Image = image;
            }
        }
    }

    // ---- Leiste ----------------------------------------------------------------------------------------------------

    [RelayCommand]
    public void ShowHighlights() => Mode = StoryViewMode.Highlights;

    [RelayCommand]
    public void ShowAllPhotos() => Mode = StoryViewMode.All;

    /// <summary>Tages-Chip: zum Abschnitt springen (eingeklappte Tage werden aufgeklappt).</summary>
    [RelayCommand]
    public void JumpToDay(StoryDayChip? chip)
    {
        if (chip is null)
        {
            return;
        }

        foreach (var c in DayChips)
        {
            c.IsSelected = ReferenceEquals(c, chip);
        }

        if (!_dayRows.ContainsKey(chip.Index))
        {
            ShowAllDays = true; // baut die Zeilen neu
        }

        if (_dayRows.TryGetValue(chip.Index, out var row))
        {
            LastScrollRow = row;
            ScrollRequested?.Invoke(row);
        }
    }

    [RelayCommand]
    public void ToggleDay(StoryDayFooterRow? footer)
    {
        if (footer is null)
        {
            return;
        }

        if (!_expandedDays.Remove(footer.Day.Index))
        {
            _expandedDays.Add(footer.Day.Index);
        }

        RebuildRows();
        if (_dayRows.TryGetValue(footer.Day.Index, out var row) && !_expandedDays.Contains(footer.Day.Index))
        {
            LastScrollRow = row;
            ScrollRequested?.Invoke(row);
        }
    }

    [RelayCommand]
    public void ShowAllDaysNow() => ShowAllDays = true;

    /// <summary>Die Fotos, mit denen die Diashow startet: Highlights (sonst alle) bzw. alle im Modus „Alle Fotos“.</summary>
    public IReadOnlyList<int> SlideshowFileIds
        => Mode == StoryViewMode.Highlights && Highlights.Count > 0 ? Highlights.Select(h => h.FileId).ToList() : Details?.FileIds ?? [];

    [RelayCommand]
    public void StartSlideshow()
    {
        var ids = SlideshowFileIds;
        if (_services.Slideshow is null || ids.Count == 0)
        {
            return;
        }

        _services.Slideshow.Start(ids, 0, Title);
    }

    [RelayCommand]
    public void Back() => _close();

    // ---- Fotos -----------------------------------------------------------------------------------------------------

    /// <summary>Klick: im Modus „Titelbild wählen“ bzw. „Fotos entfernen“ wirkt er, sonst nicht (Doppelklick öffnet).</summary>
    [RelayCommand]
    public async Task TileClickAsync(StoryTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        if (PickMode != StoryPickMode.None)
        {
            _lastPickAt = DateTime.UtcNow;
        }

        switch (PickMode)
        {
            case StoryPickMode.Cover:
                await SetCoverAsync(tile);
                break;
            case StoryPickMode.Remove:
                tile.IsMarked = !tile.IsMarked;
                foreach (var twin in _tiles.GetValueOrDefault(tile.FileId) ?? [])
                {
                    twin.IsMarked = tile.IsMarked;
                }

                MarkedCount = _tiles.Values.Count(list => list.Any(t => t.IsMarked));
                break;
        }
    }

    /// <summary>Doppelklick: Lightbox mit den Fotos der Story, ab diesem.</summary>
    [RelayCommand]
    public void OpenTile(StoryTileViewModel? tile)
    {
        // im Auswahl-Modus (bzw. direkt nach einer Auswahl per Klick) öffnet ein Doppelklick nichts
        if (tile is null || _services.Lightbox is null || Details is null || PickMode != StoryPickMode.None
            || DateTime.UtcNow - _lastPickAt < TimeSpan.FromMilliseconds(700))
        {
            return;
        }

        var ids = Details.FileIds;
        var index = Math.Max(0, ids.ToList().IndexOf(tile.FileId));
        var start = Math.Max(0, Math.Min(index - LightboxLimit / 2, ids.Count - LightboxLimit));
        var entries = ids.Skip(start).Take(LightboxLimit)
            .Select(id => _media.TryGetValue(id, out var m) ? m : null).OfType<StoryMediaInfo>()
            .Select(m => new LightboxEntry(m.FileId, m.Path, m.Name, m.MediaType, m.IsFavorite, m.Rating, m.Size, m.ModifiedAtUtc))
            .ToList();
        var position = entries.FindIndex(e => e.FileId == tile.FileId);
        if (entries.Count > 0)
        {
            _services.Lightbox.Open(entries, Math.Max(0, position), System.Windows.Application.Current?.MainWindow, Lumora.Services.Usage.UsageViewContext.Story); // gedämpft (ADR-0072)
        }
    }

    [RelayCommand]
    public void PickCover() => PickMode = StoryPickMode.Cover;

    [RelayCommand]
    public void BeginRemove()
    {
        PickMode = StoryPickMode.Remove;
        MarkedCount = 0;
    }

    [RelayCommand]
    public void CancelPick()
    {
        PickMode = StoryPickMode.None;
        foreach (var tile in _tiles.Values.SelectMany(t => t))
        {
            tile.IsMarked = false;
        }

        MarkedCount = 0;
    }

    [RelayCommand]
    public async Task SetCoverAsync(StoryTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        PickMode = StoryPickMode.None;
        if (await RunAsync(() => _services.Stories.SetCoverAsync(StoryId, tile.FileId)))
        {
            Lumora.Services.Usage.UsageReport.Used([tile.FileId]); // Nutzungs-Signale (ADR-0072)
            StatusText = "Titelbild geändert.";
            await RefreshCardAsync(reloadCover: true);
        }
    }

    /// <summary>Gemeinsame Menü-Aktionen für „Als Titelbild für ▸“ (ADR-0068).</summary>
    public Lumora.PhotoMenu.PhotoActions? PhotoActions => _services.PhotoActions;

    /// <summary>Nach „Als Titelbild für ▸“: Meldung zeigen und das Titelbild neu laden.</summary>
    public async Task AfterCoverMenuAsync(string text)
    {
        StatusText = text;
        await RefreshCardAsync(reloadCover: true);
    }

    [RelayCommand]
    public async Task AutomaticCoverAsync()
    {
        PickMode = StoryPickMode.None;
        if (await RunAsync(() => _services.Stories.SetCoverAsync(StoryId, null)))
        {
            StatusText = "Titelbild wieder automatisch.";
            await RefreshCardAsync(reloadCover: true);
        }
    }

    private bool CanConfirmRemove() => MarkedCount > 0;

    [RelayCommand(CanExecute = nameof(CanConfirmRemove))]
    public async Task ConfirmRemoveAsync()
    {
        var ids = _tiles.Where(kv => kv.Value.Any(t => t.IsMarked)).Select(kv => kv.Key).ToList();
        PickMode = StoryPickMode.None;
        MarkedCount = 0;
        if (ids.Count == 0)
        {
            return;
        }

        if (await RunAsync(() => _services.Stories.RemoveFilesAsync(StoryId, ids)))
        {
            StatusText = $"{ids.Count:N0} {(ids.Count == 1 ? "Foto" : "Fotos")} aus der Story entfernt — sie bleiben in der Galerie.";
            await LoadAsync();
        }
    }

    /// <summary>Kontextmenü der Kachel: nur dieses Foto aus der Story nehmen.</summary>
    [RelayCommand]
    public async Task RemoveTileAsync(StoryTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        if (await RunAsync(() => _services.Stories.RemoveFilesAsync(StoryId, [tile.FileId])))
        {
            StatusText = "Foto aus der Story entfernt — es bleibt in der Galerie.";
            await LoadAsync();
        }
    }

    [RelayCommand]
    public void AddPhotos()
    {
        if (_services.Gallery is null)
        {
            return;
        }

        _services.Gallery.BeginAdd(StoryId, Title);
    }

    // ---- Menü ------------------------------------------------------------------------------------------------------

    /// <summary>„Titel ändern…“: nur eigener Text; leer = zurück zur sachlichen Überschrift.</summary>
    [RelayCommand]
    public async Task RenameAsync()
    {
        var text = await _services.Dialogs.PromptTextAsync(
            "Titel ändern",
            $"Eigener Titel — leer lassen für die sachliche Überschrift „{AutoTitle}“:",
            HasCustomTitle ? Title : string.Empty,
            "Übernehmen");
        if (text is null)
        {
            return;
        }

        await SetTitleAsync(text);
    }

    /// <summary>Eigenen Titel setzen bzw. mit leerem Text zurücksetzen (öffentlich für Tests/Rauchtest).</summary>
    public async Task SetTitleAsync(string? text)
    {
        var title = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (await RunAsync(() => _services.Stories.SetCustomTitleAsync(StoryId, title)))
        {
            StatusText = title is null ? "Wieder die sachliche Überschrift." : "Titel geändert.";
            await RefreshCardAsync();
        }
    }

    [RelayCommand]
    public Task ResetTitleAsync() => SetTitleAsync(null);

    [RelayCommand]
    public async Task TogglePinAsync()
    {
        var pinned = !IsPinned;
        if (await RunAsync(() => _services.Stories.SetPinnedAsync(StoryId, pinned)))
        {
            StatusText = pinned ? "Angeheftet — steht oben in der Übersicht." : "Nicht mehr angeheftet.";
            await RefreshCardAsync();
        }
    }

    /// <summary>„Als Album speichern“: die Fotos der aktuellen Ansicht (Highlights bzw. alle) als neues eigenes Album.</summary>
    [RelayCommand]
    public async Task SaveAsAlbumAsync()
    {
        try
        {
            ErrorText = null;
            var highlightsOnly = Mode == StoryViewMode.Highlights && Highlights.Count > 0;
            var album = await _services.Stories.SaveAsAlbumAsync(StoryId, null, highlightsOnly);
            StatusText = $"Als Album „{album.Name}“ gespeichert ({album.CountText}) — verknüpft, keine Kopien.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            _logger.LogWarning(ex, "Storys: als Album speichern fehlgeschlagen");
        }
    }

    [RelayCommand]
    public void ShowInGallery()
    {
        if (Details is { } d && _services.Gallery is not null)
        {
            _services.Gallery.ShowInGallery(d.FileIds);
        }
    }

    [RelayCommand]
    public async Task HideAsync()
    {
        if (await RunAsync(() => _services.Stories.SetHiddenAsync(StoryId, true)))
        {
            _close();
        }
    }

    [RelayCommand]
    public async Task RecomputeAsync()
    {
        if (await RunAsync(() => _services.Stories.RecomputeAsync(StoryId)))
        {
            StatusText = "Wird neu berechnet — die Story aktualisiert sich gleich.";
        }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (!CanDelete)
        {
            return;
        }

        var ok = await _services.Dialogs.ConfirmAsync(
            "Story löschen",
            $"Die Story „{Title}“ löschen? Nur die Story geht — die Fotos bleiben, wo sie sind.",
            "Story löschen",
            isDestructive: true);
        if (ok && await RunAsync(() => _services.Stories.DeleteAsync(StoryId)))
        {
            _close();
        }
    }

    /// <summary>
    /// „Als Collage-Bild speichern…“ (ADR-0063): Dialog mit den Highlights (sonst allen Fotos) der Story, Überschrift =
    /// Titel der Story (sachlich bzw. eigener). Gespeichert wird erst nach „Speichern“ im Dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveCollage))]
    private async Task SaveCollageAsync()
    {
        if (_services.Collage is null || Details is null)
        {
            return;
        }

        IReadOnlyList<int> ids = Highlights.Count >= 12 ? Highlights.Select(h => h.FileId).ToList() : Details.FileIds;
        var saved = await _services.Collage.ShowAsync(new Assistant.CollageRequest(Title, ids));
        if (saved is not null)
        {
            StatusText = $"Collage gespeichert: {System.IO.Path.GetFileName(saved.Path)} ({saved.Width} × {saved.Height}).";
        }
    }

    private async Task RefreshCardAsync(bool reloadCover = false)
    {
        var card = await _services.Stories.GetAsync(StoryId);
        if (card is null || Details is null)
        {
            return;
        }

        var coverChanged = card.CoverFileId != Details.Card.CoverFileId;
        Details = Details with { Card = card };
        RefreshHeader();
        if (reloadCover || coverChanged)
        {
            await LoadCoverAsync();
        }
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        try
        {
            ErrorText = null;
            await action();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            _logger.LogError(ex, "Storys: Aktion an Story {Id} fehlgeschlagen", StoryId);
        }

        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        _coverCts?.Cancel();
        _messenger.UnregisterAll(this);
    }
}
