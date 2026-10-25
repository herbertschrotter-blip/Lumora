using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.ModelPackages;
using Lumora.Settings.ModelPackages;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;
using Wpf.Ui.Appearance;

namespace Lumora.Wpf.Tests.Settings;

/// <summary>
/// Einstellungen › Modelle &amp; Daten (ADR-0070, Mockup 19): Liste nach Gruppen mit Zustand, Lizenz-Hinweis und
/// Update-Hinweis; Laden nur nach Bestätigung (Dialog mit Name, Quelle, Größe, Lizenz, Ziel), Abbrechen, Prüfen → defekt,
/// Entfernen mit Rückfrage, Update suchen ohne Quelle, Ollama nur Status; Darstellung hell/dunkel ohne Binding-Fehler.
/// Echter Manager mit Fake-Downloader — kein Netz.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ModelPackagesViewModelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumora-wpf-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Task OnUiAsync(Func<Task> scenario) => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    [Fact]
    public Task Liste_nach_Gruppen_mit_Zustand_Lizenz_und_Update_Hinweis() => OnUiAsync(async () =>
    {
        var (vm, _, _, _) = Create();
        await vm.RefreshAsync();

        vm.IsLoaded.Should().BeTrue();
        vm.ModelItems.Select(i => i.Id).Should().Equal(TestId);
        vm.DataItems.Select(i => i.Id).Should().Equal(ModelPackageCatalog.GeoNamesId);
        vm.ExternalItems.Select(i => i.Id).Should().Equal(ModelPackageCatalog.AssistantId);
        var item = vm.ModelItems.Single();
        item.StateText.Should().Be("nicht geladen");
        item.Tone.Should().Be(ModelPackageTone.Neutral);
        item.CanLoad.Should().BeTrue();
        item.LoadLabel.Should().Be("Laden…");
        item.CanRemove.Should().BeFalse();
        item.CanVerify.Should().BeFalse();
        item.LicenseRestricted.Should().BeTrue();
        item.UpdateHint.Should().Be("Version 2, keine automatische Prüfung.");
        item.VersionText.Should().Be("Version 2");
        item.Folder.Should().StartWith(_root);

        var geo = vm.DataItems.Single();
        geo.StateText.Should().Be("eingebaut");
        geo.CanLoad.Should().BeFalse();
        geo.CanVerify.Should().BeTrue();
        geo.CanCheckUpdate.Should().BeFalse();
        geo.SizeText.Should().EndWith("MB");

        var ollama = vm.ExternalItems.Single();
        ollama.StateText.Should().Be("bereit");
        ollama.Tone.Should().Be(ModelPackageTone.Success);
        ollama.IsExternal.Should().BeTrue();
        (ollama.CanLoad || ollama.CanRemove).Should().BeFalse("Ollama verwaltet sein Modell selbst");
        vm.Dispose();
    });

    [Fact]
    public Task Laden_nur_nach_Bestaetigung_mit_Name_Quelle_Groesse_Lizenz_und_Ziel() => OnUiAsync(async () =>
    {
        var (vm, downloader, dialogs, _) = Create();
        await vm.RefreshAsync();
        var item = vm.ModelItems.Single();

        dialogs.ConfirmAnswer = false;
        await item.LoadCommand.ExecuteAsync(null);
        downloader.Requests.Should().Be(0, "abgelehnt → nichts geladen");
        dialogs.Titles.Should().Equal("Testmodell laden?");
        dialogs.LastMessage.Should().Contain("Name: Test-Modell (Version 2)").And.Contain("Quelle: Hersteller — geladen von example.invalid")
            .And.Contain("Größe: 1 MB").And.Contain("Lizenz: nur nicht-kommerziell").And.Contain("Ziel: " + item.Folder).And.Contain("SHA-256");

        dialogs.ConfirmAnswer = true;
        await item.LoadCommand.ExecuteAsync(null);

        downloader.Requests.Should().Be(1);
        item.StateText.Should().Be("geladen");
        item.Tone.Should().Be(ModelPackageTone.Success);
        item.CheckedText.Should().Contain("SHA-256 stimmt");
        item.Message.Should().Be("Geladen und geprüft (SHA-256).");
        item.MessageIsError.Should().BeFalse();
        item.IsDownloading.Should().BeFalse();
        item.CanLoad.Should().BeFalse();
        item.CanRemove.Should().BeTrue();
        vm.Dispose();
    });

    [Fact]
    public Task Abbrechen_waehrend_des_Ladens() => OnUiAsync(async () =>
    {
        var (vm, downloader, dialogs, _) = Create();
        await vm.RefreshAsync();
        var item = vm.ModelItems.Single();
        downloader.Block = true;
        dialogs.ConfirmAnswer = true;

        var running = item.LoadCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => item.IsDownloading && downloader.Started);
        item.CancelCommand.CanExecute(null).Should().BeTrue();
        item.CancelCommand.Execute(null);
        await running;

        item.Message.Should().Be("Abgebrochen — angefangene Dateien wurden verworfen.");
        item.IsDownloading.Should().BeFalse();
        item.StateText.Should().Be("nicht geladen");
        Directory.EnumerateFiles(_root, "*.download", SearchOption.AllDirectories).Should().BeEmpty();
        vm.Dispose();
    });

    [Fact]
    public Task Pruefen_meldet_defekt_dann_Neu_laden_Entfernen_mit_Rueckfrage_und_Update_suchen() => OnUiAsync(async () =>
    {
        var (vm, _, dialogs, _) = Create();
        await vm.RefreshAsync();
        var item = vm.ModelItems.Single();
        dialogs.ConfirmAnswer = true;
        await item.LoadCommand.ExecuteAsync(null);

        var file = Directory.EnumerateFiles(item.Folder!, "model.onnx").Single();
        var bytes = File.ReadAllBytes(file);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(file, bytes);
        await item.VerifyCommand.ExecuteAsync(null);
        item.MessageIsError.Should().BeTrue();
        item.Message.Should().Contain("model.onnx");
        item.StateText.Should().Be("defekt");
        item.Tone.Should().Be(ModelPackageTone.Critical);
        item.LoadLabel.Should().Be("Neu laden…");
        item.CanLoad.Should().BeTrue();

        await item.CheckUpdateCommand.ExecuteAsync(null);
        item.Message.Should().Be("Version 2, keine automatische Prüfung.");

        dialogs.ConfirmAnswer = false;
        await item.RemoveCommand.ExecuteAsync(null);
        File.Exists(file).Should().BeTrue("abgelehnt");
        dialogs.ConfirmAnswer = true;
        await item.RemoveCommand.ExecuteAsync(null);
        File.Exists(file).Should().BeFalse();
        item.StateText.Should().Be("nicht geladen");
        vm.Dispose();
    });

    [Fact]
    public Task Assistent_oeffnet_seine_Einstellungen() => OnUiAsync(async () =>
    {
        var (vm, _, _, navigation) = Create();
        await vm.RefreshAsync();
        vm.ExternalItems.Single().OpenAssistantSettingsCommand.Execute(null);
        navigation.Tags.Should().Equal(NavigationTags.SettingsAssistant);
        vm.Dispose();
    });

    [Fact]
    public Task Darstellung_hell_und_dunkel_ohne_Binding_Fehler() => OnUiAsync(async () =>
    {
        var (vm, _, _, _) = Create();
        await vm.RefreshAsync(); // nie synchron auf dem UI-Thread warten — die Zustände kommen über Task.Run zurück
        vm.ModelItems.Single().ShowMessage("Stimmt nicht: model.onnx.", error: true);
        using var errors = new BindingErrors();
        var section = new SettingsModelPackagesSection { DataContext = vm };
        var window = new Window { Content = section, Width = 1000, Height = 900, ShowActivated = false };
        window.Show();
        try
        {
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme);
                window.UpdateLayout();
                section.ModelList.ActualHeight.Should().BeGreaterThan(100);
                section.DataList.ActualHeight.Should().BeGreaterThan(50);
            }

            await WaitUntilAsync(() => vm.IsLoaded); // Loaded der Ansicht stößt noch einmal RefreshAsync an
            errors.Messages.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            vm.Dispose();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }
    });

    [Fact]
    public void Dialogtext_nennt_Archiv_Hinweis_und_Groesse_auf_der_Platte()
    {
        var manager = new ModelPackageManager([new FaceModelPackageSource(new FixedFaceProvider(Path.Combine(_root, "buffalo_l")))], new NoMetadata());
        var text = ModelDownloadDialog.MessageOf(manager.DescribeDownload(ModelPackageCatalog.FacesId));
        text.Should().Contain("InsightFace buffalo_l (Version 0.7)").And.Contain("github.com").And.Contain("275 MB")
            .And.Contain("danach 182 MB auf der Platte").And.Contain("nicht-kommerzielle").And.Contain("det_10g.onnx");
        ModelDownloadDialog.SizeText(1_929_000_000).Should().EndWith(" GB");
    }

    // ---- Hilfen ---------------------------------------------------------------------------------

    internal const string TestId = "test.wpf";

    private (ModelPackagesViewModel Vm, BlockingDownloader Downloader, RecordingDialogs Dialogs, RecordingNavigationService Navigation) Create()
    {
        var content = new byte[1 << 20];
        new Random(5).NextBytes(content);
        var definition = new ModelPackageDefinition(
            TestId, "Testmodell", "Test-Modell", "Zum Testen.", ModelPackageGroup.Model, "2",
            new ModelPackageOrigin("Hersteller", new Uri("https://example.invalid/")), "nur nicht-kommerziell", LicenseRestricted: true,
            DownloadSize: content.Length, FolderName: "test",
            Files: [new ModelPackageFile("model.onnx", content.Length, Convert.ToHexStringLower(SHA256.HashData(content)))]);
        var downloader = new BlockingDownloader(content);
        var sources = new IModelPackageSource[]
        {
            new FileSetModelPackageSource(definition, new Uri("https://example.invalid/m/"), new ModelStorage(_root), downloader, messenger: new WeakReferenceMessenger()),
            new GeoNamesPackageSource(),
            new OllamaPackageSource(() => new ReadyRuntime()),
        };
        var manager = new ModelPackageManager(sources, new NoMetadata());
        var dialogs = new RecordingDialogs();
        var navigation = new RecordingNavigationService();
        return (new ModelPackagesViewModel(manager, dialogs, navigation: navigation), downloader, dialogs, navigation);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Bedingung nicht erreicht");
            }

            await Task.Delay(20);
        }
    }

    internal sealed class BlockingDownloader(byte[] content) : IModelDownloader
    {
        public int Requests { get; private set; }

        public bool Block { get; set; }

        public volatile bool Started;

        public async Task DownloadAsync(Uri source, string target, long expectedSize, IProgress<long>? progress, CancellationToken ct)
        {
            Requests++;
            await File.WriteAllBytesAsync(target, content[..1000], ct);
            Started = true;
            if (Block)
            {
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
            }

            await File.WriteAllBytesAsync(target, content, ct);
            progress?.Report(content.Length);
        }
    }

    /// <summary>Eintrag, dessen Laden eine Funktion ist (Gesichter/Motive-Tests: Anbieter-Fake statt Dateien).</summary>
    internal sealed class DelegatingSource(ModelPackageDefinition definition, Func<IProgress<ModelPackageProgress>?, CancellationToken, Task> download) : IModelPackageSource
    {
        public ModelPackageDefinition Definition => definition;

        public ModelPackageCapabilities Capabilities => ModelPackageCapabilities.Download;

        public string? Folder => @"C:\Test\models";

        public bool IsOverridden => false;

        public Uri? DownloadUri => new("https://example.invalid/");

        public int Downloads { get; private set; }

        public Task<ModelPackageStatus> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new ModelPackageStatus(Downloads > 0 ? ModelPackageState.Installed : ModelPackageState.NotInstalled, "x"));

        public async Task DownloadAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct)
        {
            await download(progress, ct);
            Downloads++;
        }

        public Task<ModelVerifyResult> VerifyAsync(IProgress<ModelPackageProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task RemoveAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class NoMetadata : IModelMetadataClient
    {
        public Task<string> GetStringAsync(Uri uri, CancellationToken ct) => throw new InvalidOperationException("kein Netz im Test");
    }

    private sealed class ReadyRuntime : Lumora.Services.Assistant.IAssistantRuntime
    {
        public Task<Lumora.Services.Assistant.AssistantRuntimeStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new Lumora.Services.Assistant.AssistantRuntimeStatus(Lumora.Services.Assistant.AssistantRuntimeState.Ready, "qwen2.5:3b", "0.12.0"));

        public Task PullModelAsync(string model, IProgress<Lumora.Services.Assistant.OllamaPullProgress>? progress, CancellationToken ct) => throw new InvalidOperationException();

        public bool TryStartOllama() => false;
    }

    private sealed class FixedFaceProvider(string folder) : Lumora.Services.Faces.Models.IFaceModelProvider
    {
        public string ModelFolder => folder;

        public bool IsOverridden => false;

        public string DetectorPath => Path.Combine(folder, "det_10g.onnx");

        public string RecognizerPath => Path.Combine(folder, "w600k_r50.onnx");

        public Lumora.Services.Faces.Models.FaceModelState State => Lumora.Services.Faces.Models.FaceModelState.Missing;

        public Lumora.Services.Faces.Models.FaceModelDownloadInfo DownloadInfo => Lumora.Services.Faces.Models.FaceModelDownloadInfo.BuffaloL;

        public Task DownloadAsync(IProgress<Lumora.Services.Faces.Models.FaceModelDownloadProgress>? progress, CancellationToken ct) => throw new InvalidOperationException();
    }

    internal sealed class RecordingDialogs : IDialogService
    {
        public bool ConfirmAnswer { get; set; }

        public List<string> Titles { get; } = [];

        public string? LastMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
        {
            Titles.Add(title);
            LastMessage = message;
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
            => Task.FromResult(DialogChoice.None);

        public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK") => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public string? PickFolder(string title) => null;
    }

    private sealed class BindingErrors : TraceListener
    {
        public BindingErrors()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }

        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
