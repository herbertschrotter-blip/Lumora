using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Exclusions;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Shell;
using Lumora.Wpf.Tests.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Exclusions;

/// <summary>
/// Einstellungen › Bibliotheken &amp; Eingänge › „Ausschlüsse beim Einlesen“ (ADR-0066): allgemeine Liste mit Standard .thumbs/.temp,
/// Namen hinzufügen (Rückfrage mit Zahlen, Entfernen + Rückgängig nimmt den Namen wieder heraus) und entfernen (liest neu ein),
/// Ordner der Bibliothek mit „Ausschluss aufheben“ (liest neu ein), „bereits katalogisiert“ entfernen.
/// </summary>
public sealed class ScanExclusionsSettingsTests : IAsyncLifetime
{
    private ArchiveTestDb _db = null!;
    private readonly FakeFolderEditDialogs _dialogs = new();
    private readonly CountingScanService _scans = new();
    private ScanExclusionStore _store = null!;
    private FileOperationService _ops = null!;

    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var root = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
        var a = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "A"), root.Id);
        var cache = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, "A", ".cache"), a.Id);
        var thumbs = await _db.AddFolderAsync(_db.ArchiveRootId, Path.Combine(_db.ArchiveDir, ".thumbs"), root.Id);
        await _db.AddFileAsync(a, "a.jpg");
        await _db.AddFileAsync(cache, "c1.jpg");
        await _db.AddFileAsync(cache, "c2.jpg");
        await _db.AddFileAsync(thumbs, "t.jpg"); // vor 0.4.33 eingelesen
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

    private ScanExclusionsSettingsViewModel Create()
    {
        var service = new ScanExclusionService(_db.Factory, _store, _ops, _scans, NullLogger<ScanExclusionService>.Instance);
        var actions = new ScanExclusionActions(service, _dialogs, NullLogger<ScanExclusionActions>.Instance);
        var libraries = new LibraryService(_db.Factory, _db.Paths, NullLogger<LibraryService>.Instance, new WeakReferenceMessenger());
        return new ScanExclusionsSettingsViewModel(actions, new LibraryScope(_db.Factory), libraries, NullLogger<ScanExclusionsSettingsViewModel>.Instance, new WeakReferenceMessenger());
    }

    private async Task<int> CountFilesAsync(string name)
    {
        await using var ctx = _db.Factory.Create();
        return await ctx.Files.CountAsync(f => f.Name == name);
    }

    [Fact]
    public async Task Standardliste_und_bereits_katalogisierte_thumbs_lassen_sich_entfernen()
    {
        var vm = Create();
        await vm.RefreshAsync();

        vm.FolderNames.Should().Equal(".temp", ".thumbs");
        vm.HasFolders.Should().BeFalse();
        vm.CatalogedExcluded.Should().Be(1);
        vm.CatalogedText.Should().Be("1 Datei in ausgeschlossenen Ordnern ist noch in der Bibliothek.");
        vm.LibraryName.Should().Be("Test");

        await vm.RemoveExistingCommand.ExecuteAsync(null);

        _dialogs.Requests.Single().Message.Should().Contain("1 Datei in ausgeschlossenen Ordnern ist noch in der Bibliothek");
        (await CountFilesAsync("t.jpg")).Should().Be(0);
        vm.CatalogedExcluded.Should().Be(0);
        vm.CanUndo.Should().BeTrue();

        await vm.UndoCommand.ExecuteAsync(null);

        (await CountFilesAsync("t.jpg")).Should().Be(1);
        vm.FolderNames.Should().Contain(".thumbs", "die Regel bestand schon — Rückgängig nimmt nur die Einträge zurück");
    }

    [Fact]
    public async Task Namen_hinzufuegen_mit_Entfernen_und_Rueckgaengig_nimmt_ihn_wieder_heraus()
    {
        var vm = Create();
        await vm.RefreshAsync();
        vm.NewName = ".cache";

        await vm.AddNameCommand.ExecuteAsync(null);

        _dialogs.Requests.Single().Message.Should().Contain("2 Dateien in solchen Ordnern sind schon in der Bibliothek");
        vm.FolderNames.Should().Contain(".cache");
        vm.NewName.Should().BeEmpty();
        (await CountFilesAsync("c1.jpg")).Should().Be(0);
        vm.ResultText.Should().Contain("2 Dateien aus der Bibliothek entfernt");

        await vm.UndoCommand.ExecuteAsync(null);

        (await CountFilesAsync("c1.jpg")).Should().Be(1);
        vm.FolderNames.Should().NotContain(".cache");
    }

    [Fact]
    public async Task Ungueltiger_oder_doppelter_Name_wird_gemeldet()
    {
        var vm = Create();
        await vm.RefreshAsync();

        vm.NewName = "a\\b";
        await vm.AddNameCommand.ExecuteAsync(null);
        vm.ResultIsWarning.Should().BeTrue();
        vm.ResultText.Should().Contain("kein gültiger Ordnername");

        vm.NewName = ".THUMBS";
        await vm.AddNameCommand.ExecuteAsync(null);
        vm.ResultText.Should().Contain("steht schon in der Liste");
        _dialogs.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Name_entfernen_und_Ordner_Ausschluss_aufheben_lesen_die_Bibliothek_neu_ein()
    {
        var vm = Create();
        await _store.AddFolderAsync(_db.LibraryId, Path.Combine(_db.ArchiveDir, "A"));
        await vm.RefreshAsync();
        vm.Folders.Should().ContainSingle().Which.Display.Should().Be("archiv › A");

        await vm.LiftFolderCommand.ExecuteAsync(vm.Folders[0]);
        await vm.Actions.LastRescan!;

        vm.Folders.Should().BeEmpty();
        vm.ResultText.Should().Contain("Ausschluss aufgehoben");
        _scans.Calls.Should().Be(1);

        await vm.RemoveNameCommand.ExecuteAsync(".temp");
        await vm.Actions.LastRescan!;

        vm.FolderNames.Should().Equal(".thumbs");
        _scans.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Rueckgaengig_ohne_zurueckgeholte_Eintraege_laesst_den_Ausschluss_stehen()
    {
        var vm = Create();
        await _store.SetFolderNamesAsync([".thumbs", ".cache"]);

        var result = await vm.Actions.UndoAsync(9999, new ExclusionUndo.Name(".cache"));

        result.IsWarning.Should().BeTrue();
        (await _store.GetFolderNamesAsync()).Should().Contain(".cache", "nichts kam zurück — die Regel bleibt");
    }

    [Fact]
    public async Task Schon_ueber_den_Elternordner_ausgeschlossen_wird_gemeldet()
    {
        var vm = Create();
        await _store.AddFolderAsync(_db.LibraryId, Path.Combine(_db.ArchiveDir, "A"));

        var result = await vm.Actions.ExcludeFolderAsync(_db.LibraryId, Path.Combine(_db.ArchiveDir, "A", "Neu"), "Neu");

        result!.IsWarning.Should().BeTrue();
        result.Message.Should().Contain("schon ausgeschlossen");
    }

    /// <summary>Scan-Dienst, der nur zählt.</summary>
    private sealed class CountingScanService : IScanService
    {
        public int Calls;

        public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
            => ScanAsync(libraryId, mode, ScanOptions.Default, progress, cancellationToken);

        public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ScanResult(libraryId, string.Empty, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) { Mode = mode });
        }
    }
}
