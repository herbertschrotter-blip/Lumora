using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.HiddenLibraries.SecurityCheck;

namespace Lumora.Shell;

/// <summary>
/// Sicherheits-Check in der Statusleiste (ADR-0064): „Sicherheits-Check: x von y erfüllt“. Nur der Dienst der versteckten
/// Sitzung sendet die Nachricht — in der normalen Sitzung bleibt der Text leer und die Anzeige verborgen.
/// </summary>
public sealed partial class StatusBarViewModel
{
    /// <summary>null = ausblenden (normale Sitzung bzw. noch nicht geprüft).</summary>
    [ObservableProperty] private string? _securityCheckText;

    /// <summary>Im Konstruktor aufgerufen (eine Zeile in <c>StatusBarViewModel.cs</c>).</summary>
    /// <param name="service">Nur in der versteckten Sitzung registriert; ein schon vorliegendes Ergebnis gleich zeigen.</param>
    private void InitSecurityCheck(ISecurityCheckService? service)
    {
        _messenger.Register<StatusBarViewModel, SecurityCheckSummaryMessage>(this, static (r, m) => UiThread.Post(() => r.SecurityCheckText = m.Text));
        SecurityCheckText = service?.Last?.StatusText;
    }
}
