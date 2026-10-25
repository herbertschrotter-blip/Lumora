using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Motifs;
using Lumora.Services.Motifs;
using Lumora.Services.Motifs.Models;
using Lumora.Shell;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.Motifs;

/// <summary>
/// Einstellungen › Motive (ADR-0052): Laden, Download nur nach Bestätigung (Dateien, Quelle, Größen, Lizenz), Abbrechen,
/// Begriffe anlegen/umbenennen/Beschreibung/Schwelle/löschen über den Dienst, globale Schwelle, Texte von Index und
/// Statusleiste.
/// </summary>
public sealed class MotifSettingsViewModelTests
{
    [Fact]
    public async Task Laden_zeigt_Zustand_Schwelle_und_Begriffe_der_aktiven_Bibliothek()
    {
        var (vm, index, terms, _, _) = Create();
        index.Settings = new MotifSettings(true, false, 0.02);
        vm.Load();
        await WaitAsync(() => vm.Terms.Count == 3);

        vm.IsEnabled.Should().BeTrue();
        vm.UseGpu.Should().BeFalse();
        vm.SelectedGlobalThreshold!.Value.Should().Be(0.02);
        vm.GlobalThresholdChoices.Should().Contain(c => c.Label == "1 % (Standard)");
        vm.Terms.Select(t => (t.Name, t.FileCountText)).Should().Equal(("Hund", "12 Dateien"), ("Strand", "1 Datei"), ("Eigener", "0 Dateien"));
        vm.Terms[0].SelectedThreshold!.Label.Should().Be("Standard");
        vm.Terms[1].SelectedThreshold!.Label.Should().Be("5 %");
        vm.TermsSummary.Should().Contain("3 Begriffe").And.Contain("„Fotos“").And.Contain("2 davon mit Treffern");
        terms.LastLibrary.Should().Be(7);
        vm.IsModelReady.Should().BeTrue();
        vm.ModelStatusText.Should().Contain("SigLIP 2");
    }

    [Fact]
    public async Task Download_nur_nach_Bestaetigung_mit_Dateien_Quelle_Groessen_und_Lizenz()
    {
        var (vm, index, terms, models, dialogs) = Create();
        terms.ModelAvailable = false;
        vm.Load();
        vm.IsModelReady.Should().BeFalse();
        vm.DownloadModelCommand.CanExecute(null).Should().BeTrue();

        dialogs.ConfirmAnswer = false;
        await vm.DownloadModelCommand.ExecuteAsync(null);
        models.Downloads.Should().Be(0);
        dialogs.LastMessage.Should().Contain("SigLIP 2").And.Contain("onnx/vision_model.onnx").And.Contain("372 MB")
            .And.Contain("huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX").And.Contain("Apache-2.0")
            .And.Contain(models.ModelFolder).And.Contain("SHA-256").And.Contain("verlassen den Rechner nicht");
        dialogs.LastMessage.Should().Contain("690 MB");

        dialogs.ConfirmAnswer = true;
        terms.ModelAvailable = true; // nach dem Download vorhanden
        await vm.DownloadModelCommand.ExecuteAsync(null);
        models.Downloads.Should().Be(1);
        index.RunRequests.Should().Be(1);
        vm.Message.Should().Contain("heruntergeladen");
        vm.IsModelReady.Should().BeTrue();
        vm.IsDownloading.Should().BeFalse();
    }

    [Fact]
    public async Task Mit_Manager_dieselbe_Bestaetigung_wie_Modelle_und_Daten_und_Laden_ueber_den_Manager()
    {
        // ADR-0070: der Knopf in Einstellungen › Motive lädt über den Update-Manager
        var libraries = new InMemoryLibraries();
        libraries.Add(7, "Fotos", 20, DateTime.UtcNow);
        var index = new FakeIndex();
        var terms = new FakeTerms { ModelAvailable = false };
        var models = new FakeModels();
        var dialogs = new Dialogs();
        var source = new Lumora.Wpf.Tests.Settings.ModelPackagesViewModelTests.DelegatingSource(
            Lumora.Services.ModelPackages.ModelPackageCatalog.Motifs,
            (p, ct) => models.DownloadAsync(null, ct));
        var manager = new Lumora.Services.ModelPackages.ModelPackageManager([source], new Lumora.Wpf.Tests.Settings.ModelPackagesViewModelTests.NoMetadata());
        var vm = new MotifSettingsViewModel(index, terms, models, libraries, dialogs, NullLogger<MotifSettingsViewModel>.Instance, new WeakReferenceMessenger(), packages: manager);
        vm.Load();

        dialogs.ConfirmAnswer = false;
        await vm.DownloadModelCommand.ExecuteAsync(null);
        source.Downloads.Should().Be(0);
        dialogs.LastMessage.Should().Contain("SigLIP 2 base").And.Contain("Hugging Face").And.Contain("Apache-2.0").And.Contain("SHA-256");

        dialogs.ConfirmAnswer = true;
        terms.ModelAvailable = true;
        await vm.DownloadModelCommand.ExecuteAsync(null);
        source.Downloads.Should().Be(1);
        models.Downloads.Should().Be(1);
        index.RunRequests.Should().Be(1);
        vm.IsModelReady.Should().BeTrue();
    }

    [Fact]
    public async Task Download_abbrechen_und_Fehler_als_Meldung()
    {
        var (vm, _, terms, models, dialogs) = Create();
        terms.ModelAvailable = false;
        dialogs.ConfirmAnswer = true;
        models.Block = true;
        vm.Load();
        var running = vm.DownloadModelCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.IsDownloading);
        vm.CancelDownloadCommand.Execute(null);
        await running;
        vm.Message.Should().Contain("abgebrochen");
        vm.MessageIsError.Should().BeFalse();

        models.Block = false;
        models.Failure = new MotifModelDownloadException("Die Prüfsumme von tokenizer.json stimmt nicht.");
        await vm.StartDownloadAsync();
        vm.Message.Should().Contain("Prüfsumme");
        vm.MessageIsError.Should().BeTrue();
    }

    [Fact]
    public async Task Begriffe_anlegen_umbenennen_beschreiben_schwelle_loeschen_ueber_den_Dienst()
    {
        var (vm, _, terms, _, dialogs) = Create();
        vm.Load();
        await WaitAsync(() => vm.Terms.Count == 3);

        vm.AddTermCommand.CanExecute(null).Should().BeFalse();
        vm.NewTermName = " Leuchtturm-Süd ";
        vm.NewTermPrompt = "a photo of a lighthouse";
        await vm.AddTermCommand.ExecuteAsync(null);
        terms.Calls.Should().Contain("add 7 Leuchtturm-Süd a photo of a lighthouse");
        vm.NewTermName.Should().BeEmpty();
        vm.Terms.Should().HaveCount(4);

        terms.NextError = new MotifTermException("Den Begriff „Hund“ gibt es schon.");
        vm.NewTermName = "hund";
        await vm.AddTermCommand.ExecuteAsync(null);
        vm.MessageIsError.Should().BeTrue();
        vm.Message.Should().Contain("gibt es schon");

        dialogs.PromptAnswer = "Hunde";
        await vm.RenameTermCommand.ExecuteAsync(vm.Terms[0]);
        terms.Calls.Should().Contain("update 1 Hunde a photo of a dog");

        dialogs.PromptAnswer = "a dog on a beach";
        await vm.EditPromptCommand.ExecuteAsync(vm.Terms.First(t => t.Id == 2));
        terms.Calls.Should().Contain("update 2 Strand a dog on a beach");

        var row = vm.Terms.First(t => t.Id == 1);
        row.SelectedThreshold = row.ThresholdChoices.First(c => c.Label == "3 %");
        await WaitAsync(() => terms.Calls.Contains("threshold 1 0.03"));

        dialogs.ConfirmAnswer = false;
        await vm.DeleteTermCommand.ExecuteAsync(vm.Terms.First(t => t.Id == 3));
        terms.Calls.Should().NotContain(c => c.StartsWith("delete", StringComparison.Ordinal));
        dialogs.ConfirmAnswer = true;
        await vm.DeleteTermCommand.ExecuteAsync(vm.Terms.First(t => t.Id == 3));
        terms.Calls.Should().Contain("delete 3");
        vm.Terms.Should().NotContain(t => t.Id == 3);
    }

    [Fact]
    public async Task Globale_Schwelle_Schalter_und_Neu_analysieren()
    {
        var (vm, index, _, _, dialogs) = Create();
        vm.Load();
        vm.SelectedGlobalThreshold = vm.GlobalThresholdChoices.First(c => c.Value == 0.03);
        await WaitAsync(() => index.Settings.DefaultThreshold == 0.03);
        vm.IsEnabled = false;
        await WaitAsync(() => !index.Settings.Enabled);
        vm.UseGpu = false;
        await WaitAsync(() => !index.Settings.UseGpu);

        dialogs.ConfirmAnswer = true;
        await vm.RebuildCommand.ExecuteAsync(null);
        index.Rebuilds.Should().Be(1);
        vm.Message.Should().Contain("neu analysiert");
    }

    [Fact]
    public void Zustandstexte_fuer_Einstellungen_und_Statusleiste()
    {
        MotifSettingsViewModel.Describe(MotifIndexStatus.Disabled).Should().Be("Ausgeschaltet.");
        MotifSettingsViewModel.Describe(new MotifIndexStatus(MotifIndexState.ModelMissing)).Should().Contain("Modell");
        MotifSettingsViewModel.Describe(new MotifIndexStatus(MotifIndexState.Running, 5, 10, 26, "DirectML"))
            .Should().Be("Läuft: 5 von 10 Fotos und Videos analysiert · DirectML · 26 ms/Foto");
        MotifSettingsViewModel.Describe(new MotifIndexStatus(MotifIndexState.Waiting, Message: "Wartet auf den Scan")).Should().Be("Wartet auf den Scan…");

        StatusBarViewModel.DescribeMotifIndex(new MotifIndexStatus(MotifIndexState.Running, 1234, 19556, 26)).Should().Be($"Motive: {1234:N0} / {19556:N0} · 26 ms/Foto");
        StatusBarViewModel.DescribeMotifIndex(new MotifIndexStatus(MotifIndexState.Paused, 3, 9)).Should().Be("Motive: 3 / 9 · angehalten");
        StatusBarViewModel.DescribeMotifIndex(new MotifIndexStatus(MotifIndexState.Waiting, Message: "Wartet auf den Scan")).Should().Be("Motive: wartet auf den scan…");
        StatusBarViewModel.DescribeMotifIndex(new MotifIndexStatus(MotifIndexState.Idle, 9, 9)).Should().BeNull();
        StatusBarViewModel.DescribeMotifIndex(MotifIndexStatus.Disabled).Should().BeNull();
        MotifThresholdChoice.Format(0.005).Should().Be("0,5 %");
        MotifThresholdChoice.Format(0.03).Should().Be("3 %");
    }

    [Fact]
    public async Task Ein_ueberholter_langsamer_Lesevorgang_bringt_keinen_alten_Stand_zurueck()
    {
        // Rauchtest-Wackler „Motive … eigener Begriff gelöscht“ (Issue #20, 0.4.41): ein älteres Neuladen kam nach dem
        // neueren zurück und schrieb den Stand von vorher in die Liste.
        var (vm, _, terms, _, _) = Create();
        vm.Load();
        await WaitAsync(() => vm.Terms.Count == 3);

        var hold = new TaskCompletionSource();
        terms.HoldNextRead = hold;
        var slow = vm.LoadTermsAsync(); // liest den Stand mit 3 Begriffen und wartet
        await terms.AddTermAsync(7, "Neu");
        await vm.LoadTermsAsync();
        vm.Terms.Should().HaveCount(4);

        hold.SetResult();
        await slow;
        vm.Terms.Select(t => t.Name).Should().Contain("Neu", "der überholte Lesevorgang darf die Liste nicht überschreiben");
        vm.Terms.Should().HaveCount(4);
    }

    private static (MotifSettingsViewModel Vm, FakeIndex Index, FakeTerms Terms, FakeModels Models, Dialogs Dialogs) Create()
    {
        var libraries = new InMemoryLibraries();
        libraries.Add(7, "Fotos", 20, DateTime.UtcNow);
        var index = new FakeIndex();
        var terms = new FakeTerms();
        var models = new FakeModels();
        var dialogs = new Dialogs();
        var vm = new MotifSettingsViewModel(index, terms, models, libraries, dialogs, NullLogger<MotifSettingsViewModel>.Instance, new WeakReferenceMessenger());
        return (vm, index, terms, models, dialogs);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Bedingung nicht erfüllt");
            }

            await Task.Delay(10);
        }
    }

    internal sealed class FakeIndex : IMotifIndexService
    {
        public MotifIndexStatus Status { get; set; } = new(MotifIndexState.Idle, 3, 3);

        public MotifSettings Settings { get; set; } = MotifSettings.Default;

        public bool IsPaused { get; private set; }

        public int RunRequests { get; private set; }

        public int Rebuilds { get; private set; }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            Settings = Settings with { Enabled = enabled };
            return Task.CompletedTask;
        }

        public Task SetUseGpuAsync(bool useGpu, CancellationToken ct = default)
        {
            Settings = Settings with { UseGpu = useGpu };
            return Task.CompletedTask;
        }

        public Task SetDefaultThresholdAsync(double threshold, CancellationToken ct = default)
        {
            Settings = Settings with { DefaultThreshold = threshold };
            return Task.CompletedTask;
        }

        public void Pause() => IsPaused = true;

        public void Resume() => IsPaused = false;

        public void CancelRun()
        {
        }

        public void RequestRun() => RunRequests++;

        public Task<int> RebuildAsync(CancellationToken ct = default)
        {
            Rebuilds++;
            return Task.FromResult(42);
        }
    }

    internal sealed class FakeTerms : IMotifTermService
    {
        private readonly List<MotifTermInfo> _terms =
        [
            new(1, 7, "Hund", "a photo of a dog", null, 12, true),
            new(2, 7, "Strand", "a photo of a beach", 0.05, 1, true),
            new(3, 7, "Eigener", null, null, 0, true),
        ];

        public List<string> Calls { get; } = [];

        public int? LastLibrary { get; private set; }

        public Exception? NextError { get; set; }

        public bool ModelAvailable { get; set; } = true;

        public bool IsModelAvailable => ModelAvailable;

        /// <summary>Hält den nächsten Lesevorgang an (Stand von vorher), bis die Quelle gesetzt wird.</summary>
        public TaskCompletionSource? HoldNextRead { get; set; }

        public async Task<IReadOnlyList<MotifTermInfo>> GetTermsAsync(int libraryId, CancellationToken ct = default)
        {
            LastLibrary = libraryId;
            var snapshot = _terms.ToList();
            if (HoldNextRead is { } hold)
            {
                HoldNextRead = null;
                await hold.Task;
            }

            return snapshot;
        }

        public Task<int> AddTermAsync(int libraryId, string name, string? prompt = null, CancellationToken ct = default)
        {
            ThrowIfError();
            Calls.Add($"add {libraryId} {name.Trim()} {prompt}");
            _terms.Add(new MotifTermInfo(_terms.Max(t => t.Id) + 1, libraryId, name.Trim(), prompt, null, 0, true));
            return Task.FromResult(_terms[^1].Id);
        }

        public Task UpdateTermAsync(int termId, string name, string? prompt, CancellationToken ct = default)
        {
            ThrowIfError();
            Calls.Add($"update {termId} {name} {prompt}");
            return Task.CompletedTask;
        }

        public Task SetThresholdAsync(int termId, double? threshold, CancellationToken ct = default)
        {
            Calls.Add(FormattableString.Invariant($"threshold {termId} {threshold}"));
            return Task.CompletedTask;
        }

        public Task DeleteTermAsync(int termId, CancellationToken ct = default)
        {
            Calls.Add($"delete {termId}");
            _terms.RemoveAll(t => t.Id == termId);
            return Task.CompletedTask;
        }

        public Task<int> EnsureTextVectorsAsync(int libraryId, CancellationToken ct = default) => Task.FromResult(0);

        public Task RecomputeAllAsync(int libraryId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<FileMotifInfo>> GetMotifsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileMotifInfo>>([]);

        public Task<IReadOnlyList<MotifSearchHit>> SearchAsync(int libraryId, string text, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MotifSearchHit>>([]);

        private void ThrowIfError()
        {
            if (NextError is { } error)
            {
                NextError = null;
                throw error;
            }
        }
    }

    internal sealed class FakeModels : IMotifModelProvider
    {
        public string ModelFolder => @"C:\Daten\models\siglip2-base-patch16-224";

        public bool IsOverridden => false;

        public MotifModelState State { get; set; } = MotifModelState.Missing;

        public MotifModelDownloadInfo DownloadInfo => MotifModelDownloadInfo.Siglip2Base;

        public int Downloads { get; private set; }

        public bool Block { get; set; }

        public Exception? Failure { get; set; }

        public string PathOf(string relativePath) => System.IO.Path.Combine(ModelFolder, relativePath);

        public async Task DownloadAsync(IProgress<MotifModelDownloadProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new MotifModelDownloadProgress("Wird heruntergeladen", 1, 2));
            if (Block)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            Downloads++;
            State = MotifModelState.Ready;
        }
    }

    internal sealed class Dialogs : IDialogService
    {
        public bool ConfirmAnswer { get; set; }

        public string? PromptAnswer { get; set; }

        public string? LastMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
        {
            LastMessage = message;
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
            => Task.FromResult(DialogChoice.None);

        public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK") => Task.FromResult(PromptAnswer);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public string? PickFolder(string title) => null;
    }
}
