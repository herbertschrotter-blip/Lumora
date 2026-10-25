using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Exclusions;
using Lumora.Folders;
using Lumora.Gallery.Services;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Shell;
using Lumora.Wpf.Tests.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.Exclusions;

/// <summary>Rückfragen und Zielauswahl mit festen Antworten; merkt sich Titel und Text.</summary>
internal sealed class FakeFolderEditDialogs : IFolderEditDialogs
{
    public bool ConfirmAnswer { get; set; } = true;

    public DialogChoice ChooseAnswer { get; set; } = DialogChoice.Primary;

    public string? PickResult { get; set; }

    public string? LastPickInitial { get; private set; }

    public List<(string Title, string Message)> Requests { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Requests.Add((title, message));
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText)
    {
        Requests.Add((title, message));
        return Task.FromResult(ChooseAnswer);
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        LastPickInitial = initialDirectory;
        return PickResult;
    }
}

/// <summary>
/// Reiter „Ordner“ (ADR-0066): ausgeschlossene Ordner ausgeblendet samt Hinweis, Rechtsklick › „Ordner ausschließen…“ mit
/// Rückfrage und Rückgängig, „Verschieben nach…“ und Ziehen (Vorprüfung, Bestätigung, Rückgängig) — gegen eine echte, migrierte
/// DB mit echten Ordnern und den echten Diensten.
/// </summary>
public sealed class FolderTreeExclusionMoveTests : IAsyncLifetime
{
    private ArchiveTestDb _db = null!;
    private readonly FakeFolderEditDialogs _dialogs = new();
    private ScanExclusionStore _store = null!;
    private FileOperationService _ops = null!;
    private int _reise;
    private int _ziel;
    private int _datei;

    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var root = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
        var reise = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "Reise"), root.Id);
        var thumbs = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "Reise", ".thumbs"), reise.Id);
        var ziel = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "Ziel"), root.Id);
        _reise = reise.Id;
        _ziel = ziel.Id;
        _datei = (await _db.AddFileAsync(reise, "foto.jpg")).Id;
        await _db.AddFileAsync(thumbs, "foto_t.jpg");
        await _db.AddFileAsync(ziel, "z.jpg");
        _store = new ScanExclusionStore(_db.Factory, new WeakReferenceMessenger());
        _ops = new FileOperationService(_db.Factory, _db.Paths, NullService.Create<IRecycleBin>(), new Lumora.Services.Media.MediaFormatRegistry(),
            NullLogger<FileOperationService>.Instance, new WeakReferenceMessenger(), exclusions: _store);
    }

    public Task DisposeAsync()
    {
        _ops.Dispose();
        _db.Dispose();
        return Task.CompletedTask;
    }

    private FolderTreeViewModel Create(ILibraryScope? scope = null)
    {
        var libraries = new LibraryService(_db.Factory, _db.Paths, NullLogger<LibraryService>.Instance, new WeakReferenceMessenger());
        var folderActions = new FolderActions(null!, null!, null!, null!, null!, null!, null!, NullService.Create<IGalleryDialogService>(), NullLogger<FolderActions>.Instance);
        var service = new ScanExclusionService(_db.Factory, _store, _ops, NullService.Create<IScanService>(), NullLogger<ScanExclusionService>.Instance);
        return new FolderTreeViewModel(
            _db.Factory,
            libraries,
            _ops,
            new ScriptedDialogs(),
            folderActions,
            NullLogger<FolderTreeViewModel>.Instance,
            scope: scope,
            exclusionActions: new ScanExclusionActions(service, _dialogs, NullLogger<ScanExclusionActions>.Instance),
            moveActions: new FolderMoveActions(_ops, _dialogs, NullLogger<FolderMoveActions>.Instance));
    }

    private static FolderNode? Find(IEnumerable<FolderNode> nodes, string path)
    {
        foreach (var node in nodes.Where(n => !n.IsPlaceholder))
        {
            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (path.StartsWith(node.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || node.IsLibraryHeader)
            {
                node.IsExpanded = true;
                if (Find(node.Children, path) is { } hit)
                {
                    return hit;
                }
            }
        }

        return null;
    }

    private async Task<FolderTreeViewModel> LoadedAsync(ILibraryScope? scope = null)
    {
        var vm = Create(scope);
        await vm.EnsureLoadedAsync();
        return vm;
    }

    private async Task ReloadAsync(FolderTreeViewModel vm)
    {
        await vm.RefreshCommand.ExecuteAsync(null);
    }

    // ------------------------------------------------------------------ Ausschlüsse

    [Fact]
    public async Task Ausgeschlossene_Ordner_erscheinen_nicht_im_Baum_mit_dezentem_Hinweis()
    {
        var vm = await LoadedAsync();

        Find(vm.Roots, Path.Combine(_db.ArchiveDir, "Reise", ".thumbs")).Should().BeNull("„.thumbs“ steht in der allgemeinen Liste");
        Find(vm.Roots, Path.Combine(_db.ArchiveDir, "Reise")).Should().NotBeNull();
        vm.ExcludedFoldersText.Should().Be("1 Ordner ausgeschlossen");
        vm.HasExclusionActions.Should().BeTrue();
        vm.HasMoveActions.Should().BeTrue();
    }

    [Fact]
    public async Task Ordner_ausschliessen_mit_Entfernen_und_Rueckgaengig_hebt_den_Ausschluss_wieder_auf()
    {
        var vm = await LoadedAsync();
        var ziel = Find(vm.Roots, Path.Combine(_db.ArchiveDir, "Ziel"))!;

        await vm.ExcludeFolderCommand.ExecuteAsync(ziel);

        var request = _dialogs.Requests.Single();
        request.Title.Should().Be("Ordner ausschließen");
        request.Message.Should().Contain("1 Datei darin ist schon in der Bibliothek");
        (await _store.GetFoldersAsync(_db.LibraryId)).Should().ContainSingle(f => f.FullPath == Path.Combine(_db.ArchiveDir, "Ziel"));
        await using (var ctx = _db.Factory.Create())
        {
            (await ctx.Files.CountAsync(f => f.Name == "z.jpg")).Should().Be(0, "aus der Bibliothek entfernt");
            (await ctx.OperationBatches.OrderBy(b => b.Id).LastAsync()).OperationType.Should().Be(OperationType.ExcludeFromCatalog);
        }

        vm.ResultMessage.Should().Contain("aus der Bibliothek entfernt");
        vm.CanUndoRemoval.Should().BeTrue();
        await ReloadAsync(vm);
        Find(vm.Roots, Path.Combine(_db.ArchiveDir, "Ziel")).Should().BeNull("jetzt ausgeschlossen");

        await vm.UndoRemovalCommand.ExecuteAsync(null);

        vm.ResultMessage.Should().Contain("wieder in der Bibliothek").And.Contain("Ausschluss ist aufgehoben");
        (await _store.GetFoldersAsync(_db.LibraryId)).Should().BeEmpty();
        await using (var ctx = _db.Factory.Create())
        {
            (await ctx.Files.CountAsync(f => f.Name == "z.jpg")).Should().Be(1);
        }
    }

    [Fact]
    public async Task Nur_kuenftig_ausschliessen_laesst_die_Eintraege_und_bietet_kein_Rueckgaengig()
    {
        _dialogs.ChooseAnswer = DialogChoice.Secondary;
        var vm = await LoadedAsync();

        await vm.ExcludeFolderCommand.ExecuteAsync(Find(vm.Roots, Path.Combine(_db.ArchiveDir, "Ziel")));

        (await _store.GetFoldersAsync(_db.LibraryId)).Should().ContainSingle();
        await using var ctx = _db.Factory.Create();
        (await ctx.Files.CountAsync(f => f.Name == "z.jpg")).Should().Be(1);
        vm.CanUndoRemoval.Should().BeFalse();
        vm.ResultMessage.Should().Contain("bleibt in der Bibliothek");
    }

    [Fact]
    public async Task Wurzel_laesst_sich_nicht_ausschliessen_oder_verschieben_und_Alle_verknuepften_sperrt()
    {
        var vm = await LoadedAsync();
        var root = Find(vm.Roots, _db.ArchiveDir)!;
        root.CanExclude.Should().BeFalse();
        root.CanMove.Should().BeFalse();
        vm.ExcludeFolderCommand.CanExecute(root).Should().BeFalse();
        vm.MoveFolderCommand.CanExecute(root).Should().BeFalse();

        var linked = await LoadedAsync(new AllLinkedScope(_db.LibraryId));
        var reise = Find(linked.Roots, Path.Combine(_db.ArchiveDir, "Reise"))!;
        await linked.ExcludeFolderCommand.ExecuteAsync(reise);
        linked.ResultWarning.Should().Contain("Alle verknüpften");
        _dialogs.Requests.Should().BeEmpty();
        linked.CanDropFolder(reise, Find(linked.Roots, Path.Combine(_db.ArchiveDir, "Ziel"))).Should().BeFalse();
    }

    // ------------------------------------------------------------------ Verschieben

    [Fact]
    public async Task Verschieben_nach_ueber_die_Zielauswahl_mit_Rueckgaengig_FileIds_bleiben()
    {
        var vm = await LoadedAsync();
        _dialogs.PickResult = Path.Combine(_db.ArchiveDir, "Ziel");

        await vm.MoveFolderCommand.ExecuteAsync(Find(vm.Roots, Path.Combine(_db.ArchiveDir, "Reise")));

        _dialogs.LastPickInitial.Should().Be(_db.ArchiveDir, "Zielauswahl beginnt in der Wurzel");
        _dialogs.Requests.Should().BeEmpty("die Auswahl ist die Bestätigung");
        vm.ResultMessage.Should().Be("„Reise“ nach „Ziel“ verschoben.");
        Directory.Exists(Path.Combine(_db.ArchiveDir, "Ziel", "Reise", ".thumbs")).Should().BeTrue();
        await using (var ctx = _db.Factory.Create())
        {
            (await ctx.Files.SingleAsync(f => f.Id == _datei)).FolderId.Should().Be(_reise);
            (await ctx.Folders.SingleAsync(f => f.Id == _reise)).ParentId.Should().Be(_ziel);
        }

        await vm.UndoRemovalCommand.ExecuteAsync(null);

        vm.ResultMessage.Should().Contain("wieder am alten Platz");
        Directory.Exists(Path.Combine(_db.ArchiveDir, "Reise")).Should().BeTrue();
        await using (var ctx = _db.Factory.Create())
        {
            (await ctx.Folders.SingleAsync(f => f.Id == _reise)).Path.Should().Be(Path.Combine(_db.ArchiveDir, "Reise"));
        }
    }

    [Fact]
    public async Task Ziehen_prueft_vor_fragt_nach_und_meldet_Ablehnungen()
    {
        var vm = await LoadedAsync();
        var reise = Find(vm.Roots, Path.Combine(_db.ArchiveDir, "Reise"))!;
        var ziel = Find(vm.Roots, Path.Combine(_db.ArchiveDir, "Ziel"))!;
        var root = Find(vm.Roots, _db.ArchiveDir)!;

        vm.CanDropFolder(reise, reise).Should().BeFalse("nicht auf sich selbst");
        vm.CanDropFolder(reise, root).Should().BeFalse("liegt schon dort");
        vm.CanDropFolder(root, ziel).Should().BeFalse("Wurzel nicht");
        vm.CanDropFolder(reise, ziel).Should().BeTrue();

        _dialogs.ConfirmAnswer = false;
        await vm.DropFolderAsync(reise, ziel);
        _dialogs.Requests.Single().Message.Should().Contain("„Reise“ samt Inhalt nach „Ziel“ verschieben?");
        Directory.Exists(Path.Combine(_db.ArchiveDir, "Reise")).Should().BeTrue("abgebrochen");

        _dialogs.ConfirmAnswer = true;
        Directory.CreateDirectory(Path.Combine(_db.ArchiveDir, "Ziel", "Reise"));
        await vm.DropFolderAsync(reise, ziel);
        vm.ResultWarning.Should().Contain("Zusammenführen");
        vm.CanUndoRemoval.Should().BeFalse();
    }

    /// <summary>Bereich „Alle verknüpften“ mit nur der einen Bibliothek (für die Sperre genügt das Kennzeichen).</summary>
    private sealed class AllLinkedScope(int libraryId) : ILibraryScope
    {
        private readonly LibraryScopeInfo _info = new(libraryId, 1, "Familie", [libraryId], [libraryId, 999], true);

        public Task<LibraryScopeInfo> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_info);

        public Task<LibraryScopeInfo> GetForLibraryAsync(int id, CancellationToken ct = default) => Task.FromResult(_info);

        public Task<IReadOnlyList<LibraryGroupInfo>> GetGroupsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LibraryGroupInfo>>([]);

        public Task ShowAllLinkedAsync(int groupId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RenameGroupAsync(int groupId, string name, CancellationToken ct = default) => Task.CompletedTask;
    }
}
