using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Thumbnails;

namespace Lumora.AppConnect;

/// <summary>
/// Fotos aus dem Katalog für andere Apps (ADR-0073 Stufe 5, Vertrag 1.1 Abschnitt 8.1): nur lesend über eine rohe
/// SQLite-Verbindung, jede Abfrage nur in den freigegebenen Bibliotheken. Sortiert wie die Galerie über
/// <c>COALESCE(CapturedAt, ModifiedAtUtc)</c> (Index <c>IX_Files_Library_Captured</c>, ADR-0046). Versteckte Bibliotheken
/// liegen nie in diesem Katalog — die App-Verbindung läuft mit <c>_hidden</c> gar nicht (ADR-0073).
/// </summary>
public sealed class CatalogPhotoCatalog : IPhotoCatalog
{
    private const string TimeKey = "COALESCE(Files.CapturedAt, Files.ModifiedAtUtc)";

    private const string Columns = $"""
        Files.Id, Files.LibraryId, Files.MediaType, Files.CapturedAt, Files.CapturedAtOffset, Files.Latitude, Files.Longitude,
        Files.GeoAccuracy, Files.GeoSource, Files.Rating, Files.IsFavorite, Files.Width, Files.Height, Files.DurationMs,
        CASE WHEN fp.ContentSize = Files.Size AND fp.ContentModifiedAt = Files.ModifiedAtUtc THEN fp.ContentHash END,
        {TimeKey}
        """;


    private readonly ILibraryContextFactory _factory;
    private readonly IThumbnailService? _thumbnails;
    private readonly IImageThumbnailRenderer? _renderer;
    private readonly string _tempFolder;
    private readonly TimeZoneInfo _zone;
    private readonly SemaphoreSlim _rendering = new(2, 2);

    /// <param name="tempFolder">Arbeitsordner für verkleinerte Vorschaubilder (unter der App-Wurzel, wird geleert).</param>
    public CatalogPhotoCatalog(ILibraryContextFactory factory, IThumbnailService? thumbnails, IImageThumbnailRenderer? renderer, string tempFolder,
        TimeZoneInfo? zone = null)
    {
        _factory = factory;
        _thumbnails = thumbnails;
        _renderer = renderer;
        _tempFolder = tempFolder;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public CatalogPhotoCatalog(ILibraryContextFactory factory, IThumbnailService? thumbnails, IImageThumbnailRenderer? renderer, IAppDataPaths paths)
        : this(factory, thumbnails, renderer, Path.Combine(paths.AppDataRoot, "appconnect", "tmp"))
    {
    }

    /// <summary>Wie lange ein fehlendes Vorschaubild höchstens erzeugt wird, bevor 503 <c>busy</c> kommt.</summary>
    internal TimeSpan ThumbnailWait { get; init; } = TimeSpan.FromSeconds(10);

    public Task<PhotoPage> SearchAsync(PhotoQuery query, PhotoScope scope, CancellationToken ct)
        => RunAsync(connection =>
        {
            if (scope.Libraries.Count == 0 || (!query.Photos && !query.Videos))
            {
                return new PhotoPage([], null);
            }

            using var cmd = connection.CreateCommand();
            var where = new StringBuilder(Scope(cmd, scope.Libraries));
            if (!(query.Photos && query.Videos))
            {
                where.Append(query.Photos ? " AND Files.MediaType = 0" : " AND Files.MediaType = 1");
            }

            if (query.FromUtc is { } from)
            {
                where.Append($" AND {TimeKey} >= $from");
                cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeSeconds());
            }

            if (query.ToUtc is { } to)
            {
                where.Append($" AND {TimeKey} <= $to");
                cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeSeconds());
            }

            if (query.Bounds is { } b)
            {
                where.Append(" AND Files.Latitude BETWEEN $minLat AND $maxLat AND Files.Longitude BETWEEN $minLon AND $maxLon");
                cmd.Parameters.AddWithValue("$minLat", b.MinLat);
                cmd.Parameters.AddWithValue("$maxLat", b.MaxLat);
                cmd.Parameters.AddWithValue("$minLon", b.MinLon);
                cmd.Parameters.AddWithValue("$maxLon", b.MaxLon);
            }

            if (query.After is { } after)
            {
                where.Append($" AND ({TimeKey} > $afterTime OR ({TimeKey} = $afterTime AND Files.Id > $afterId))");
                cmd.Parameters.AddWithValue("$afterTime", after.Time);
                cmd.Parameters.AddWithValue("$afterId", after.FileId);
            }

            cmd.CommandText = $"SELECT {Columns} FROM Files LEFT JOIN FileFingerprints fp ON fp.FileId = Files.Id WHERE {where} "
                              + $"ORDER BY {TimeKey}, Files.Id LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", query.Limit + 1);
            var rows = ReadRows(cmd);
            string? next = null;
            if (rows.Count > query.Limit)
            {
                rows.RemoveAt(rows.Count - 1);
                next = new PhotoCursor(rows[^1].Time, rows[^1].Id).Encode();
            }

            return new PhotoPage(ToPhotos(connection, rows, scope.WithPeople), next);
        }, ct);

    public Task<Photo?> GetAsync(int fileId, PhotoScope scope, CancellationToken ct)
        => RunAsync(connection =>
        {
            if (scope.Libraries.Count == 0)
            {
                return null;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM Files LEFT JOIN FileFingerprints fp ON fp.FileId = Files.Id "
                              + $"WHERE Files.Id = $id AND {Scope(cmd, scope.Libraries)};";
            cmd.Parameters.AddWithValue("$id", fileId);
            return ToPhotos(connection, ReadRows(cmd), scope.WithPeople).FirstOrDefault();
        }, ct);

    public Task<IReadOnlyList<Photo>> ByChecksumAsync(byte[] sha256, PhotoScope scope, CancellationToken ct)
        => RunAsync<IReadOnlyList<Photo>>(connection =>
        {
            if (scope.Libraries.Count == 0)
            {
                return [];
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM FileFingerprints fp CROSS JOIN Files ON Files.Id = fp.FileId "
                              + "WHERE fp.ContentHash = $hash AND fp.ContentSize = Files.Size AND fp.ContentModifiedAt = Files.ModifiedAtUtc "
                              + $"AND {Scope(cmd, scope.Libraries)} ORDER BY {TimeKey}, Files.Id LIMIT 500;";
            cmd.Parameters.AddWithValue("$hash", sha256);
            return ToPhotos(connection, ReadRows(cmd), scope.WithPeople);
        }, ct);

    public async Task<string?> ThumbnailETagAsync(int fileId, int size, IReadOnlyCollection<int> libraries, CancellationToken ct)
        => await FindFileAsync(fileId, libraries, ct).ConfigureAwait(false) is { } file ? ETag(file, size) : null;

    public async Task<PhotoThumbnail?> ThumbnailAsync(int fileId, int size, IReadOnlyCollection<int> libraries, CancellationToken ct)
    {
        if (await FindFileAsync(fileId, libraries, ct).ConfigureAwait(false) is not { } file)
        {
            return null;
        }

        if (_thumbnails is null || _renderer is null)
        {
            throw new PhotoBusyException(30);
        }

        var cached = await CachedThumbnailAsync(file, ct).ConfigureAwait(false);
        // Große Vorschau eines Fotos aus dem Original (das Vorschaubild hat nur 400 px an der kurzen Kante); kann der Renderer
        // das Original nicht lesen, bleibt es beim Vorschaubild
        var fromOriginal = size > ThumbnailSizing.ShortEdge && file.MediaType == MediaType.Image && File.Exists(file.Path);
        await _rendering.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jpeg = await Task.Run(() => (fromOriginal ? Fit(file.Path, isCached: false, size) : null) ?? Fit(cached, isCached: true, size), ct).ConfigureAwait(false);
            return jpeg is null ? throw new PhotoBusyException(30) : new PhotoThumbnail(jpeg, ETag(file, size));
        }
        finally
        {
            _rendering.Release();
        }
    }

    public async Task<OriginalLocation?> OriginalAsync(int fileId, IReadOnlyCollection<int> libraries, CancellationToken ct)
        => await FindFileAsync(fileId, libraries, ct).ConfigureAwait(false) is { } file
            ? new OriginalLocation(file.Path, File.Exists(file.Path))
            : null;

    public Task<IReadOnlyList<(int Id, string Name)>> LibrariesAsync(CancellationToken ct)
        => RunAsync<IReadOnlyList<(int Id, string Name)>>(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, Name FROM Libraries ORDER BY Name COLLATE NOCASE, Id;";
            using var reader = cmd.ExecuteReader();
            var result = new List<(int, string)>();
            while (reader.Read())
            {
                result.Add((reader.GetInt32(0), reader.GetString(1)));
            }

            return result;
        }, ct);

    // --- Zeilen → Photo ---------------------------------------------------------------------------------------------

    private List<Row> ReadRows(SqliteCommand cmd)
    {
        var rows = new List<Row>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetInt32(0),
                reader.GetInt32(1),
                (MediaType)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : (GeoAccuracy)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : (GeoSource)reader.GetInt32(8),
                reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                reader.GetBoolean(10),
                reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                reader.IsDBNull(13) ? null : reader.GetInt32(13),
                reader.IsDBNull(14) ? null : (byte[])reader.GetValue(14),
                reader.GetInt64(15)));
        }

        return rows;
    }

    private List<Photo> ToPhotos(SqliteConnection connection, List<Row> rows, bool withPeople)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var libraries = LibraryNames(connection);
        var people = withPeople ? People(connection, rows.Select(r => r.Id).ToList()) : null;
        return rows.Select(r =>
        {
            var (taken, takenLocal, takenUtc) = Times(r.CapturedAt, r.Offset);
            var hasPlace = r.Lat is not null && r.Lon is not null;
            return new Photo
            {
                Id = PhotoIds.Of(r.Id),
                Checksum = r.Hash is { Length: 32 } hash ? PhotoIds.FormatChecksum(hash) : null,
                MediaType = r.MediaType == MediaType.Video ? "video" : "photo",
                Taken = taken,
                TakenLocal = takenLocal,
                TakenUtc = takenUtc,
                Lat = hasPlace ? r.Lat : null,
                Lon = hasPlace ? r.Lon : null,
                GeoAccuracy = hasPlace ? AccuracyText(r.Accuracy) : null,
                GeoSource = hasPlace ? SourceText(r.Source) : null,
                Rating = Math.Clamp(r.Rating, 0, 5),
                Favorite = r.Favorite,
                Width = r.Width,
                Height = r.Height,
                DurationS = r.MediaType == MediaType.Video && r.DurationMs is int ms ? ms / 1000.0 : null,
                LibraryName = libraries.GetValueOrDefault(r.LibraryId, string.Empty),
                People = people is null ? null : people.GetValueOrDefault(r.Id) ?? [],
            };
        }).ToList();
    }

    /// <summary>
    /// Zeiten nach Abschnitt 3: mit bekanntem Abstand <c>taken</c> (mit Offset) und <c>takenUtc</c>; ohne Abstand nur die
    /// Wanduhrzeit, wie Lumora sie zeigt (Zeitzone dieses PCs, ADR-0046) — dann ist UTC nicht sicher bestimmbar.
    /// </summary>
    internal (string? Taken, string? TakenLocal, string? TakenUtc) Times(long? capturedAt, int? offsetMinutes)
    {
        if (capturedAt is not long seconds)
        {
            return (null, null, null);
        }

        var utc = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (offsetMinutes is int minutes)
        {
            var local = utc.ToOffset(TimeSpan.FromMinutes(minutes));
            return (local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), null,
                utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        var wall = utc.UtcDateTime + _zone.GetUtcOffset(utc);
        return (null, wall.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), null);
    }

    private static string? AccuracyText(GeoAccuracy? accuracy) => accuracy switch
    {
        GeoAccuracy.Exact => "precise",
        GeoAccuracy.Estimated => "estimated",
        GeoAccuracy.Unknown => "unknown",
        _ => null,
    };

    private static string? SourceText(GeoSource? source) => source switch
    {
        GeoSource.Exif => "exif",
        GeoSource.Video => "video",
        GeoSource.Manual => "manual",
        GeoSource.Track => "track",
        GeoSource.Neighbors => "neighbors",
        _ => null,
    };

    private static Dictionary<int, string> LibraryNames(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM Libraries;";
        using var reader = cmd.ExecuteReader();
        var names = new Dictionary<int, string>();
        while (reader.Read())
        {
            names[reader.GetInt32(0)] = reader.GetString(1);
        }

        return names;
    }

    /// <summary>Bestätigte Personen je Datei (ohne ausgeblendete), jeder Name einmal.</summary>
    private static Dictionary<int, IReadOnlyList<PhotoPerson>> People(SqliteConnection connection, List<int> fileIds)
    {
        var result = new Dictionary<int, List<PhotoPerson>>();
        foreach (var chunk in fileIds.Chunk(500))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT DISTINCT fa.FileId, p.Name FROM Faces fa CROSS JOIN Persons p ON p.Id = fa.PersonId
                WHERE fa.FileId IN ({string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)))})
                  AND fa.State = {(int)FaceState.Confirmed} AND p.IsHidden = 0
                ORDER BY fa.FileId, p.Name;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                if (!result.TryGetValue(id, out var list))
                {
                    result[id] = list = [];
                }

                list.Add(new PhotoPerson(reader.GetString(1)));
            }
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<PhotoPerson>)p.Value);
    }

    // --- Datei, Vorschaubild ----------------------------------------------------------------------------------------

    private Task<FileRow?> FindFileAsync(int fileId, IReadOnlyCollection<int> libraries, CancellationToken ct)
        => RunAsync(connection =>
        {
            if (libraries.Count == 0)
            {
                return null;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Files.LibraryId, Files.MediaType, d.Path, Files.Name, Files.ModifiedAtUtc FROM Files "
                              + $"CROSS JOIN Folders d ON d.Id = Files.FolderId WHERE Files.Id = $id AND {Scope(cmd, libraries)};";
            cmd.Parameters.AddWithValue("$id", fileId);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new FileRow(fileId, reader.GetInt32(0), (MediaType)reader.GetInt32(1), Path.Combine(reader.GetString(2), reader.GetString(3)), reader.GetInt64(4))
                : (FileRow?)null;
        }, ct);

    private static string ETag(FileRow file, int size)
        => string.Create(CultureInfo.InvariantCulture, $"\"lum-{file.Id}-{size}-{file.ModifiedAt}\"");

    /// <summary>Vorschaubild aus dem Cache von Lumora — fehlt es, wird es wie für die Galerie erzeugt (höchstens 10 s).</summary>
    private async Task<string> CachedThumbnailAsync(FileRow file, CancellationToken ct)
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(ThumbnailReadyEventArgs e)
        {
            if (e.FileId == file.Id)
            {
                ready.TrySetResult(e.ThumbnailPath);
            }
        }

        _thumbnails!.ThumbnailReady += OnReady;
        try
        {
            await _thumbnails.RequestThumbnailsAsync(
                [new ThumbnailRequest(file.LibraryId, file.Id, file.Path, file.MediaType, DateTimeOffset.FromUnixTimeSeconds(file.ModifiedAt).UtcDateTime)],
                ct).ConfigureAwait(false);
            try
            {
                return await ready.Task.WaitAsync(ThumbnailWait, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new PhotoBusyException(10);
            }
        }
        finally
        {
            _thumbnails.ThumbnailReady -= OnReady;
        }
    }

    /// <summary>
    /// JPEG mit längster Seite ≤ <paramref name="size"/>; passt das Vorschaubild schon, unverändert (Originale immer neu, wegen
    /// der Drehung). null = nicht lesbar.
    /// </summary>
    private byte[]? Fit(string source, bool isCached, int size)
    {
        var dims = _renderer!.ReadDimensions(source);
        if (dims is not { } d || d.Width <= 0 || d.Height <= 0)
        {
            return null;
        }

        if (isCached && Math.Max(d.Width, d.Height) <= size)
        {
            return File.ReadAllBytes(source);
        }

        // kurze Kante so wählen, dass die lange Kante ≤ size bleibt (der Renderer begrenzt sie zusätzlich auf das 3-Fache)
        var shortEdge = Math.Max(1, (int)Math.Floor((double)size * Math.Min(d.Width, d.Height) / Math.Max(d.Width, d.Height)));
        Directory.CreateDirectory(_tempFolder);
        var target = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            return _renderer.Render(source, target, shortEdge).Success ? File.ReadAllBytes(target) : null;
        }
        finally
        {
            File.Delete(target);
        }
    }

    // --- SQL --------------------------------------------------------------------------------------------------------

    private static string Scope(SqliteCommand cmd, IReadOnlyCollection<int> libraries)
    {
        var names = new List<string>();
        var i = 0;
        foreach (var id in libraries.Distinct())
        {
            var name = "$lib" + i++.ToString(CultureInfo.InvariantCulture);
            cmd.Parameters.AddWithValue(name, id);
            names.Add(name);
        }

        return $"Files.LibraryId IN ({string.Join(", ", names)}) AND Files.MediaType IN (0, 1)";
    }

    private Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
        => Task.Run(
            () =>
            {
                try
                {
                    using var connection = _factory.OpenSqliteConnection();
                    return work(connection);
                }
                catch (Exception ex) when (SqliteBusyRetry.IsBusy(ex))
                {
                    throw new PhotoBusyException(2);
                }
            },
            ct);

    private readonly record struct Row(
        int Id, int LibraryId, MediaType MediaType, long? CapturedAt, int? Offset, double? Lat, double? Lon, GeoAccuracy? Accuracy,
        GeoSource? Source, int Rating, bool Favorite, int Width, int Height, int? DurationMs, byte[]? Hash, long Time);

    private readonly record struct FileRow(int Id, int LibraryId, MediaType MediaType, string Path, long ModifiedAt);
}
