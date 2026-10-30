using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.Relocation;
using Lumora.Services.Operations;
using Lumora.Shell.HiddenLibraries;
using Lumora.Shell.HiddenLibraries.Relocation;

namespace Lumora.Wpf.Tests.HiddenLibraries;

/// <summary>
/// „Container verschieben“ (Mockup 26) mit Attrappen für Container und Katalog-Wechsel, echte Dateien im Temp-Ordner:
/// Kopieren und Prüfen, Umbenennen, Abbrechen und Aushänge-Fehler lassen die alte Datei unberührt und hängen sie wieder
/// ein; danach fragt Lumora das Kennwort und öffnet die versteckte Bibliothek wieder. Dazu der Assistent (Laufwerke, Ziel).
/// </summary>
[Collection(Lumora.Wpf.Tests.Fixtures.UiTestCollection.Name)]
public sealed class ContainerRelocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", "relocate-" + Guid.NewGuid().ToString("N"));
    private readonly HiddenLibraryStore _store = new(iterations: 1_000);
    private readonly FakeContainers _containers = new();
    private readonly FakeHostSwitcher _switcher;
    private readonly string _source;

    public ContainerRelocationTests()
    {
        var normal = Directory.CreateDirectory(Path.Combine(_root, "normal")).FullName;
        _switcher = new FakeHostSwitcher(normal);
        _source = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "alt")).FullName, "Privat.vhdx");
        var bytes = new byte[3 * 1024 * 1024 + 17];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(_source, bytes);
    }

    public void Dispose()
    {
        HiddenSessionPrivacy.IsHiddenSessionActive = false;
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
        }

        Directory.Delete(_root, recursive: true);
    }

    private async Task<CatalogSessionManager> OpenedAsync()
    {
        var appdata = Directory.CreateDirectory(Path.Combine(_root, "appdata")).FullName;
        var manager = new CatalogSessionManager(
            new HiddenModeOptions(true, appdata, appdata, MaxFolderDepth: int.MaxValue, UseUiHooks: false),
            _store, new FixedDriveRoots(_root), _containers, TimeProvider.System, NullLogger.Instance);
        manager.AttachSwitcher(_switcher);
        (await manager.CreateAsync(Path.Combine(_root, "container", "Tresor" + Guid.NewGuid().ToString("N")[..6]), "Tresor", "kennwort-1")).Success.Should().BeTrue();
        return manager;
    }

    private ContainerRelocationPlan Plan(bool rename = false, OldContainerFile old = OldContainerFile.Keep, bool vhdx = true)
        => new(_source, Path.Combine(_root, "neu", "Privat.vhdx"), 'X', vhdx, old, rename);

    [Fact]
    public Task Kopieren_pruefen_am_neuen_Ort_oeffnen_und_die_Bibliothek_wieder_oeffnen() => OnUiAsync(async () =>
    {
        var manager = await OpenedAsync();
        _switcher.Dialogs = new ScriptedHiddenDialogs { UnlockAnswer = new HiddenLibraryUnlockAnswer("kennwort-1", false) };
        var progress = new FakeProgress();
        var plan = Plan();

        var result = await manager.RelocateContainerAsync(plan, () => progress);

        result.Success.Should().BeTrue(result.Message);
        File.ReadAllBytes(plan.TargetFile).Should().Equal(File.ReadAllBytes(_source), "geprüfte Kopie");
        File.Exists(plan.TargetFile + ContainerFileCopier.PartSuffix).Should().BeFalse();
        File.Exists(_source).Should().BeTrue("„Behalten“");
        _containers.Dismounted.Should().ContainSingle();
        _containers.MountedImages.Should().Equal(plan.TargetFile);
        progress.Steps.Should().Equal(ContainerRelocationStep.Dismount, ContainerRelocationStep.Copy, ContainerRelocationStep.Verify, ContainerRelocationStep.Mount, ContainerRelocationStep.Reopen);
        progress.Result!.Value.Success.Should().BeTrue();
        progress.Result!.Value.Message.Should().Contain("wieder als X: geöffnet").And.Contain("weiter am alten Ort");
        manager.IsHiddenModeEnabled.Should().BeTrue("Verschieben beendet den versteckten Modus nicht");
        manager.IsHiddenSessionActive.Should().BeTrue("nach dem Kennwort wieder offen");
    });

    [Fact]
    public Task Alte_Datei_in_den_Papierkorb_und_Umbenennen_auf_demselben_Laufwerk() => OnUiAsync(async () =>
    {
        var manager = await OpenedAsync();
        var bin = new CollectingRecycleBin();
        var progress = new FakeProgress();
        var copy = Plan(old: OldContainerFile.RecycleBin, vhdx: false);
        (await manager.RelocateContainerAsync(copy, () => progress, bin)).Success.Should().BeTrue();
        bin.Paths.Should().Equal(_source);
        _containers.Mounted.Should().Equal((copy.TargetFile, 'X'));
        progress.Result!.Value.Message.Should().Contain("Papierkorb");

        // zweites Mal: zurück auf den alten Ordner — gleiches Laufwerk, nur umbenennen
        File.Delete(_source);
        var manager2 = await OpenedAsync();
        var rename = new ContainerRelocationPlan(copy.TargetFile, _source, 'X', false, OldContainerFile.RecycleBin, IsRename: true);
        (await manager2.RelocateContainerAsync(rename, () => new FakeProgress(), bin)).Success.Should().BeTrue();
        File.Exists(_source).Should().BeTrue();
        File.Exists(copy.TargetFile).Should().BeFalse("umbenannt, nicht kopiert");
        bin.Paths.Should().ContainSingle("beim Umbenennen gibt es keine alte Datei");
    });

    [Fact]
    public Task Abbrechen_loescht_die_halbe_Kopie_und_oeffnet_die_alte_Datei_wieder() => OnUiAsync(async () =>
    {
        var manager = await OpenedAsync();
        var progress = new FakeProgress();
        progress.Cancel();
        var plan = Plan();

        var result = await manager.RelocateContainerAsync(plan, () => progress);

        result.Success.Should().BeFalse();
        File.Exists(plan.TargetFile).Should().BeFalse();
        File.Exists(plan.TargetFile + ContainerFileCopier.PartSuffix).Should().BeFalse();
        File.Exists(_source).Should().BeTrue();
        _containers.MountedImages.Should().Equal(_source);
        progress.Result!.Value.Message.Should().StartWith("Abgebrochen.").And.Contain("wieder als X: geöffnet");
        manager.IsHiddenModeEnabled.Should().BeTrue();
    });

    [Fact]
    public Task Laesst_sich_der_Container_nicht_aushaengen_bleibt_alles_wie_es_ist() => OnUiAsync(async () =>
    {
        var manager = await OpenedAsync();
        _containers.DismountSucceeds = false;
        var progress = new FakeProgress();

        (await manager.RelocateContainerAsync(Plan(), () => progress)).Success.Should().BeFalse();

        _containers.MountedImages.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "neu")).Should().BeFalse();
        progress.Result!.Value.Message.Should().Contain("nicht aushängen");
    });

    [Fact]
    public Task Am_neuen_Ort_nicht_zu_oeffnen_haengt_die_alte_Datei_wieder_ein() => OnUiAsync(async () =>
    {
        var manager = await OpenedAsync();
        _containers.VirtualDiskResult = new ContainerCommandResult(false, "Kennwort abgelehnt");
        var progress = new FakeProgress();
        var plan = Plan();

        (await manager.RelocateContainerAsync(plan, () => progress)).Success.Should().BeFalse();

        _containers.MountedImages.Should().Equal(plan.TargetFile, _source);
        File.Exists(_source).Should().BeTrue();
        progress.Result!.Value.Message.Should().Contain("Die Kopie bleibt liegen");
    });

    [Fact]
    public void Assistent_schlaegt_das_Laufwerk_mit_dem_meisten_Platz_vor_und_prueft_das_Ziel()
    {
        var sourceRoot = Path.GetPathRoot(_source)!;
        var drives = new FixedDrives(
            (sourceRoot, "System", 50L << 30, 1L << 40, false),
            ("E:\\", "Daten", 2700L << 30, 4L << 40, false),
            ("F:\\", "USB", 900L << 30, 2L << 40, true),
            ("G:\\", "Klein", 1L << 20, 1L << 30, false),
            ("X:\\", "Container", 1L << 30, 2L << 30, false));
        var vm = new ContainerRelocationViewModel('x', isVeraCrypt: false, _source, drives, exists: _ => false, syncFolders: []);

        vm.Drives.Select(d => d.Name).Should().NotContain("X:", "der Container selbst ist kein Ziel");
        vm.Drives.Single(d => d.Name == "G:").IsUsable.Should().BeFalse();
        vm.Drives.Single(d => d.Name == "F:").FreeText.Should().Contain("wechselbar");
        vm.Drives.Single(d => d.IsSelected).Name.Should().Be("E:");
        vm.TargetFile.Should().Be(Path.Combine("E:\\", Path.GetRelativePath(sourceRoot, _source)));
        vm.Check.CanStart.Should().BeTrue();
        vm.Check.IsRename.Should().BeFalse();

        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.IsStep3.Should().BeTrue();
        vm.NextText.Should().Be("Verschieben starten");
        vm.RecycleOldFile = true;
        vm.NextCommand.Execute(null);
        vm.Result.Should().Be(new ContainerRelocationPlan(_source, vm.TargetFile, 'X', true, OldContainerFile.RecycleBin, false));

        vm.SetTargetFolder(Path.Combine("E:\\", "OneDrive", "Fotos"));
        vm.HasProblem.Should().BeTrue();
        vm.ProblemText.Should().Contain("Cloud");
    }

    [Fact]
    public void VeraCrypt_ohne_erkannte_Datei_verlangt_die_Wahl()
    {
        var vm = new ContainerRelocationViewModel('X', isVeraCrypt: true, null, new FixedDrives(), exists: _ => false, syncFolders: []);
        vm.HasSource.Should().BeFalse();
        vm.NextCommand.CanExecute(null).Should().BeFalse();
        vm.SourceDetails.Should().Contain("VeraCrypt verrät den Pfad nicht");
        vm.PickSourceText.Should().Be("Datei wählen…");
        vm.ReopenText.Should().Contain("VeraCrypt fragt");
    }

    /// <summary>Auf dem UI-Thread: Progress&lt;T&gt; stellt Meldungen dann der Reihe nach zu wie in der App.</summary>
    private static Task OnUiAsync(Func<Task> scenario)
        => Lumora.Wpf.Tests.Fixtures.TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(60));

    private sealed class FakeProgress : IContainerRelocationProgress
    {
        private readonly CancellationTokenSource _cts = new();

        public List<ContainerRelocationStep> Steps { get; } = [];

        public (bool Success, string Message)? Result { get; private set; }

        public CancellationToken Cancellation => _cts.Token;

        public void Cancel() => _cts.Cancel();

        public void Step(ContainerRelocationStep step)
        {
            if (Steps.LastOrDefault() != step || Steps.Count == 0)
            {
                Steps.Add(step);
            }
        }

        public void Copy(ContainerCopyProgress progress)
        {
        }

        public void Finish(bool success, string message) => Result = (success, message);
    }

    private sealed class CollectingRecycleBin : IRecycleBin
    {
        public List<string> Paths { get; } = [];

        public void MoveToRecycleBin(string filePath)
        {
            Paths.Add(filePath);
            File.Delete(filePath);
        }
    }

    private sealed class FixedDrives(params (string Root, string Label, long Free, long Total, bool Removable)[] drives) : IRelocationDriveSource
    {
        public IReadOnlyList<(string Root, string Label, long Free, long Total, bool Removable)> GetDrives() => drives;
    }
}
