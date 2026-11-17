using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Lightbox;
using Lumora.Services.Cloud;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Cloud;

/// <summary>
/// Kriterium 5 (ADR-0077): Lightbox bei einem Platzhalter — zuerst die 1024-px-Vorschau mit „Wird von OneDrive geladen …“,
/// dann das Original (Download durch Windows) mit Abbrechen; ohne Internet eine Meldung, nie „fehlt“; danach melden sich die
/// Analysen. Fakes für Shell und Laden; das „Original“ ist ein kleines JPEG im Temp-Ordner.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class LightboxCloudTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Zeigt_die_Vorschau_mit_Hinweis_dann_das_Original_und_meldet_lokal()
    {
        WithFile(path => TestUiThread.Run(() =>
        {
            var cloud = new CloudUiTests.FakeCloudFiles();
            cloud.States[path] = new CloudFileState(true, true, false, false);
            var preview = new CloudUiTests.FakePreviewSource { Preview = TestImages.LeftRedRightBlue(64, 48) };
            var hydrator = new GatedHydrator();
            using var vm = Create(cloud, hydrator, preview);

            vm.Load([Entry(path)], 0);
            PumpUntil(() => vm.ImageSource is not null && vm.IsCloudLoading);

            vm.ImageSource!.PixelWidth.Should().Be(64, "zuerst die Vorschau von OneDrive");
            vm.CloudNotice.Should().StartWith(LightboxViewModel.CloudLoadingText);
            vm.HasCloudNotice.Should().BeTrue();
            preview.Requests.Should().Equal((path, LightboxViewModel.CloudPreviewBox));
            vm.IsCurrentCloudFile.Should().BeTrue();

            hydrator.Progress?.Report(0.5);
            PumpUntil(() => vm.CloudNotice?.Contains("50", StringComparison.Ordinal) == true);
            cloud.States[path] = new CloudFileState(true, false, false, false); // Windows hat geladen
            hydrator.Complete();
            PumpUntil(() => vm.ImageSource is { PixelWidth: 200 } && !vm.IsLoading);

            vm.CloudNotice.Should().BeNull();
            vm.IsCloudLoading.Should().BeFalse();
            vm.ErrorMessage.Should().BeNull();
            cloud.BecameLocal.Should().ContainSingle().Which.FileId.Should().Be(1);
            return 0;
        }, Timeout));
    }

    [Fact]
    public void Abbrechen_laesst_die_Vorschau_stehen_und_bietet_erneut_laden()
    {
        WithFile(path => TestUiThread.Run(() =>
        {
            var cloud = new CloudUiTests.FakeCloudFiles();
            cloud.States[path] = new CloudFileState(true, true, false, false);
            var hydrator = new GatedHydrator();
            using var vm = Create(cloud, hydrator, new CloudUiTests.FakePreviewSource { Preview = TestImages.LeftRedRightBlue(64, 48) });
            vm.Load([Entry(path)], 0);
            PumpUntil(() => vm.IsCloudLoading && hydrator.Started == 1);

            vm.CancelCloudLoadCommand.Execute(null);
            PumpUntil(() => !vm.IsCloudLoading);

            vm.CloudNotice.Should().Be(LightboxViewModel.CloudCancelledText);
            vm.CanRetryCloudLoad.Should().BeTrue();
            vm.ImageSource!.PixelWidth.Should().Be(64, "die Vorschau bleibt");
            hydrator.WasCancelled.Should().BeTrue();

            var retry = vm.RetryCloudLoadCommand.ExecuteAsync(null);
            PumpUntil(() => hydrator.Started == 2 && vm.IsCloudLoading);
            cloud.States[path] = new CloudFileState(true, false, false, false);
            hydrator.Complete();
            PumpUntil(() => retry.IsCompleted && vm.ImageSource is { PixelWidth: 200 });
            vm.CloudNotice.Should().BeNull();
            return 0;
        }, Timeout));
    }

    [Fact]
    public void Ohne_Internet_eine_Meldung_nie_fehlt()
    {
        WithFile(path => TestUiThread.Run(() =>
        {
            var cloud = new CloudUiTests.FakeCloudFiles();
            cloud.States[path] = new CloudFileState(true, true, false, false);
            var hydrator = new GatedHydrator();
            using var vm = Create(cloud, hydrator, new CloudUiTests.FakePreviewSource()); // auch keine Vorschau (offline)
            vm.Load([Entry(path)], 0);
            PumpUntil(() => hydrator.Started == 1);

            hydrator.Fail(new CloudFileUnavailableException("keine Verbindung"));
            PumpUntil(() => !vm.IsCloudLoading);

            vm.CloudNotice.Should().Be(LightboxViewModel.CloudOfflineNoPreviewText);
            vm.CloudNotice.Should().NotContain("fehlt");
            vm.ErrorMessage.Should().BeNull("ein Platzhalter ist nie „fehlt“ oder kaputt");
            vm.CanRetryCloudLoad.Should().BeTrue();
            cloud.BecameLocal.Should().BeEmpty();
            return 0;
        }, Timeout));
    }

    [Fact]
    public void Lokale_Datei_laedt_wie_bisher_ohne_Hinweis()
    {
        WithFile(path => TestUiThread.Run(() =>
        {
            var cloud = new CloudUiTests.FakeCloudFiles();
            var hydrator = new GatedHydrator();
            using var vm = Create(cloud, hydrator, new CloudUiTests.FakePreviewSource());
            vm.Load([Entry(path)], 0);
            PumpUntil(() => vm.ImageSource is { PixelWidth: 200 } && !vm.IsLoading);

            hydrator.Started.Should().Be(0);
            vm.HasCloudNotice.Should().BeFalse();
            vm.IsCurrentCloudFile.Should().BeFalse();
            return 0;
        }, Timeout));
    }

    private static LightboxViewModel Create(ICloudFileService cloud, ICloudFileHydrator hydrator, Lumora.Thumbnails.Cloud.ICloudPreviewSource preview)
        => new(new LibVlcHost(NullLogger<LibVlcHost>.Instance), contextFactory: null!, NullLogger<LightboxViewModel>.Instance,
            cloudFiles: cloud, cloudHydrator: hydrator, cloudPreview: preview);

    private static LightboxEntry Entry(string path) => new(1, path, Path.GetFileName(path), MediaType.Image, false, null, 1000, DateTime.UtcNow);

    private static void WithFile(Action<string> test)
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var path = Path.Combine(dir, "online.jpg");
            TestImages.SaveJpeg(path, TestImages.LeftRedRightBlue(200, 150));
            test(path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException("Bedingung nicht erreicht");
            }

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    /// <summary>Laden des Originals auf Zuruf: fertig, gescheitert oder abgebrochen.</summary>
    private sealed class GatedHydrator : ICloudFileHydrator
    {
        private TaskCompletionSource _current = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public IProgress<double>? Progress { get; private set; }

        public bool WasCancelled { get; private set; }

        public async Task HydrateAsync(string path, IProgress<double>? progress, CancellationToken ct)
        {
            Progress = progress;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _current = done;
            Interlocked.Increment(ref _started);
            using var registration = ct.Register(() =>
            {
                WasCancelled = true;
                done.TrySetCanceled(ct);
            });
            await done.Task;
        }

        public void Complete() => _current.TrySetResult();

        public void Fail(Exception ex) => _current.TrySetException(ex);
    }
}
