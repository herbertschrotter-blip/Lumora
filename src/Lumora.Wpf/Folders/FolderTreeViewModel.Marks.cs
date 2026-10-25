using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Marks;
using Lumora.Services.Categories;
using Lumora.Services.Marks;
using Lumora.Services.Tags;
using Lumora.Shell;

namespace Lumora.Folders;

/// <summary>
/// Tag- und Kategorie-Symbole im Ordnerbaum (Mockup 16, 0.4.35): je Ordner die Zahl der Dateien DIREKT darin mit Tags bzw.
/// Kategorie-Werten (<see cref="FolderNode.Marks"/>), Tooltip mit „n von m Fotos“ und den häufigsten Namen. Die Zahlen kommen
/// in einer gruppierten Abfrage für den ganzen Bereich (<see cref="IFolderMarkCounter"/>, ADR-0048) — nach dem Aufbau des
/// Baums im Hintergrund, damit der Baum nicht darauf wartet. Neu gezählt wird gebündelt nach Zuweisungen
/// (<c>FilesChangedMessage</c> Metadata), Änderungen an Tag-/Kategorie-Stammdaten und beim Umlegen des Schalters
/// „Im Ordnerbaum“; ist er aus, wird nichts abgefragt.
/// </summary>
public sealed partial class FolderTreeViewModel
{
    private const int MarksDebounceMs = 400;

    private IFolderMarkCounter? _markCounter;
    private ITagService? _markTags;
    private ICategoryService? _markCategories;
    private MarkSettingsState _markSettings = new();
    private FolderMarkCounts _folderMarks = FolderMarkCounts.Empty;
    private FolderMarkNames _markNames = FolderMarkNames.Empty;
    private int[] _markLibraryIds = [];
    private int _marksVersion;
    private bool _markNamesStale = true; // Namen nur nach Stammdaten-Änderung bzw. Neuaufbau neu laden, nicht bei jeder Zuweisung
    private CancellationTokenSource? _marksDebounce;

    /// <summary>Letzte angestoßene Zählung (Tests, Rauchtest).</summary>
    internal Task MarksRefresh { get; private set; } = Task.CompletedTask;

    private void InitMarks(IFolderMarkCounter? counter, MarkSettingsState? settings, ITagService? tags, ICategoryService? categories)
    {
        _markCounter = counter;
        _markTags = tags;
        _markCategories = categories;
        _markSettings = settings ?? new MarkSettingsState();
        if (counter is null)
        {
            return;
        }

        PropertyChangedEventManager.AddHandler(_markSettings, OnMarkSettingsChanged, nameof(MarkSettingsState.ShowInFolderTree));
        _messenger.Register<FolderTreeViewModel, TagsChangedMessage>(this, static (r, m) => UiThread.Post(r.OnMarkNamesChanged));
        _messenger.Register<FolderTreeViewModel, CategoriesChangedMessage>(this, static (r, m) => UiThread.Post(r.OnMarkNamesChanged));
    }

    private void OnMarkNamesChanged()
    {
        _markNamesStale = true;
        ScheduleMarksRefresh();
    }

    private void OnMarkSettingsChanged(object? sender, PropertyChangedEventArgs e) => UiThread.Post(() => MarksRefresh = RefreshMarksAsync());

    /// <summary>Nach dem Neuaufbau des Baums (ReloadAsync): Bereich merken und sofort zählen.</summary>
    private void OnTreeLoadedForMarks(IReadOnlyCollection<int> libraryIds)
    {
        _markLibraryIds = libraryIds.ToArray();
        _markNamesStale = true; // Bereich kann gewechselt haben (andere Gruppe → andere Tags/Kategorien)
        ApplyMarks(); // neue Knoten bekommen die bisherigen Zahlen, bis die frische Zählung da ist
        MarksRefresh = RefreshMarksAsync();
    }

    /// <summary>Zuweisung von Tags/Kategorien (oder Favorit/Bewertung) in einer Bibliothek des Bereichs → gebündelt neu zählen.</summary>
    private void OnMetadataChangedForMarks(int libraryId)
    {
        if (IsInLoadedScope(libraryId))
        {
            ScheduleMarksRefresh();
        }
    }

    private void ScheduleMarksRefresh()
    {
        if (_markCounter is null || _loadedLibraryId is null || !_markSettings.ShowInFolderTree)
        {
            return;
        }

        _marksDebounce?.Cancel();
        _marksDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _marksDebounce = cts;
        MarksRefresh = RefreshDebouncedAsync(cts.Token);
    }

    private async Task RefreshDebouncedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(MarksDebounceMs, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RefreshMarksAsync().ConfigureAwait(true);
    }

    private async Task RefreshMarksAsync()
    {
        var version = ++_marksVersion;
        if (_markCounter is null || !_markSettings.ShowInFolderTree || _markLibraryIds.Length == 0)
        {
            _folderMarks = FolderMarkCounts.Empty;
            ApplyMarks();
            return;
        }

        try
        {
            // Zählung und Namen parallel; beide werden abgewartet (keine unbeobachtete Ausnahme)
            var countTask = _markCounter.CountAsync(_markLibraryIds);
            var reloadNames = _markNamesStale;
            _markNamesStale = false;
            var namesTask = reloadNames ? LoadMarkNamesAsync() : Task.FromResult(FolderMarkNames.Empty);
            await Task.WhenAll(countTask, namesTask).ConfigureAwait(true);
            if (reloadNames)
            {
                _markNames = namesTask.Result; // auch für eine inzwischen neuere Zählung
            }

            if (version != _marksVersion)
            {
                return; // inzwischen neuer angestoßen
            }

            var counts = countTask.Result;
            _folderMarks = counts;
            ApplyMarks();
            _logger.LogDebug("Ordnerbaum: Tag-/Kategorie-Zahlen für {Folders} Ordner", counts.ByFolder.Count);
        }
        catch (Exception ex)
        {
            _markNamesStale = true;
            _logger.LogWarning(ex, "Ordnerbaum: Tag-/Kategorie-Zahlen konnten nicht geladen werden");
        }
    }

    private async Task<FolderMarkNames> LoadMarkNamesAsync()
    {
        IReadOnlyList<TagInfo> tags = _markTags is null ? [] : await _markTags.GetAllAsync().ConfigureAwait(true);
        IReadOnlyList<CategoryInfo> categories = _markCategories is null ? [] : await _markCategories.GetAllAsync().ConfigureAwait(true);
        var labels = new Dictionary<int, string>();
        foreach (var category in categories)
        {
            foreach (var value in category.Values)
            {
                labels[value.Id] = $"{category.Name}: {value.Name}";
            }
        }

        return new FolderMarkNames(tags.ToDictionary(t => t.Id), labels);
    }

    /// <summary>Zahlen eines (neuen) Knotens; Kopfknoten und Platzhalter haben keine.</summary>
    private FolderMarkSummary? MarksFor(FolderNode node)
    {
        if (!node.IsFolder || !_markSettings.ShowInFolderTree || _folderMarks.Get(node.Id) is not { IsEmpty: false } count)
        {
            return null;
        }

        return new FolderMarkSummary(count, node.FileCount, _markNames);
    }

    private FolderNode WithMarks(FolderNode node)
    {
        node.Marks = MarksFor(node);
        return node;
    }

    /// <summary>Allen bereits erzeugten Knoten die aktuellen Zahlen geben (Kinder entstehen lazy und holen sie selbst).</summary>
    private void ApplyMarks()
    {
        void Visit(IEnumerable<FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder)
                {
                    continue;
                }

                node.Marks = MarksFor(node);
                Visit(node.Children);
            }
        }

        Visit(Roots);
    }
}
