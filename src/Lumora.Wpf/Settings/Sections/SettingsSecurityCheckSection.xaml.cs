using System.Windows.Controls;

namespace Lumora.Settings.Sections;

/// <summary>
/// Reiter „Sicherheits-Check“ (ADR-0064) — nur in der geöffneten versteckten Sitzung eingefügt
/// (<see cref="Shell.HiddenLibraries.SecurityCheck.SecurityCheckUi.AddSettingsTab"/>). DataContext = SecurityCheckViewModel.
/// </summary>
public partial class SettingsSecurityCheckSection : UserControl
{
    public SettingsSecurityCheckSection() => InitializeComponent();
}
