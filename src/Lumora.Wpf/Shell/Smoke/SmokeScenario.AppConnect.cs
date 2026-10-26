using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Lumora.AppConnect;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „App-Verbindung“ (ADR-0073, Stufe 1): Lumora hat sich unter der Rauchtest-Wurzel angemeldet
/// (<c>AppVerbindung\apps\lumora.json</c>, laufend, mit Port, ohne Geheimnis), die Schnittstelle auf 127.0.0.1 beantwortet
/// <c>GET /av/v1/info</c> und weist eine Anfrage mit <c>Origin</c> ab. Der echte Ordner unter <c>%LocalAppData%</c> bleibt
/// unberührt.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task AppConnectAsync()
    {
        var files = new RegistrationFiles(RegistrationFiles.ResolveFolder(_paths.AppDataRoot));
        AppRegistration? registration = null;
        for (var i = 0; i < 60 && registration is not { Running: true, Port: > 0 }; i++)
        {
            registration = files.Read(AppConnectContract.AppId);
            if (registration is not { Running: true, Port: > 0 })
            {
                await Task.Delay(250).ConfigureAwait(true);
            }
        }

        Check(registration is { Running: true, Port: > 0, Mode: "window", ContractVersion: "1.1" }
              && registration.Pid == Environment.ProcessId,
            $"Anmeldedatei unter der Rauchtest-Wurzel: läuft, Port, Modus window, Vertrag 1.1 (Port {registration?.Port})");
        var text = File.ReadAllText(files.PathOf(AppConnectContract.AppId));
        Check(!text.Contains("secret", StringComparison.OrdinalIgnoreCase), "kein Geheimnis in der Anmeldedatei");

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{registration!.Port}") };
        using var info = await http.GetAsync("/av/v1/info").ConfigureAwait(true);
        var json = JsonDocument.Parse(await info.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Check(info.IsSuccessStatusCode
              && json.GetProperty("appId").GetString() == "lumora"
              && json.GetProperty("contractVersion").GetString() == "1.1"
              && !json.TryGetProperty("grantedToYou", out _),
            $"GET /av/v1/info: {(int)info.StatusCode}, Lumora {json.GetProperty("appVersion").GetString()}, ohne Geheimnis keine Rechte");

        using var browser = new HttpRequestMessage(HttpMethod.Get, "/av/v1/info");
        browser.Headers.Add("Origin", "https://example.org");
        using var refused = await http.SendAsync(browser).ConfigureAwait(true);
        var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Check((int)refused.StatusCode == 403 && error.GetProperty("error").GetString() == ErrorCodes.ForbiddenOrigin,
            "Anfrage mit Origin → 403 forbidden_origin");
    }
}
