using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.AppConnect;

/// <summary>Fehler der Gegenseite nach Abschnitt 11 (Code aus dem Vertrag; Text nur fürs Protokoll, ohne Pfade).</summary>
public sealed class AppConnectException(string code, string message, HttpStatusCode? status = null) : Exception(message)
{
    public string Code { get; } = code;

    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Wer fragt: App-Kennung, Name und Rechte (Standard Lumora; Tests geben eine andere Kennung).</summary>
public sealed record AppConnectClientIdentity(string AppId, string DisplayName, IReadOnlyList<string> Provides, IReadOnlyList<string> Wants)
{
    public static AppConnectClientIdentity Lumora { get; } =
        new(AppConnectContract.AppId, AppConnectContract.DisplayName, AppConnectContract.Provides, AppConnectContract.Wants);
}

/// <summary>Wartezeiten des Abfragers (Vertrag: Status höchstens jede Sekunde, höchstens 5 Minuten; Start höchstens 20 s).</summary>
public sealed record AppConnectClientTimings(TimeSpan PollInterval, TimeSpan PairingTimeout, TimeSpan StartTimeout)
{
    public static AppConnectClientTimings Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(20));
}

/// <summary>Ende einer Kopplungsanfrage aus Sicht des Abfragers.</summary>
public enum PairingOutcome
{
    Accepted,
    Rejected,
    Expired,
}

/// <summary>
/// Lumora als Abfrager (ADR-0073, Stufe 3; Vertrag 1.1 Abschnitte 4, 5, 9): andere Apps über ihre Anmeldedatei finden, mit
/// sechsstelligem Code koppeln, Rechte abfragen, trennen. Antwortet die Gegenseite auf eine Anfrage mit Geheimnis mit 401
/// <c>not_paired</c>, gilt die Kopplung als getrennt und wird auch hier gelöscht. Nie Geheimnisse ins Protokoll.
/// </summary>
public sealed class AppConnectClient
{
    private readonly RegistrationFiles _registrations;
    private readonly ConnectionStore _connections;
    private readonly HttpClient _http;
    private readonly AppConnectClientIdentity _own;
    private readonly AppConnectClientTimings _timings;
    private readonly Func<string, bool> _start;
    private readonly Func<string, string, bool> _startInBackground;
    private readonly ILogger _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _held = new(StringComparer.Ordinal);

    /// <param name="start">Startet eine App normal (ohne Hintergrund-Schalter) über ihren <c>exePath</c>; Tests geben einen Fake.</param>
    /// <param name="startInBackground">Startet eine App mit Befehlszeile (Hintergrundstart, Abschnitt 9); Tests geben einen Fake.</param>
    public AppConnectClient(RegistrationFiles registrations, ConnectionStore connections, HttpClient? http = null,
        AppConnectClientIdentity? own = null, AppConnectClientTimings? timings = null, Func<string, bool>? start = null, ILogger? logger = null,
        Func<string, string, bool>? startInBackground = null)
    {
        _startInBackground = startInBackground ?? StartWith;
        _registrations = registrations;
        _connections = connections;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _own = own ?? AppConnectClientIdentity.Lumora;
        _timings = timings ?? AppConnectClientTimings.Default;
        _start = start ?? (exe => StartWith(exe, string.Empty));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Wie lange <see cref="PairAsync"/> höchstens auf die Bestätigung wartet (Oberfläche: „läuft ab um …“).</summary>
    public TimeSpan PairingTimeout => _timings.PairingTimeout;

    /// <summary>Andere Apps mit Anmeldedatei (laufend oder nicht).</summary>
    public IReadOnlyList<AppRegistration> Peers() => _registrations.Others(_own.AppId);

    /// <summary>Sechsstelliger Code für den Kopplungsdialog der Gegenseite (Abschnitt 5, Schritt 1).</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public static bool SameMajor(string? contractVersion)
        => int.TryParse(contractVersion?.Split('.')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
           && major == AppConnectContract.Major;

    /// <summary>Was Lumora bei der Gegenseite erfragt (eigene Wünsche, die sie anbietet) bzw. ihr anbietet (was sie möchte).</summary>
    public (IReadOnlyList<string> Requested, IReadOnlyList<string> Offered) RightsFor(AppRegistration peer)
    {
        var requested = _own.Wants.Where(peer.Provides.Contains).ToList();
        var offered = (peer.Wants.Count > 0 ? _own.Provides.Where(peer.Wants.Contains) : _own.Provides).ToList();
        return (requested, offered);
    }

    /// <summary>
    /// Sorgt dafür, dass die Gegenseite läuft: sonst über ihren <c>exePath</c> normal starten (Abschnitt 5, Ränder — der
    /// Hintergrundstart ist erst nach der Kopplung erlaubt) und höchstens 20 s auf die Anmeldedatei mit Port warten.
    /// </summary>
    public async Task<AppRegistration> EnsureRunningAsync(string appId, CancellationToken ct = default)
    {
        var registration = _registrations.Read(appId) ?? throw new AppConnectException(ErrorCodes.NotFound, $"Keine Anmeldedatei für {appId}.");
        if (RegistrationFiles.IsAlive(registration))
        {
            return registration;
        }

        if (!_start(registration.ExePath))
        {
            throw new AppConnectException(ErrorCodes.Unavailable, $"{registration.DisplayName} ließ sich nicht starten.");
        }

        _logger.LogInformation("App-Verbindung: {AppId} gestartet, warte auf Bereitschaft", appId);
        return await WaitReadyAsync(registration, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sorgt dafür, dass die Gegenseite läuft — wenn nötig im Hintergrund (Abschnitt 9): nur mit dem Recht
    /// <c>background.start</c>, das sie Lumora gegeben hat; Befehlszeile <c>&lt;backgroundArg&gt; --started-by lumora:&lt;pid&gt;</c>.
    /// </summary>
    public async Task<AppRegistration> EnsureRunningInBackgroundAsync(string appId, CancellationToken ct = default)
    {
        var registration = _registrations.Read(appId) ?? throw new AppConnectException(ErrorCodes.NotFound, $"Keine Anmeldedatei für {appId}.");
        if (RegistrationFiles.IsAlive(registration))
        {
            return registration;
        }

        if (_connections.Find(appId) is not { } connection || !connection.GrantedByPeer.Contains(Rights.BackgroundStart, StringComparer.Ordinal))
        {
            throw new AppConnectException(ErrorCodes.MissingRight, $"{registration.DisplayName} erlaubt Lumora keinen Hintergrundstart.");
        }

        var arguments = BackgroundLaunch.ArgumentsFor(registration.BackgroundArg, _own.AppId, Environment.ProcessId);
        if (string.IsNullOrWhiteSpace(registration.BackgroundArg) || !_startInBackground(registration.ExePath, arguments))
        {
            throw new AppConnectException(ErrorCodes.Unavailable, $"{registration.DisplayName} ließ sich nicht im Hintergrund starten.");
        }

        _logger.LogInformation("App-Verbindung: {AppId} im Hintergrund gestartet, warte auf Bereitschaft", appId);
        return await WaitReadyAsync(registration, ct).ConfigureAwait(false);
    }

    /// <summary>Fenster der Gegenseite zeigen: normal starten — läuft sie schon, holt ihr zweiter Start das Fenster nach vorn (Abschnitt 9).</summary>
    public bool Open(string appId)
        => _registrations.Read(appId) is { } registration && _start(registration.ExePath);

    /// <summary>Hält Lumora gerade eine Zusage bei <paramref name="appId"/>?</summary>
    public bool Holds(string appId) => _held.ContainsKey(appId);

    /// <summary>
    /// Halte-Zusage nehmen bzw. verlängern (Abschnitt 7): „Lumora braucht dich noch <paramref name="seconds"/> Sekunden“. Eine
    /// abgelaufene Zusage wird neu genommen.
    /// </summary>
    public async Task<LeaseResponse> HoldAsync(string appId, int seconds, CancellationToken ct = default)
    {
        var peer = _registrations.Read(appId) ?? throw new AppConnectException(ErrorCodes.NotFound, $"Keine Anmeldedatei für {appId}.");
        var body = new LeaseRequest { Seconds = seconds };
        if (_held.TryGetValue(appId, out var leaseId))
        {
            using var extended = await SendAsync(peer, HttpMethod.Put, "/lease/" + Uri.EscapeDataString(leaseId), body, withSecret: true, ct).ConfigureAwait(false);
            if (extended.StatusCode != HttpStatusCode.NotFound)
            {
                return await ReadAsync<LeaseResponse>(extended, peer, ct).ConfigureAwait(false);
            }
        }

        using var created = await SendAsync(peer, HttpMethod.Post, "/lease", body, withSecret: true, ct).ConfigureAwait(false);
        var lease = await ReadAsync<LeaseResponse>(created, peer, ct).ConfigureAwait(false);
        _held[appId] = lease.LeaseId;
        return lease;
    }

    /// <summary>
    /// Zusage freigeben („Im Hintergrund beenden“, Herbert 2026-10-27): die Gegenseite beendet sich danach selbst nach ihren
    /// Regeln (Abschnitt 9). Lumora vergisst die Zusage auch, wenn die Gegenseite nicht erreichbar ist.
    /// </summary>
    public async Task ReleaseAsync(string appId, CancellationToken ct = default)
    {
        if (!_held.TryRemove(appId, out var leaseId))
        {
            return;
        }

        try
        {
            if (_registrations.Read(appId) is { } peer && RegistrationFiles.IsAlive(peer))
            {
                using var response = await SendAsync(peer, HttpMethod.Delete, "/lease/" + Uri.EscapeDataString(leaseId), null, withSecret: true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AppConnectException)
        {
            _logger.LogInformation("App-Verbindung: {AppId} beim Freigeben der Zusage nicht erreichbar", appId);
        }
    }

    private async Task<AppRegistration> WaitReadyAsync(AppRegistration registration, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < _timings.StartTimeout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, _timings.StartTimeout.TotalMilliseconds / 4)), ct).ConfigureAwait(false);
            if (_registrations.Read(registration.AppId) is { } fresh && RegistrationFiles.IsAlive(fresh))
            {
                return fresh;
            }
        }

        throw new AppConnectException(ErrorCodes.Unavailable, $"{registration.DisplayName} ist nach dem Start nicht bereit.");
    }

    /// <summary><c>GET /info</c> — mit Geheimnis, wenn gekoppelt (dann mit <c>grantedToYou</c>).</summary>
    public async Task<InfoResponse> InfoAsync(AppRegistration peer, CancellationToken ct = default)
    {
        using var response = await SendAsync(peer, HttpMethod.Get, "/info", null, withSecret: _connections.Find(peer.AppId) is not null, ct).ConfigureAwait(false);
        return await ReadAsync<InfoResponse>(response, peer, ct).ConfigureAwait(false);
    }

    /// <summary>Sendet die Kopplungsanfrage (die Gegenseite zeigt dann einen Dialog mit demselben Code); liefert die <c>pairingId</c>.</summary>
    public async Task<string> BeginPairingAsync(AppRegistration peer, string code, CancellationToken ct = default)
    {
        var info = await InfoAsync(peer, ct).ConfigureAwait(false);
        if (!SameMajor(info.ContractVersion))
        {
            throw new AppConnectException(ErrorCodes.ContractMismatch, $"{peer.DisplayName} setzt Vertrag {info.ContractVersion} um, Lumora {AppConnectContract.Version}.");
        }

        var (requested, offered) = RightsFor(peer);
        var body = new PairingRequest { AppId = _own.AppId, DisplayName = _own.DisplayName, Code = code, RequestedRights = requested, OfferedRights = offered };
        using var response = await SendAsync(peer, HttpMethod.Post, "/pairing", body, withSecret: false, ct).ConfigureAwait(false);
        var created = await ReadAsync<PairingCreatedResponse>(response, peer, ct).ConfigureAwait(false);
        _logger.LogInformation("App-Verbindung: Kopplung bei {AppId} angefragt", peer.AppId);
        return created.PairingId;
    }

    /// <summary><c>GET /pairing/{id}</c> — ohne Geheimnis, nur mit <c>X-AV-App</c> (Abschnitt 5, Schritt 3).</summary>
    public async Task<PairingStatusResponse> PairingStatusAsync(AppRegistration peer, string pairingId, CancellationToken ct = default)
    {
        using var response = await SendAsync(peer, HttpMethod.Get, "/pairing/" + Uri.EscapeDataString(pairingId), null, withSecret: false, ct).ConfigureAwait(false);
        return await ReadAsync<PairingStatusResponse>(response, peer, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ganze Kopplung: Anfrage senden, Status höchstens jede Sekunde und höchstens 5 Minuten abfragen, bei „accepted“ speichern.
    /// <paramref name="code"/> zeigt die Oberfläche an, damit der Benutzer ihn mit dem Dialog der Gegenseite vergleicht.
    /// </summary>
    public async Task<PairingOutcome> PairAsync(AppRegistration peer, string code, CancellationToken ct = default)
    {
        var pairingId = await BeginPairingAsync(peer, code, ct).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _timings.PairingTimeout)
        {
            await Task.Delay(_timings.PollInterval, ct).ConfigureAwait(false);
            PairingStatusResponse status;
            try
            {
                status = await PairingStatusAsync(peer, pairingId, ct).ConfigureAwait(false);
            }
            catch (AppConnectException ex) when (ex.Code == ErrorCodes.NotFound)
            {
                return PairingOutcome.Expired; // Gegenseite hat die Anfrage vergessen (neu gestartet oder lange abgelaufen)
            }

            switch (status.Status)
            {
                case "accepted":
                    CompletePairing(peer, status);
                    return PairingOutcome.Accepted;
                case "rejected":
                    _logger.LogInformation("App-Verbindung: {AppId} hat die Kopplung abgelehnt", peer.AppId);
                    return PairingOutcome.Rejected;
                case "expired":
                    return PairingOutcome.Expired;
            }
        }

        return PairingOutcome.Expired;
    }

    /// <summary>Speichert die angenommene Kopplung: was die Gegenseite erlaubt und was sie von Lumora nutzen möchte.</summary>
    public void CompletePairing(AppRegistration peer, PairingStatusResponse status)
    {
        if (status.Status != "accepted" || status.Secret is not { Length: >= 22 } secret || !IsBase64Url(secret))
        {
            throw new AppConnectException(ErrorCodes.BadRequest, $"{peer.DisplayName}: Antwort ohne gültiges Geheimnis.");
        }

        var grantedByPeer = (status.GrantedRights ?? []).Where(Rights.IsKnown).Distinct(StringComparer.Ordinal).ToList();
        var grantedToPeer = (status.AcceptedOfferedRights ?? []).Where(_own.Provides.Contains).Distinct(StringComparer.Ordinal).ToList();
        _connections.Add(new AppConnection(peer.AppId, peer.DisplayName, grantedToPeer, grantedByPeer, DateTime.UtcNow), secret);
        _logger.LogInformation("App-Verbindung: mit {AppId} gekoppelt, {Granted} Rechte erhalten, {Offered} erlaubt",
            peer.AppId, grantedByPeer.Count, grantedToPeer.Count);
    }

    /// <summary>Fragt ab, was die Gegenseite Lumora erlaubt (<c>grantedToYou</c>), und merkt es sich (Abschnitt 5, Schritt 7).</summary>
    public async Task RefreshGrantsAsync(AppRegistration peer, CancellationToken ct = default)
    {
        if (_connections.Find(peer.AppId) is null)
        {
            return;
        }

        var info = await InfoAsync(peer, ct).ConfigureAwait(false);
        if (info.GrantedToYou is not { } granted)
        {
            // /info ist ohne Geheimnis erreichbar (Abschnitt 3) und nennt grantedToYou nur „mit gültigem Geheimnis“ (Abschnitt 7) —
            // fehlt es trotz mitgesendetem Geheimnis, kennt die Gegenseite die Kopplung nicht mehr: wie 401 not_paired behandeln
            _connections.Remove(peer.AppId);
            _logger.LogInformation("App-Verbindung: {AppId} kennt die Kopplung nicht mehr — getrennt", peer.AppId);
            throw new AppConnectException(ErrorCodes.NotPaired, $"{peer.DisplayName} kennt die Kopplung nicht mehr.");
        }

        _connections.SetGrantedByPeer(peer.AppId, granted);
    }

    /// <summary>Trennt die Kopplung (Abschnitt 5, Schritt 6): Gegenseite benachrichtigen, wenn sie läuft; lokal immer löschen.</summary>
    public async Task UnpairAsync(string appId, CancellationToken ct = default)
    {
        var peer = _registrations.Read(appId);
        try
        {
            if (peer is not null && RegistrationFiles.IsAlive(peer) && _connections.Find(appId) is not null)
            {
                using var response = await SendAsync(peer, HttpMethod.Delete, "/pairing", null, withSecret: true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AppConnectException)
        {
            _logger.LogInformation("App-Verbindung: {AppId} beim Trennen nicht erreichbar", appId);
        }

        _held.TryRemove(appId, out _);
        _connections.Remove(appId);
        _logger.LogInformation("App-Verbindung: Kopplung mit {AppId} getrennt", appId);
    }

    // --- Transport --------------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(AppRegistration peer, HttpMethod method, string path, object? body, bool withSecret, CancellationToken ct)
    {
        if (peer.Port is not int port || !RegistrationFiles.IsAlive(peer))
        {
            throw new AppConnectException(ErrorCodes.Unavailable, $"{peer.DisplayName} läuft nicht.");
        }

        // Nur 127.0.0.1 (Abschnitt 3); der Host-Kopf ergibt sich daraus als 127.0.0.1:<port>
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{AppConnectContract.BasePath}{path}");
        request.Headers.Add(AppConnectContract.AppHeader, _own.AppId);
        if (withSecret && _connections.Secret(peer.AppId) is { } secret)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: AppConnectContract.Json);
        }

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized && withSecret)
        {
            // Gegenseite hat getrennt (Abschnitt 5, Ränder): eigene Kopplung ebenfalls löschen
            _connections.Remove(peer.AppId);
            _logger.LogInformation("App-Verbindung: {AppId} kennt die Kopplung nicht mehr — getrennt", peer.AppId);
        }

        return response;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, AppRegistration peer, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            ErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ErrorResponse>(AppConnectContract.Json, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // kein Fehlerrumpf nach Abschnitt 11
            }

            throw new AppConnectException(error?.Error ?? ErrorCodes.BadRequest, error?.Message ?? $"{peer.DisplayName}: HTTP {(int)response.StatusCode}", response.StatusCode);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(AppConnectContract.Json, ct).ConfigureAwait(false)
                   ?? throw new AppConnectException(ErrorCodes.BadRequest, $"{peer.DisplayName}: leere Antwort");
        }
        catch (JsonException ex)
        {
            throw new AppConnectException(ErrorCodes.BadRequest, $"{peer.DisplayName}: Antwort passt nicht zum Vertrag ({ex.GetType().Name})");
        }
    }

    private static bool IsBase64Url(string text) => text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Starten mit Befehlszeile (leer = normal mit Fenster) — nur eine vorhandene <c>.exe</c> aus der Anmeldedatei.</summary>
    private static bool StartWith(string exePath, string arguments)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exePath))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath) });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
