using System.Globalization;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Activity;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;

namespace Lumora.Services.Export;

/// <summary>
/// Umsetzung von <see cref="IPhotoExportService"/> (ADR-0068). Liest Pfade und Zustand aus dem Katalog (Stücke zu
/// <see cref="ChunkSize"/> Ids), prüft jede Datei auf der Platte; der Export läuft auf einem Hintergrund-Thread Datei für Datei,
/// meldet Fortschritt an die Aktivitäts-Anzeige und lässt sich dort abbrechen. In der versteckten Bibliothek ist beides gesperrt
/// (<see cref="ICatalogStoragePaths.IsHidden"/>, ADR-0049). Protokoll nur mit Zahlen, nie mit Pfaden.
/// </summary>
public sealed class PhotoExportService : IPhotoExportService
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<PhotoExportService> _logger;
    private readonly IPhotoExportEncoder? _encoder;
    private readonly IActivityCenter? _activities;
    private readonly ICatalogStoragePaths? _catalog;

    public PhotoExportService(
        ILibraryContextFactory factory,
        ILogger<PhotoExportService> logger,
        IPhotoExportEncoder? encoder = null,
        IActivityCenter? activities = null,
        ICatalogStoragePaths? catalog = null)
    {
        _factory = factory;
        _logger = logger;
        _encoder = encoder;
        _activities = activities;
        _catalog = catalog;
    }

    public bool IsBlocked => _catalog?.IsHidden == true;

    public async Task<ExternalFileSet> GetExternalFilesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (IsBlocked)
        {
            return ExternalFileSet.BlockedSet;
        }

        if (fileIds.Count == 0)
        {
            return ExternalFileSet.Empty;
        }

        var rows = await Task.Run(() => ReadRows(fileIds, ct), ct).ConfigureAwait(false);
        var paths = new List<string>();
        var ids = new List<int>();
        int missing = 0, archives = 0;
        foreach (var id in fileIds.Distinct())
        {
            if (!rows.TryGetValue(id, out var row) || row.Missing || !File.Exists(row.Path))
            {
                missing++;
            }
            else if (row.MediaType == MediaType.Archive)
            {
                archives++;
            }
            else
            {
                paths.Add(row.Path);
                ids.Add(id);
            }
        }

        return new ExternalFileSet(paths, ids, missing, archives, false);
    }

    public async Task<PhotoExportResult> ExportAsync(IReadOnlyList<int> fileIds, string targetDirectory, PhotoExportOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentNullException.ThrowIfNull(options);
        if (IsBlocked)
        {
            return PhotoExportResult.Blocked;
        }

        var encoder = _encoder ?? throw new InvalidOperationException("Exportieren ist nicht verfügbar.");
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Kein Zielordner.", nameof(targetDirectory));
        }

        var set = await GetExternalFilesAsync(fileIds, ct).ConfigureAwait(false);
        var rows = await Task.Run(() => ReadRows(set.FileIds, ct), ct).ConfigureAwait(false);
        var photos = set.FileIds.Where(id => rows.TryGetValue(id, out var r) && r.MediaType == MediaType.Image).ToList();
        var videos = set.FileIds.Count - photos.Count;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var folderName = Path.GetFileName(targetDirectory.TrimEnd('\\', '/'));
        var handle = _activities?.Start(new ActivityStartInfo(
            ActivityKind.FileOperation, "Exportieren (verkleinert)", string.IsNullOrEmpty(folderName) ? null : $"nach {folderName}",
            UserInitiated: true, Cancel: () => cts.Cancel()));
        var exported = 0;
        var errors = new List<string>();
        var canceled = false;
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(targetDirectory);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < photos.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var row = rows[photos[i]];
                    var target = FreeTarget(targetDirectory, Path.GetFileNameWithoutExtension(row.Path), used);
                    try
                    {
                        encoder.EncodeJpeg(row.Path, target, Math.Max(64, options.MaxLongEdge), Math.Clamp(options.JpegQuality, 1, 100));
                        exported++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors.Add($"{Path.GetFileName(row.Path)}: {ex.Message}");
                        TryDelete(target);
                    }

                    handle?.Report(i + 1, photos.Count);
                }
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            // z. B. Zielordner nicht anlegbar (schreibgeschützt, Laufwerk weg): Eintrag nicht ewig „läuft“ lassen
            _logger.LogWarning(ex, "Export verkleinert: abgebrochen nach {Exported} Datei(en)", exported);
            handle?.Fail($"Exportieren fehlgeschlagen: {ex.Message}");
            throw;
        }

        var result = new PhotoExportResult(exported, set.SkippedMissing, set.SkippedArchives, videos, errors.Count, errors, canceled);
        _logger.LogInformation(
            "Export verkleinert: {Exported} exportiert, {Videos} Videos, {Missing} fehlend, {Archives} Archive ausgelassen, {Failed} fehlgeschlagen, abgebrochen={Canceled}",
            exported, videos, set.SkippedMissing, set.SkippedArchives, errors.Count, canceled);
        if (handle is not null)
        {
            if (canceled)
            {
                handle.Canceled(result.Summary);
            }
            else
            {
                var warn = errors.Count > 0 || videos > 0 || set.Skipped > 0;
                handle.Complete(new ActivityCompletion(
                    result.Summary,
                    warn ? ActivityState.Warning : ActivityState.Succeeded,
                    errors.Count > 0 ? [ActivityAction.Details] : null,
                    errors.Count > 0 ? string.Join(Environment.NewLine, errors.Take(50)) : null));
            }
        }

        return result;
    }

    private sealed record Row(string Path, MediaType MediaType, bool Missing);

    private Dictionary<int, Row> ReadRows(IReadOnlyList<int> fileIds, CancellationToken ct)
    {
        var map = new Dictionary<int, Row>();
        using var connection = _factory.OpenSqliteConnection();
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.Id, d.Path, f.Name, f.MediaType, f.Missing FROM Files f JOIN Folders d ON d.Id = f.FolderId
                WHERE f.Id IN ({string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)))});
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                map[reader.GetInt32(0)] = new Row(Path.Combine(reader.GetString(1), reader.GetString(2)), (MediaType)reader.GetInt32(3), reader.GetBoolean(4));
            }
        }

        return map;
    }

    /// <summary>„Name.jpg“, sonst „Name (2).jpg“ … — nie überschreiben (auch nicht innerhalb desselben Laufs).</summary>
    internal static string FreeTarget(string directory, string stem, ISet<string> used)
    {
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? $"{stem}.jpg" : $"{stem} ({i}).jpg";
            var path = Path.Combine(directory, name);
            if (!used.Contains(path) && !File.Exists(path) && !Directory.Exists(path))
            {
                used.Add(path);
                return path;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path); // eigene, halb geschriebene Exportdatei — nie eine Bibliotheksdatei
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
