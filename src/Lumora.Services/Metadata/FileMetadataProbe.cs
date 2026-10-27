using System.Collections.Concurrent;

namespace Lumora.Services.Metadata;

/// <summary>Was ein Lesedurchgang aus dem Dateikopf holt: Aufnahmedatum, Koordinate und GPS-Angaben (ohne Dateiname/XMP).</summary>
public sealed record FileMetadata(CaptureDateValue? MediaDate, GeoReading? MediaGps, GpsExifDetails? GpsDetails)
{
    public static FileMetadata Empty { get; } = new(null, null, null);
}

/// <summary>
/// Liest den Dateikopf einer Datei einmal für Aufnahmedatum, Orte und Genauigkeit (ADR-0074) und merkt sich das Ergebnis
/// kurz — der Hintergrund-Planer lässt die drei Dienste Stapel um Stapel über dieselben Dateien laufen, so dass nur der
/// erste die Datei öffnet.
/// </summary>
public interface IFileMetadataProbe
{
    FileMetadata Read(string path);
}

/// <summary>
/// Standard-Umsetzung: eine geöffnete Datei, drei Leser (<see cref="MediaDateReader"/>, <see cref="MediaGpsReader"/>,
/// <see cref="ExifGpsDetailsReader"/>). Merkt sich höchstens <see cref="Capacity"/> Dateien, gültig solange Größe und
/// Änderungszeit gleich sind (eine kurze Abfrage statt eines erneuten Lesens).
/// </summary>
public sealed class FileMetadataProbe : IFileMetadataProbe
{
    public const int Capacity = 4096;

    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _clock;

    /// <summary>Wie oft wirklich gelesen wurde (Tests, Protokoll).</summary>
    public long Reads => Interlocked.Read(ref _reads);

    private long _reads;

    public FileMetadata Read(string path)
    {
        if (!TryStat(path, out var length, out var modified))
        {
            return FileMetadata.Empty;
        }

        if (_cache.TryGetValue(path, out var hit) && hit.Length == length && hit.Modified == modified)
        {
            hit.LastUse = Interlocked.Increment(ref _clock);
            return hit.Metadata;
        }

        var metadata = ReadOnce(path);
        Interlocked.Increment(ref _reads);
        _cache[path] = new Entry(length, modified, metadata) { LastUse = Interlocked.Increment(ref _clock) };
        if (_cache.Count > Capacity)
        {
            Trim();
        }

        return metadata;
    }

    internal static FileMetadata ReadOnce(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.RandomAccess);
            var date = MediaDateReader.Read(stream);
            stream.Position = 0;
            var gps = MediaGpsReader.Read(stream);
            stream.Position = 0;
            var details = ExifGpsDetailsReader.Read(stream);
            return new FileMetadata(date, gps, details);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return FileMetadata.Empty;
        }
    }

    private static bool TryStat(string path, out long length, out DateTime modified)
    {
        try
        {
            var info = new FileInfo(path);
            length = info.Length;
            modified = info.LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            length = 0;
            modified = default;
            return false;
        }
    }

    /// <summary>Die ältere Hälfte vergessen.</summary>
    private void Trim()
    {
        var cut = _cache.Values.Select(e => e.LastUse).OrderBy(t => t).ElementAtOrDefault(_cache.Count / 2);
        foreach (var (key, entry) in _cache)
        {
            if (entry.LastUse <= cut)
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(long Length, DateTime Modified, FileMetadata Metadata)
    {
        public long LastUse { get; set; }
    }
}
