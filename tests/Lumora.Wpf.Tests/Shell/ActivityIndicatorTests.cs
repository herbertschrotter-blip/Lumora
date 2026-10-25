using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Lumora.Services.Activity;
using Lumora.Shell;
using Lumora.Shell.Activity;
using Lumora.Wpf.Tests.Fixtures;
using Wpf.Ui.Appearance;
using ActivityKind = Lumora.Services.Activity.ActivityKind;

namespace Lumora.Wpf.Tests.Shell;

/// <summary>
/// Aktivitäts-Anzeige unten rechts (ADR-0067, Mockup 15): Statusleiste (wichtigster Vorgang, „+n“, „Alles erledigt“/„Keine
/// Aktivität“), Aufklapp-Liste (Läuft/Wartet/Zuletzt fertig, Zeilen in place), kurze Meldung mit Rückgängig, Darstellung hell
/// und dunkel ohne Binding-Fehler.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ActivityIndicatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static T OnUi<T>(Func<T> action) => TestUiThread.Run(action, Timeout);

    private static void OnUi(Action action) => TestUiThread.Run(() => { action(); return 0; }, Timeout);

    [Fact]
    public void Statusleiste_zeigt_den_wichtigsten_Vorgang_mit_Zaehler_dann_Alles_erledigt_dann_Keine_Aktivitaet()
    {
        using var center = new ActivityCenter();
        var offset = TimeSpan.Zero;
        var vm = OnUi(() => new ActivityStatusViewModel(center, utcNow: () => DateTime.UtcNow + offset));
        try
        {
            OnUi(() => vm.StatusText).Should().Be("Keine Aktivität");
            OnUi(() => vm.IsIdle).Should().BeTrue();

            var faces = center.Start(new ActivityStartInfo(ActivityKind.Faces, "Gesichter erkennen", KeepInRecent: false, Key: "faces"));
            faces.Report(1240, 5212);
            var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", "nach „Urlaub 2024“", UserInitiated: true));
            move.Report(120, 340);
            var motifs = center.Start(new ActivityStartInfo(ActivityKind.Motifs, "Motive erkennen", KeepInRecent: false, Key: "motifs"));
            motifs.Wait("Grafikkarte gerade belegt");
            OnUi(vm.Refresh);

            OnUi(() => vm.StatusText).Should().Be("Verschieben: 120 von 340");
            OnUi(() => vm.OthersText).Should().Be("+2");
            OnUi(() => vm.ShowPrimaryBar).Should().BeTrue();
            OnUi(() => vm.PrimaryFraction).Should().BeApproximately(120 / 340.0, 0.0001);
            OnUi(() => vm.HeaderSummary).Should().Be("2 laufen · 1 wartet");
            OnUi(() => vm.StatusToolTip).Should().Be("nach „Urlaub 2024“");

            move.Complete(new ActivityCompletion("340 Dateien verschoben"));
            faces.Dismiss();
            OnUi(vm.Refresh);
            OnUi(() => vm.StatusText).Should().Be("Motive erkennen — wartet", "ohne Laufendes der wartende");
            OnUi(() => vm.PrimaryIsWaiting).Should().BeTrue();
            OnUi(() => vm.OthersText).Should().BeNull();

            motifs.Dismiss();
            OnUi(vm.Refresh);
            OnUi(() => vm.StatusText).Should().Be("Alles erledigt", "kurz nach dem letzten Ende");
            OnUi(() => vm.ShowAllDone).Should().BeTrue();
            offset = ActivityStatusViewModel.AllDoneDuration + TimeSpan.FromSeconds(1);
            OnUi(vm.Refresh);
            OnUi(() => vm.StatusText).Should().Be("Keine Aktivität");
            OnUi(() => vm.IsIdle).Should().BeTrue();
        }
        finally
        {
            OnUi(vm.Dispose);
        }
    }

    [Fact]
    public void Liste_hat_drei_Abschnitte_und_aktualisiert_Zeilen_in_place()
    {
        using var center = new ActivityCenter();
        var vm = OnUi(() => new ActivityStatusViewModel(center));
        try
        {
            var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", "nach „Urlaub 2024“", UserInitiated: true, Cancel: () => { }));
            move.Report(120, 340);
            var motifs = center.Start(new ActivityStartInfo(ActivityKind.Motifs, "Motive erkennen", KeepInRecent: false));
            motifs.Wait("wartet auf den Katalog");
            var scan = center.Start(new ActivityStartInfo(ActivityKind.Scan, "Einlesen „Familienfotos“"));
            scan.Complete(new ActivityCompletion("34 neu, 3 geändert, 2 fehlen", Actions: [ActivityAction.ShowMissing]));
            var copy = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true));
            copy.Complete(new ActivityCompletion("2 Dateien übersprungen", ActivityState.Warning, [ActivityAction.Details], "2 Dateien: Ziel existiert bereits."));
            OnUi(vm.Refresh);

            var row = OnUi(() => vm.Running.Single());
            OnUi(() => row.Title).Should().Be("Verschieben: 120 von 340");
            OnUi(() => row.Subtitle).Should().Be("nach „Urlaub 2024“");
            OnUi(() => row.CanCancel).Should().BeTrue();
            OnUi(() => row.ShowPause).Should().BeFalse();
            OnUi(() => vm.Waiting.Single().Title).Should().Be("Motive erkennen — wartet");
            OnUi(() => vm.Waiting.Single().Subtitle).Should().Be("wartet auf den Katalog");
            OnUi(() => vm.Recent.Select(r => r.Title).ToList()).Should().Equal("Verschieben", "Einlesen „Familienfotos“");
            OnUi(() => vm.Recent[1].Subtitle).Should().Be("gerade eben · 34 neu, 3 geändert, 2 fehlen");
            OnUi(() => vm.Recent[1].HasShowMissing).Should().BeTrue();
            OnUi(() => vm.Recent[0].HasDetails && vm.Recent[0].IsWarning).Should().BeTrue();

            move.Report(200, 340);
            OnUi(vm.Refresh);
            OnUi(() => vm.Running.Single()).Should().BeSameAs(row, "gleiche Id → gleiche Zeile (UI-Automation)");
            OnUi(() => row.Title).Should().Be("Verschieben: 200 von 340");
        }
        finally
        {
            OnUi(vm.Dispose);
        }
    }

    [Fact]
    public async Task Kurze_Meldung_nur_fuer_Benutzer_Vorgaenge_mit_Rueckgaengig()
    {
        using var center = new ActivityCenter();
        var vm = OnUi(() => new ActivityStatusViewModel(center));
        try
        {
            var undone = 0;
            center.Start(new ActivityStartInfo(ActivityKind.Duplicates, "Doppelte suchen", KeepInRecent: false)).Complete(new ActivityCompletion("fertig"));
            OnUi(() => vm.IsToastVisible).Should().BeFalse("Hintergrund-Index ohne Meldung");

            var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", "nach „Urlaub 2024“", UserInitiated: true));
            move.Complete(new ActivityCompletion("340 Dateien verschoben", Actions: [ActivityAction.Undo(_ => { undone++; return Task.FromResult("Rückgängig: 340 Dateien zurückverschoben"); })]));
            await WaitUntilAsync(() => OnUi(() => vm.IsToastVisible));
            OnUi(() => vm.ToastTitle).Should().Be("340 Dateien verschoben");
            OnUi(() => vm.ToastSubtitle).Should().Be("nach „Urlaub 2024“");
            OnUi(() => vm.ToastHasUndo && vm.ShowToastOverlay).Should().BeTrue();

            OnUi(() => vm.IsFlyoutOpen = true);
            OnUi(() => vm.ShowToastOverlay).Should().BeFalse("Liste und Meldung überdecken sich nicht");
            OnUi(() => vm.IsFlyoutOpen = false);

            await TestUiThread.Dispatcher.InvokeAsync(() => vm.UndoToastCommand.ExecuteAsync(null)).Task.Unwrap().WaitAsync(Timeout);

            undone.Should().Be(1);
            OnUi(() => vm.ToastTitle).Should().Be("Rückgängig: 340 Dateien zurückverschoben", "Ergebnis als kurze Meldung, kein zweiter Eintrag");
            OnUi(() => vm.ToastHasUndo).Should().BeFalse();
            OnUi(() => vm.DismissToastCommand.Execute(null));
            center.GetOverview().Recent.First().HasAction(ActivityActionKind.Undo).Should().BeFalse();

            var canceled = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true));
            canceled.Canceled();
            await Task.Delay(50);
            OnUi(() => vm.IsToastVisible).Should().BeFalse("Abbrechen hat der Benutzer selbst ausgelöst");
        }
        finally
        {
            OnUi(vm.Dispose);
        }
    }

    [Fact]
    public async Task Pause_Abbrechen_und_Details_gehen_an_Zentrale_und_Dialog()
    {
        using var center = new ActivityCenter();
        var dialogs = new ScriptedDialogs();
        var vm = OnUi(() => new ActivityStatusViewModel(center, dialogs));
        try
        {
            var calls = new List<string>();
            var faces = center.Start(new ActivityStartInfo(ActivityKind.Faces, "Gesichter erkennen", Cancel: () => calls.Add("cancel"), Pause: () => calls.Add("pause"), Resume: () => calls.Add("resume")));
            faces.Report(1, 10);
            center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", UserInitiated: true))
                .Complete(new ActivityCompletion("1 übersprungen", ActivityState.Warning, [ActivityAction.Details], "1 Datei: Ziel existiert bereits."));
            OnUi(vm.Refresh);

            var row = OnUi(() => vm.Running.Single());
            OnUi(() => row.PauseCommand.Execute(null));
            OnUi(() => row.ShowResume).Should().BeTrue();
            OnUi(() => row.ResumeCommand.Execute(null));
            OnUi(() => row.CancelCommand.Execute(null));
            calls.Should().Equal("pause", "resume", "cancel");

            await TestUiThread.Dispatcher.InvokeAsync(() => vm.Recent.Single().ShowDetailsCommand.ExecuteAsync(null)).Task.Unwrap().WaitAsync(Timeout);
            dialogs.Errors.Should().Equal("1 Datei: Ziel existiert bereits.");
        }
        finally
        {
            OnUi(vm.Dispose);
        }
    }

    [Fact]
    public void Darstellung_hell_und_dunkel_mit_aufgeklappter_Liste_und_Meldung_ohne_Binding_Fehler()
    {
        using var center = new ActivityCenter();
        var move = center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "Verschieben", "nach „Urlaub 2024“", UserInitiated: true, Cancel: () => { }));
        move.Report(120, 340);
        center.Start(new ActivityStartInfo(ActivityKind.Faces, "Gesichter erkennen", Pause: () => { }, Resume: () => { }, Cancel: () => { })).Report(5, 50);
        center.Start(new ActivityStartInfo(ActivityKind.Motifs, "Motive erkennen")).Wait("wartet auf den Scan");
        center.Start(new ActivityStartInfo(ActivityKind.FileOperation, "In den Papierkorb: Ordner „alt“", UserInitiated: true))
            .Complete(new ActivityCompletion("58 Dateien im Papierkorb", Actions: [ActivityAction.Undo(_ => Task.FromResult("ok"))]));

        OnUi(() =>
        {
            using var errors = new BindingErrors();
            var vm = new ActivityStatusViewModel(center);
            var indicator = new ActivityIndicator { DataContext = vm };
            var host = new Grid { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Height = 28 };
            host.Children.Add(indicator);
            var window = new Window { Content = host, Width = 900, Height = 700, ShowActivated = false };
            window.Show();
            try
            {
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                {
                    ApplicationThemeManager.Apply(theme);
                    vm.IsFlyoutOpen = true;
                    window.UpdateLayout();
                    var flyout = (FrameworkElement)indicator.FindName("Flyout");
                    flyout.IsVisible.Should().BeTrue();
                    flyout.ActualHeight.Should().BeGreaterThan(150, "drei Abschnitte mit vier Zeilen");
                    var point = flyout.TranslatePoint(new Point(0, flyout.ActualHeight), indicator);
                    point.Y.Should().BeLessThan(0, "die Liste ragt über die Statusleiste nach oben");

                    vm.IsFlyoutOpen = false;
                    vm.ShowToast(center.GetOverview().Recent.Single());
                    window.UpdateLayout();
                    ((FrameworkElement)indicator.FindName("Toast")).IsVisible.Should().BeTrue();
                    vm.DismissToastCommand.Execute(null);
                }

                errors.Messages.Should().BeEmpty();
            }
            finally
            {
                window.Close();
                vm.Dispose();
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
            }
        });
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

    /// <summary>Sammelt WPF-Binding-Fehler während des Tests.</summary>
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
