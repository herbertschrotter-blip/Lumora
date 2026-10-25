using System.Windows.Controls;

namespace Lumora.Settings.Sections;

/// <summary>
/// Karte „Ausschlüsse beim Einlesen“ (ADR-0066) unter der Bibliotheksliste in Einstellungen › Bibliotheken &amp; Eingänge.
/// DataContext = <see cref="Exclusions.ScanExclusionsSettingsViewModel"/>.
/// </summary>
public partial class SettingsScanExclusionsSection : UserControl
{
    public SettingsScanExclusionsSection() => InitializeComponent();
}
