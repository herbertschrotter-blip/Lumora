using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Removal;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Services.Removal;

namespace Lumora.Wpf.Tests.Removal;

/// <summary>Einstellungen › Speicher &amp; Wartung › „Alle fehlenden entfernen (n)“ (ADR-0065): Zahl, Bereich, Rückgängig, Sperre in „Alle verknüpften“.</summary>
public sealed class MissingFilesSettingsTests
{
    private readonly FakeRemovalService _service = new();
    private readonly FakeRemovalDialogs _dialogs = new();

    private MissingFilesSettingsViewModel Create(bool allLinked = false)
        => new(new RemovalActions(_service, _dialogs, NullLogger<RemovalActions>.Instance), new Scope(allLinked), NullLogger<MissingFilesSettingsViewModel>.Instance, new WeakReferenceMessenger());

    [Fact]
    public async Task Zahl_und_Knopf_fuer_die_aktuelle_Bibliothek_mit_Rueckgaengig()
    {
        _service.MissingByFolder = new() { [10] = 2, [11] = 3 };
        _service.Summary = new MissingSummary([1, 2, 3, 4, 5], 0, 0, [], UserDataCounts.None);
        _service.RemoveResult = new MissingRemovalBatchResult(12, 5, 0, 0, 0, 2, 0, []);
        var vm = Create();

        await vm.RefreshAsync();

        vm.MissingCount.Should().Be(5);
        vm.ButtonText.Should().Be("Alle fehlenden entfernen (5)");
        vm.SummaryText.Should().Contain("5 Dateien");
        vm.RemoveAllCommand.CanExecute(null).Should().BeTrue();

        _service.MissingByFolder = new();
        await vm.RemoveAllCommand.ExecuteAsync(null);

        _service.RemoveCalls.Should().ContainSingle().Which.Should().BeOfType<MissingTarget.InLibraries>()
            .Which.LibraryIds.Should().Equal(3);
        _dialogs.Requests.Single().Message.Should().Contain("in dieser Bibliothek");
        vm.ResultText.Should().Contain("5 Dateien aus der Bibliothek entfernt");
        vm.CanUndo.Should().BeTrue();
        vm.MissingCount.Should().Be(0, "neu gezählt");
        vm.RemoveAllCommand.CanExecute(null).Should().BeFalse("n = 0");

        await vm.UndoCommand.ExecuteAsync(null);
        _service.UndoCalls.Should().Equal(12);
        vm.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task Alle_verknuepften_gesperrt()
    {
        _service.MissingByFolder = new() { [10] = 2 };
        var vm = Create(allLinked: true);

        await vm.RefreshAsync();

        vm.SummaryText.Should().Contain("Alle verknüpften");
        vm.RemoveAllCommand.CanExecute(null).Should().BeFalse();
    }

    private sealed class Scope(bool allLinked) : ILibraryScope
    {
        private LibraryScopeInfo Info => new(3, 1, "G", allLinked ? [3, 4] : [3], [3, 4], allLinked);

        public Task<LibraryScopeInfo> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(Info);

        public Task<LibraryScopeInfo> GetForLibraryAsync(int libraryId, CancellationToken ct = default) => Task.FromResult(Info);

        public Task<IReadOnlyList<LibraryGroupInfo>> GetGroupsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LibraryGroupInfo>>([]);

        public Task ShowAllLinkedAsync(int groupId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RenameGroupAsync(int groupId, string name, CancellationToken ct = default) => Task.CompletedTask;
    }
}
