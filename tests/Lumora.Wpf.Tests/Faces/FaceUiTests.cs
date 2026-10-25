using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Faces;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Faces.Models;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Faces;

/// <summary>
/// Oberfläche der Gesichtserkennung Teil 1 (ADR-0035): Einstellungs-Abschnitt (Schalter, Download nur nach
/// Bestätigung, Fortschritt, Abbruch, Index neu aufbauen) und Statusleiste (Text, Pause/Fortsetzen/Abbrechen).
/// Ohne UI-Thread — Nachrichten werden ohne Application direkt übernommen.
/// </summary>
public sealed class FaceUiTests
{
    [Fact]
    public async Task Einstellungen_zeigen_Zustand_und_speichern_die_Schalter()
    {
        var index = new FakeIndex { Settings = new FaceIndexSettings(false, true) };
        var models = new FakeModels();
        var vm = new FaceSettingsViewModel(index, models, new FakeDialogs(true), NullLogger<FaceSettingsViewModel>.Instance, new WeakReferenceMessenger());

        vm.Load();

        vm.IsEnabled.Should().BeFalse("Standard aus");
        vm.UseGpu.Should().BeTrue();
        vm.IsModelReady.Should().BeFalse();
        vm.ModelStatusText.Should().Be("Modell nicht vorhanden.");
        index.EnabledCalls.Should().BeEmpty("Laden speichert nichts");

        vm.IsEnabled = true;
        vm.UseGpu = false;
        await Task.Delay(50);
        index.EnabledCalls.Should().Equal(true);
        index.GpuCalls.Should().Equal(false);
        FaceSettingsViewModel.DownloadButtonText.Should().Contain("275 MB").And.Contain("nicht-kommerzielle");
    }

    [Fact]
    public async Task Download_nur_nach_Bestaetigung_mit_Fortschritt_danach_Index_angestossen()
    {
        var index = new FakeIndex();
        var models = new FakeModels();
        var declined = new FaceSettingsViewModel(index, models, new FakeDialogs(false), NullLogger<FaceSettingsViewModel>.Instance, new WeakReferenceMessenger());
        declined.Load();
        await declined.DownloadModelCommand.ExecuteAsync(null);
        models.Downloads.Should().Be(0, "kein Download ohne Bestätigung");

        var dialogs = new FakeDialogs(true);
        var vm = new FaceSettingsViewModel(index, models, dialogs, NullLogger<FaceSettingsViewModel>.Instance, new WeakReferenceMessenger());
        vm.Load();
        await vm.DownloadModelCommand.ExecuteAsync(null);

        dialogs.LastMessage.Should().Contain("nicht-kommerzielle").And.Contain(models.ModelFolder);
        models.Downloads.Should().Be(1);
        vm.IsModelReady.Should().BeTrue();
        vm.IsDownloading.Should().BeFalse();
        vm.Message.Should().Be("Modell heruntergeladen und geprüft.");
        index.RunRequests.Should().Be(1);
        vm.DownloadModelCommand.CanExecute(null).Should().BeFalse("Modell vorhanden");
    }

    [Fact]
    public async Task Mit_Manager_dieselbe_Bestaetigung_wie_Modelle_und_Daten_und_Laden_ueber_den_Manager()
    {
        // ADR-0070: der Knopf in Einstellungen › Gesichter lädt über den Update-Manager (Bestätigung, Aktivität, Vermerk)
        var index = new FakeIndex();
        var models = new FakeModels();
        var source = new Lumora.Wpf.Tests.Settings.ModelPackagesViewModelTests.DelegatingSource(
            Lumora.Services.ModelPackages.ModelPackageCatalog.Faces,
            (p, ct) => models.DownloadAsync(p is null ? null : new Progress<FaceModelDownloadProgress>(x => p.Report(new Lumora.Services.ModelPackages.ModelPackageProgress(x.Stage, x.Done, x.Total))), ct));
        var manager = new Lumora.Services.ModelPackages.ModelPackageManager([source], new Lumora.Wpf.Tests.Settings.ModelPackagesViewModelTests.NoMetadata());

        var declined = new FaceSettingsViewModel(index, models, new FakeDialogs(false), NullLogger<FaceSettingsViewModel>.Instance, new WeakReferenceMessenger(), packages: manager);
        declined.Load();
        await declined.DownloadModelCommand.ExecuteAsync(null);
        source.Downloads.Should().Be(0, "kein Download ohne Bestätigung");

        var dialogs = new FakeDialogs(true);
        var vm = new FaceSettingsViewModel(index, models, dialogs, NullLogger<FaceSettingsViewModel>.Instance, new WeakReferenceMessenger(), packages: manager);
        vm.Load();
        await vm.DownloadModelCommand.ExecuteAsync(null);

        dialogs.LastMessage.Should().Contain("InsightFace buffalo_l").And.Contain("example.invalid").And.Contain("275 MB").And.Contain("nicht-kommerzielle");
        source.Downloads.Should().Be(1);
        models.Downloads.Should().Be(1);
        vm.IsModelReady.Should().BeTrue();
        vm.Message.Should().Be("Modell heruntergeladen und geprüft.");
        index.RunRequests.Should().Be(1);
    }

    [Fact]
    public async Task Download_abbrechen_und_Fehler_werden_gemeldet()
    {
        var models = new FakeModels { Block = true };
        var vm = new FaceSettingsViewModel(new FakeIndex(), models, new FakeDialogs(true), NullLogger<FaceSettingsViewModel>.Instance, new WeakReferenceMessenger());
        vm.Load();
        var running = vm.StartDownloadAsync();
        vm.IsDownloading.Should().BeTrue();
        vm.CancelDownloadCommand.CanExecute(null).Should().BeTrue();
        vm.CancelDownloadCommand.Execute(null);
        await running;
        vm.Message.Should().Contain("abgebrochen");
        vm.IsModelReady.Should().BeFalse();

        models.Block = false;
        models.Fail = new FaceModelDownloadException("Die Prüfsumme stimmt nicht.");
        await vm.StartDownloadAsync();
        vm.MessageIsError.Should().BeTrue();
        vm.Message.Should().Be("Die Prüfsumme stimmt nicht.");
    }

    [Fact]
    public async Task Index_neu_aufbauen_fragt_nach()
    {
        var index = new FakeIndex { RebuildResult = 12 };
        var vm = new FaceSettingsViewModel(index, new FakeModels(), new FakeDialogs(true), NullLogger<FaceSettingsViewModel>.Instance, new WeakReferenceMessenger());
        await vm.RebuildIndexCommand.ExecuteAsync(null);
        index.Rebuilds.Should().Be(1);
        vm.Message.Should().Contain("12");

        var declined = new FaceSettingsViewModel(index, new FakeModels(), new FakeDialogs(false), NullLogger<FaceSettingsViewModel>.Instance, new WeakReferenceMessenger());
        await declined.RebuildIndexCommand.ExecuteAsync(null);
        index.Rebuilds.Should().Be(1);
    }

    [Fact]
    public void Statustexte_fuer_Einstellungen_und_Statusleiste()
    {
        var running = new FaceIndexStatus(FaceIndexState.Running, 1234, 19556, 37.2, "DirectML", LibraryId: 1);
        StatusBarViewModel.DescribeFaceIndex(running).Should().Be($"Gesichter: {1234:N0} / {19556:N0} · 37 ms/Foto");
        StatusBarViewModel.DescribeFaceIndex(running with { State = FaceIndexState.Paused }).Should().EndWith("· angehalten");
        StatusBarViewModel.DescribeFaceIndex(new FaceIndexStatus(FaceIndexState.Idle, 5, 5)).Should().BeNull();
        StatusBarViewModel.DescribeFaceIndex(FaceIndexStatus.Disabled).Should().BeNull();
        StatusBarViewModel.DescribeFaceIndex(new FaceIndexStatus(FaceIndexState.ModelMissing)).Should().BeNull();
        StatusBarViewModel.DescribeFaceIndex(new FaceIndexStatus(FaceIndexState.Error, Message: "kaputt")).Should().Be("Gesichtserkennung: kaputt");

        FaceSettingsViewModel.Describe(running).Should().Contain("Läuft").And.Contain("DirectML").And.Contain("ms/Foto");
        FaceSettingsViewModel.Describe(FaceIndexStatus.Disabled).Should().Be("Ausgeschaltet.");
        FaceSettingsViewModel.Describe(new FaceIndexStatus(FaceIndexState.Idle, 5, 5, ExecutionProvider: "CPU")).Should().StartWith("Bereit:");
    }

    [Fact]
    public void Statusleiste_folgt_den_Nachrichten_und_steuert_Pause_Fortsetzen_Abbrechen()
    {
        var messenger = new WeakReferenceMessenger();
        var index = new FakeIndex();
        var statusBar = new StatusBarViewModel(
            NullService.Create<IThumbnailService>(),
            NullService.Create<ILibraryService>(),
            NullService.Create<IFileOperationService>(),
            new ScanGate(messenger),
            NullLogger<StatusBarViewModel>.Instance,
            messenger,
            index);
        try
        {
            statusBar.FaceIndexText.Should().BeNull();
            statusBar.PauseFaceIndexCommand.CanExecute(null).Should().BeFalse();

            messenger.Send(new FaceIndexStatusMessage(new FaceIndexStatus(FaceIndexState.Running, 3, 10, 40, "CPU")));
            statusBar.FaceIndexText.Should().StartWith("Gesichter: 3 / 10");
            statusBar.FaceIndexShowPause.Should().BeTrue();
            statusBar.BackgroundActivityText.Should().Contain("Gesichter");

            statusBar.PauseFaceIndexCommand.Execute(null);
            index.Pauses.Should().Be(1);
            statusBar.FaceIndexShowPause.Should().BeFalse();
            statusBar.ResumeFaceIndexCommand.CanExecute(null).Should().BeTrue();
            statusBar.ResumeFaceIndexCommand.Execute(null);
            index.Resumes.Should().Be(1);

            statusBar.CancelFaceIndexCommand.Execute(null);
            index.Cancels.Should().Be(1);

            messenger.Send(new FaceIndexStatusMessage(new FaceIndexStatus(FaceIndexState.Idle, 10, 10)));
            statusBar.FaceIndexText.Should().BeNull();
            statusBar.CancelFaceIndexCommand.CanExecute(null).Should().BeFalse();

            messenger.Send(new FaceIndexStatusMessage(new FaceIndexStatus(FaceIndexState.Error, Message: "Modell defekt")));
            statusBar.FaceIndexIsWarning.Should().BeTrue();
            statusBar.FaceIndexToolTip.Should().Be("Modell defekt");
        }
        finally
        {
            statusBar.Dispose();
        }
    }

    private sealed class FakeIndex : IFaceIndexService
    {
        public FaceIndexStatus Status { get; set; } = FaceIndexStatus.Disabled;
        public FaceIndexSettings Settings { get; set; } = FaceIndexSettings.Default;
        public bool IsPaused { get; private set; }
        public List<bool> EnabledCalls { get; } = [];
        public List<bool> GpuCalls { get; } = [];
        public int Pauses { get; private set; }
        public int Resumes { get; private set; }
        public int Cancels { get; private set; }
        public int RunRequests { get; private set; }
        public int Rebuilds { get; private set; }
        public int RebuildResult { get; set; }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            EnabledCalls.Add(enabled);
            return Task.CompletedTask;
        }

        public Task SetUseGpuAsync(bool useGpu, CancellationToken ct = default)
        {
            GpuCalls.Add(useGpu);
            return Task.CompletedTask;
        }

        public void Pause()
        {
            Pauses++;
            IsPaused = true;
        }

        public void Resume()
        {
            Resumes++;
            IsPaused = false;
        }

        public void CancelRun() => Cancels++;

        public void RequestRun() => RunRequests++;

        public Task<int> RebuildAsync(CancellationToken ct = default)
        {
            Rebuilds++;
            return Task.FromResult(RebuildResult);
        }
    }

    private sealed class FakeModels : IFaceModelProvider
    {
        public string ModelFolder => @"C:\Test\models\buffalo_l";
        public bool IsOverridden => false;
        public string DetectorPath => ModelFolder + @"\det_10g.onnx";
        public string RecognizerPath => ModelFolder + @"\w600k_r50.onnx";
        public FaceModelState State { get; private set; } = FaceModelState.Missing;
        public FaceModelDownloadInfo DownloadInfo => FaceModelDownloadInfo.BuffaloL;
        public int Downloads { get; private set; }
        public bool Block { get; set; }
        public Exception? Fail { get; set; }

        public async Task DownloadAsync(IProgress<FaceModelDownloadProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new FaceModelDownloadProgress("Wird heruntergeladen", 1 << 20, 2 << 20));
            if (Block)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (Fail is not null)
            {
                throw Fail;
            }

            Downloads++;
            State = FaceModelState.Ready;
        }
    }

    private sealed class FakeDialogs(bool answer) : IDialogService
    {
        public string? LastMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
        {
            LastMessage = message;
            return Task.FromResult(answer);
        }

        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
            => Task.FromResult(DialogChoice.None);

        public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK") => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public string? PickFolder(string title) => null;
    }
}
