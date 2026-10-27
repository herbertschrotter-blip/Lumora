using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Appearance;
using Lumora.AppConnect;
using Lumora.Settings;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „App-Verbindung“ (ADR-0073). Stufe 1: Lumora hat sich unter der Rauchtest-Wurzel angemeldet
/// (<c>AppVerbindung\apps\lumora.json</c>, laufend, mit Port, ohne Geheimnis), die Schnittstelle auf 127.0.0.1 beantwortet
/// <c>GET /av/v1/info</c> und weist eine Anfrage mit <c>Origin</c> ab. Stufe 2: eine Test-App koppelt per HTTP, der Dialog
/// (Mockup 11) zeigt Code und Rechte (hell/dunkel), „Verbinden“ → Geheimnis abgeholt, <c>/info</c> mit Rechten, Trennen.
/// Stufe 4: der Reiter „Verbundene Apps“ zeigt die gekoppelte Test-App mit ihren Rechten und meldet das Trennen.
/// Stufe 5: ohne freigegebene Bibliothek findet die Test-App nichts; mit Häkchen im Reiter Fotos, ein echtes Vorschaubild
/// (JPEG, ETag/304) und das Original.
/// Der echte Ordner unter <c>%LocalAppData%</c> bleibt unberührt.
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

        await PairingAsync(http).ConfigureAwait(true);
    }

    /// <summary>Stufe 2: Kopplung als Anbieter mit Dialog (Mockup 11), Geheimnis, Rechte, Trennen.</summary>
    private async Task PairingAsync(HttpClient http)
    {
        var app = (App)Application.Current;
        using var begin = await http.PostAsJsonAsync("/av/v1/pairing", new
        {
            appId = "rauchtest",
            displayName = "Rauchtest-App",
            code = "246810",
            requestedRights = new[] { Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal, Rights.BackgroundStart },
            offeredRights = new[] { Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead },
        }).ConfigureAwait(true);
        var pairingId = JsonDocument.Parse(await begin.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("pairingId").GetString();
        await WaitUntilAsync(() => app.PairingPrompt?.Current is { IsVisible: true }, ShortWait, "Kopplungsdialog offen").ConfigureAwait(true);
        var dialog = app.PairingPrompt!.Current!;
        Check((int)begin.StatusCode == 202
              && dialog.Choices.Select(c => (c.Permission.Key, c.IsChecked)).SequenceEqual([("photos", true), ("tours", true), ("background", false)])
              && PairingDialogWindow.FormatCode("246810") == "246 810",
            $"Dialog „Rauchtest-App möchte sich verbinden“: Code 246 810, Zeilen {string.Join(", ", dialog.Choices.Select(c => c.Text))}");
        await _theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await SnapshotAsync("76-kopplung-hell", dialog).ConfigureAwait(true);
        await _theme.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
        await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("77-kopplung-dunkel", dialog).ConfigureAwait(true);
        await _theme.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
        dialog.Accept();

        JsonElement status = default;
        for (var i = 0; i < 40; i++)
        {
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"/av/v1/pairing/{pairingId}");
            poll.Headers.Add(AppConnectContract.AppHeader, "rauchtest");
            using var polled = await http.SendAsync(poll).ConfigureAwait(true);
            status = JsonDocument.Parse(await polled.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
            if (status.GetProperty("status").GetString() == "accepted")
            {
                break;
            }

            await Task.Delay(100).ConfigureAwait(true);
        }

        var secret = status.TryGetProperty("secret", out var s) ? s.GetString() : null;
        Check(secret is { Length: 43 } && app.AppConnections?.Find("rauchtest") is { } connection
              && connection.GrantedToPeer.SequenceEqual([Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal]),
            "Verbinden → Geheimnis abgeholt, gekoppelt ohne Hintergrundstart");
        var stored = File.ReadAllText(Path.Combine(_paths.AppDataRoot, "appconnect", "connections.json"));
        Check(!stored.Contains(secret!, StringComparison.Ordinal), "connections.json ohne Geheimnis im Klartext (DPAPI)");

        using var granted = Authorized(HttpMethod.Get, "/av/v1/info", secret!);
        using var grantedResponse = await http.SendAsync(granted).ConfigureAwait(true);
        var grantedJson = JsonDocument.Parse(await grantedResponse.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Check(grantedJson.TryGetProperty("grantedToYou", out var g) && g.GetArrayLength() == 3, "GET /info mit Geheimnis nennt die erlaubten Rechte");

        // Stufe 4: Reiter „Verbundene Apps“ — die Test-App hat keine Anmeldedatei, ist aber verbunden
        await OpenSettingsSectionAsync(NavigationTags.SettingsConnectedApps, "Reiter „Verbundene Apps“").ConfigureAwait(true);
        var view = _settingsPage.FindName("ConnectedAppsView") as FrameworkElement;
        var apps = view?.DataContext as ConnectedAppsViewModel;
        await WaitUntilAsync(() => apps?.Apps.Any(a => a.AppId == "rauchtest" && a.IsPaired) == true, ShortWait, "Test-App im Reiter").ConfigureAwait(true);
        var item = apps!.Apps.Single(a => a.AppId == "rauchtest");
        Check(apps.IsAvailable && apps.Summary == "1 App verbunden"
              && item.GrantedToPeer.Where(o => o.IsOn).Select(o => o.Right).SequenceEqual([Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal])
              && item.ReceivedFromPeer.Where(o => o.IsOn).Select(o => o.Right).SequenceEqual([Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead])
              && item.CanDisconnect && !item.CanPair,
            $"Verbundene Apps: {item.DisplayName} „{item.StateText}“ ({item.DetailText}), Rechte in beide Richtungen");
        await SnapshotAsync("78-verbundene-apps").ConfigureAwait(true);
        await PhotosForPeerAsync(http, secret!, item).ConfigureAwait(true);

        using var unpair = Authorized(HttpMethod.Delete, "/av/v1/pairing", secret!);
        using var unpaired = await http.SendAsync(unpair).ConfigureAwait(true);
        Check((int)unpaired.StatusCode == 204 && app.AppConnections!.Find("rauchtest") is null, "Trennen → 204, Kopplung weg");
        await WaitUntilAsync(() => apps.Apps.All(a => a.AppId != "rauchtest"), ShortWait, "Test-App aus dem Reiter").ConfigureAwait(true);
        Check(apps.Message == "Die Verbindung mit Rauchtest-App ist getrennt." && apps.Summary == "Noch keine App verbunden",
            $"Verbundene Apps nach dem Trennen: „{apps.Message}“");
    }

    /// <summary>Stufe 5: Foto-Endpunkte (Vertrag 8.1) mit der Rauchtest-Bibliothek.</summary>
    private async Task PhotosForPeerAsync(HttpClient http, string secret, ConnectedAppItemViewModel item)
    {
        const string search = "/av/v1/photos?fromUtc=1970-01-01T00:00:00Z&limit=500";
        using var none = Authorized(HttpMethod.Get, search, secret);
        using var noneResponse = await http.SendAsync(none).ConfigureAwait(true);
        var noneJson = JsonDocument.Parse(await noneResponse.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Check(noneResponse.IsSuccessStatusCode && noneJson.GetProperty("items").GetArrayLength() == 0 && item.LibrariesHint is not null,
            "Fotos: neue Kopplung sieht keine Bibliothek (0 Treffer, Hinweis in der Karte)");

        Check(item.Libraries.Count > 0, $"Karte zeigt {item.Libraries.Count} Bibliothek(en) zum Anhaken");
        item.Libraries[0].IsOn = true;
        using var found = Authorized(HttpMethod.Get, search, secret);
        using var foundResponse = await http.SendAsync(found).ConfigureAwait(true);
        var items = JsonDocument.Parse(await foundResponse.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("items");
        var photo = items.EnumerateArray().FirstOrDefault(i => i.GetProperty("mediaType").GetString() == "photo");
        Check(foundResponse.IsSuccessStatusCode && items.GetArrayLength() > 0 && photo.ValueKind == JsonValueKind.Object
              && items.EnumerateArray().All(i => i.GetProperty("id").GetString()!.StartsWith("lum-", StringComparison.Ordinal) && !i.TryGetProperty("people", out _)),
            $"Fotos: mit freigegebener Bibliothek {items.GetArrayLength()} Treffer, ohne Personen");

        var id = photo.GetProperty("id").GetString();
        using var thumb = Authorized(HttpMethod.Get, $"/av/v1/photos/{id}/thumbnail?size=256", secret);
        using var thumbResponse = await http.SendAsync(thumb).ConfigureAwait(true);
        var jpeg = await thumbResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
        var etag = thumbResponse.Headers.ETag?.Tag;
        using var again = Authorized(HttpMethod.Get, $"/av/v1/photos/{id}/thumbnail?size=256", secret);
        if (etag is not null)
        {
            again.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        }

        using var againResponse = await http.SendAsync(again).ConfigureAwait(true);
        Check(thumbResponse.IsSuccessStatusCode && thumbResponse.Content.Headers.ContentType?.MediaType == "image/jpeg"
              && jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8 && (int)againResponse.StatusCode == 304,
            $"Vorschaubild 256: JPEG ({jpeg.Length} Bytes), ETag, If-None-Match → {(int)againResponse.StatusCode}");

        using var original = Authorized(HttpMethod.Get, $"/av/v1/photos/{id}/original", secret);
        using var originalResponse = await http.SendAsync(original).ConfigureAwait(true);
        var originalJson = JsonDocument.Parse(await originalResponse.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Check(originalResponse.IsSuccessStatusCode && originalJson.GetProperty("available").GetBoolean()
              && File.Exists(originalJson.GetProperty("path").GetString()),
            "Original: Pfad vorhanden, available = true");
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string secret)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add(AppConnectContract.AppHeader, "rauchtest");
        return request;
    }
}
