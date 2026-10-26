using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.AppConnect;

/// <summary>Was die Schnittstelle über Lumora sagt (Abschnitt 7, <c>GET /info</c>).</summary>
public sealed record AppConnectIdentity(string AppVersion);

/// <summary>
/// Lokale Schnittstelle nach Vertrag „App-Verbindung“ 1.1 (ADR-0073): HTTP nur auf <c>127.0.0.1</c>, Basispfad
/// <c>/av/v1</c>, Schutz gegen Browser-Angriffe (Abschnitt 3), Fehler nach Abschnitt 11. Stufe 1 beantwortet nur
/// <c>GET /info</c>; alles andere braucht eine Kopplung (Stufe 2) und endet bis dahin mit 401 <c>not_paired</c>.
/// </summary>
public sealed partial class AppConnectServer : IAsyncDisposable
{
    /// <summary>Mehr Anfragen je Sekunde → 429 (Abschnitt 11).</summary>
    public const int MaxRequestsPerSecond = 50;

    private const string PeerKey = "av.peer";

    private readonly AppConnectIdentity _identity;
    private readonly IAppConnectPeers _peers;
    private readonly Func<string> _mode;
    private readonly Func<long> _second;
    private readonly ILogger _logger;
    private readonly Lock _rateLock = new();
    private WebApplication? _app;
    private long _rateSecond = -1;
    private int _rateCount;

    /// <param name="mode">Aktueller Modus für <c>GET /info</c> (<c>window</c> | <c>background</c>).</param>
    /// <param name="second">Laufende Sekunde für die Anfragegrenze (Tests geben eine eigene Uhr).</param>
    public AppConnectServer(AppConnectIdentity identity, IAppConnectPeers peers, Func<string> mode, ILogger? logger = null, Func<long>? second = null)
    {
        _identity = identity;
        _peers = peers;
        _mode = mode;
        _logger = logger ?? NullLogger.Instance;
        _second = second ?? (() => Environment.TickCount64 / 1000);
    }

    /// <summary>Port, auf dem die Schnittstelle Anfragen annimmt; 0 vor dem Start.</summary>
    public int Port { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>Startet auf <c>127.0.0.1</c> mit einem freien Port (Abschnitt 3); danach nimmt sie Anfragen an.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(IPAddress.Loopback, 0); // nie 0.0.0.0, nie „localhost“
        });
        var app = builder.Build();
        app.Use(GuardAsync);
        MapEndpoints(app);
        await app.StartAsync(ct).ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
        _app = app;
        _logger.LogInformation("App-Verbindung: Schnittstelle bereit (Vertrag {Version})", AppConnectContract.Version);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not { } app)
        {
            return;
        }

        _app = null;
        Port = 0;
        // offene Anfragen nicht abwarten, die womöglich am UI-Thread hängen
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Beenden nicht aufhalten
        }

        await app.DisposeAsync().ConfigureAwait(false);
    }

    // --- Schutz und Anmeldung (Abschnitte 3 und 5) ----------------------------------------------------------------

    private async Task GuardAsync(HttpContext ctx, RequestDelegate next)
    {
        var request = ctx.Request;
        if (request.Headers.ContainsKey("Origin"))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, ErrorCodes.ForbiddenOrigin, "Anfragen aus Webseiten (Kopfzeile Origin) sind nicht erlaubt.").ConfigureAwait(false);
            return;
        }

        if (request.Host.Value != $"127.0.0.1:{Port}")
        {
            await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, ErrorCodes.ForbiddenHost, "Kopfzeile Host muss 127.0.0.1:<port> sein.").ConfigureAwait(false);
            return;
        }

        if (!TakeRateToken())
        {
            ctx.Response.Headers.RetryAfter = "1";
            await WriteErrorAsync(ctx, StatusCodes.Status429TooManyRequests, ErrorCodes.TooManyRequests, "Zu viele Anfragen.").ConfigureAwait(false);
            return;
        }

        var path = request.Path.Value ?? string.Empty;
        if (!path.StartsWith(AppConnectContract.BasePath + "/", StringComparison.Ordinal))
        {
            if (OtherVersion().IsMatch(path) && path != AppConnectContract.BasePath)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status426UpgradeRequired, ErrorCodes.ContractMismatch, $"Vertragsversion passt nicht; Lumora setzt {AppConnectContract.Version} um.").ConfigureAwait(false);
            }
            else
            {
                await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Unbekannter Pfad.").ConfigureAwait(false);
            }

            return;
        }

        var peer = Authenticate(request);
        if (peer is not null)
        {
            ctx.Items[PeerKey] = peer;
        }

        if (!IsOpen(request.Method, path) && peer is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized, ErrorCodes.NotPaired, "Nicht gekoppelt oder Geheimnis ungültig.").ConfigureAwait(false);
            return;
        }

        await next(ctx).ConfigureAwait(false);
    }

    /// <summary>Ohne Geheimnis erreichbar: <c>GET /info</c>, <c>POST /pairing</c>, <c>GET /pairing/{id}</c> (Abschnitt 3).</summary>
    internal static bool IsOpen(string method, string path)
        => (HttpMethods.IsGet(method) && path == AppConnectContract.BasePath + "/info")
           || (HttpMethods.IsPost(method) && path == AppConnectContract.BasePath + "/pairing")
           || (HttpMethods.IsGet(method) && path.StartsWith(AppConnectContract.BasePath + "/pairing/", StringComparison.Ordinal));

    private string? Authenticate(HttpRequest request)
    {
        var app = request.Headers[AppConnectContract.AppHeader].ToString();
        var auth = request.Headers.Authorization.ToString();
        if (app.Length == 0 || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return null;
        }

        return _peers.Verify(app, auth["Bearer ".Length..].Trim()) ? app : null;
    }

    private bool TakeRateToken()
    {
        var second = _second();
        lock (_rateLock)
        {
            if (second != _rateSecond)
            {
                _rateSecond = second;
                _rateCount = 0;
            }

            return ++_rateCount <= MaxRequestsPerSecond;
        }
    }

    [GeneratedRegex(@"^/av/v\d+(/|$)")]
    private static partial Regex OtherVersion();

    // --- Endpunkte ------------------------------------------------------------------------------------------------

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet(AppConnectContract.BasePath + "/info", Info);
        app.MapFallback(() => Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Unbekannter Pfad."));
    }

    private IResult Info(HttpContext ctx)
    {
        var peer = ctx.Items[PeerKey] as string;
        return Results.Json(new InfoResponse
        {
            AppId = AppConnectContract.AppId,
            DisplayName = AppConnectContract.DisplayName,
            AppVersion = _identity.AppVersion,
            ContractVersion = AppConnectContract.Version,
            Provides = AppConnectContract.Provides,
            GrantedToYou = peer is null ? null : _peers.GrantedTo(peer),
            Mode = peer is null ? null : _mode(),
        }, AppConnectContract.Json);
    }

    private static IResult Error(int status, string code, string message)
        => Results.Json(new ErrorResponse(code, message), AppConnectContract.Json, statusCode: status);

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new ErrorResponse(code, message), AppConnectContract.Json).ConfigureAwait(false);
    }
}
