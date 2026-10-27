namespace Lumora.AppConnect;

/// <summary>
/// Übergabe der App-Verbindung an die Oberfläche (ADR-0073, Stufe 4). Abfrager und Kopplungen entstehen erst nach dem
/// Hauptfenster (<c>App.StartAppConnectAsync</c>); bis dahin — und bei einem Start mit <c>_hidden</c> immer — bleibt der
/// Reiter „Verbundene Apps“ ohne Verbindung.
/// </summary>
public sealed class AppConnectRuntime
{
    public AppConnectClient? Client { get; private set; }

    public ConnectionStore? Connections { get; private set; }

    /// <summary>App-Verbindung ist bereit (einmal je Start).</summary>
    public event EventHandler? Attached;

    public void Attach(AppConnectClient client, ConnectionStore connections)
    {
        Client = client;
        Connections = connections;
        Attached?.Invoke(this, EventArgs.Empty);
    }
}
