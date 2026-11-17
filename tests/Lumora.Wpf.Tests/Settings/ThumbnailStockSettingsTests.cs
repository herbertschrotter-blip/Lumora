using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Thumbnails;
using Lumora.Settings.Sections;
using Lumora.Settings.ThumbnailStock;
using Lumora.Wpf.Tests.Fixtures;
using Wpf.Ui.Appearance;

namespace Lumora.Wpf.Tests.Settings;

/// <summary>
/// Schalter „Vorschaubilder auf Vorrat erstellen“ (0.5.40, Akzeptanzkriterium 6): zeigt den Stand des Dienstes, speichert über
/// den Dienst (je Katalog), Fehler als Hinweis; in Speicher &amp; Wartung › Vorschaubilder ohne Binding-Fehler (hell/dunkel).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ThumbnailStockSettingsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Zeigt_den_Stand_und_speichert_ueber_den_Dienst()
    {
        var service = new FakeStockService { IsEnabled = false };
        var vm = new ThumbnailStockSettingsViewModel(service, NullLogger<ThumbnailStockSettingsViewModel>.Instance);
        vm.IsEnabled.Should().BeFalse("Stand des Dienstes, ohne zu speichern");
        service.Saved.Should().BeEmpty();

        vm.IsEnabled = true;
        await vm.SavePending;
        service.Saved.Should().Equal(true);
        vm.StatusText.Should().BeNull();

        service.Fail = true;
        vm.IsEnabled = false;
        await vm.SavePending;
        vm.StatusText.Should().StartWith("Nicht gespeichert:");

        service.Fail = false;
        service.IsEnabled = true;
        vm.Refresh();
        vm.IsEnabled.Should().BeTrue();
        service.Saved.Should().Equal(true);
    }

    [Fact]
    public Task Schalter_in_Speicher_und_Wartung_ohne_Binding_Fehler() => OnUiAsync(() =>
    {
        var vm = new ThumbnailStockSettingsViewModel(new FakeStockService(), NullLogger<ThumbnailStockSettingsViewModel>.Instance);
        using var errors = new BindingErrors();
        var section = new SettingsStorageSection();
        section.ThumbnailStockPanel.Visibility.Should().Be(Visibility.Collapsed, "ohne ViewModel ausgeblendet (SettingsPage blendet ein)");
        section.ThumbnailStockPanel.DataContext = vm;
        section.ThumbnailStockPanel.Visibility = Visibility.Visible;
        var window = new Window { Content = section, Width = 1000, Height = 900, ShowActivated = false, Left = -10000 };
        window.Show();
        try
        {
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme);
                window.UpdateLayout();
            }

            var toggle = Find<global::Wpf.Ui.Controls.ToggleSwitch>(section.ThumbnailStockPanel).Single();
            AutomationProperties.GetName(toggle).Should().Be("Vorschaubilder auf Vorrat erstellen");
            toggle.IsChecked.Should().BeTrue("Standard an");
            toggle.IsChecked = false;
            vm.IsEnabled.Should().BeFalse();
            errors.Messages.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }

        return Task.CompletedTask;
    });

    private static Task OnUiAsync(Func<Task> scenario) => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private static IEnumerable<T> Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Find<T>(child))
            {
                yield return nested;
            }
        }
    }

    private sealed class FakeStockService : IThumbnailStockService
    {
        public List<bool> Saved { get; } = [];

        public bool Fail { get; set; }

        public ThumbnailStockStatus Status => ThumbnailStockStatus.Idle;

        public bool IsEnabled { get; set; } = true;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            if (Fail)
            {
                return Task.FromException(new IOException("Katalog gesperrt"));
            }

            Saved.Add(enabled);
            IsEnabled = enabled;
            return Task.CompletedTask;
        }

        public void RequestPass()
        {
        }

        public Task<ThumbnailStockReport> RunPassAsync(CancellationToken ct = default) => Task.FromResult(ThumbnailStockReport.Empty);
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
