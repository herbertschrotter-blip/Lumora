using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Lumora.AppConnect;

/// <summary>Person in einem Suchergebnis (nur mit Recht <c>photos.people</c>).</summary>
public sealed record PhotoPerson(string Name);

/// <summary>Ein Foto oder Video nach Vertrag 1.1 Abschnitt 8.1 (Schema <c>Photo</c>). Leere Felder werden weggelassen.</summary>
public sealed record Photo
{
    public required string Id { get; init; }

    /// <summary><c>sha256:</c> + Kleinbuchstaben-Hex über den ganzen Dateiinhalt (Abschnitt 10); null, solange nicht berechnet.</summary>
    public string? Checksum { get; init; }

    /// <summary><c>photo</c> | <c>video</c>.</summary>
    public required string MediaType { get; init; }

    /// <summary>Aufnahmezeit mit Offset, z. B. <c>2020-03-15T14:56:10+01:00</c>; null, wenn der Offset unbekannt ist.</summary>
    public string? Taken { get; init; }

    /// <summary>Wanduhrzeit ohne Offset — nur, wenn <see cref="Taken"/> null ist.</summary>
    public string? TakenLocal { get; init; }

    public string? TakenUtc { get; init; }

    public double? Lat { get; init; }

    public double? Lon { get; init; }

    /// <summary><c>precise</c> | <c>estimated</c> | <c>unknown</c>.</summary>
    public string? GeoAccuracy { get; init; }

    /// <summary><c>exif</c> | <c>video</c> | <c>manual</c> | <c>track</c> | <c>neighbors</c>.</summary>
    public string? GeoSource { get; init; }

    public int Rating { get; init; }

    public bool Favorite { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public double? DurationS { get; init; }

    public required string LibraryName { get; init; }

    /// <summary>Nur mit Recht <c>photos.people</c>, sonst fehlt das Feld.</summary>
    public IReadOnlyList<PhotoPerson>? People { get; init; }
}

/// <summary>Seite einer Suche (Schema <c>PhotoPage</c>); <c>nextCursor</c> steht immer drin, null = Ende.</summary>
public sealed record PhotoPage(IReadOnlyList<Photo> Items, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);

/// <summary><c>{ "items": [Photo] }</c> von <c>by-checksum</c>.</summary>
public sealed record PhotoList(IReadOnlyList<Photo> Items);

/// <summary>Antwort von <c>GET /photos/{id}/original</c> (Schema <c>OriginalLocation</c>).</summary>
public sealed record OriginalLocation(string Path, bool Available);

/// <summary>Vorschaubild als JPEG mit ETag.</summary>
public sealed record PhotoThumbnail(byte[] Jpeg, string ETag);

/// <summary>Was die anfragende App sehen darf: freigegebene Bibliotheken, Personen nur mit Recht.</summary>
public sealed record PhotoScope(IReadOnlyCollection<int> Libraries, bool WithPeople);

/// <summary>Suche nach Zeitraum und/oder Gebiet (Abschnitt 8.1).</summary>
public sealed record PhotoQuery(
    DateTime? FromUtc,
    DateTime? ToUtc,
    PhotoBounds? Bounds,
    bool Photos,
    bool Videos,
    int Limit,
    PhotoCursor? After);

/// <summary>Gebiet <c>minLon,minLat,maxLon,maxLat</c>.</summary>
public readonly record struct PhotoBounds(double MinLon, double MinLat, double MaxLon, double MaxLat);

/// <summary>
/// Fortsetzung einer Suche: Sortierzeit (Unix-Sekunden, Aufnahmezeit bzw. Änderungszeit) und Datei. Für die andere App ein
/// undurchsichtiger Text (Abschnitt 3).
/// </summary>
public readonly record struct PhotoCursor(long Time, int FileId)
{
    public string Encode()
        => ConnectionStore.Base64Url(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Time}:{FileId}")));

    public static PhotoCursor? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 64)
        {
            return null;
        }

        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split(':');
            return parts.Length == 2
                   && long.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var time)
                   && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? new PhotoCursor(time, id)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Kennungen nach Abschnitt 10: undurchsichtig für die andere App, für Lumora <c>lum-</c> + Katalog-Nummer.</summary>
public static class PhotoIds
{
    private const string Prefix = "lum-";

    public static string Of(int fileId) => Prefix + fileId.ToString(CultureInfo.InvariantCulture);

    public static int? Parse(string? id)
        => id is not null && id.StartsWith(Prefix, StringComparison.Ordinal)
           && int.TryParse(id.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var fileId) && fileId > 0
            ? fileId
            : null;

    /// <summary><c>sha256:</c> + 64 Kleinbuchstaben-Hex → 32 Bytes; sonst null.</summary>
    public static byte[]? ParseChecksum(string? checksum)
    {
        if (checksum is not { Length: 71 } || !checksum.StartsWith("sha256:", StringComparison.Ordinal)
            || checksum.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
        {
            return null;
        }

        return Convert.FromHexString(checksum.AsSpan(7));
    }

    public static string FormatChecksum(byte[] hash) => "sha256:" + Convert.ToHexStringLower(hash);
}

/// <summary>Die Datenbank ist gerade gesperrt oder das Vorschaubild noch nicht fertig → 503 <c>busy</c> mit <c>Retry-After</c>.</summary>
public sealed class PhotoBusyException(int retryAfterSeconds) : Exception("Lumora ist gerade beschäftigt.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Fotos für andere Apps (ADR-0073 Stufe 5, Vertrag 1.1 Abschnitt 8.1) — nur aus den übergebenen Bibliotheken; was nicht
/// darin liegt, gibt es für die andere App nicht (kein Ergebnis, keine Zählung, kein Hinweis).
/// </summary>
public interface IPhotoCatalog
{
    Task<PhotoPage> SearchAsync(PhotoQuery query, PhotoScope scope, CancellationToken ct);

    Task<Photo?> GetAsync(int fileId, PhotoScope scope, CancellationToken ct);

    Task<IReadOnlyList<Photo>> ByChecksumAsync(byte[] sha256, PhotoScope scope, CancellationToken ct);

    /// <summary>ETag ohne das Bild zu erzeugen (für <c>If-None-Match</c>); null = unbekannt bzw. nicht freigegeben.</summary>
    Task<string?> ThumbnailETagAsync(int fileId, int size, IReadOnlyCollection<int> libraries, CancellationToken ct);

    /// <summary>JPEG, längste Seite ≤ <paramref name="size"/>, Ausrichtung angewendet; null = unbekannt bzw. nicht freigegeben.</summary>
    Task<PhotoThumbnail?> ThumbnailAsync(int fileId, int size, IReadOnlyCollection<int> libraries, CancellationToken ct);

    Task<OriginalLocation?> OriginalAsync(int fileId, IReadOnlyCollection<int> libraries, CancellationToken ct);

    /// <summary>Normale Bibliotheken für die Auswahl in Einstellungen › Verbundene Apps.</summary>
    Task<IReadOnlyList<(int Id, string Name)>> LibrariesAsync(CancellationToken ct);
}
