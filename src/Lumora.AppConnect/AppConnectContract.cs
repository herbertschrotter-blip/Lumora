using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumora.AppConnect;

/// <summary>
/// Feste Werte aus dem Vertrag „App-Verbindung“ (Repo herbertschrotter-blip/app-verbindung, VERTRAG.md). Lumora setzt
/// <see cref="Version"/> um; die Werte stehen dort, hier nur als Konstanten — nichts davon eigenmächtig ändern.
/// </summary>
public static class AppConnectContract
{
    /// <summary>Umgesetzte Vertragsversion (Abschnitt 12).</summary>
    public const string Version = "1.1";

    /// <summary>Basispfad (Abschnitt 3).</summary>
    public const string BasePath = "/av/v1";

    /// <summary>App-Kennung von Lumora (Abschnitt 2).</summary>
    public const string AppId = "lumora";

    public const string DisplayName = "Lumora";

    /// <summary>Schalter für den Start ohne Fenster (Abschnitt 9; der Modus selbst folgt in Stufe 6).</summary>
    public const string BackgroundArg = "--background";

    /// <summary>Kopfzeile mit der App-Kennung des Abfragers (Abschnitt 3).</summary>
    public const string AppHeader = "X-AV-App";

    /// <summary>Rechte, die Lumora anbieten kann (Abschnitt 6).</summary>
    public static readonly IReadOnlyList<string> Provides =
        [Rights.PhotosSearch, Rights.PhotosThumbnail, Rights.PhotosOriginal, Rights.PhotosPeople, Rights.BackgroundStart];

    /// <summary>Rechte, die Lumora bei anderen erfragen möchte (Abschnitt 6).</summary>
    public static readonly IReadOnlyList<string> Wants =
        [Rights.ToursRead, Rights.TracksRead, Rights.SummitsRead, Rights.BackgroundStart];

    /// <summary>JSON wie im Vertrag: camelCase, Wertelisten als Text.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Für die Anmeldedatei: <c>port: null</c> beim Beenden muss ausdrücklich drinstehen (Abschnitt 4).</summary>
    public static readonly JsonSerializerOptions RegistrationJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

/// <summary>Rechte (Abschnitt 6).</summary>
public static class Rights
{
    public const string PhotosSearch = "photos.search";
    public const string PhotosThumbnail = "photos.thumbnail";
    public const string PhotosOriginal = "photos.original";
    public const string PhotosPeople = "photos.people";
    public const string ToursRead = "tours.read";
    public const string TracksRead = "tracks.read";
    public const string SummitsRead = "summits.read";
    public const string BackgroundStart = "background.start";
}

/// <summary>Fehlercodes (Abschnitt 11).</summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string NotPaired = "not_paired";
    public const string MissingRight = "missing_right";
    public const string ForbiddenOrigin = "forbidden_origin";
    public const string ForbiddenHost = "forbidden_host";
    public const string NotFound = "not_found";
    public const string Unavailable = "unavailable";
    public const string ContractMismatch = "contract_mismatch";
    public const string TooManyRequests = "too_many_requests";
    public const string Busy = "busy";
}

/// <summary>Anmeldedatei (Abschnitt 4, Schema <c>AppRegistration</c>). Kein Geheimnis darin.</summary>
public sealed record AppRegistration
{
    public required string AppId { get; init; }

    public required string DisplayName { get; init; }

    public required string AppVersion { get; init; }

    public required string ContractVersion { get; init; }

    public required string ExePath { get; init; }

    public required string BackgroundArg { get; init; }

    public required bool Running { get; init; }

    public int? Pid { get; init; }

    public int? Port { get; init; }

    /// <summary><c>window</c> oder <c>background</c>; null, wenn die App nicht läuft.</summary>
    public string? Mode { get; init; }

    public DateTime? StartedAtUtc { get; init; }

    public required IReadOnlyList<string> Provides { get; init; }

    public required IReadOnlyList<string> Wants { get; init; }
}

/// <summary>Antwort von <c>GET /av/v1/info</c> (Abschnitt 7, Schema <c>Info</c>).</summary>
public sealed record InfoResponse
{
    public required string AppId { get; init; }

    public required string DisplayName { get; init; }

    public required string AppVersion { get; init; }

    public required string ContractVersion { get; init; }

    public required IReadOnlyList<string> Provides { get; init; }

    /// <summary>Nur mit gültigem Geheimnis: Rechte für den Abfrager.</summary>
    public IReadOnlyList<string>? GrantedToYou { get; init; }

    /// <summary>Nur mit gültigem Geheimnis.</summary>
    public string? Mode { get; init; }
}

/// <summary>Fehlerrumpf (Abschnitt 11, Schema <c>Error</c>); <c>message</c> nie mit Dateinamen oder Pfaden von Fotos.</summary>
public sealed record ErrorResponse(string Error, string Message);
