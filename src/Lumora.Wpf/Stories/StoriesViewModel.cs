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
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Stories;
using Lumora.Shell;

namespace Lumora.Stories;

/// <summary>
/// Seite „Storys“ (ADR-0060, Mockup 09) — Übersicht und Gastgeber der Story-Ansicht: Kopf mit Zählern und Suche (Ort,
/// Person, Jahr; clientseitig über Überschrift, Untertitel, Art und Jahr), Art-Filter mit Zählern, „Ausgeblendet (n)“ mit
/// „Wieder zeigen“, Rückblick-Banner „Heute vor N Jahren“ (nur wenn es heute einen gibt), Abschnitte Angeheftet, Neu,
/// Reisen &amp; Ereignisse mit Jahres-Chips, Personen, Orte, eigene Storys. Die Übersicht ist eine virtualisierte Liste von
/// Zeilen (Kartenreihen so breit wie die Seite) — hunderte Storys bleiben flüssig; Vorschaubilder und Personen einer Reihe
/// kommen erst, wenn sie sichtbar wird. Lädt nach <see cref="StoriesChangedMessage"/> (gebündelt) und beim Bibliothekswechsel.
/// </summary>
public sealed partial class StoriesViewModel : ObservableObject, IDisposable
{
    /// <summary>Mindestbreite einer Karte (Mockup: minmax(232px, 1fr)).</summary>
    public const double MinCardWidth = 232;

    /// <summary>Abstand zwischen Karten.</summary>
    public const double CardGap = 16;

    /// <summary>Im Filter „Alle“: so viele Reihen Ereignisse, dann „Alle anzeigen“.</summary>
    public const int EventRowsInAll = 2;

    private readonly StoryPageServices _services;
    private readonly ILogger<StoriesViewModel> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IMessenger _messenger;
    private readonly StoriesNavigationBadge _badge;
    private readonly Dictionary<int, StoryCardViewModel> _cards = new();
    private readonly Dictionary<string, StoryMemoryViewModel> _memories = new(StringComparer.Ordinal);
    private IReadOnlyList<StoryCardViewModel> _hidden = [];
    private StoryYearChip? _selectedYear;
    private int _reloadScheduled;
    private bool _loaded;
    private long _loadSequence;

    public StoriesViewModel(
        StoryPageServices services,
        ILogger<StoriesViewModel> logger,
        StoriesNavigationBadge? badge = null,
        IMessenger? messenger = null,
        ILoggerFactory? loggerFactory = null,
        Assistant.StoryAssistantViewModel? assistant = null)
    {
        _services = services;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _badge = badge ?? StoriesNavigationBadge.Shared;
        Assistant = assistant; // Story-Assistent (ADR-0063): Leiste über der Übersicht
        if (assistant is not null)
        {
            assistant.OpenStory = OpenStoryAsync;
        }

        KindOptions =
        [
            new StoryKindOption(StoryKindFilter.All, "Alle") { IsSelected = true },
            new StoryKindOption(StoryKindFilter.Events, "Ereignisse & Reisen"),
            new StoryKindOption(StoryKindFilter.Memories, "Rückblicke"),
            new StoryKindOption(StoryKindFilter.Persons, "Personen"),
            new StoryKindOption(StoryKindFilter.Places, "Orte"),
        ];
        foreach (var option in KindOptions)
        {
            option.Owner = this;
        }

        _services.Images.ImagesReady += OnImagesReady;
        if (_services.Faces is not null)
        {
            _services.Faces.ImagesReady += OnFacesReady;
        }

        if (_services.Gallery is not null)
        {
            _services.Gallery.OpenRequested += OnOpenRequested;
        }

        _messenger.Register<StoriesViewModel, StoriesChangedMessage>(this, static (r, _) => r.ScheduleReload());
        _messenger.Register<StoriesViewModel, ActiveLibraryChangedMessage>(this, static (r, _) => UiThread.Post(() => r.OnLibraryChanged()));
    }

    public ObservableCollection<StoryOverviewRow> Rows { get; } = new();

    public ObservableCollection<StoryKindOption> KindOptions { get; }

    public StoryOverview Overview { get; private set; } = StoryOverview.Empty;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private string _summaryText = string.Empty;

    [ObservableProperty] private string? _statusText;

    [ObservableProperty] private string? _errorText;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private bool _showHidden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenText), nameof(HasHidden))]
    private int _hiddenCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStoryOpen))]
    private StoryViewModel? _currentStory;

    public bool IsStoryOpen => CurrentStory is not null;

    public bool HasHidden => HiddenCount > 0 || ShowHidden;

    /// <summary>„Ausgeblendet (3)“.</summary>
    public string HiddenText => $"Ausgeblendet ({HiddenCount:N0})";

    public StoryKindFilter KindFilter => KindOptions.FirstOrDefault(o => o.IsSelected)?.Filter ?? StoryKindFilter.All;

    /// <summary>Banner „Heute vor N Jahren“ (null, wenn es heute keinen gibt).</summary>
    public StoryMemoryViewModel? MemoryBanner { get; private set; }

    /// <summary>Breite der Kartenreihen in DIP (setzt die Seite).</summary>
    public double LayoutWidth { get; private set; } = 1000;

    public int Columns => ColumnsFor(LayoutWidth);

    public static int ColumnsFor(double width) => Math.Max(1, (int)((Math.Max(0, width) + CardGap) / (MinCardWidth + CardGap)));

    public bool HasSlideshow => _services.Slideshow is not null;

    /// <summary>Story-Assistent (ADR-0063) oder null (Tests ohne Assistent).</summary>
    public Assistant.StoryAssistantViewModel? Assistant { get; }

    // ---- Laden -----------------------------------------------------------------------------------------------------

    /// <summary>Beim Anzeigen der Seite: einmal laden, danach nur nach Nachrichten.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            await LoadAsync();
        }

        if (Assistant is not null)
        {
            await Assistant.RefreshAsync();
        }

        if (_services.Gallery?.TakePendingOpen() is { } pending)
        {
            await OpenStoryAsync(pending);
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var sequence = Interlocked.Increment(ref _loadSequence);
        IsLoading = true;
        try
        {
            ErrorText = null;
            var overview = await _services.Stories.GetOverviewAsync();
            IReadOnlyList<StoryCard> hidden = ShowHidden ? await _services.Stories.GetHiddenAsync() : [];
            if (sequence != Interlocked.Read(ref _loadSequence))
            {
                return; // ein neuerer Durchgang läuft
            }

            Apply(overview, hidden);
            _loaded = true;
        }
        catch (Exception ex)
        {
            ErrorText = $"Storys konnten nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Storys: Übersicht nicht geladen");
        }
        finally
        {
            if (sequence == Interlocked.Read(ref _loadSequence))
            {
                IsLoading = false;
            }
        }
    }

    private void Apply(StoryOverview overview, IReadOnlyList<StoryCard> hidden)
    {
        Overview = overview;
        HiddenCount = overview.HiddenCount;
        _badge.Update(overview.NewCount);
        SummaryText = overview.Counts.All == 0
            ? "Storys entstehen automatisch aus Datum, Ort und Personen"
            : $"{overview.Counts.All:N0} {(overview.Counts.All == 1 ? "Story" : "Storys")} aus {overview.TotalFiles:N0} Fotos und Videos · automatisch aus Datum, Ort und Personen";
        Count(StoryKindFilter.All, overview.Counts.All);
        Count(StoryKindFilter.Events, overview.Counts.EventsAndTrips);
        Count(StoryKindFilter.Memories, overview.Counts.Memories);
        Count(StoryKindFilter.Persons, overview.Counts.Persons);
        Count(StoryKindFilter.Places, overview.Counts.Places);

        var all = overview.Pinned.Concat(overview.New).Concat(overview.EventsAndTrips).Concat(overview.Persons).Concat(overview.Places).Concat(overview.Custom).Concat(hidden);
        var keep = new HashSet<int>();
        foreach (var card in all)
        {
            keep.Add(card.Id);
            if (_cards.TryGetValue(card.Id, out var existing) && existing.Card == card)
            {
                continue;
            }

            var vm = new StoryCardViewModel(card) { Owner = this };
            if (existing is not null && existing.Card.CoverFileId == card.CoverFileId && existing.Card.FileCount == card.FileCount
                && existing.Card.StartAt == card.StartAt && existing.Card.EndAt == card.EndAt)
            {
                // nur Zustand geändert (angeheftet, gesehen …): Bilder und Personen übernehmen
                vm.PreviewIds = existing.PreviewIds;
                vm.Image1 = existing.Image1;
                vm.Image2 = existing.Image2;
                vm.Image3 = existing.Image3;
                vm.PersonFace = existing.PersonFace;
                vm.PersonsLoaded = existing.PersonsLoaded;
                foreach (var avatar in existing.Avatars)
                {
                    vm.Avatars.Add(avatar);
                }
            }

            _cards[card.Id] = vm;
        }

        foreach (var id in _cards.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _cards.Remove(id);
        }

        _hidden = hidden.Select(c => _cards[c.Id]).ToList();
        var memoryKeys = new HashSet<string>(overview.Memories.Select(m => m.Key), StringComparer.Ordinal);
        foreach (var key in _memories.Keys.Where(k => !memoryKeys.Contains(k)).ToList())
        {
            _memories.Remove(key);
        }

        foreach (var memory in overview.Memories)
        {
            if (!_memories.TryGetValue(memory.Key, out var existing) || existing.Memory != memory)
            {
                var vm = new StoryMemoryViewModel(memory) { Owner = this };
                foreach (var id in memory.PreviewFileIds.Take(5))
                {
                    vm.Images.Add(new StoryTileImage(id));
                }

                vm.ApplyImages(id => _services.Images.TryGet(id));

                _memories[memory.Key] = vm;
            }
        }

        MemoryBanner = overview.Memories.FirstOrDefault(m => m.Kind == StoryMemoryKind.OnThisDay) is { } banner ? _memories[banner.Key] : null;
        RebuildRows();
    }

    private void Count(StoryKindFilter filter, int count)
    {
        foreach (var option in KindOptions.Where(o => o.Filter == filter))
        {
            option.Count = count;
        }
    }

    /// <summary>Gebündelt neu laden (die Erkennung meldet in Schüben; Benutzer-Aktionen sofort danach).</summary>
    private void ScheduleReload()
    {
        if (Interlocked.Exchange(ref _reloadScheduled, 1) != 0)
        {
            return;
        }

        UiThread.Post(async () =>
        {
            await Task.Delay(250);
            Volatile.Write(ref _reloadScheduled, 0);
            if (_loaded)
            {
                await LoadAsync();
            }
        });
    }

    private void OnLibraryChanged()
    {
        CloseStory();
        _cards.Clear();
        _memories.Clear();
        if (_loaded)
        {
            _loaded = false;
            _ = LoadAsync(); // nur, wenn die Seite schon einmal gezeigt wurde (sonst beim ersten Anzeigen)
        }
    }

    // ---- Zeilen ----------------------------------------------------------------------------------------------------

    /// <summary>Breite geändert (Seite): Kartenreihen neu umbrechen, wenn sich die Spaltenzahl oder Kartenbreite ändert.</summary>
    public void SetLayoutWidth(double width)
    {
        if (!double.IsFinite(width) || width < 100 || Math.Abs(width - LayoutWidth) < 1)
        {
            return;
        }

        LayoutWidth = Math.Floor(width);
        OnPropertyChanged(nameof(Columns));
        RebuildRows();
    }

    partial void OnSearchTextChanged(string value) => RebuildRows();

    partial void OnShowHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(HasHidden));
        _ = LoadAsync();
    }

    /// <summary>Zeilen aus Übersicht, Filter, Suche, Jahr und Breite (rein, ohne Datenbank).</summary>
    public void RebuildRows()
    {
        var rows = new List<StoryOverviewRow>();
        var columns = Columns;
        var cardWidth = Math.Floor((LayoutWidth - CardGap * (columns - 1)) / columns);
        var search = SearchText.Trim();
        var filter = KindFilter;

        void Cards(IEnumerable<StoryCardViewModel> cards, int maxRows = int.MaxValue)
        {
            var list = cards.ToList();
            foreach (var card in list)
            {
                card.Width = cardWidth;
            }

            for (var i = 0; i < list.Count && i / columns < maxRows; i += columns)
            {
                rows.Add(new OverviewCardRow(list.Skip(i).Take(columns).ToList(), cardWidth));
            }
        }

        IEnumerable<StoryCardViewModel> Vm(IEnumerable<StoryCard> cards)
            => cards.Where(c => _cards.ContainsKey(c.Id) && Matches(c, search)).Select(c => _cards[c.Id]);

        if (ShowHidden)
        {
            rows.Add(new OverviewSectionRow("Ausgeblendet", _hidden.Count.ToString("N0", CultureInfo.CurrentCulture), "Zurück zu den Storys", StoryKindFilter.All));
            var hidden = _hidden.Where(c => Matches(c.Card, search)).ToList();
            if (hidden.Count == 0)
            {
                rows.Add(new OverviewNoteRow("Keine ausgeblendeten Storys", "Ausgeblendete Storys erscheinen hier und lassen sich mit „Wieder zeigen“ zurückholen."));
            }

            Cards(hidden);
            ReplaceRows(rows);
            return;
        }

        // Rückblicke
        var memories = Overview.Memories.Where(m => MatchesMemory(m, search)).Select(m => _memories[m.Key]).ToList();
        if (filter == StoryKindFilter.All)
        {
            if (MemoryBanner is { } banner && memories.Contains(banner))
            {
                rows.Add(new OverviewMemoryRow(banner));
            }

            // „Vergessene Schätze“ (ADR-0072) unter „Alle“ zusätzlich zum Banner „Heute vor … Jahren“
            rows.AddRange(memories.Where(m => m.Memory.Kind == StoryMemoryKind.ForgottenTreasures).Select(m => new OverviewMemoryRow(m)));
        }
        else if (filter == StoryKindFilter.Memories)
        {
            rows.Add(new OverviewSectionRow("Rückblicke von heute", memories.Count.ToString("N0", CultureInfo.CurrentCulture)));
            rows.AddRange(memories.Select(m => new OverviewMemoryRow(m)));
            if (memories.Count == 0)
            {
                rows.Add(new OverviewNoteRow("Heute keine Rückblicke", "„Heute vor … Jahren“ erscheint, sobald es an diesem Kalendertag in früheren Jahren genug Fotos gibt."));
            }
        }

        // Angeheftet
        var pinned = Vm(Overview.Pinned.Where(c => InFilter(c.Kind, filter))).ToList();
        if (pinned.Count > 0)
        {
            rows.Add(new OverviewSectionRow("Angeheftet", pinned.Count.ToString("N0", CultureInfo.CurrentCulture)));
            Cards(pinned);
        }

        // Neu
        if (filter is StoryKindFilter.All or StoryKindFilter.Events)
        {
            var fresh = Vm(Overview.New.Where(c => InFilter(c.Kind, filter))).ToList();
            if (fresh.Count > 0)
            {
                rows.Add(new OverviewSectionRow("Neu", "seit dem letzten Besuch"));
                Cards(fresh, filter == StoryKindFilter.All ? 1 : int.MaxValue);
            }
        }

        // Reisen & Ereignisse mit Jahren
        if (filter is StoryKindFilter.All or StoryKindFilter.Events)
        {
            var events = Vm(Overview.EventsAndTrips).ToList();
            if (events.Count > 0)
            {
                var inAll = filter == StoryKindFilter.All;
                var more = inAll && events.Count > columns * EventRowsInAll ? "Alle anzeigen" : null;
                rows.Add(new OverviewSectionRow("Reisen & Ereignisse", events.Count.ToString("N0", CultureInfo.CurrentCulture), more, StoryKindFilter.Events));
                var chips = YearChips(events);
                if (chips.Count > 2)
                {
                    rows.Add(new OverviewYearsRow(chips));
                }

                var year = chips.FirstOrDefault(c => c.IsSelected);
                Cards(events.Where(c => year is null || year.Matches(c.Card.Year)), inAll ? EventRowsInAll : int.MaxValue);
            }
        }

        // Personen
        if (filter is StoryKindFilter.All or StoryKindFilter.Persons)
        {
            var persons = Vm(Overview.Persons).ToList();
            if (persons.Count > 0)
            {
                var more = filter == StoryKindFilter.All && persons.Count > columns ? $"Alle {persons.Count:N0}" : null;
                rows.Add(new OverviewSectionRow("Personen", "Jahre mit vielen bestätigten Fotos", more, StoryKindFilter.Persons));
                Cards(persons, filter == StoryKindFilter.All ? 1 : int.MaxValue);
            }
        }

        // Orte
        if (filter is StoryKindFilter.All or StoryKindFilter.Places)
        {
            var places = Vm(Overview.Places).ToList();
            if (places.Count > 0)
            {
                var more = filter == StoryKindFilter.All && places.Count > columns ? $"Alle {places.Count:N0}" : null;
                rows.Add(new OverviewSectionRow("Orte", "Orte mit mehreren Besuchen", more, StoryKindFilter.Places));
                Cards(places, filter == StoryKindFilter.All ? 1 : int.MaxValue);
            }
        }

        // Eigene Storys (aus einer Auswahl)
        if (filter == StoryKindFilter.All)
        {
            var custom = Vm(Overview.Custom).ToList();
            if (custom.Count > 0)
            {
                rows.Add(new OverviewSectionRow("Eigene Storys", custom.Count.ToString("N0", CultureInfo.CurrentCulture)));
                Cards(custom);
            }
        }

        if (!rows.Any(r => r is OverviewCardRow or OverviewMemoryRow) && !rows.Any(r => r is OverviewNoteRow))
        {
            rows.Add(search.Length > 0
                ? new OverviewNoteRow($"Keine Story zu „{search}“", "Gesucht wird in Überschrift, Orten, Personen, Art und Jahr.")
                : Overview.Counts.All == 0
                    ? new OverviewNoteRow("Noch keine Storys", "Lumora bildet Storys automatisch im Hintergrund, sobald Aufnahmedaten und Orte gelesen sind — Ereignisse, Reisen, Rückblicke, Personen und Orte. Eigene Storys: in der Galerie Fotos markieren → Rechtsklick „Als Story…“.")
                    : new OverviewNoteRow("Nichts in dieser Ansicht", "Wähle oben eine andere Art."));
        }

        ReplaceRows(rows);
    }

    private void ReplaceRows(List<StoryOverviewRow> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            row.Owner = this;
            Rows.Add(row);
        }
    }

    private List<StoryYearChip> YearChips(IReadOnlyList<StoryCardViewModel> events)
    {
        var years = events.GroupBy(c => c.Card.Year).OrderByDescending(g => g.Key).ToList();
        var chips = new List<StoryYearChip> { new("Alle", events.Count, null, null) };
        const int shown = 6;
        chips.AddRange(years.Take(shown).Select(g => new StoryYearChip(g.Key.ToString(CultureInfo.InvariantCulture), g.Count(), g.Key, g.Key)));
        if (years.Count > shown)
        {
            var older = years.Skip(shown).ToList();
            chips.Add(new StoryYearChip("ältere", older.Sum(g => g.Count()), null, older[0].Key));
        }

        foreach (var chip in chips)
        {
            chip.Owner = this;
        }

        var selected = _selectedYear is { } s ? chips.FirstOrDefault(c => c.Label == s.Label) : null;
        (selected ?? chips[0]).IsSelected = true;
        return chips;
    }

    private static bool InFilter(StoryKind kind, StoryKindFilter filter) => filter switch
    {
        StoryKindFilter.Events => kind is StoryKind.Event or StoryKind.Trip,
        StoryKindFilter.Memories => kind == StoryKind.Memory,
        StoryKindFilter.Persons => kind == StoryKind.Person,
        StoryKindFilter.Places => kind == StoryKind.Place,
        _ => true,
    };

    /// <summary>Suche: alle Wörter müssen in Überschrift, sachlicher Überschrift, Untertitel, Art oder Jahr(en) vorkommen.</summary>
    internal static bool Matches(StoryCard card, string search)
        => MatchesText(search, card.Title, card.AutoTitle, card.Subtitle, card.KindLabel, card.FirstDay.Year.ToString(CultureInfo.InvariantCulture), card.LastDay.Year.ToString(CultureInfo.InvariantCulture));

    internal static bool MatchesMemory(StoryMemory memory, string search)
        => MatchesText(search, memory.Kicker, memory.Title, memory.Subtitle, "Rückblick", memory.Date.Year.ToString(CultureInfo.InvariantCulture));

    private static bool MatchesText(string search, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var haystack = string.Join(" ", fields.Where(f => !string.IsNullOrEmpty(f)));
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => CultureInfo.CurrentCulture.CompareInfo.IndexOf(haystack, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }

    // ---- Filter-Befehle --------------------------------------------------------------------------------------------

    [RelayCommand]
    public void SelectKind(StoryKindOption? option)
    {
        if (option is null)
        {
            return;
        }

        foreach (var o in KindOptions)
        {
            o.IsSelected = ReferenceEquals(o, option);
        }

        OnPropertyChanged(nameof(KindFilter));
        if (ShowHidden)
        {
            ShowHidden = false; // lädt neu und baut die Zeilen
            return;
        }

        RebuildRows();
    }

    [RelayCommand]
    public void ShowMore(OverviewSectionRow? section)
    {
        if (section?.MoreTarget is not { } target)
        {
            return;
        }

        if (ShowHidden)
        {
            ShowHidden = false;
            return;
        }

        SelectKind(KindOptions.First(o => o.Filter == target));
    }

    [RelayCommand]
    public void SelectYear(StoryYearChip? chip)
    {
        _selectedYear = chip?.FromYear is null && chip?.ToYear is null ? null : chip;
        RebuildRows();
    }

    [RelayCommand]
    private void ToggleHidden() => ShowHidden = !ShowHidden;

    /// <summary>„Story aus Auswahl…“: Hinweis, wo das geht.</summary>
    [RelayCommand]
    private void StoryFromSelectionHint()
        => StatusText = "Story aus Auswahl: in der Galerie Fotos markieren → Rechtsklick „Als Story…“.";

    // ---- Bilder ----------------------------------------------------------------------------------------------------

    /// <summary>Eine Kartenreihe wird sichtbar: Vorschaubilder und Personen ihrer Karten laden.</summary>
    public async Task RequestRowAsync(OverviewCardRow row)
    {
        try
        {
            var needPreviews = row.Cards.Where(c => c.PreviewIds is null).Select(c => c.Id).ToList();
            if (needPreviews.Count > 0)
            {
                var previews = await _services.Stories.GetPreviewFileIdsAsync(needPreviews, 3);
                foreach (var card in row.Cards.Where(c => c.PreviewIds is null))
                {
                    card.PreviewIds = previews.TryGetValue(card.Id, out var ids) ? ids : [];
                }
            }

            var files = row.Cards.SelectMany(c => c.PreviewIds ?? []).Distinct().ToList();
            var missing = files.Where(id => _services.Images.TryGet(id) is null).ToList();
            if (missing.Count > 0)
            {
                var media = await _services.Media.LoadAsync(missing);
                _services.Images.Request(media.Values);
            }

            foreach (var card in row.Cards)
            {
                card.ApplyImages(id => _services.Images.TryGet(id));
            }

            var needPersons = row.Cards.Where(c => !c.PersonsLoaded).ToList();
            if (needPersons.Count > 0)
            {
                var persons = await _services.Media.LoadPersonsAsync(needPersons.Select(c => c.Id).ToList(), 3);
                foreach (var card in needPersons)
                {
                    card.PersonsLoaded = true;
                    card.Avatars.Clear();
                    foreach (var info in persons.GetValueOrDefault(card.Id) ?? [])
                    {
                        card.Avatars.Add(new StoryAvatarViewModel(info) { Image = info.FaceId is { } f ? _services.Faces?.TryGet(f) : null });
                    }

                    if (card.IsPerson)
                    {
                        card.PersonFace = card.Avatars.FirstOrDefault(a => a.Info.PersonId == card.Card.PersonId)?.Image;
                    }
                }

                RequestFaces(needPersons.SelectMany(c => c.Avatars).Select(a => a.Info));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storys: Bilder einer Kartenreihe nicht geladen");
        }
    }

    /// <summary>Rückblick-Banner wird sichtbar: seine Bilder laden (das erste groß).</summary>
    public async Task RequestMemoryAsync(StoryMemoryViewModel memory)
    {
        try
        {
            var ids = memory.Images.Where(i => i.Image is null).Select(i => i.FileId).ToList();
            if (ids.Count == 0)
            {
                return;
            }

            var media = await _services.Media.LoadAsync(ids);
            if (memory.Images.Count > 0 && media.TryGetValue(memory.Images[0].FileId, out var first))
            {
                _services.Images.Request([first], large: true);
            }

            _services.Images.Request(media.Values.Where(m => memory.Images.Count == 0 || m.FileId != memory.Images[0].FileId));
            memory.ApplyImages(id => _services.Images.TryGet(id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storys: Bilder des Rückblicks nicht geladen");
        }
    }

    private void RequestFaces(IEnumerable<StoryAvatarInfo> avatars)
    {
        if (_services.Faces is not { } faces)
        {
            return;
        }

        foreach (var group in avatars.Where(a => a.FaceId is not null && faces.TryGet(a.FaceId.Value) is null).GroupBy(a => a.LibraryId))
        {
            faces.Request(group.Key, group.Select(a => (long)a.FaceId!.Value).Distinct().ToList());
        }
    }

    private void OnImagesReady(IReadOnlyList<int> fileIds)
    {
        var ready = fileIds.ToHashSet();
        foreach (var card in _cards.Values)
        {
            if (card.PreviewIds?.Any(ready.Contains) == true)
            {
                card.ApplyImages(id => _services.Images.TryGet(id));
            }
        }

        foreach (var memory in _memories.Values)
        {
            if (memory.Images.Any(i => ready.Contains(i.FileId)))
            {
                memory.ApplyImages(id => _services.Images.TryGet(id));
            }
        }

        CurrentStory?.OnImagesReady(ready);
    }

    private void OnFacesReady(IReadOnlyList<(long FaceId, ImageSource Image)> batch)
    {
        var map = new Dictionary<long, ImageSource>();
        foreach (var (faceId, image) in batch)
        {
            map[faceId] = image; // dieselbe Person kann mehrfach im Stapel stehen
        }

        foreach (var card in _cards.Values)
        {
            foreach (var avatar in card.Avatars)
            {
                if (avatar.Info.FaceId is { } f && map.TryGetValue(f, out var image))
                {
                    avatar.Image = image;
                    if (card.IsPerson && avatar.Info.PersonId == card.Card.PersonId)
                    {
                        card.PersonFace = image;
                    }
                }
            }
        }

        CurrentStory?.OnFacesReady(map);
    }

    // ---- Aktionen --------------------------------------------------------------------------------------------------

    [RelayCommand]
    public Task OpenCardAsync(StoryCardViewModel? card) => card is null ? Task.CompletedTask : OpenStoryAsync(card.Id);

    /// <summary>Rückblick öffnen: wird dabei als Story gespeichert (anheften, Titelbild …).</summary>
    [RelayCommand]
    public async Task OpenMemoryAsync(StoryMemoryViewModel? memory)
    {
        if (memory is null)
        {
            return;
        }

        var id = await RunAsync(() => _services.Stories.OpenMemoryAsync(memory.Memory.Key));
        if (id is { } storyId)
        {
            await OpenStoryAsync(storyId);
        }
    }

    /// <summary>Story öffnen (Übersicht, Galerie, Rauchtest).</summary>
    public async Task OpenStoryAsync(int storyId)
    {
        CloseStory();
        var story = new StoryViewModel(_services, storyId, CloseStory, _loggerFactory?.CreateLogger<StoryViewModel>() ?? NullLogger<StoryViewModel>.Instance);
        CurrentStory = story;
        await story.LoadAsync();
        if (story.NotFound && ReferenceEquals(CurrentStory, story))
        {
            CloseStory();
            StatusText = "Diese Story gibt es nicht mehr.";
        }
    }

    [RelayCommand]
    public void CloseStory()
    {
        if (CurrentStory is { } story)
        {
            CurrentStory = null;
            story.Dispose();
        }
    }

    private void OnOpenRequested(int storyId)
    {
        // vorgemerkt UND gemeldet: wer zuerst abholt, öffnet (sonst öffnete EnsureLoadedAsync dieselbe Story ein zweites Mal)
        if (_services.Gallery?.TakePendingOpen() is { } id)
        {
            UiThread.Post(() => _ = OpenStoryAsync(id));
        }
    }

    /// <summary>„Diashow“ einer Karte: Highlights (sonst alle Fotos) ab dem Anfang.</summary>
    [RelayCommand]
    public async Task StartCardSlideshowAsync(StoryCardViewModel? card)
    {
        if (card is null || _services.Slideshow is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var highlights = await _services.Stories.GetHighlightsAsync(card.Id);
            IReadOnlyList<int> ids = highlights.Count > 0 ? highlights.Select(h => h.FileId).ToList() : (await _services.Stories.GetDetailsAsync(card.Id))?.FileIds ?? [];
            if (ids.Count > 0)
            {
                _services.Slideshow.Start(ids, 0, card.Title);
            }

            return 0;
        });
    }

    [RelayCommand]
    public async Task StartMemorySlideshowAsync(StoryMemoryViewModel? memory)
    {
        if (memory is null || _services.Slideshow is null)
        {
            return;
        }

        var id = await RunAsync(() => _services.Stories.OpenMemoryAsync(memory.Memory.Key));
        if (id is { } storyId)
        {
            await RunAsync(async () =>
            {
                var highlights = await _services.Stories.GetHighlightsAsync(storyId);
                var ids = highlights.Count > 0 ? highlights.Select(h => h.FileId).ToList() : (await _services.Stories.GetDetailsAsync(storyId))?.FileIds ?? [];
                if (ids.Count > 0)
                {
                    _services.Slideshow!.Start(ids, 0, memory.Title);
                }

                return 0;
            });
        }
    }

    [RelayCommand]
    public Task TogglePinAsync(StoryCardViewModel? card)
        => card is null ? Task.CompletedTask : RunAsync(async () => { await _services.Stories.SetPinnedAsync(card.Id, !card.IsPinned); return 0; });

    [RelayCommand]
    public async Task HideAsync(StoryCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        await RunAsync(async () => { await _services.Stories.SetHiddenAsync(card.Id, true); return 0; });
        StatusText = $"„{card.Title}“ ausgeblendet — unter „Ausgeblendet“ wieder zeigen.";
    }

    [RelayCommand]
    public async Task UnhideAsync(StoryCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        await RunAsync(async () => { await _services.Stories.SetHiddenAsync(card.Id, false); return 0; });
        StatusText = $"„{card.Title}“ wird wieder gezeigt.";
        await LoadAsync();
    }

    private async Task<T?> RunAsync<T>(Func<Task<T>> action)
        where T : struct
    {
        try
        {
            ErrorText = null;
            return await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            _logger.LogError(ex, "Storys: Aktion fehlgeschlagen");
        }

        return null;
    }

    public void Dispose()
    {
        CloseStory();
        Assistant?.Dispose();
        _services.Images.ImagesReady -= OnImagesReady;
        if (_services.Faces is not null)
        {
            _services.Faces.ImagesReady -= OnFacesReady;
        }

        if (_services.Gallery is not null)
        {
            _services.Gallery.OpenRequested -= OnOpenRequested;
        }

        _messenger.UnregisterAll(this);
    }
}
