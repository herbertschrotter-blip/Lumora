using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Services.Watch;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Watch;

/// <summary>
/// Ordnerüberwachung mit echtem FileSystemWatcher auf dem Temp-Ordner der <see cref="TempLibraryFixture"/> und einem
/// Scan-Fake. Ruhezeit 1 s (Minimum); alle Waits mit Timeout.
/// </summary>
public sealed partial class LibraryWatchServiceTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoEventWindow = TimeSpan.FromSeconds(2.5);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<WatchStatus> _statuses = new();

        public TempLibraryFixture Fixture { get; private set; } = null!;
        public int LibraryId { get; private set; }
        public FakeScanService Scans { get; private set; } = null!;
        public WatchSettingsStore Store { get; private set; } = null!;
        public LibraryWatchService Service { get; private set; } = null!;

        /// <summary>Gemeinsame Scan-Sperre wie in der App (ScanService-Singleton und Ordnerüberwachung).</summary>
        public ScanGate Gate { get; } = new();

        /// <summary>Gemeinsame Liste eigener Pfade wie in der App (FileOperationService und Ordnerüberwachung).</summary>
        public OwnChangeRegistry OwnChanges { get; } = new();

        public IReadOnlyList<WatchStatus> Statuses
        {
            get
            {
                lock (_gate)
                {
                    return _statuses.ToList();
                }
            }
        }

        public static async Task<Harness> CreateAsync(bool start = true, bool enabled = true, Action<FakeScanService>? configureScans = null)
        {
            var h = new Harness();
            h.Fixture = await TempLibraryFixture.CreateAsync();
            var library = await h.Fixture.CreateLibraryAsync();
            h.LibraryId = library.Id;
            h.Scans = new FakeScanService(h.Fixture.Messenger) { ScanGate = h.Gate };
            configureScans?.Invoke(h.Scans);
            h.Store = new WatchSettingsStore(h.Fixture.ContextFactory);
            await h.Store.SaveAsync(new WatchSettings(enabled, Quiet));
            h.Fixture.Messenger.Register<Harness, WatchStatusChangedMessage>(h, static (r, m) => r.Record(m.Status));
            h.Service = h.CreateService();
            if (start)
            {
                await h.Service.StartAsync();
            }

            return h;
        }

        public LibraryWatchService CreateService() => new(
            new LibraryService(Fixture.ContextFactory, Fixture.Paths, NullLogger<LibraryService>.Instance),
            Scans,
            Store,
            Fixture.Formats,
            Fixture.Paths,
            NullLogger<LibraryWatchService>.Instance,
            Fixture.Messenger,
            rootRetryInterval: TimeSpan.FromMilliseconds(400),
            scanGate: Gate,
            ownChanges: OwnChanges,
            exclusions: new Lumora.Services.Scanning.Exclusions.ScanExclusionStore(Fixture.ContextFactory, Fixture.Messenger)); // ADR-0066 wie in der App

        /// <summary>FileOperationService wie in der App: dieselbe Scan-Sperre und dieselbe Liste eigener Pfade.</summary>
        public FileOperationService CreateFileOperations() => Fixture.CreateFileOperationService(scanGate: Gate, ownChanges: OwnChanges);

        public string WriteFile(string relativePath) => Fixture.WriteLibraryFile(relativePath);

        public async Task WaitForScanStartAsync(string what)
        {
            (await Scans.Started.WaitAsync(EventTimeout)).Should().BeTrue($"ein Scan sollte starten: {what}");
        }

        public async Task WaitForStateAsync(WatchState state, string what)
        {
            await WaitUntilAsync(() => Service.Status.State == state, $"{what} (Zustand {state}, ist {Service.Status.State})");
        }

        public static async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + EventTimeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new Xunit.Sdk.XunitException($"Timeout nach {EventTimeout.TotalSeconds:N0} s: {what}");
                }

                await Task.Delay(50);
            }
        }

        private void Record(WatchStatus status)
        {
            lock (_gate)
            {
                _statuses.Add(status);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync();
            Service.Dispose();
            Fixture.Dispose();
        }
    }

    [Fact]
    public async Task Start_reads_settings_and_watches_the_active_library()
    {
        await using var h = await Harness.CreateAsync();

        h.Service.IsEnabled.Should().BeTrue();
        h.Service.QuietPeriod.Should().Be(Quiet);
        h.Service.Status.Should().BeEquivalentTo(new WatchStatus(h.LibraryId, WatchState.Idle));
    }

    [Fact]
    public async Task Several_events_within_the_quiet_period_trigger_exactly_one_incremental_scan()
    {
        await using var h = await Harness.CreateAsync();

        for (var i = 0; i < 5; i++)
        {
            h.WriteFile($"2024\\IMG_{i:D4}.jpg");
            await Task.Delay(60);
        }

        await h.WaitForScanStartAsync("nach fünf Dateianlagen");
        await h.WaitForStateAsync(WatchState.Idle, "Scan fertig");
        await Task.Delay(NoEventWindow);

        h.Scans.Calls.Should().Be(1, "alle Ereignisse liegen innerhalb der Ruhezeit");
        h.Scans.Modes.Should().AllBeEquivalentTo(ScanMode.Incremental);
        var states = h.Statuses.Select(s => s.State).ToList();
        states.Should().Contain(WatchState.ChangesPending).And.Contain(WatchState.Scanning);
        h.Statuses.First(s => s.State == WatchState.ChangesPending).ScanDueAtUtc.Should().NotBeNull();
        h.Statuses.Last().Should().Match<WatchStatus>(s => s.State == WatchState.Idle && s.LastAutoScan != null && s.LastAutoScan.Added == 1);
    }

    [Fact]
    public async Task File_creation_and_deletion_each_trigger_a_scan()
    {
        await using var h = await Harness.CreateAsync();

        var path = h.WriteFile("neu\\IMG_0001.jpg");
        await h.WaitForScanStartAsync("nach Anlegen");
        await h.WaitForStateAsync(WatchState.Idle, "erster Scan fertig");
        h.Scans.Calls.Should().Be(1);

        File.Delete(path);
        await h.WaitForScanStartAsync("nach Löschen");
        await h.WaitForStateAsync(WatchState.Idle, "zweiter Scan fertig");
        h.Scans.Calls.Should().Be(2);
    }

    [Fact]
    public async Task No_second_scan_starts_while_the_own_scan_is_running_but_afterwards()
    {
        await using var h = await Harness.CreateAsync(configureScans: s => s.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        h.WriteFile("a\\IMG_0001.jpg");
        await h.WaitForScanStartAsync("erster Scan");
        h.Service.Status.State.Should().Be(WatchState.Scanning);

        h.WriteFile("a\\IMG_0002.jpg"); // während des Scans
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(1, "kein Doppelstart während eines laufenden Scans");
        h.Service.Status.State.Should().Be(WatchState.Scanning);

        var gate = h.Scans.Gate!;
        h.Scans.Gate = null;
        gate.SetResult();

        await h.WaitForScanStartAsync("gesammelte Ereignisse nach dem Scan erneut geprüft");
        await h.WaitForStateAsync(WatchState.Idle, "zweiter Scan fertig");
        h.Scans.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Manual_scan_in_progress_defers_the_automatic_scan_until_it_is_finished()
    {
        await using var h = await Harness.CreateAsync();

        h.Fixture.Messenger.Send(new ScanProgressMessage(h.LibraryId, new ScanProgress(0, 0, 0, null)));
        h.WriteFile("b\\IMG_0001.jpg");
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0, "ein manueller Scan läuft");
        h.Service.Status.State.Should().Be(WatchState.Idle, "kein Countdown während eines fremden Scans");

        var manual = new ScanResult(h.LibraryId, h.Fixture.LibraryRoot, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) { Mode = ScanMode.Incremental };
        h.Fixture.Messenger.Send(new ScanCompletedMessage(h.LibraryId, manual));

        await h.WaitForScanStartAsync("nach Ende des manuellen Scans");
        await h.WaitForStateAsync(WatchState.Idle, "automatischer Scan fertig");
        h.Scans.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Temp_thumbnail_and_non_media_files_are_ignored()
    {
        await using var h = await Harness.CreateAsync();

        // Direkt in der Wurzel: ein neu angelegter Unterordner wäre selbst ein relevantes Ereignis (Ordner-Created).
        h.WriteFile("IMG_0001.jpg.tmp");
        h.WriteFile("~IMG_0002.jpg");
        h.WriteFile("Thumbs.db");
        h.WriteFile("notizen.txt");
        h.WriteFile("download.part");
        h.WriteFile(".thumbnails\\IMG_0003.jpg"); // Ordner mit Punkt-Präfix: ignoriert samt Inhalt
        await Task.Delay(NoEventWindow);

        h.Scans.Calls.Should().Be(0);
        h.Statuses.Should().NotContain(s => s.State == WatchState.ChangesPending);
        h.Service.Status.State.Should().Be(WatchState.Idle);
    }

    [Fact]
    public async Task Stop_ends_the_watch_and_later_changes_do_nothing()
    {
        await using var h = await Harness.CreateAsync();

        await h.Service.StopAsync();
        h.Service.Status.State.Should().Be(WatchState.Disabled);

        h.WriteFile("d\\IMG_0001.jpg");
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Disabling_via_setting_stops_and_persists_enabling_resumes_immediately()
    {
        await using var h = await Harness.CreateAsync();

        await h.Service.SetEnabledAsync(false);
        h.Service.Status.State.Should().Be(WatchState.Disabled);
        (await h.Store.LoadAsync()).Enabled.Should().BeFalse();

        h.WriteFile("e\\IMG_0001.jpg");
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0);

        await h.Service.SetEnabledAsync(true);
        h.Service.Status.State.Should().Be(WatchState.Idle);
        (await h.Store.LoadAsync()).Enabled.Should().BeTrue();

        h.WriteFile("e\\IMG_0002.jpg");
        await h.WaitForScanStartAsync("nach Wiedereinschalten");
        await h.WaitForStateAsync(WatchState.Idle, "Scan fertig");
        h.Scans.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Setting_persisted_as_disabled_is_honoured_on_start()
    {
        await using var h = await Harness.CreateAsync(enabled: false);

        h.Service.IsEnabled.Should().BeFalse();
        h.Service.Status.State.Should().Be(WatchState.Disabled);

        h.WriteFile("f\\IMG_0001.jpg");
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Quiet_period_setting_is_clamped_persisted_and_applied()
    {
        await using var h = await Harness.CreateAsync();

        await h.Service.SetQuietPeriodAsync(TimeSpan.FromSeconds(120));
        h.Service.QuietPeriod.Should().Be(TimeSpan.FromSeconds(WatchSettings.MaxQuietSeconds));
        (await h.Store.LoadAsync()).QuietPeriod.Should().Be(TimeSpan.FromSeconds(WatchSettings.MaxQuietSeconds));

        await h.Service.SetQuietPeriodAsync(TimeSpan.Zero);
        h.Service.QuietPeriod.Should().Be(TimeSpan.FromSeconds(WatchSettings.MinQuietSeconds));

        h.WriteFile("g\\IMG_0001.jpg");
        await h.WaitForScanStartAsync("mit Ruhezeit 1 s");
    }

    [Fact]
    public async Task Mass_missing_blocks_the_automatic_retry_until_a_manual_scan_succeeds()
    {
        await using var h = await Harness.CreateAsync(configureScans: s =>
        {
            s.Outcome = ScanOutcome.MassMissingBlocked;
            s.MissingDetected = 12_000;
        });

        h.WriteFile("h\\IMG_0001.jpg");
        await h.WaitForScanStartAsync("erster automatischer Scan");
        await h.WaitForStateAsync(WatchState.Blocked, "Mass-Missing-Guard");
        h.Service.Status.Reason.Should().Contain(12_000.ToString("N0")).And.Contain("Bestätigung");

        h.WriteFile("h\\IMG_0002.jpg");
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(1, "kein automatischer Wiederholungs-Scan nach MassMissingBlocked");
        h.Service.Status.State.Should().Be(WatchState.Blocked);

        // Benutzer bestätigt auf der Bibliotheksseite → manueller Scan endet Completed → Sperre weg
        h.Scans.Outcome = ScanOutcome.Completed;
        var manual = new ScanResult(h.LibraryId, h.Fixture.LibraryRoot, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) { Mode = ScanMode.Incremental };
        h.Fixture.Messenger.Send(new ScanCompletedMessage(h.LibraryId, manual));
        h.Service.Status.State.Should().Be(WatchState.Idle);

        h.WriteFile("h\\IMG_0003.jpg");
        await h.WaitForScanStartAsync("nach Aufhebung der Sperre");
        await h.WaitForStateAsync(WatchState.Idle, "Scan fertig");
        h.Scans.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Library_offline_blocks_the_automatic_retry()
    {
        await using var h = await Harness.CreateAsync(configureScans: s => s.Outcome = ScanOutcome.LibraryOffline);

        h.WriteFile("i\\IMG_0001.jpg");
        await h.WaitForScanStartAsync("automatischer Scan");
        await h.WaitForStateAsync(WatchState.Blocked, "Bibliothek offline");
        h.Service.Status.Reason.Should().Contain("offline");

        h.WriteFile("i\\IMG_0002.jpg");
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Unavailable_root_is_retried_and_a_scan_is_scheduled_when_it_returns()
    {
        await using var h = await Harness.CreateAsync(start: false);
        Directory.Delete(h.Fixture.LibraryRoot, recursive: true);

        await h.Service.StartAsync();
        h.Service.Status.State.Should().Be(WatchState.RootUnavailable);
        h.Service.Status.Reason.Should().Contain(h.Fixture.LibraryRoot);

        Directory.CreateDirectory(h.Fixture.LibraryRoot);
        await h.WaitForScanStartAsync("Wurzel wieder erreichbar → Scan nach Ruhezeit");
        await h.WaitForStateAsync(WatchState.Idle, "Scan fertig");
        h.Scans.Calls.Should().Be(1);

        h.WriteFile("j\\IMG_0001.jpg");
        await h.WaitForScanStartAsync("Watcher läuft nach der Wiederherstellung");
    }

    [Fact]
    public async Task Switching_the_active_library_moves_the_watch()
    {
        await using var h = await Harness.CreateAsync();
        var otherRoot = Path.Combine(h.Fixture.OutsideDir, "other-lib");
        Directory.CreateDirectory(otherRoot);
        var libraries = new LibraryService(h.Fixture.ContextFactory, h.Fixture.Paths, NullLogger<LibraryService>.Instance);
        var other = await libraries.CreateAsync("Andere", otherRoot);
        await libraries.SetActiveAsync(other.Id);
        // LibraryService sendet auf WeakReferenceMessenger.Default; der Test-Messenger der Fixture bekommt die Nachricht hier
        h.Fixture.Messenger.Send(new ActiveLibraryChangedMessage(other.Id));

        await Harness.WaitUntilAsync(() => h.Service.Status.LibraryId == other.Id, "Wechsel auf die andere Bibliothek");

        h.WriteFile("k\\IMG_0001.jpg"); // alte Wurzel: nicht mehr überwacht
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0);

        TempLibraryFixture.WriteFile(Path.Combine(otherRoot, "IMG_0002.jpg"));
        await h.WaitForScanStartAsync("neue Wurzel wird überwacht");
    }

    [Fact]
    public async Task Settings_store_defaults_and_roundtrip()
    {
        using var fixture = await TempLibraryFixture.CreateAsync();
        var store = new WatchSettingsStore(fixture.ContextFactory);

        (await store.LoadAsync()).Should().Be(WatchSettings.Default);

        await store.SaveAsync(new WatchSettings(false, TimeSpan.FromSeconds(7)));
        (await store.LoadAsync()).Should().Be(new WatchSettings(false, TimeSpan.FromSeconds(7)));

        await store.SaveAsync(new WatchSettings(true, TimeSpan.FromSeconds(999)));
        (await store.LoadAsync()).Should().Be(new WatchSettings(true, TimeSpan.FromSeconds(60)));
    }
}
