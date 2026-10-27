namespace Lumora.AppConnect;

/// <summary>
/// Gekoppelte Apps aus Sicht des Anbieters (Abschnitte 5 und 6): prüft Geheimnis und App-Kennung und nennt die Rechte, die
/// Lumora dem Abfrager erteilt hat. Seit Stufe 2 (0.5.8) <see cref="ConnectionStore"/>; ohne Kopplung <see cref="NoPeers"/>.
/// </summary>
public interface IAppConnectPeers
{
    /// <summary>true, wenn <paramref name="secret"/> das Geheimnis der Kopplung mit <paramref name="appId"/> ist.</summary>
    bool Verify(string appId, string secret);

    /// <summary>Rechte, die Lumora <paramref name="appId"/> erlaubt; leer, wenn nicht gekoppelt.</summary>
    IReadOnlyList<string> GrantedTo(string appId);

    /// <summary>Trennen (Abschnitt 5, Schritt 6); true, wenn es die Kopplung gab.</summary>
    bool Remove(string appId);

    /// <summary>Bibliotheken, aus denen <paramref name="appId"/> Fotos sieht (Abschnitt 8.1); leer = keine.</summary>
    IReadOnlyList<int> LibrariesOf(string appId) => [];
}

/// <summary>Noch keine Kopplung möglich (Stufe 1): jede geschützte Anfrage endet mit 401 <c>not_paired</c>.</summary>
public sealed class NoPeers : IAppConnectPeers
{
    public static NoPeers Instance { get; } = new();

    public bool Verify(string appId, string secret) => false;

    public IReadOnlyList<string> GrantedTo(string appId) => [];

    public bool Remove(string appId) => false;
}
