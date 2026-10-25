using Microsoft.Extensions.Logging.Abstractions;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Libraries;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Services.Removal;
using Lumora.Services.Scanning;
using Lumora.Services.Scanning.Exclusions;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Exclusions;

/// <summary>
/// #22: Die Rückfrage von „Neu aufbauen“ nennt Einträge in ausgeschlossenen Ordnern mit ihren Benutzerdaten und bietet „behalten“
/// (Scan mit <see cref="ScanOptions.KeepCatalogedInExcludedFolders"/>) oder „herausnehmen“; ohne solche Einträge die bekannte Bestätigung.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class RebuildExclusionPromptTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private sealed class RecordingScans : IScanService
    {
        public List<(ScanMode Mode, ScanOptions Options)> Calls { get; } = [];

        public int KeptToReport { get; set; }

        public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
            => ScanAsync(libraryId, mode, ScanOptions.Default, progress, cancellationToken);

        public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add((mode, options));
            return Task.FromResult(new ScanResult(libraryId, @"C:\Fotos", 3, 10, 10, 1024, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
            {
                Mode = mode,
                KeptExcluded = options.KeepCatalogedInExcludedFolders ? KeptToReport : 0,
            });
        }
    }

    private sealed class FakeExclusions(ExcludedFilesSummary summary, bool fail = false) : IScanExclusionService
    {
        public List<ExclusionScope> Scopes { get; } = [];

        public IScanExclusionStore Store => throw new NotSupportedException();

        public Task<ExcludedFilesSummary> SummarizeAsync(ExclusionScope scope, CancellationToken ct = default)
        {
            Scopes.Add(scope);
            return fail ? Task.FromException<ExcludedFilesSummary>(new InvalidOperationException("Katalog gesperrt")) : Task.FromResult(summary);
        }

        public Task<ExclusionRemovalResult> RemoveFromCatalogAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FileOperationBatchResult?> UndoRemovalAsync(int batchId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ScanResult> RescanAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Dialogs(DialogChoice choice) : IDialogService
    {
        public List<string> Confirms { get; } = [];

        public List<(string Message, string Primary, string Secondary, bool Destructive)> Chooses { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
        {
            Confirms.Add(message);
            return Task.FromResult(choice != DialogChoice.None);
        }

        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
        {
            Chooses.Add((message, primaryText, secondaryText, secondaryIsDestructive));
            return Task.FromResult(choice);
        }

        public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK") => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public string? PickFolder(string title) => null;
    }

    private static readonly ExcludedFilesSummary TwoWithData = new([11, 12], new UserDataCounts(2, 1, 0, 1, 2));

    private static LibraryListItem Library() => new(
        new LibraryInfo(7, "Fotos", @"C:\Fotos", DateTime.UtcNow, DateTime.UtcNow, 3, 10, true, []),
        isActive: true);

    private static LibraryViewModel Create(RecordingScans scans, IDialogService dialogs, IScanExclusionService? exclusions)
        => new(
            NullService.Create<ILibraryService>(),
            scans,
            dialogs,
            NullService.Create<INavigationService>(),
            NullLogger<LibraryViewModel>.Instance,
            new WeakReferenceMessenger(),
            exclusions: exclusions);

    [Fact]
    public Task Mit_Eintraegen_nennt_die_Rueckfrage_Zahl_und_Benutzerdaten_und_behalten_scannt_mit_der_Option() => OnUiAsync(async () =>
    {
        var scans = new RecordingScans { KeptToReport = 2 };
        var dialogs = new Dialogs(DialogChoice.Primary);
        var exclusions = new FakeExclusions(TwoWithData);
        var vm = Create(scans, dialogs, exclusions);

        await vm.RebuildCommand.ExecuteAsync(Library());

        exclusions.Scopes.Should().ContainSingle().Which.Should().BeOfType<ExclusionScope.Current>().Which.LibraryIds.Should().Equal([7], "nur die eigene Bibliothek");
        dialogs.Confirms.Should().BeEmpty();
        var (message, primary, secondary, destructive) = dialogs.Chooses.Should().ContainSingle().Subject;
        primary.Should().Be(LibraryViewModel.RebuildKeepText);
        secondary.Should().Be(LibraryViewModel.RebuildDropText);
        destructive.Should().BeTrue("Herausnehmen verliert Angaben");
        message.Should().Contain("2 Dateien in ausgeschlossenen Ordnern")
            .And.Contain("1 mit Sternen/Favorit")
            .And.Contain("1 in Alben")
            .And.Contain("2 mit Tags/Kategorien")
            .And.Contain("neue Dateien in diesen Ordnern bleiben ausgeschlossen");
        scans.Calls.Should().ContainSingle();
        scans.Calls[0].Mode.Should().Be(ScanMode.ClearAndRescan);
        scans.Calls[0].Options.KeepCatalogedInExcludedFolders.Should().BeTrue();
        vm.ScanSummary.Should().Contain("Davon 2 Dateien in ausgeschlossenen Ordnern behalten.");
    });

    [Fact]
    public Task Herausnehmen_scannt_ohne_die_Option() => OnUiAsync(async () =>
    {
        var scans = new RecordingScans();
        var vm = Create(scans, new Dialogs(DialogChoice.Secondary), new FakeExclusions(TwoWithData));

        await vm.RebuildCommand.ExecuteAsync(Library());

        scans.Calls.Should().ContainSingle();
        scans.Calls[0].Mode.Should().Be(ScanMode.ClearAndRescan);
        scans.Calls[0].Options.KeepCatalogedInExcludedFolders.Should().BeFalse();
        vm.ScanSummary.Should().NotContain("behalten");
    });

    [Fact]
    public Task Abbrechen_startet_keinen_Neuaufbau() => OnUiAsync(async () =>
    {
        var scans = new RecordingScans();
        var vm = Create(scans, new Dialogs(DialogChoice.None), new FakeExclusions(TwoWithData));

        await vm.RebuildCommand.ExecuteAsync(Library());

        scans.Calls.Should().BeEmpty();
    });

    [Fact]
    public Task Ohne_Eintraege_bleibt_es_bei_der_bekannten_Bestaetigung() => OnUiAsync(async () =>
    {
        var scans = new RecordingScans();
        var dialogs = new Dialogs(DialogChoice.Primary);
        var vm = Create(scans, dialogs, new FakeExclusions(ExcludedFilesSummary.Empty));

        await vm.RebuildCommand.ExecuteAsync(Library());

        dialogs.Chooses.Should().BeEmpty();
        dialogs.Confirms.Should().ContainSingle().Which.Should().Be(LibraryViewModel.RebuildIntroText + "\n\n" + LibraryViewModel.RebuildTipText);
        scans.Calls.Should().ContainSingle();
        scans.Calls[0].Options.KeepCatalogedInExcludedFolders.Should().BeFalse();
    });

    [Fact]
    public void Ein_Eintrag_ohne_Benutzerdaten_steht_in_der_Einzahl_ohne_Verlust_Satz()
    {
        var text = LibraryViewModel.RebuildWithExcludedText(new ExcludedFilesSummary([5], UserDataCounts.None));

        text.Should().Contain("1 Datei in ausgeschlossenen Ordnern ist noch in der Bibliothek");
        text.Should().NotContain("gingen beim Herausnehmen verloren");
    }

    [Fact]
    public Task Scheitert_das_Zaehlen_sagt_die_Bestaetigung_dass_behalten_wird() => OnUiAsync(async () =>
    {
        var scans = new RecordingScans();
        var dialogs = new Dialogs(DialogChoice.Primary);
        var vm = Create(scans, dialogs, new FakeExclusions(TwoWithData, fail: true));

        await vm.RebuildCommand.ExecuteAsync(Library());

        dialogs.Confirms.Should().ContainSingle().Which.Should().Contain(LibraryViewModel.RebuildUncountedText);
        scans.Calls.Should().ContainSingle();
        scans.Calls[0].Options.KeepCatalogedInExcludedFolders.Should().BeTrue();
    });
}
