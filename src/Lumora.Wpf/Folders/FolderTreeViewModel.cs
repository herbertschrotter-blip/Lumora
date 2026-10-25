using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Shell;

namespace Lumora.Folders;

/// <summary>
/// Ordnerbaum der aktiven Bibliothek. Lädt alle Folder-Zeilen (eine Query) und die Dateianzahl je Ordner
/// (eine GroupBy-Query), baut daraus einen In-Memory-Index und materialisiert Knoten lazy beim Aufklappen.
/// Oberste Knoten sind die Wurzeln der Bibliothek mit Rolle (ADR-0020: Archiv zuerst, dann Eingänge).
/// Auswahl → FolderSelectedMessage; „Gesamte Bibliothek“ → FolderId null.
/// Kontextmenü „Neuer Ordner“ / „Umbenennen“ läuft protokolliert über <see cref="IFileOperationService"/>.
/// Lädt neu bei ActiveLibraryChangedMessage, ScanCompletedMessage, LibraryRootsChangedMessage, FoldersChangedMessage
/// und (gebündelt) nach Datei-Operationen (FilesChangedMessage Moved/Removed/Added: Zähler und neue Ordner);
/// aufgeklappte Knoten und die Auswahl werden dabei über den Pfad wiederhergestellt.
/// </summary>
public sealed partial class FolderTreeViewModel : ObservableObject
{
    private const string WholeLibraryText = "Gesamte Bibliothek";
    private const int ReloadDebounceMs = 300;

    private sealed record FolderRow(int Id, int? ParentId, int? RootId, string Name, string Path, int LibraryId = 0);

    private readonly ILibraryScope _scope;
    private LibraryScopeInfo _loadedScope = LibraryScopeInfo.None;
    private Dictionary<int, int> _libraryOfFolder = new();
    private readonly ILibraryContextFactory _factory;
    private readonly ILibraryService _libraries;
    private readonly IFileOperationService _fileOps;
    private readonly IDialogService _dialogs;
    private readonly FolderActions _folderActions;
    private readonly ILogger<FolderTreeViewModel> _logger;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;

    private Dictionary<int, List<FolderRow>> _childrenByParent = new();
    private List<FolderRow> _rootRows = new();
    private Dictionary<int, int> _directCounts = new();
    private Dictionary<int, int> _totalCounts = new();
    private Dictionary<int, LibraryRootInfo> _rootsById = new();
    private FolderNode? _selectedNode;
    private int _libraryId;
    private int? _loadedLibraryId;
    private bool _reloadPending;
    private CancellationTokenSource? _reloadDebounce;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasLibrary;

    [ObservableProperty]
    private string? _libraryName;

    [ObservableProperty]
    private string? _hintText;

    [ObservableProperty]
    private string _selectionText = WholeLibraryText;

    [ObservableProperty]
    private bool _includeSubfolders = true;

    public FolderTreeViewModel(
        ILibraryContextFactory factory,
        ILibraryService libraries,
        IFileOperationService fileOps,
        IDialogService dialogs,
        FolderActions folderActions,
        ILogger<FolderTreeViewModel> logger,
        Libraries.IInboxActions? inboxActions = null,
        Gallery.CaptureDates.CaptureDateActions? captureDateActions = null,
        ILibraryScope? scope = null,
        Removal.RemovalActions? removalActions = null,
        Exclusions.ScanExclusionActions? exclusionActions = null,
        FolderMoveActions? moveActions = null,
        Lumora.Services.Marks.IFolderMarkCounter? markCounter = null,
        Marks.MarkSettingsState? markSettings = null,
        Lumora.Services.Tags.ITagService? markTags = null,
        Lumora.Services.Categories.ICategoryService? markCategories = null)
    {
        _exclusionActions = exclusionActions; // „Ordner ausschließen…“, ausgeschlossene Ordner ausblenden (ADR-0066, FolderTreeViewModel.Exclusions.cs)
        _moveActions = moveActions; // „Verschieben nach…“ und Ziehen (ADR-0066, FolderTreeViewModel.Move.cs)
        _removalActions = removalActions; // Ordner in den Papierkorb, Fehlende entfernen (ADR-0065, FolderTreeViewModel.Removal.cs)
        _scope = scope ?? new LibraryScope(factory); // Bereich: Bibliothek bzw. „Alle verknüpften“ (ADR-0048)
        _inboxActions = inboxActions; // „Eingang hinzufügen…“ im Seitenkopf (ADR-0042, FolderTreeViewModel.AddInbox.cs)
        _captureDateActions = captureDateActions; // „Datum setzen…“ (ADR-0046, FolderTreeViewModel.CaptureDate.cs)
        _folderActions = folderActions;
        _factory = factory;
        _libraries = libraries;
        _fileOps = fileOps;
        _dialogs = dialogs;
        _logger = logger;

        _messenger.Register<FolderTreeViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.ReloadAsync(); }));
        _messenger.Register<FolderTreeViewModel, ScanCompletedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.ReloadAsync(); }));
        _messenger.Register<FolderTreeViewModel, LibraryRootsChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.ReloadAsync(); }));
        _messenger.Register<FolderTreeViewModel, FoldersChangedMessage>(this, static (r, m) => UiThread.Post(() => r.ScheduleReload(m.LibraryId)));
        _messenger.Register<FolderTreeViewModel, FilesChangedMessage>(this, static (r, m) => UiThread.Post(() => r.OnFilesChanged(m)));
        _messenger.Register<FolderTreeViewModel, ScanExclusionsChangedMessage>(this, static (r, m) => UiThread.Post(() => r.ScheduleReload(m.LibraryId ?? r._libraryId))); // ADR-0066
        InitMarks(markCounter, markSettings, markTags, markCategories); // Tag-/Kategorie-Zahlen je Ordner (Mockup 16, FolderTreeViewModel.Marks.cs)
    }

    [ObservableProperty]
    private string? _errorMessage;

    // ---- Kontextmenü: Neuer Ordner / Umbenennen (ADR-0020, protokolliert über IFileOperationService) ----

    [RelayCommand]
    private async Task CreateSubfolderAsync(FolderNode? parent)
    {
        if (parent is null || !parent.IsFolder || !HasLibrary)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("Neuer Ordner", $"Neuer Ordner in „{parent.Path}“:", "Neuer Ordner", "Anlegen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            // Bibliothek des Elternordners (in „Alle verknüpften“ nicht zwingend die aktive, ADR-0048)
            var libraryId = _libraryOfFolder.GetValueOrDefault(parent.Id, _libraryId);
            var result = await _fileOps.CreateFolderAsync(libraryId, parent.Path, name.Trim()).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Ordner konnte nicht angelegt werden.";
                return;
            }

            parent.IsExpanded = true;
            _logger.LogInformation("Ordnerbaum: Ordner angelegt {Path}", result.Path);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner konnte nicht angelegt werden: {ex.Message}";
            _logger.LogWarning(ex, "Ordnerbaum: Neuer Ordner in {Parent} fehlgeschlagen", parent.Path);
        }
    }

    /// <summary>Ordner umbenennen — mit Dateien über eine protokollierte RenameFolder-Operation (rückgängig machbar); Wurzeln nicht.</summary>
    [RelayCommand]
    private async Task RenameFolderAsync(FolderNode? node)
    {
        if (node is null || !node.CanRename || !HasLibrary)
        {
            return;
        }

        var newName = await _dialogs.PromptTextAsync("Ordner umbenennen", $"Neuer Name für „{node.Name}“:", node.Name, "Umbenennen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == node.Name)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            var result = await _fileOps.RenameFolderAsync(node.Id, newName.Trim()).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Ordner konnte nicht umbenannt werden.";
                return;
            }

            if (_selectedNode is not null && ReferenceEquals(_selectedNode, node))
            {
                SelectionText = result.Path; // Pfad des gewählten Knotens hat sich geändert; Reload stellt die Auswahl darüber wieder her
            }

            _logger.LogInformation("Ordnerbaum: Ordner umbenannt → {Path}", result.Path);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner konnte nicht umbenannt werden: {ex.Message}";
            _logger.LogWarning(ex, "Ordnerbaum: Umbenennen von {Path} fehlgeschlagen", node.Path);
        }
    }

    public ObservableCollection<FolderNode> Roots { get; } = new();

    /// <summary>Beim Anzeigen der Seite: nur laden, wenn noch nichts geladen ist (Nachrichten halten den Baum aktuell).</summary>
    public Task EnsureLoadedAsync() => _loadedLibraryId is null ? ReloadAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    [RelayCommand]
    private void SelectWholeLibrary()
    {
        ResetSelection();
        if (HasLibrary)
        {
            _messenger.Send(new FolderSelectedMessage(_libraryId, null, IncludeSubfolders));
            _logger.LogInformation("Ordnerauswahl: gesamte Bibliothek (Id={LibraryId})", _libraryId);
        }
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        if (HasLibrary && _selectedNode is { IsLibraryHeader: false })
        {
            _messenger.Send(new FolderSelectedMessage(_libraryId, _selectedNode.Id, value));
        }
    }

    internal void OnNodeSelected(FolderNode node)
    {
        if (node.IsPlaceholder || !HasLibrary)
        {
            return;
        }

        if (node.IsLibraryHeader)
        {
            // Kopf einer Bibliothek in „Alle verknüpften“ (ADR-0048): wirkt wie „Gesamte Bibliothek“ (alle des Bereichs).
            _selectedNode = node;
            SelectionText = WholeLibraryText;
            _messenger.Send(new FolderSelectedMessage(_libraryId, null, IncludeSubfolders));
            return;
        }

        _selectedNode = node;
        SelectionText = node.Path;
        _messenger.Send(new FolderSelectedMessage(_libraryId, node.Id, IncludeSubfolders));
        _logger.LogInformation("Ordnerauswahl: {Path} (FolderId={FolderId}, Unterordner={Sub})", node.Path, node.Id, IncludeSubfolders);
    }

    internal IReadOnlyList<FolderNode> CreateChildren(FolderNode parent) =>
        _childrenByParent.TryGetValue(parent.Id, out var rows)
            ? rows.Select(CreateNode).ToList()
            : Array.Empty<FolderNode>();

    /// <summary>Datei-Operationen (Papierkorb, Verschieben, Undo) ändern Zähler und legen Ordner an → gebündelt neu laden.</summary>
    private void OnFilesChanged(FilesChangedMessage message)
    {
        if (message.Kind == FilesChangeKind.Metadata)
        {
            OnMetadataChangedForMarks(message.LibraryId); // Tags/Kategorien zugewiesen → nur die Zahlen neu (Mockup 16)
            return;
        }

        ScheduleReload(message.LibraryId);
    }

    /// <summary>Bibliothek gehört zum geladenen Bereich (Baum geladen; aktive Bibliothek oder „Alle verknüpften“).</summary>
    private bool IsInLoadedScope(int libraryId)
        => _loadedLibraryId is not null && (libraryId == _loadedLibraryId || _loadedScope.Contains(libraryId));

    private void ScheduleReload(int libraryId)
    {
        if (!IsInLoadedScope(libraryId))
        {
            return;
        }

        _reloadDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadDebounce = cts;
        _ = ReloadDebouncedAsync(cts.Token);
    }

    private async Task ReloadDebouncedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(ReloadDebounceMs, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        if (IsLoading)
        {
            _reloadPending = true; // nach dem laufenden Ladevorgang noch einmal (kein Update darf verloren gehen)
            return;
        }

        IsLoading = true;
        try
        {
            var active = await _libraries.GetActiveAsync().ConfigureAwait(true);
            if (active is null)
            {
                HasLibrary = false;
                LibraryName = null;
                HintText = "Keine aktive Bibliothek — zuerst unter „Bibliothek“ anlegen und scannen.";
                Roots.Clear();
                ResetSelection();
                _loadedLibraryId = null;
                return;
            }

            // Bereich (ADR-0048): eine Bibliothek oder alle verknüpften — dann je Bibliothek ein Kopfknoten.
            var scope = await _scope.GetCurrentAsync().ConfigureAwait(true);
            if (scope.LibraryId != active.Id)
            {
                scope = await _scope.GetForLibraryAsync(active.Id).ConfigureAwait(true);
            }

            var libraries = new List<LibraryInfo> { active };
            foreach (var otherId in scope.LibraryIds.Where(id => id != active.Id))
            {
                if (await _libraries.GetAsync(otherId).ConfigureAwait(true) is { } other)
                {
                    libraries.Add(other);
                }
            }

            libraries = libraries.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var libraryIds = libraries.Select(l => l.Id).ToArray();

            _libraryId = active.Id;
            LibraryName = scope.IsAllLinked ? $"Alle verknüpften ({scope.GroupName})" : active.Name;
            HasLibrary = true;
            var offline = libraries.SelectMany(l => l.Roots).Where(r => !r.IsAvailable).Select(r => r.Path).ToList();
            HintText = offline.Count == 0
                ? null
                : offline.Count == 1
                    ? $"Wurzel offline: {offline[0]} ist nicht erreichbar."
                    : $"{offline.Count} Wurzeln nicht erreichbar: {string.Join(", ", offline)}";
            _rootsById = libraries.SelectMany(l => l.Roots).ToDictionary(r => r.Id);

            List<FolderRow> rows;
            Dictionary<int, int> counts;
            Dictionary<int, int> missing; // fehlende je Ordner (ADR-0065)
            await using (var ctx = _factory.Create())
            {
                rows = await ctx.Folders.AsNoTracking()
                    .Where(f => libraryIds.Contains(f.LibraryId))
                    .Select(f => new FolderRow(f.Id, f.ParentId, f.RootId, f.Name, f.Path, f.LibraryId))
                    .ToListAsync()
                    .ConfigureAwait(true);
                var grouped = await ctx.Files.AsNoTracking()
                    .Where(f => libraryIds.Contains(f.LibraryId))
                    .GroupBy(f => f.FolderId)
                    .Select(g => new { FolderId = g.Key, Count = g.Count(), Missing = g.Sum(f => f.Missing ? 1 : 0) }) // fehlende: ADR-0065
                    .ToListAsync()
                    .ConfigureAwait(true);
                counts = grouped.ToDictionary(x => x.FolderId, x => x.Count);
                missing = grouped.Where(x => x.Missing > 0).ToDictionary(x => x.FolderId, x => x.Missing);
            }

            rows = await HideExcludedFoldersAsync(rows, libraryIds).ConfigureAwait(true); // ADR-0066
            _libraryOfFolder = rows.ToDictionary(r => r.Id, r => r.LibraryId);
            BuildIndex(rows, counts);
            BuildMissingIndex(missing); // ADR-0065
            if (scope.IsAllLinked)
            {
                AddLibraryHeaders(libraries);
            }

            _loadedScope = scope;
            AddInboxCommand.NotifyCanExecuteChanged();

            // Zustand über Pfade merken: aufgeklappte Knoten und Auswahl überleben den Neuaufbau
            var expandedPaths = CollectExpandedPaths();
            var selectedPath = _selectedNode?.Path;

            Roots.Clear();
            ResetSelection();
            foreach (var root in _rootRows)
            {
                Roots.Add(CreateNode(root));
            }

            if (expandedPaths.Count > 0)
            {
                RestoreExpansion(Roots, expandedPaths);
            }
            else if (Roots.Count == 1)
            {
                Roots[0].IsExpanded = true;
            }
            else if (scope.IsAllLinked)
            {
                foreach (var header in Roots)
                {
                    header.IsExpanded = true; // Bibliotheken aufgeklappt: ihre Wurzeln sind sofort sichtbar
                }
            }

            if (selectedPath is not null)
            {
                RestoreSelection(selectedPath);
            }

            _loadedLibraryId = active.Id;
            OnTreeLoadedForMarks(libraryIds); // Tag-/Kategorie-Zahlen im Hintergrund (Mockup 16)
            _logger.LogInformation(
                "Ordnerbaum geladen: Library={LibraryId} Ordner={Folders} Wurzeln={Roots} Dateien={Files}",
                active.Id, rows.Count, _rootRows.Count, counts.Values.Sum());
        }
        catch (Exception ex)
        {
            HintText = $"Ordnerbaum konnte nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Ordnerbaum konnte nicht geladen werden");
        }
        finally
        {
            IsLoading = false;
        }

        if (_reloadPending)
        {
            _reloadPending = false;
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    private HashSet<string> CollectExpandedPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(IEnumerable<FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder)
                {
                    continue;
                }

                if (node.IsExpanded)
                {
                    result.Add(node.Path);
                    Visit(node.Children);
                }
            }
        }

        Visit(Roots);
        return result;
    }

    private static void RestoreExpansion(IEnumerable<FolderNode> nodes, HashSet<string> expandedPaths)
    {
        foreach (var node in nodes)
        {
            if (node.IsPlaceholder || !expandedPaths.Contains(node.Path))
            {
                continue;
            }

            node.IsExpanded = true; // lädt die Kinder
            RestoreExpansion(node.Children, expandedPaths);
        }
    }

    /// <summary>Auswahl per Pfad wiederherstellen; der Knoten sendet dabei erneut FolderSelectedMessage (Galerie ignoriert unveränderte Filter).</summary>
    private void RestoreSelection(string path)
    {
        var node = FindLoadedNode(Roots, path);
        if (node is null)
        {
            _logger.LogInformation("Ordnerbaum: zuvor gewählter Ordner existiert nicht mehr: {Path}", path);
            return;
        }

        node.IsSelected = true;
    }

    private static FolderNode? FindLoadedNode(IEnumerable<FolderNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (node.IsPlaceholder)
            {
                continue;
            }

            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (node.IsExpanded && FindLoadedNode(node.Children, path) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void BuildIndex(List<FolderRow> rows, Dictionary<int, int> counts)
    {
        var ids = rows.Select(r => r.Id).ToHashSet();
        var comparer = StringComparer.CurrentCultureIgnoreCase;

        _childrenByParent = new Dictionary<int, List<FolderRow>>();
        _rootRows = new List<FolderRow>();
        foreach (var row in rows)
        {
            // Wurzel = ParentId NULL (oder verwaiste ParentId — defensiv ebenfalls als Wurzel zeigen)
            if (row.ParentId is { } parentId && ids.Contains(parentId))
            {
                if (!_childrenByParent.TryGetValue(parentId, out var list))
                {
                    list = new List<FolderRow>();
                    _childrenByParent[parentId] = list;
                }

                list.Add(row);
            }
            else
            {
                _rootRows.Add(row);
            }
        }

        foreach (var list in _childrenByParent.Values)
        {
            list.Sort((a, b) => comparer.Compare(a.Name, b.Name));
        }

        // Wurzeln: Archiv zuerst, dann Eingänge in ihrer SortOrder, verwaiste Zeilen zuletzt nach Pfad (ADR-0020)
        _rootRows.Sort((a, b) =>
        {
            var ka = RootSortKey(a);
            var kb = RootSortKey(b);
            var byKey = ka.CompareTo(kb);
            return byKey != 0 ? byKey : comparer.Compare(a.Path, b.Path);
        });

        _directCounts = counts;
        _totalCounts = new Dictionary<int, int>(rows.Count);
        foreach (var root in _rootRows)
        {
            ComputeTotal(root.Id);
        }
    }

    /// <summary>Synthetische Id des Kopfknotens einer Bibliothek (nie eine Folders-Id).</summary>
    private static int HeaderId(int libraryId) => int.MinValue + libraryId;

    /// <summary>
    /// Ansicht „Alle verknüpften“ (ADR-0048): oberste Knoten sind die Bibliotheken, darunter ihre Wurzeln wie gewohnt
    /// (Archiv zuerst, dann Eingänge). Der Kopfknoten trägt die Summe der Dateien der Bibliothek.
    /// </summary>
    private void AddLibraryHeaders(IReadOnlyList<LibraryInfo> libraries)
    {
        var headers = new List<FolderRow>(libraries.Count);
        foreach (var library in libraries)
        {
            var id = HeaderId(library.Id);
            var roots = _rootRows.Where(r => r.LibraryId == library.Id).ToList();
            _childrenByParent[id] = roots;
            _totalCounts[id] = roots.Sum(r => _totalCounts.GetValueOrDefault(r.Id));
            headers.Add(new FolderRow(id, null, null, library.Name, "\u0001Bibliothek:" + library.Id, library.Id));
        }

        _rootRows = headers;
    }

    private int ComputeTotal(int folderId)
    {
        var total = _directCounts.GetValueOrDefault(folderId);
        if (_childrenByParent.TryGetValue(folderId, out var children))
        {
            foreach (var child in children)
            {
                total += ComputeTotal(child.Id);
            }
        }

        _totalCounts[folderId] = total;
        return total;
    }

    private int RootSortKey(FolderRow row)
    {
        if (row.RootId is { } rootId && _rootsById.TryGetValue(rootId, out var root) && IsRootRow(row, root))
        {
            return root.IsArchive ? 0 : 1 + root.SortOrder;
        }

        return int.MaxValue;
    }

    /// <summary>Zeile ist die Wurzel selbst (Pfad = Wurzelpfad), nicht ein verwaister Unterordner mit ParentId NULL.</summary>
    private static bool IsRootRow(FolderRow row, LibraryRootInfo root) =>
        string.Equals(
            row.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            root.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private string? RoleLabelOf(FolderRow row) =>
        row.ParentId is null && row.RootId is { } rootId && _rootsById.TryGetValue(rootId, out var root) && IsRootRow(row, root)
            ? root.RoleText
            : null;

    private FolderNode CreateNode(FolderRow row) => WithMarks(CreateNodeCore(row)); // Tag-/Kategorie-Zahlen (Mockup 16)

    private FolderNode CreateNodeCore(FolderRow row) => row.Id < 0 && row.Path.StartsWith('\u0001')
        ? new FolderNode(this, row.Id, row.Name, row.Path, 0, _totalCounts.GetValueOrDefault(row.Id),
            _childrenByParent.TryGetValue(row.Id, out var roots) && roots.Count > 0, "Bibliothek", isInInbox: false, isLibraryHeader: true)
        : new FolderNode(
            this,
            row.Id,
            row.Name,
            row.Path,
            _directCounts.GetValueOrDefault(row.Id),
            _totalCounts.GetValueOrDefault(row.Id),
            _childrenByParent.ContainsKey(row.Id),
            RoleLabelOf(row),
            row.RootId is { } rootId && _rootsById.TryGetValue(rootId, out var root) && root.IsInbox,
            missingCount: _totalMissing.GetValueOrDefault(row.Id)); // ADR-0065

    private void ResetSelection()
    {
        if (_selectedNode is not null)
        {
            _selectedNode.IsSelected = false;
            _selectedNode = null;
        }

        SelectionText = WholeLibraryText;
    }
}
