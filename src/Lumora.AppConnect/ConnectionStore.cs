using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lumora.AppConnect;

/// <summary>Eine gekoppelte App (ohne Geheimnis im Klartext).</summary>
/// <param name="GrantedToPeer">Rechte, die Lumora der App erlaubt.</param>
/// <param name="GrantedByPeer">Rechte, die die App Lumora erlaubt (von ihr angeboten, von Lumora angenommen).</param>
public sealed record AppConnection(string AppId, string DisplayName, IReadOnlyList<string> GrantedToPeer, IReadOnlyList<string> GrantedByPeer, DateTime PairedAtUtc);

/// <summary>Schützt das Geheimnis einer Kopplung (Windows: DPAPI für den Benutzer; Tests ersetzen ihn).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Windows-DPAPI im Benutzerbereich (Abschnitt 5, Schritt 4): nur dieser Benutzer auf diesem PC kann entschlüsseln.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Lumora.AppVerbindung"u8.ToArray();

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Gekoppelte Apps (Abschnitte 5 und 6), gespeichert in <c>&lt;App-Wurzel&gt;\appconnect\connections.json</c> — das Geheimnis nur
/// verschlüsselt (<see cref="ISecretProtector"/>), nie in Protokollen oder der Anmeldedatei. Prüft Geheimnisse in konstanter Zeit.
/// </summary>
public sealed class ConnectionStore : IAppConnectPeers
{
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ConnectionStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        Load();
    }

    /// <summary>Kopplungen haben sich geändert (neu, getrennt, Rechte).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AppConnection> All
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Select(e => e.Connection).OrderBy(c => c.DisplayName, StringComparer.CurrentCulture).ToList();
            }
        }
    }

    public AppConnection? Find(string appId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(appId, out var e) ? e.Connection : null;
        }
    }

    public bool Verify(string appId, string secret)
    {
        byte[] expected;
        lock (_lock)
        {
            if (!_entries.TryGetValue(appId, out var e))
            {
                return false;
            }

            expected = e.Secret;
        }

        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(secret));
    }

    public IReadOnlyList<string> GrantedTo(string appId) => Find(appId)?.GrantedToPeer ?? [];

    /// <summary>Geheimnis der Kopplung für Anfragen als Abfrager (Stufe 3); null ohne Kopplung. Nie protokollieren.</summary>
    public string? Secret(string appId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(appId, out var e) ? Encoding.UTF8.GetString(e.Secret) : null;
        }
    }

    /// <summary>Was die andere App Lumora erlaubt (aus <c>grantedToYou</c>, Abschnitt 5 Schritt 7).</summary>
    public void SetGrantedByPeer(string appId, IEnumerable<string> rights)
    {
        var known = rights.Where(Rights.IsKnown).Distinct(StringComparer.Ordinal).ToList();
        lock (_lock)
        {
            if (!_entries.TryGetValue(appId, out var e) || e.Connection.GrantedByPeer.SequenceEqual(known, StringComparer.Ordinal))
            {
                return;
            }

            _entries[appId] = e with { Connection = e.Connection with { GrantedByPeer = known } };
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Neue Kopplung (ersetzt eine alte derselben App) mit frischem Geheimnis.</summary>
    public void Add(AppConnection connection, string secret)
    {
        lock (_lock)
        {
            _entries[connection.AppId] = new Entry(connection, Encoding.UTF8.GetBytes(secret));
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Trennen: Geheimnis und Rechte weg (Abschnitt 5, Schritt 6). true, wenn es die Kopplung gab.</summary>
    public bool Remove(string appId)
    {
        lock (_lock)
        {
            if (!_entries.Remove(appId))
            {
                return false;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>32 Zufallsbytes, Base64url ohne Auffüllung (Abschnitt 5, Schritt 4).</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredConnection>>(File.ReadAllText(_path), AppConnectContract.RegistrationJson) ?? [];
            foreach (var s in stored)
            {
                try
                {
                    var secret = _protector.Unprotect(Convert.FromBase64String(s.ProtectedSecret));
                    _entries[s.AppId] = new Entry(new AppConnection(s.AppId, s.DisplayName, s.GrantedToPeer ?? [], s.GrantedByPeer ?? [], s.PairedAtUtc), secret);
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    // anderer Benutzer/PC oder beschädigt — diese Kopplung gilt als getrennt
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // unlesbar: ohne Kopplungen weiter (neu koppeln)
        }
    }

    private void Save()
    {
        var stored = _entries.Values.Select(e => new StoredConnection(
            e.Connection.AppId, e.Connection.DisplayName, Convert.ToBase64String(_protector.Protect(e.Secret)),
            e.Connection.GrantedToPeer, e.Connection.GrantedByPeer, e.Connection.PairedAtUtc)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = $"{_path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, AppConnectContract.RegistrationJson));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed record Entry(AppConnection Connection, byte[] Secret);

    private sealed record StoredConnection(string AppId, string DisplayName, string ProtectedSecret, IReadOnlyList<string>? GrantedToPeer, IReadOnlyList<string>? GrantedByPeer, DateTime PairedAtUtc);
}
