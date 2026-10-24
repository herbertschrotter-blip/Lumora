using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Folders;
using Lumora.Gallery.Services;
using Lumora.Removal;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Services.Removal;
using Lumora.Wpf.Tests.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.Removal;

/// <summary>
/// Kontextmenü des Ordnerbaums (ADR-0065): Zähler „fehlt“ samt Unterordnern, „Fehlende entfernen (n)“ nur bei n &gt; 0,
/// „In den Papierkorb…“ mit Rückgängig im Ergebnisfeld, Sperre in „Alle verknüpften“ — gegen eine echte, migrierte DB.
/// </summary>
public sealed class FolderTreeRemovalTests : IAsyncLifetime
{
    private ArchiveTestDb _db = null!;
    private readonly FakeRemovalService _service = new();
    private readonly FakeRemovalDialogs _dialogs = new();
    private int _f2020;
    private int _f2021;

    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var root = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
        var y2020 = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "2020"), root.Id);
        var feier = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "2020", "Feier"), y2020.Id);
        var y2021 = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "2021"), root.Id);
        _f2020 = y2020.Id;
        _f2021 = y2021.Id;
        var m1 = await _db.AddFileAsync(y2020, "weg1.jpg");
        var m2 = await _db.AddFileAsync(feier, "weg2.jpg");
        var m3 = await _db.AddFileAsync(feier, "weg3.jpg");
        await _db.AddFileAsync(y2020, "da.jpg");
        await _db.AddFileAsync(y2021, "auch-da.jpg");
        await using var ctx = _db.Factory.Create();
        await ctx.Files.Where(f => f.Id == m1.Id || f.Id == m2.Id || f.Id == m3.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.Missing, true));
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private FolderTreeViewModel Create(ILibraryScope? scope = null, bool withRemoval = true)
    {
        var libraries = new LibraryService(_db.Factory, _db.Paths, NullLogger<LibraryService>.Instance, new WeakReferenceMessenger());
        var folderActions = new FolderActions(null!, null!, null!, null!, null!, null!, null!, NullService.Create<IGalleryDialogService>(), NullLogger<FolderActions>.Instance);
        return new FolderTreeViewModel(
            _db.Factory,
            libraries,
            NullService.Create<IFileOperationService>(),
            new ScriptedDialogs(),
            folderActions,
            NullLogger<FolderTreeViewModel>.Instance,
            scope: scope,
            removalActions: withRemoval ? new RemovalActions(_service, _dialogs, NullLogger<RemovalActions>.Instance) : null);
    }

    private static FolderNode Find(IEnumerable<FolderNode> nodes, string path)
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
                try
                {
                    return Find(node.Children, path);
                }
                catch (InvalidOperationException) when (node.IsLibraryHeader)
                {
                }
            }
        }

        throw new InvalidOperationException("Knoten nicht gefunden: " + path);
    }

    [Fact]
    public async Task Zaehler_fehlt_samt_Unterordnern_und_Menueeintrag_nur_bei_n_groesser_0()
    {
        var vm = Create();
        await vm.EnsureLoadedAsync();

        var y2020 = Find(vm.Roots, Path.Combine(_db.ArchiveDir, "2020"));
        var y2021 = Find(vm.Roots, Path.Combine(_db.ArchiveDir, "2021"));
        y2020.MissingCount.Should().Be(3);
        y2020.HasMissing.Should().BeTrue();
        y2020.RemoveMissingHeader.Should().Be("Fehlende entfernen (3)");
        Find(vm.Roots, Path.Combine(_db.ArchiveDir, "2020", "Feier")).MissingCount.Should().Be(2);
        y2021.HasMissing.Should().BeFalse();
        vm.HasRemovalActions.Should().BeTrue();
        vm.RecycleFolderCommand.CanExecute(y2021).Should().BeTrue();

        await vm.RemoveMissingInFolderCommand.ExecuteAsync(y2021);
        _service.SummaryCalls.Should().BeEmpty("ohne Fehlende passiert nichts");
    }

    [Fact]
    public async Task Fehlende_entfernen_am_Ordner_mit_Rueckgaengig_im_Ergebnisfeld()
    {
        _service.Summary = new MissingSummary([1, 2, 3], 0, 0, [], UserDataCounts.None);
        _service.RemoveResult = new MissingRemovalBatchResult(9, 3, 0, 0, 0, 1, 0, []);
        var vm = Create();
        await vm.EnsureLoadedAsync();
        var y2020 = Find(vm.Roots, Path.Combine(_db.ArchiveDir, "2020"));

        await vm.RemoveMissingInFolderCommand.ExecuteAsync(y2020);

        _service.RemoveCalls.Should().ContainSingle().Which.Should().Be(new MissingTarget.InFolder(_f2020));
        _dialogs.Requests.Single().Message.Should().Contain("in „2020“ (mit Unterordnern)");
        vm.ResultMessage.Should().Contain("3 Dateien aus der Bibliothek entfernt");
        vm.CanUndoRemoval.Should().BeTrue();

        await vm.UndoRemovalCommand.ExecuteAsync(null);

        _service.UndoCalls.Should().Equal(9);
        vm.CanUndoRemoval.Should().BeFalse();
        vm.ResultMessage.Should().Contain("Rückgängig");
    }

    [Fact]
    public async Task In_den_Papierkorb_und_Ausblenden_nimmt_das_Rueckgaengig_mit()
    {
        _service.Preview = FakeRemovalService.PreviewOf(_f2021);
        var vm = Create();
        await vm.EnsureLoadedAsync();

        await vm.RecycleFolderCommand.ExecuteAsync(Find(vm.Roots, Path.Combine(_db.ArchiveDir, "2021")));

        _service.RecycleCalls.Should().Equal((_f2021, false));
        vm.CanUndoRemoval.Should().BeTrue();
        vm.DismissResultCommand.Execute(null);
        vm.CanUndoRemoval.Should().BeFalse();
        vm.ResultText.Should().BeNull();
    }

    [Fact]
    public async Task Alle_verknuepften_sperrt_wie_die_anderen_Datei_Aktionen()
    {
        var vm = Create(new AllLinkedScope(_db.LibraryId));
        await vm.EnsureLoadedAsync();
        var node = Find(vm.Roots, Path.Combine(_db.ArchiveDir, "2020"));

        await vm.RecycleFolderCommand.ExecuteAsync(node);
        await vm.RemoveMissingInFolderCommand.ExecuteAsync(node);

        vm.ResultWarning.Should().Contain("Alle verknüpften");
        _service.RecycleCalls.Should().BeEmpty();
        _service.SummaryCalls.Should().BeEmpty();
        _dialogs.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Ohne_Dienst_keine_Menueeintraege()
    {
        var vm = Create(withRemoval: false);
        await vm.EnsureLoadedAsync();

        vm.HasRemovalActions.Should().BeFalse();
        vm.RecycleFolderCommand.CanExecute(Find(vm.Roots, Path.Combine(_db.ArchiveDir, "2021"))).Should().BeFalse();
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
