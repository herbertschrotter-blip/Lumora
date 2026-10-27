using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lumora.AppConnect;

/// <summary>
/// Foto-Endpunkte als Anbieter (ADR-0073 Stufe 5, Vertrag 1.1 Abschnitt 8.1). Das Recht wird bei jeder Anfrage geprüft
/// (Abschnitt 6); geantwortet wird nur aus den Bibliotheken, die der Benutzer der App freigegeben hat — alles andere gibt es
/// für sie nicht (404, keine Zählung). Protokoll nur mit Anzahlen und App-Kennung (Abschnitt 13).
/// </summary>
public sealed partial class AppConnectServer
{
    /// <summary>Erlaubte Größen der Vorschaubilder (längste Seite).</summary>
    public static readonly int[] ThumbnailSizes = [256, 512, 1024];

    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    private void MapPhotoEndpoints(WebApplication app)
    {
        var b = AppConnectContract.BasePath + "/photos";
        app.MapGet(b, (Delegate)SearchPhotosAsync);
        app.MapGet(b + "/by-checksum/{checksum}", (Delegate)PhotosByChecksumAsync);
        app.MapGet(b + "/{id}", (Delegate)GetPhotoAsync);
        app.MapGet(b + "/{id}/thumbnail", (Delegate)PhotoThumbnailAsync);
        app.MapGet(b + "/{id}/original", (Delegate)PhotoOriginalAsync);
    }

    private Task<IResult> SearchPhotosAsync(HttpContext ctx)
        => PhotosAsync(ctx, Rights.PhotosSearch, async (catalog, peer) =>
        {
            if (ParseQuery(ctx.Request.Query) is not { } query)
            {
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                    "Ungültige Suche: mindestens Zeitraum (fromUtc/toUtc) oder Gebiet (bbox), limit 1…500, mediaType photo|video|all.");
            }

            var page = await catalog.SearchAsync(query, ScopeOf(peer), ctx.RequestAborted).ConfigureAwait(false);
            _logger.LogInformation("App-Verbindung: {AppId}: {Count} Fotos gesucht", peer, page.Items.Count);
            return Results.Json(page, AppConnectContract.Json);
        });

    private Task<IResult> GetPhotoAsync(HttpContext ctx, string id)
        => PhotosAsync(ctx, Rights.PhotosSearch, async (catalog, peer) =>
            PhotoIds.Parse(id) is int fileId && await catalog.GetAsync(fileId, ScopeOf(peer), ctx.RequestAborted).ConfigureAwait(false) is { } photo
                ? Results.Json(photo, AppConnectContract.Json)
                : NotFound());

    private Task<IResult> PhotosByChecksumAsync(HttpContext ctx, string checksum)
        => PhotosAsync(ctx, Rights.PhotosSearch, async (catalog, peer) =>
        {
            if (PhotoIds.ParseChecksum(checksum) is not { } hash)
            {
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "Prüfsumme muss sha256:<64 Hex-Zeichen> sein.");
            }

            var items = await catalog.ByChecksumAsync(hash, ScopeOf(peer), ctx.RequestAborted).ConfigureAwait(false);
            _logger.LogInformation("App-Verbindung: {AppId}: {Count} Fotos über die Prüfsumme gefunden", peer, items.Count);
            return Results.Json(new PhotoList(items), AppConnectContract.Json);
        });

    private Task<IResult> PhotoThumbnailAsync(HttpContext ctx, string id)
        => PhotosAsync(ctx, Rights.PhotosThumbnail, async (catalog, peer) =>
        {
            var sizeText = ctx.Request.Query["size"].ToString();
            var size = sizeText.Length == 0 ? ThumbnailSizes[0] : int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
            if (!ThumbnailSizes.Contains(size))
            {
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "size muss 256, 512 oder 1024 sein.");
            }

            if (PhotoIds.Parse(id) is not int fileId)
            {
                return NotFound();
            }

            var libraries = _peers.LibrariesOf(peer);
            if (ctx.Request.Headers.IfNoneMatch.ToString() is { Length: > 0 } ifNoneMatch
                && await catalog.ThumbnailETagAsync(fileId, size, libraries, ctx.RequestAborted).ConfigureAwait(false) is { } etag
                && ifNoneMatch.Split(',').Any(t => t.Trim() == etag))
            {
                ctx.Response.Headers.ETag = etag;
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            if (await catalog.ThumbnailAsync(fileId, size, libraries, ctx.RequestAborted).ConfigureAwait(false) is not { } thumbnail)
            {
                return NotFound();
            }

            ctx.Response.Headers.ETag = thumbnail.ETag;
            return Results.Bytes(thumbnail.Jpeg, "image/jpeg");
        });

    private Task<IResult> PhotoOriginalAsync(HttpContext ctx, string id)
        => PhotosAsync(ctx, Rights.PhotosOriginal, async (catalog, peer) =>
            PhotoIds.Parse(id) is int fileId && await catalog.OriginalAsync(fileId, _peers.LibrariesOf(peer), ctx.RequestAborted).ConfigureAwait(false) is { } original
                ? Results.Json(original, AppConnectContract.Json)
                : NotFound());

    /// <summary>Recht prüfen (403 <c>missing_right</c>), dann antworten; gesperrte Datenbank bzw. fehlendes Vorschaubild → 503 <c>busy</c>.</summary>
    private async Task<IResult> PhotosAsync(HttpContext ctx, string right, Func<IPhotoCatalog, string, Task<IResult>> answer)
    {
        var peer = Peer(ctx)!;
        if (!_peers.GrantedTo(peer).Contains(right))
        {
            return Error(StatusCodes.Status403Forbidden, ErrorCodes.MissingRight, $"Recht {right} nicht erteilt.");
        }

        if (_photos is null)
        {
            ctx.Response.Headers.RetryAfter = "30";
            return Error(StatusCodes.Status503ServiceUnavailable, ErrorCodes.Busy, "Der Fotokatalog ist noch nicht bereit.");
        }

        try
        {
            return await answer(_photos, peer).ConfigureAwait(false);
        }
        catch (PhotoBusyException busy)
        {
            ctx.Response.Headers.RetryAfter = busy.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Error(StatusCodes.Status503ServiceUnavailable, ErrorCodes.Busy, "Lumora ist gerade beschäftigt — bitte später erneut fragen.");
        }
    }

    private PhotoScope ScopeOf(string peer) => new(_peers.LibrariesOf(peer), _peers.GrantedTo(peer).Contains(Rights.PhotosPeople));

    private static IResult NotFound() => Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Foto unbekannt.");

    /// <summary>Parameter von <c>GET /photos</c> (Abschnitte 3 und 8.1); null = ungültig (400).</summary>
    internal static PhotoQuery? ParseQuery(IQueryCollection q)
    {
        DateTime? from = null, to = null;
        if (q["fromUtc"].ToString() is { Length: > 0 } fromText)
        {
            if (ParseUtc(fromText) is not { } f)
            {
                return null;
            }

            from = f;
        }

        if (q["toUtc"].ToString() is { Length: > 0 } toText)
        {
            if (ParseUtc(toText) is not { } t)
            {
                return null;
            }

            to = t;
        }

        PhotoBounds? bounds = null;
        if (q["bbox"].ToString() is { Length: > 0 } bboxText)
        {
            var parts = bboxText.Split(',');
            var values = new double[4];
            if (parts.Length != 4 || parts.Select((p, i) => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])).Any(ok => !ok))
            {
                return null;
            }

            var box = new PhotoBounds(values[0], values[1], values[2], values[3]);
            if (box.MinLon > box.MaxLon || box.MinLat > box.MaxLat || box.MinLat < -90 || box.MaxLat > 90 || box.MinLon < -180 || box.MaxLon > 180)
            {
                return null;
            }

            bounds = box;
        }

        if ((from is null && to is null && bounds is null) || (from > to))
        {
            return null;
        }

        var (photos, videos) = q["mediaType"].ToString() switch
        {
            "" or "all" => (true, true),
            "photo" => (true, false),
            "video" => (false, true),
            _ => (false, false),
        };
        if (!photos && !videos)
        {
            return null;
        }

        var limit = DefaultLimit;
        if (q["limit"].ToString() is { Length: > 0 } limitText
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit))
        {
            return null;
        }

        PhotoCursor? after = null;
        if (q["cursor"].ToString() is { Length: > 0 } cursorText)
        {
            if (PhotoCursor.Decode(cursorText) is not { } c)
            {
                return null;
            }

            after = c;
        }

        return new PhotoQuery(from, to, bounds, photos, videos, limit, after);
    }

    private static DateTime? ParseUtc(string text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.UtcDateTime : null;
}
