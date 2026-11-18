using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace Lumora.Merge;

/// <summary>
/// Fenster „Zusammenfügen“ (ADR-0078 Punkt 8): nicht modal, eines je Sitzung (<see cref="MergeWindowService"/>). Schließen
/// während des Speicherns wirkt wie „Im Hintergrund weiterrechnen“ — das Speichern rechnet zu Ende, eine Vorschau bricht ab.
/// </summary>
public sealed partial class MergeWindow : Shell.Dialogs.AppFluentWindow
{
    private bool _closing;

    public MergeWindow(MergeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Owner = () => this;
        viewModel.CloseRequested += OnCloseRequested;
        Loaded += async (_, _) => await viewModel.LoadAsync().ConfigureAwait(true);
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public MergeViewModel ViewModel { get; }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        if (ViewModel.IsSaving && !ViewModel.InBackground)
        {
            ViewModel.ContinueInBackgroundCommand.Execute(null); // Schließen = im Hintergrund weiterrechnen (ADR-0078 Punkt 12)
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.CloseRequested -= OnCloseRequested;
        _ = ViewModel.DisposeAsync().AsTask();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !ViewModel.IsSaving)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Stage.ZoomIn();

    private void OnZoomOut(object sender, RoutedEventArgs e) => Stage.ZoomOut();

    private void OnZoomFit(object sender, RoutedEventArgs e) => Stage.ZoomToFit();
}

/// <summary>true → Collapsed, sonst Visible.</summary>
public sealed class MergeInverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null bzw. leerer Text → Collapsed.</summary>
public sealed class MergeNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
