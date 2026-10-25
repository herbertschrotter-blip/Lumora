using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Covers;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Shell;

namespace Lumora.Folders;

/// <summary>Treffer der Ordnersuche: Name und Pfad unterhalb der Wurzel.</summary>
public sealed record FolderSearchHit(int Id, string Name, string Path);

/// <summary>
/// Ordnerleiste der Galerie (ADR-0068, Mockup 18): Ordnersuche, Ordner aus der Galerie heraus zeigen (Breadcrumb, Unterordner-
/// Kachel, Kreuz am Chip), „Nur diesen Ordner zeigen“, „Neu einlesen“ (nur dieser Ordner), „Im Explorer anzeigen“ und das
/// Titelbild des Ordners. Der Baum selbst bleibt wie auf der früheren Seite „Ordner“.
/// </summary>
public sealed partial class FolderTreeViewModel
{
    /// <summary>Höchstens so viele Treffer zeigt die Ordnersuche.</summary>
    public const int MaxSearchHits = 60;

    private IScanService? _scanService;
    private ICoverService? _covers;
    private bool _revealing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    private string? _folderSearchText;

    /// <summary>Treffer der Ordnersuche (statt des Baums angezeigt, solange gesucht wird).</summary>
    public ObservableCollection<FolderSearchHit> FolderSearchResults { get; } = new();

    public bool IsSearching => !string.IsNullOrWhiteSpace(FolderSearchText);

    private void InitPane(IScanService? scanService, ICoverService? covers)
    {
        _scanService = scanService;
        _covers = covers;
        // Ordner kommt aus der Galerie (Breadcrumb, Unterordner-Kachel, Kreuz am Chip): Baum nachziehen, ohne erneut zu senden
        _messenger.Register<FolderTreeViewModel, FolderSelectedMessage>(this, static (r, m) => UiThread.Post(() => r.OnFolderSelectedElsewhere(m)));
    }

    public bool HasRescan => _scanService is not null;

    public bool HasCoverActions => _covers is not null;

    partial void OnFolderSearchTextChanged(string? value)
    {
        FolderSearchResults.Clear();
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var row in AllRows()
                     .Where(r => r.Id > 0 && r.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                     .OrderBy(r => !r.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase))
                     .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(MaxSearchHits))
        {
            FolderSearchResults.Add(new FolderSearchHit(row.Id, row.Name, row.Path));
        }
    }

    /// <summary>Treffer anklicken: Ordner im Baum zeigen und wählen, Suche leeren.</summary>
    [RelayCommand]
    private void OpenSearchHit(FolderSearchHit? hit)
    {
        if (hit is null)
        {
            return;
        }

        FolderSearchText = null;
        if (RevealFolder(hit.Id) is { } node)
        {
            node.IsSelected = true;
        }
    }

    /// <summary>
    /// Ordner im Baum aufklappen bis zum Knoten und ihn als gewählt zeigen — ohne neue FolderSelectedMessage (die Galerie hat den
    /// Ordner schon). Null, wenn der Ordner nicht im geladenen Baum steht.
    /// </summary>
    public FolderNode? RevealFolder(int folderId)
    {
        var chain = new List<int>();
        var parentOf = BuildParentMap();
        var current = (int?)folderId;
        var guard = 0;
        while (current is { } id && guard++ < 256)
        {
            chain.Insert(0, id);
            current = parentOf.TryGetValue(id, out var parent) ? parent : null;
        }

        IEnumerable<FolderNode> level = Roots;
        FolderNode? node = null;
        foreach (var id in chain)
        {
            var next = level.FirstOrDefault(n => n.Id == id)
                       ?? level.Where(n => n.IsLibraryHeader).Select(h => { h.IsExpanded = true; return h.Children.FirstOrDefault(c => c.Id == id); }).FirstOrDefault(n => n is not null);
            if (next is null)
            {
                return null;
            }

            node = next;
            if (id != folderId)
            {
                node.IsExpanded = true; // lädt die Kinder
            }

            level = node.Children;
        }

        return node;
    }

    /// <summary>Wie <see cref="RevealFolder"/>, zusätzlich als gewählt markieren, ohne die Galerie erneut zu benachrichtigen.</summary>
    private void SelectSilently(FolderNode node)
    {
        if (_selectedNode is not null && !ReferenceEquals(_selectedNode, node))
        {
            _selectedNode.IsSelected = false;
        }

        _revealing = true;
        try
        {
            node.IsSelected = true;
        }
        finally
        {
            _revealing = false;
        }

        _selectedNode = node;
        SelectionText = node.Path;
    }

    private void OnFolderSelectedElsewhere(FolderSelectedMessage message)
    {
        if (!HasLibrary || !IsInLoadedScope(message.LibraryId))
        {
            return;
        }

        if (message.FolderId is not { } id)
        {
            // Kopf einer Bibliothek in „Alle verknüpften“ sendet selbst „kein Ordner“ — die eigene Auswahl bleibt
            if (_selectedNode is { IsLibraryHeader: false })
            {
                ResetSelection(); // Kreuz am Chip / „Alle Fotos der Bibliothek“
            }

            return;
        }

        if (_selectedNode?.Id == id)
        {
            return;
        }

        if (RevealFolder(id) is { } node)
        {
            SelectSilently(node);
        }
    }

    /// <summary>Ergebnis im Hinweisfeld über dem Baum (ersetzt ein Rückgängig-Angebot).</summary>
    private void ShowResult(string text, bool warning)
    {
        RemovalUndoBatchId = null;
        ResultMessage = warning ? null : text;
        ResultWarning = warning ? text : null;
    }

    private Dictionary<int, int> BuildParentMap()
    {
        var map = new Dictionary<int, int>();
        foreach (var (parent, children) in _childrenByParent)
        {
            foreach (var child in children)
            {
                map[child.Id] = parent;
            }
        }

        return map;
    }

    private IEnumerable<FolderRow> AllRows() => _rootRows.Concat(_childrenByParent.Values.SelectMany(c => c)).DistinctBy(r => r.Id);

    // ---- Kontextmenü (Mockup 18) -------------------------------------------------------------------------------------

    /// <summary>„Nur diesen Ordner zeigen“: ohne Unterordner wählen.</summary>
    [RelayCommand]
    private void ShowOnlyThisFolder(FolderNode? node)
    {
        if (node is not { IsFolder: true })
        {
            return;
        }

        IncludeSubfolders = false;
        if (ReferenceEquals(_selectedNode, node))
        {
            _messenger.Send(new FolderSelectedMessage(_libraryId, node.Id, false));
        }
        else
        {
            node.IsSelected = true;
        }
    }

    /// <summary>„Neu einlesen“ nur dieses Ordners (Incremental-Scan mit Unterordnern, ADR-0068).</summary>
    [RelayCommand]
    private async Task RescanFolderAsync(FolderNode? node)
    {
        if (node is not { IsFolder: true } || _scanService is null || !HasLibrary)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            var libraryId = _libraryOfFolder.GetValueOrDefault(node.Id, _libraryId);
            var result = await _scanService.ScanAsync(libraryId, ScanMode.Incremental, new ScanOptions { SubtreePath = node.Path }, null, CancellationToken.None).ConfigureAwait(true);
            ShowResult(result.Outcome switch
            {
                ScanOutcome.LibraryOffline => $"„{node.Name}“ nicht eingelesen — Laufwerk nicht erreichbar.",
                ScanOutcome.MassMissingBlocked => $"„{node.Name}“: sehr viele Dateien fehlen — nichts als fehlend markiert. Bitte unter Einstellungen › Bibliotheken neu einlesen und bestätigen.",
                _ => $"„{node.Name}“ neu eingelesen: {result.Added:N0} neu, {result.Modified:N0} geändert, {result.MarkedMissing:N0} fehlen, {result.Reappeared:N0} wieder da.",
            }, warning: result.Outcome != ScanOutcome.Completed);
            _logger.LogInformation("Ordner neu eingelesen: {Outcome}, {Added} neu, {Modified} geändert, {Missing} fehlend", result.Outcome, result.Added, result.Modified, result.MarkedMissing);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Neu einlesen fehlgeschlagen: {ex.Message}";
            _logger.LogWarning(ex, "Ordner neu einlesen fehlgeschlagen");
        }
    }

    [RelayCommand]
    private void ShowFolderInExplorer(FolderNode? node)
    {
        if (node is not { IsFolder: true })
        {
            return;
        }

        try
        {
            Lumora.Shell.HiddenLibraries.HiddenSessionPrivacy.ThrowIfShellBlocked(); // versteckte Sitzung: keine Shell-Spuren (ADR-0049)
            if (System.IO.Directory.Exists(node.Path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{node.Path}\"") { UseShellExecute = true });
            }
            else
            {
                ErrorMessage = "Ordner ist nicht (mehr) vorhanden oder das Laufwerk ist nicht erreichbar.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Explorer konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    /// <summary>Titelbild des Ordners: wirksame Datei und ob selbst gewählt (für das Untermenü).</summary>
    public async Task<FolderCoverInfo?> GetFolderCoverAsync(FolderNode node)
    {
        if (_covers is null || node is not { IsFolder: true })
        {
            return null;
        }

        try
        {
            return await _covers.GetFolderCoverAsync(node.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Titelbild des Ordners {FolderId} nicht lesbar", node.Id);
            return null;
        }
    }

    /// <summary>„Wieder automatisch wählen lassen“ für das Titelbild des Ordners.</summary>
    [RelayCommand]
    private async Task ResetFolderCoverAsync(FolderNode? node)
    {
        if (_covers is null || node is not { IsFolder: true })
        {
            return;
        }

        try
        {
            await _covers.ResetAsync(CoverTargetKind.Folder, node.Id).ConfigureAwait(true);
            ShowResult($"Titelbild von „{node.Name}“ wird wieder automatisch gewählt.", warning: false);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Titelbild nicht zurückgesetzt: {ex.Message}";
        }
    }
}
