using System.Windows;
using System.Windows.Controls;

namespace Lumora.Folders;

/// <summary>
/// Issue #23 (0.4.44): Der Ordnerbaum zeigt den gewählten Ordner mit EINER Markierung — dem eigenen Akzent-Balken aus
/// <c>FolderTreeStyles.xaml</c> (0.4.30). Die WPF-UI-Vorlage des TreeViewItem bringt eine zweite, schmale Marke mit (Teil
/// „ActiveRectangle“); sie wird hier als lokaler Wert ausgeblendet — ein lokaler Wert schlägt den Auswahl-Trigger der Vorlage.
/// </summary>
public static class FolderTreeItemChrome
{
    /// <summary>Name des Vorlagen-Teils der WPF-UI-Auswahlmarke (WPF-UI 4).</summary>
    public const string IndicatorPartName = "ActiveRectangle";

    public static readonly DependencyProperty HideSelectionIndicatorProperty = DependencyProperty.RegisterAttached(
        "HideSelectionIndicator",
        typeof(bool),
        typeof(FolderTreeItemChrome),
        new PropertyMetadata(false, OnChanged));

    public static bool GetHideSelectionIndicator(DependencyObject element) => (bool)element.GetValue(HideSelectionIndicatorProperty);

    public static void SetHideSelectionIndicator(DependencyObject element, bool value) => element.SetValue(HideSelectionIndicatorProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeViewItem item || e.NewValue is not true)
        {
            return;
        }

        item.Loaded -= OnLoaded;
        item.Loaded += OnLoaded;
        Hide(item);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Hide((TreeViewItem)sender);

    /// <summary>Blendet die Vorlagen-Marke aus; true, wenn sie gefunden wurde (Tests).</summary>
    public static bool Hide(TreeViewItem item)
    {
        item.ApplyTemplate();
        if (item.Template?.FindName(IndicatorPartName, item) is UIElement indicator)
        {
            indicator.Visibility = Visibility.Collapsed;
            return true;
        }

        return false;
    }
}
