using System.Windows.Controls;

namespace Lumora.Settings.Sections;

/// <summary>
/// Reiter „KI-Bearbeitung“ (ADR-0076) — nur in der geöffneten versteckten Sitzung eingefügt
/// (<see cref="AiEdit.AiEditUi.AddSettingsTab"/>). DataContext = AiEditSettingsViewModel.
/// </summary>
public partial class SettingsAiEditSection : UserControl
{
    public SettingsAiEditSection() => InitializeComponent();
}
