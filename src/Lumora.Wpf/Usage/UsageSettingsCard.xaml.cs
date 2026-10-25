using System.Windows.Controls;

namespace Lumora.Usage;

/// <summary>Karte „Nutzung lernen“ (ADR-0072); geladen über <see cref="UsageSettingsViewModel.LoadAsync"/> beim Öffnen des Reiters.</summary>
public partial class UsageSettingsCard : UserControl
{
    public UsageSettingsCard() => InitializeComponent();
}
