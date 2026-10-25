using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Services.Usage;
using Lumora.Shell;
using Lumora.Stories;
using Lumora.Usage;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Usage;

/// <summary>
/// Nutzungs-Signale in der Oberfläche (ADR-0072): Lightbox misst nur aktiv (Fenster vorne), Herkunft Story/Doppelte, Zoom,
/// Zeile „Nutzung“; Einstellungen „Nutzung lernen“ + Löschen mit Rückfrage; Grund im Tooltip der Highlights; Galerie-Filter
/// „Lieblinge“; Karte hell/dunkel ohne Binding-Fehler.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class UsageUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Lightbox_misst_nur_solange_das_Fenster_vorne_ist()
    {
        TestUiThread.Run(() =>
        {
            var signals = new FakeSignals();
            using var vm = CreateLightbox(signals);
            vm.Load([Entry(1), Entry(2), Entry(3)], 0);

            signals.Clock.Advance(TimeSpan.FromSeconds(30)); // Fenster noch nicht aktiviert → zählt nicht
            vm.SetUsageActive(true);
            signals.Clock.Advance(TimeSpan.FromSeconds(4));
            vm.NextCommand.Execute(null);
            signals.Views.Should().ContainSingle().Which.Should().Be((1, TimeSpan.FromSeconds(4), false, UsageViewContext.Normal));

            signals.Clock.Advance(TimeSpan.FromSeconds(2));
            vm.SetUsageActive(false); // anderes Fenster vorne
            signals.Clock.Advance(TimeSpan.FromMinutes(5));
            vm.SetUsageActive(true);
            signals.Clock.Advance(TimeSpan.FromSeconds(1));
            vm.IsZoomFit = false; // hineingezoomt
            vm.NextCommand.Execute(null);
            signals.Views[1].Should().Be((2, TimeSpan.FromSeconds(3), true, UsageViewContext.Normal));

            signals.Clock.Advance(TimeSpan.FromMilliseconds(300)); // Schnelllauf
            vm.PreviousCommand.Execute(null);
            signals.Views.Should().HaveCount(2, "unter einer Sekunde zählt nicht");
            return 0;
        }, Timeout);
    }

    [Fact]
    public void Lightbox_aus_Story_gedaempft_aus_Doppelte_gar_nicht_letzte_Ansicht_beim_Schliessen()
    {
        TestUiThread.Run(() =>
        {
            var signals = new FakeSignals();
            var vm = CreateLightbox(signals);
            vm.UsageContext = UsageViewContext.None;
            vm.Load([Entry(1), Entry(2)], 0);
            vm.SetUsageActive(true);
            signals.Clock.Advance(TimeSpan.FromSeconds(20));
            vm.NextCommand.Execute(null);
            signals.Views.Should().BeEmpty("Seite „Doppelte“ zählt nicht");

            vm.UsageContext = UsageViewContext.Story;
            vm.Load([Entry(3)], 0);
            signals.Clock.Advance(TimeSpan.FromSeconds(7));
            vm.Dispose();
            signals.Views.Should().ContainSingle().Which.Should().Be((3, TimeSpan.FromSeconds(7), false, UsageViewContext.Story));
            return 0;
        }, Timeout);
    }

    [Fact]
    public void Lightbox_Zeile_Nutzung_zeigt_was_gelernt_wurde()
    {
        TestUiThread.Run(() =>
        {
            var service = new FakeUsageService { Totals = new UsageTotals(9, 240, 2, 0, 0, 1, 0, 6, 0) };
            using var vm = new LightboxViewModel(new LibVlcHost(NullLogger<LibVlcHost>.Instance), contextFactory: null!, NullLogger<LightboxViewModel>.Instance, usageService: service);
            vm.Load([Entry(5, MediaType.Archive)], 0);
            PumpUntil(() => vm.FileInfoRows.Any(r => r.Label == "Nutzung"));
            vm.FileInfoRows.Single(r => r.Label == "Nutzung").Value.Should().Be("9× angesehen an 6 Tagen · 4 min · 2× gezoomt · 1× verwendet");
            return 0;
        }, Timeout);
    }

    [Fact]
    public void Einstellungen_Nutzung_lernen_speichern_und_loeschen_mit_Rueckfrage()
    {
        TestUiThread.Run(() =>
        {
            var service = new FakeUsageService { Summary = new UsageSummary(12, 3, 2, new DateOnly(2026, 9, 12)) };
            var dialogs = new FakeDialogs(answer: false);
            var vm = new UsageSettingsViewModel(service, NullLogger<UsageSettingsViewModel>.Instance, dialogs);
            Wait(vm.LoadAsync());
            vm.IsEnabled.Should().BeTrue("Standard an");
            vm.FilesText.Should().Be("12");
            vm.SinceText.Should().Be("12.09.2026");

            vm.IsEnabled = false;
            PumpUntil(() => service.Saved.Count == 1);
            service.Saved[0].Should().Be(new UsageSettings(Enabled: false, ForgottenEnabled: true));

            Wait(vm.EraseCommand.ExecuteAsync(null));
            dialogs.Confirms.Should().Be(1);
            service.Erased.Should().Be(0, "abgelehnt");
            UsageSettingsViewModel.EraseMessage(vm.Summary).Should().Contain("12 Fotos an 3 Tagen").And.Contain("nicht rückgängig");

            dialogs.Answer = true;
            service.Summary = UsageSummary.Empty;
            Wait(vm.EraseCommand.ExecuteAsync(null));
            service.Erased.Should().Be(1);
            vm.HasData.Should().BeFalse();
            vm.EraseCommand.CanExecute(null).Should().BeFalse();
            return 0;
        }, Timeout);
    }

    [Fact]
    public void Karte_Nutzung_lernen_ohne_Binding_Fehler()
    {
        TestUiThread.Run(() =>
        {
            var service = new FakeUsageService { Summary = new UsageSummary(1284, 57, 38, new DateOnly(2026, 9, 12)) };
            var vm = new UsageSettingsViewModel(service, NullLogger<UsageSettingsViewModel>.Instance, new FakeDialogs(true));
            Wait(vm.LoadAsync());
            using var errors = new BindingErrors();
            var card = new UsageSettingsCard { DataContext = vm };
            var window = new Window
            {
                Width = 900, Height = 500, Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Content = card,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                GalleryGridHost.Pump(DispatcherPriority.Background);
                card.ActualHeight.Should().BeGreaterThan(100);
            }
            finally
            {
                window.Close();
            }

            errors.Messages.Should().BeEmpty("keine Binding-Fehler (hell/dunkel prüft der Rauchtest mit Screenshots)");
            return 0;
        }, Timeout);
    }

    [Fact]
    public void Grund_der_Highlights_steht_im_Tooltip()
    {
        TestUiThread.Run(() =>
        {
            var tile = new StoryTileViewModel(7, 0, 0, 100, 100, false, null, "scharf · oft angesehen", 0, true, false);
            tile.ToolTipText.Should().Be("scharf · oft angesehen");
            return 0;
        }, Timeout);
    }

    [Fact]
    public void Galerie_Filter_Lieblinge_SQL_Bedingung_und_Leertext()
    {
        var loved = new GalleryFilterState { LovedOnly = true };
        loved.ActiveCount.Should().Be(1);
        loved.Should().NotBe(GalleryFilterState.None);
        GalleryPageSource.BuildFilterClause(loved).Should().Contain("FileUsageDays").And.Contain("usage.enabled");
        GalleryViewModel.EmptyMessageForFileFilter(loved).Should().Contain("Noch keine Lieblinge");
        Enum.TryParse<global::Wpf.Ui.Controls.SymbolRegular>("HeartPulse24", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Galerie_Filter_Lieblinge_zaehlt_nur_gelernte_Lieblinge_und_nichts_bei_ausgeschaltetem_Lernen()
    {
        using var db = await Lumora.Wpf.Tests.ArchiveTarget.ArchiveTestDb.CreateAsync();
        var folder = await db.AddFolderAsync(db.ArchiveRootId, Path.Combine(db.ArchiveDir, "A"));
        var a = (await db.AddFileAsync(folder, "a.jpg")).Id;
        var b = (await db.AddFileAsync(folder, "b.jpg")).Id;
        _ = (await db.AddFileAsync(folder, "c.jpg")).Id;
        var clock = new ManualClock();
        using (var usage = new UsageSignalService(db.Factory, time: clock))
        {
            for (var day = 0; day < 3; day++)
            {
                usage.RecordView(a, TimeSpan.FromSeconds(30), zoomed: false, UsageViewContext.Normal);
                clock.Advance(TimeSpan.FromDays(1));
            }

            usage.RecordView(b, TimeSpan.FromSeconds(30), zoomed: false, UsageViewContext.Normal);
            await usage.FlushAsync();

            using var source = new GalleryPageSource(db.Factory);
            source.Open();
            var query = new GalleryQuery(db.LibraryId, new GalleryFilterState { LovedOnly = true }, null);
            (await source.GetTotalCountAsync(query, CancellationToken.None)).Should().Be(1);
            (await source.LoadPageAsync(query, 0, 10, 1, CancellationToken.None)).Single().Id.Should().Be(a);

            await usage.SaveSettingsAsync(new UsageSettings(Enabled: false));
            (await source.GetTotalCountAsync(query, CancellationToken.None)).Should().Be(0, "aus = nicht verwenden");
        }
    }

    // ---- Hilfen ------------------------------------------------------------------------------------------------------

    private static LightboxViewModel CreateLightbox(FakeSignals signals)
        => new(new LibVlcHost(NullLogger<LibVlcHost>.Instance), contextFactory: null!, NullLogger<LightboxViewModel>.Instance, usageSignals: signals);

    private static LightboxEntry Entry(int id, MediaType type = MediaType.Image)
        => new(id, Path.Combine(Path.GetTempPath(), "lumora-usage-test-gibt-es-nicht", $"f{id}.jpg"), $"f{id}.jpg", type, false, null, 1000, DateTime.UtcNow);

    private static void Wait(Task task) => PumpUntil(() => task.IsCompleted);

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

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeSignals : IUsageSignals
    {
        public ManualClock Clock { get; } = new();

        public List<(int FileId, TimeSpan Active, bool Zoomed, UsageViewContext Context)> Views { get; } = [];

        public TimeProvider Time => Clock;

        public bool IsEnabled => true;

        public void RecordView(int fileId, TimeSpan activeTime, bool zoomed, UsageViewContext context) => Views.Add((fileId, activeTime, zoomed, context));

        public void Record(IReadOnlyCollection<int> fileIds, UsageKind kind)
        {
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeUsageService : IUsageService
    {
        public UsageTotals? Totals { get; set; }

        public UsageSummary Summary { get; set; } = UsageSummary.Empty;

        public UsageSettings Settings { get; set; } = UsageSettings.Default;

        public List<UsageSettings> Saved { get; } = [];

        public int Erased { get; private set; }

        public Task<UsageSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(Settings);

        public Task SaveSettingsAsync(UsageSettings settings, CancellationToken ct = default)
        {
            Saved.Add(settings);
            Settings = settings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<int, UsageTotals>> GetTotalsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<int, UsageTotals>>(Totals is null ? new Dictionary<int, UsageTotals>() : fileIds.ToDictionary(i => i, _ => Totals));

        public Task<UsageTotals?> GetTotalsAsync(int fileId, CancellationToken ct = default) => Task.FromResult(Totals);

        public Task<UsageSummary> GetSummaryAsync(CancellationToken ct = default) => Task.FromResult(Summary);

        public Task<int> EraseAsync(CancellationToken ct = default)
        {
            Erased++;
            return Task.FromResult(12);
        }

        public Task<IReadOnlyList<int>> FindForgottenAsync(IReadOnlyCollection<int> libraryIds, DateOnly today, int max, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<int>>([]);
    }

    private sealed class FakeDialogs(bool answer) : IDialogService
    {
        public bool Answer { get; set; } = answer;

        public int Confirms { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
        {
            Confirms++;
            return Task.FromResult(Answer);
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
