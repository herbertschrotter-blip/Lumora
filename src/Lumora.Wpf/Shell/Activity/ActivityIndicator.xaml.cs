using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lumora.Shell.Activity;

/// <summary>
/// Aktivitäts-Anzeige der Statusleiste (ADR-0067); DataContext = <see cref="ActivityStatusViewModel"/>. Schließt die Liste
/// bei einem Klick daneben, mit Esc und beim Wechsel in ein anderes Fenster (die Liste ist kein Popup, siehe XAML).
/// </summary>
public partial class ActivityIndicator : UserControl
{
    private Window? _window;

    public ActivityIndicator()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private ActivityStatusViewModel? ViewModel => DataContext as ActivityStatusViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }

        _window.PreviewMouseDown += OnWindowPreviewMouseDown;
        _window.PreviewKeyDown += OnWindowPreviewKeyDown;
        _window.Deactivated += OnWindowDeactivated;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        _window.PreviewMouseDown -= OnWindowPreviewMouseDown;
        _window.PreviewKeyDown -= OnWindowPreviewKeyDown;
        _window.Deactivated -= OnWindowDeactivated;
        _window = null;
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { IsFlyoutOpen: true } vm || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (!IsWithin(source, Flyout) && !IsWithin(source, Toggle))
        {
            vm.IsFlyoutOpen = false;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is { IsFlyoutOpen: true } vm)
        {
            vm.IsFlyoutOpen = false;
            Toggle.Focus();
            e.Handled = true;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (ViewModel is { IsFlyoutOpen: true } vm && !SmokeTestRunner.IsRequested)
        {
            vm.IsFlyoutOpen = false; // der Rauchtest läuft auch im Hintergrund — dort bleibt die Liste für den Screenshot offen
        }
    }

    private static bool IsWithin(DependencyObject source, DependencyObject container)
    {
        for (var current = source; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }

        return false;
    }
}
