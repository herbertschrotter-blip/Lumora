using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Lumora.Lightbox;

/// <summary>Leiste unten in der Lightbox mit Tags und Kategorien (Mockup 16); Logik im <see cref="LightboxViewModel"/>.</summary>
public partial class LightboxMarksBar : UserControl
{
    public LightboxMarksBar()
    {
        InitializeComponent();
    }

    /// <summary>Menü des Knopfs „+ Tag / Kategorie“ (Tests).</summary>
    internal ContextMenu Menu => AddMenu;

    /// <summary>Links-Klick öffnet das Menü direkt über dem Knopf (Rechtsklick wie gewohnt).</summary>
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        AddMenu.PlacementTarget = AddButton;
        AddMenu.Placement = PlacementMode.Top;
        AddMenu.DataContext = DataContext;
        AddMenu.IsOpen = true;
    }
}
