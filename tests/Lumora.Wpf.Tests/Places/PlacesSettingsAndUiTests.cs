using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Lightbox;
using Lumora.Places;
using Lumora.Places.Styles;
using Lumora.Places.Suggestions;
using Lumora.Places.TrackSettings;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Persons;
using static Lumora.Wpf.Tests.Places.SuggestionSamples;

namespace Lumora.Wpf.Tests.Places;

/// <summary>
/// Einstellungen › Orte (ADR-0062) mit Fakes, Lightbox-Hinweis „Position geschätzt“ und die echte Seite „Orte“ im Bereich
/// „Ungenau“ (Liste virtualisiert, Karte zeichnet Spur und Korrekturen, Treffer auf dem Vorschlag) sowie der Reiter „Orte“.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PlacesSettingsAndUiTests
{
    private sealed class FakeImport : IGpsTrackImportService
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public bool Cancelled { get; private set; }

        public GpsTrackImportStatus Status => GpsTrackImportStatus.Idle;

        public bool IsRunning => false;

        public Task<GpsTrackImportReport> ImportAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            Calls.Add(paths);
            return Task.FromResult(new GpsTrackImportReport(12, 10, 2, 0, 0, false));
        }

        public void Cancel() => Cancelled = true;
    }

    private sealed class FakeAutoImport : IGpsTrackAutoImport
    {
        public int Reconfigured { get; private set; }

        public void Start()
        {
        }

        public Task ReconfigureAsync(CancellationToken ct = default)
        {
            Reconfigured++;
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> FindNewOnDevice(string activityFolder, long? sinceUnix) => [];

        public Task<GpsTrackImportReport> ImportDeviceAsync(string activityFolder, CancellationToken ct = default) => Task.FromResult(new GpsTrackImportReport(0, 0, 0, 0, 0, false));

        public Task<GpsTrackImportReport?> ScanWatchFolderAsync(CancellationToken ct = default) => Task.FromResult<GpsTrackImportReport?>(null);
    }

    private sealed class FakePicker : IGpsTrackFilePicker
    {
        public string? Zip { get; set; } = @"C:\Downloads\garmin.zip";

        public string? PickExportZip() => Zip;

        public IReadOnlyList<string> PickTrackFiles() => [@"C:\Sport\a.fit", @"C:\Sport\b.gpx"];
    }

    private sealed class Links : IMapLinkOpener
    {
        public List<Uri> Opened { get; } = [];

        public bool Open(Uri uri)
        {
            Opened.Add(uri);
            return true;
        }
    }

    private static (PlacesSettingsViewModel Vm, FakeImport Import, FakeTrackSettings Settings, FakeTrackStore Store, FakeAutoImport Auto, Links Links, FakePersonDialogs Dialogs) Settings()
    {
        var (_, service, _, settings, store) = Context(1);
        service.Cameras.Add(new CameraTimeInfo("Canon EOS 80D", 120, null));
        service.Cameras.Add(new CameraTimeInfo("Apple iPhone 12", 400, 192));
        var import = new FakeImport();
        var auto = new FakeAutoImport();
        var links = new Links();
        var dialogs = new FakePersonDialogs(confirm: true);
        var vm = new PlacesSettingsViewModel(import, store, settings, auto, service, new FixedScope(1), dialogs, new FakePicker(), links,
            NullLogger<PlacesSettingsViewModel>.Instance, new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger());
        return (vm, import, settings, store, auto, links, dialogs);
    }

    [Fact]
    public async Task Einstellungen_Stand_Export_oeffnen_ZIP_einlesen_Zeitabgleich_Kameras_und_Spuren_entfernen()
    {
        var (vm, import, settings, store, auto, links, dialogs) = Settings();
        await vm.LoadAsync();
        vm.ActivityCount.Should().Be(1);
        vm.SummaryText.Should().StartWith("1 Aktivität · 2020 · Fenix 7 · zuletzt eingelesen");
        vm.SelectedTimeZone.Id.Should().BeNull("Standard: Windows-Zeitzone");
        vm.SelectedTolerance.Minutes.Should().Be(2);
        vm.Cameras.Should().HaveCount(2);
        vm.Cameras[1].SuggestionText.Should().Be("aus GPS-Zeit ermittelt: + 3 min 12 s");
        vm.Cameras[0].SuggestionText.Should().StartWith("von Hand");

        vm.OpenGarminExportCommand.Execute(null);
        links.Opened.Should().Equal(new Uri(PlacesSettingsViewModel.GarminExportUrl));

        await vm.ImportExportZipCommand.ExecuteAsync(null);
        import.Calls.Single().Should().Equal(@"C:\Downloads\garmin.zip");
        vm.ImportResultText.Should().Be("10 Aktivitäten eingelesen, 2 schon vorhanden");
        await vm.ImportFilesCommand.ExecuteAsync(null);
        import.Calls[^1].Should().HaveCount(2);

        vm.Cameras[1].ApplySuggestionCommand.Execute(null);
        await vm.LastSave;
        settings.Value.CameraOffsets["Apple iPhone 12"].Should().Be(192);
        vm.Cameras[0].OffsetText = "-1:00:00";
        await vm.LastSave;
        settings.Value.CameraOffsets["Canon EOS 80D"].Should().Be(-3600);
        vm.Cameras[0].OffsetText = "Unsinn";
        vm.Cameras[0].IsInvalid.Should().BeTrue();

        vm.SelectedTimeZone = vm.TimeZones.First(z => z.Id == "Tokyo Standard Time");
        vm.SelectedTolerance = vm.ToleranceOptions.First(o => o.Minutes == 5);
        await vm.LastSave;
        settings.Value.TimeZoneId.Should().Be("Tokyo Standard Time");
        settings.Value.ToleranceSeconds.Should().Be(300);
        vm.UsbDetection = false;
        await vm.LastSave;
        settings.Value.UsbDetection.Should().BeFalse();
        auto.Reconfigured.Should().BeGreaterThan(0, "USB-Erkennung bzw. Ordner neu einrichten");

        await vm.ToggleActivitiesCommand.ExecuteAsync(null);
        vm.Activities.Should().ContainSingle().Which.Title.Should().Be("Wanderung Weinebene");
        await vm.RemoveTracksCommand.ExecuteAsync(null);
        dialogs.Confirms.Should().Be(1, "vorher bestätigen");
        store.Deleted.Should().Be(1);
        vm.ActivityCount.Should().Be(0);
    }

    [Fact]
    public void Lightbox_Hinweis_mit_Begruendung_und_Garmin_Vorschlag()
    {
        var item = Item(1, GeoSuggestionLevel.Track, 10);
        var text = LightboxViewModel.DescribeGeoHint(item, new FakePlacesWorld());
        text.Should().StartWith("Das Handy hat nur Breite/Länge gespeichert");
        text.Should().Contain("Laut Ihrer GPS-Spur („Wanderung Weinebene“, Fenix 7) waren Sie um 14:56");
        text.Should().Contain("bei Graz (1.853 m)");
        text.Should().EndWith("Stufe: Garmin-Spur.");
        LightboxViewModel.DescribeGeoHint(Item(2, GeoSuggestionLevel.None, 0), null).Should().Contain("keine GPS-Spur");
    }

    [Fact]
    public void Seite_Orte_Bereich_Ungenau_zeichnet_Spur_und_Korrekturen_und_Reiter_Orte_laedt()
    {
        TestUiThread.Run(() =>
        {
            var h = new PlacesHarness();
            h.SeedAustria();
            var (context, service, _, _, _) = Context(1);
            service.Inaccurate.AddRange([Item(1, GeoSuggestionLevel.Track, 0), Item(2, GeoSuggestionLevel.Sure, 30), Item(3, GeoSuggestionLevel.None, 60)]);
            var vm = h.Create(suggestions: context);
            var page = new PlacesPage(vm);
            var window = new Window { Width = 1300, Height = 850, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -5000, Top = -5000, Content = page };
            try
            {
                window.Show();
                PumpUntil(() => !vm.IsLoading && vm.EstimatedCount == 3);
                vm.ShowInaccurateModeCommand.Execute(null);
                // Standard „Nur dieses Foto“ (Mockup 25): eine Korrektur samt beschrifteter Quellen und Maßstab
                PumpUntil(() => page.MapView.LastDrawnCorrections == 1 && page.MapView.LastDrawnTrackPoints > 0 && page.MapView.LastScaleText is not null);
                vm.ShowWholeTrip = true;
                PumpUntil(() => page.MapView.LastDrawnCorrections == 3);
                var panel = FindChild<SuggestionPanel>(page)!;
                panel.IsVisible.Should().BeTrue();
                panel.RowList.Items.Count.Should().Be(4, "1 Tag + 3 Fotos");
                var point = page.MapView.SuggestionScreenPoint(1)!.Value;
                page.MapView.OverlayAt(point).Should().Be((1, true), "der Vorschlag der ausgewählten Zeile ist greifbar");

                var settings = Settings();
                var section = new SettingsPlacesSection { DataContext = settings.Vm };
                window.Content = section;
                DoEvents();
                PumpUntil(() => settings.Vm.Cameras.Count == 2);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, TimeSpan.FromSeconds(90));
    }

    private static T? FindChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            DoEvents();
            Thread.Sleep(5);
        }

        condition().Should().BeTrue("Bedingung innerhalb von 15 s");
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
