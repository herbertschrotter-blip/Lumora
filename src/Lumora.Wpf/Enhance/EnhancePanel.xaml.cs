using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lumora.Enhance;

/// <summary>Leiste „Verbessern“ (ADR-0071, Mockup 13); Logik im <see cref="EnhancePanelViewModel"/>.</summary>
public partial class EnhancePanel : UserControl
{
    public EnhancePanel()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EnhancePanelViewModel vm)
        {
            _ = vm.SetOpenAsync(false);
        }
    }
}

/// <summary>true ↔ false (für IsEnabled/IsChecked).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static InverseBoolConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
