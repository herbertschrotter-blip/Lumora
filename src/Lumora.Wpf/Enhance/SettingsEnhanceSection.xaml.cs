using System.Windows.Controls;

namespace Lumora.Enhance;

/// <summary>Reiter „Verbessern“ (ADR-0071, Mockup 13). Werte und Modell-Zustände werden beim Anzeigen gelesen.</summary>
public partial class SettingsEnhanceSection : UserControl
{
    public SettingsEnhanceSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is EnhanceSettingsViewModel vm)
            {
                _ = vm.RefreshAsync();
            }
        };
    }
}
