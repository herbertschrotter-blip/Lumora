using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.AppConnect;

/// <summary>Ergebnis von <see cref="PairingCoordinator.Begin"/>.</summary>
public enum PairingBeginResult
{
    Started,

    /// <summary>Anfrage unvollständig oder ungültig → 400 <c>bad_request</c>.</summary>
    Invalid,

    /// <summary>Schon eine offene Anfrage → 429 <c>too_many_requests</c>.</summary>
    Busy,
}

/// <summary>
/// Kopplung als Anbieter (Abschnitt 5): höchstens eine offene Anfrage, Dialog für den Benutzer, Geheimnis bei Annahme. Eine
/// Anfrage gilt 5 Minuten; danach meldet der Status <c>expired</c> (auch wenn angenommen — das Geheimnis ist nicht mehr
/// abrufbar) und nach weiteren 5 Minuten 404. Der Status gehört nur der App, die gefragt hat.
/// </summary>
public sealed partial class PairingCoordinator
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConnectionStore _store;
    private readonly IPairingPrompt _prompt;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action<string>? _onPaired;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Pending> _requests = new(StringComparer.Ordinal);

    /// <param name="onPaired">Nach einer neuen Kopplung (z. B. alte Halte-Zusagen derselben App verwerfen).</param>
    public PairingCoordinator(ConnectionStore store, IPairingPrompt prompt, TimeProvider? time = null, ILogger? logger = null, Action<string>? onPaired = null)
    {
        _onPaired = onPaired;
        _store = store;
        _prompt = prompt;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Nimmt eine Anfrage an und fragt den Benutzer (im Hintergrund).</summary>
    public PairingBeginResult Begin(PairingRequest request, out string? pairingId)
    {
        pairingId = null;
        if (!IsValid(request))
        {
            return PairingBeginResult.Invalid;
        }

        Pending pending;
        lock (_lock)
        {
            Cleanup();
            if (_requests.Values.Any(p => p.State == "pending" && !IsExpired(p)))
            {
                return PairingBeginResult.Busy;
            }

            pending = new Pending(Guid.NewGuid().ToString("N"), request, _time.GetUtcNow(), new CancellationTokenSource());
            _requests[pending.Id] = pending;
        }

        pairingId = pending.Id;
        _logger.LogInformation("App-Verbindung: Kopplungsanfrage von {AppId}", request.AppId);
        _ = AskAsync(pending);
        return PairingBeginResult.Started;
    }

    /// <summary>Status für <paramref name="appId"/>; null = unbekannt, fremd oder lange abgelaufen (404).</summary>
    public PairingStatusResponse? Status(string pairingId, string appId)
    {
        lock (_lock)
        {
            Cleanup();
            if (!_requests.TryGetValue(pairingId, out var p) || !string.Equals(p.Request.AppId, appId, StringComparison.Ordinal))
            {
                return null;
            }

            if (IsExpired(p))
            {
                return new PairingStatusResponse { Status = "expired" };
            }

            return p.State == "accepted"
                ? new PairingStatusResponse { Status = "accepted", Secret = p.Secret, GrantedRights = p.Decision!.GrantedRights, AcceptedOfferedRights = p.Decision.AcceptedOfferedRights }
                : new PairingStatusResponse { Status = p.State };
        }
    }

    private async Task AskAsync(Pending pending)
    {
        PairingDecision? decision = null;
        using var expiry = new CancellationTokenSource(Lifetime, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, pending.Cancel.Token);
        try
        {
            var info = new PairingPromptInfo(pending.Request.AppId, pending.Request.DisplayName.Trim(), pending.Request.Code,
                PairingPermissions.Lines(pending.Request.RequestedRights, pending.Request.OfferedRights));
            decision = await _prompt.AskAsync(info, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            decision = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "App-Verbindung: Kopplungsdialog fehlgeschlagen");
        }

        lock (_lock)
        {
            if (IsExpired(pending))
            {
                return; // zu spät entschieden — der Status sagt „expired“
            }

            if (decision is null)
            {
                pending.State = "rejected";
                _logger.LogInformation("App-Verbindung: Kopplung mit {AppId} abgelehnt", pending.Request.AppId);
                return;
            }

            // Nur, was angefragt bzw. angeboten wurde
            var granted = decision.GrantedRights.Intersect(pending.Request.RequestedRights, StringComparer.Ordinal).ToList();
            var accepted = decision.AcceptedOfferedRights.Intersect(pending.Request.OfferedRights, StringComparer.Ordinal).ToList();
            pending.Decision = new PairingDecision(granted, accepted);
            pending.Secret = ConnectionStore.NewSecret();
            pending.State = "accepted";
        }

        _store.Add(new AppConnection(pending.Request.AppId, pending.Request.DisplayName.Trim(), pending.Decision.GrantedRights,
            pending.Decision.AcceptedOfferedRights, _time.GetUtcNow().UtcDateTime), pending.Secret);
        _onPaired?.Invoke(pending.Request.AppId);
        _logger.LogInformation("App-Verbindung: gekoppelt mit {AppId}, {Granted} Rechte erlaubt, {Accepted} angenommen",
            pending.Request.AppId, pending.Decision.GrantedRights.Count, pending.Decision.AcceptedOfferedRights.Count);
    }

    private bool IsExpired(Pending p) => _time.GetUtcNow() - p.CreatedUtc >= Lifetime;

    /// <summary>Nach weiteren 5 Minuten ganz vergessen (dann 404).</summary>
    private void Cleanup()
    {
        var now = _time.GetUtcNow();
        foreach (var gone in _requests.Values.Where(p => now - p.CreatedUtc >= Lifetime + Lifetime).ToList())
        {
            gone.Cancel.Cancel();
            gone.Cancel.Dispose();
            _requests.Remove(gone.Id);
        }
    }

    private static bool IsValid(PairingRequest request)
        => AppIdPattern().IsMatch(request.AppId ?? string.Empty)
           && request.AppId != AppConnectContract.AppId
           && CodePattern().IsMatch(request.Code ?? string.Empty)
           && !string.IsNullOrWhiteSpace(request.DisplayName)
           && request.DisplayName.Length <= 80
           && request.RequestedRights is not null
           && request.OfferedRights is not null;

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex AppIdPattern();

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex CodePattern();

    private sealed class Pending(string id, PairingRequest request, DateTimeOffset createdUtc, CancellationTokenSource cancel)
    {
        public string Id { get; } = id;

        public PairingRequest Request { get; } = request;

        public DateTimeOffset CreatedUtc { get; } = createdUtc;

        public CancellationTokenSource Cancel { get; } = cancel;

        public string State { get; set; } = "pending";

        public string? Secret { get; set; }

        public PairingDecision? Decision { get; set; }
    }
}
