using System.Security.Cryptography;
using Lumora.Domain.Enums;
using Lumora.Services.Metadata;
using Lumora.Services.Thumbnails;
using Lumora.Services.Watch;

namespace Lumora.Services.Places;

/// <summary>Datei konnte nicht geschrieben werden — der Grund steht in der Meldung (für „Name: Grund“).</summary>
public sealed class GeoWriteException(string reason) : Exception(reason);

/// <summary>Was beim Schreiben einer Datei geschah — Grundlage für Katalog und Rückgängig-Speicher.</summary>
/// <param name="Kind">Art; <see cref="GeoTagWriteKind.CatalogOnly"/> = Datei nicht angefasst.</param>
/// <param name="WrittenPath">Geschriebene Datei (JPEG bzw. XMP), sonst null.</param>
/// <param name="OldBytes">JPEG: altes APP1-Segment (leer = keines); XMP: alte Datei (null = keine).</param>
/// <param name="WrittenHash">SHA-256 des geschriebenen Segments bzw. der XMP-Datei.</param>
/// <param name="SegmentOffset">JPEG: Lage des Segments.</param>
/// <param name="OldModifiedTicks">JPEG: Änderungszeit vorher als UTC-Ticks (volle Genauigkeit für Rückgängig).</param>
/// <param name="OldSize">JPEG: Größe vorher.</param>
/// <param name="NewSize">JPEG: Größe nachher (für den Katalog).</param>
/// <param name="NewModifiedAt">JPEG: Änderungszeit nachher (für den Katalog).</param>
public sealed record GeoFileWrite(
    GeoTagWriteKind Kind,
    string? WrittenPath = null,
    byte[]? OldBytes = null,
    byte[]? WrittenHash = null,
    long? SegmentOffset = null,
    long? OldModifiedTicks = null,
    long? OldSize = null,
    long? NewSize = null,
    long? NewModifiedAt = null)
{
    public static GeoFileWrite CatalogOnly { get; } = new(GeoTagWriteKind.CatalogOnly);
}

/// <summary>
/// Schreibt Koordinaten in Dateien und stellt sie wieder her (ADR-0050) — nie halbe Dateien: der neue Inhalt entsteht in
/// einer Temp-Datei neben dem Original, wird geprüft und dann per <see cref="File.Replace(string, string, string?)"/>
/// eingesetzt (Erstellungsdatum, Attribute und Rechte bleiben). JPEG: nur das APP1-„Exif“-Segment
/// (<see cref="JpegExifGpsWriter"/>); alle anderen Bilder (RAW, HEIC, TIFF, PNG, WebP): XMP-Begleitdatei; Videos: nur
/// Katalog. Fehler (fehlt, nur in der Cloud, schreibgeschützt, gesperrt, kein lesbares EXIF/XMP) als
/// <see cref="GeoWriteException"/> mit deutschem Grund — das Original bleibt dann unverändert.
/// </summary>
public static class GeoFileWriter
{
    private static readonly string[] JpegExtensions = [".jpg", ".jpeg", ".jpe", ".jfif"];

    /// <summary>Setzt (<paramref name="value"/>) bzw. entfernt (null) die Koordinate in der Datei.</summary>
    /// <param name="mediaType">0 Bild, 1 Video, 2 Archiv (wie <c>Files.MediaType</c>).</param>
    /// <param name="scope">Eigene Schreibvorgänge für die Ordnerüberwachung (optional).</param>
    public static GeoFileWrite Write(string path, int mediaType, GeoCoordinate? value, bool keepModifiedTime, IOwnChangeScope? scope = null)
    {
        switch (mediaType)
        {
            case 1:
                return GeoFileWrite.CatalogOnly; // Videos: nur Katalog (ADR-0050)
            case 2:
                throw new GeoWriteException("Archive haben keinen Ort.");
        }

        EnsureLocal(path);
        if (IsJpeg(path))
        {
            return WriteJpeg(path, value, keepModifiedTime, scope);
        }

        return WriteSidecar(path, value, scope);
    }

    /// <summary>JPEG an Endung und Dateikopf erkannt.</summary>
    public static bool IsJpeg(string path)
    {
        if (!JpegExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[2];
            return stream.Length > 2 && stream.Read(head) == 2 && head[0] == 0xFF && head[1] == 0xD8;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GeoWriteException(ex is UnauthorizedAccessException ? "Zugriff verweigert." : $"Datei nicht lesbar ({ex.Message.TrimEnd('.')}).");
        }
    }

    private static void EnsureLocal(string path)
    {
        if (!File.Exists(path))
        {
            throw new GeoWriteException("Datei fehlt.");
        }

        if (!ThumbnailService.IsLocallyAvailable(path))
        {
            throw new GeoWriteException("Datei liegt nur in der Cloud (OneDrive-Platzhalter) — bitte zuerst herunterladen.");
        }
    }

    // ---- JPEG ----------------------------------------------------------------------------------

    private static GeoFileWrite WriteJpeg(string path, GeoCoordinate? value, bool keepModifiedTime, IOwnChangeScope? scope)
    {
        DateTime oldModified;
        long oldSize;
        byte[]? oldSegment;
        byte[]? newSegment;
        JpegExifLocation location;
        try
        {
            var info = new FileInfo(path);
            if (info.IsReadOnly)
            {
                throw new GeoWriteException("Datei ist schreibgeschützt.");
            }

            oldModified = info.LastWriteTimeUtc;
            oldSize = info.Length;
            using var source = OpenRead(path);
            location = JpegExifGpsWriter.Locate(source) ?? throw new GeoWriteException("Kein lesbares JPEG.");
            oldSegment = location.Exists ? ReadRange(source, location.Offset, location.Length) : null;
            newSegment = JpegExifGpsWriter.BuildSegment(oldSegment, value);
        }
        catch (InvalidDataException ex)
        {
            throw new GeoWriteException($"EXIF nicht änderbar: {ex.Message}");
        }
        catch (EndOfStreamException)
        {
            throw new GeoWriteException("EXIF nicht änderbar: Datei abgeschnitten.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GeoWriteException(DescribeIo(ex));
        }

        if (newSegment is null)
        {
            return GeoFileWrite.CatalogOnly; // nichts zu entfernen
        }

        ReplaceSegment(path, location.Offset, oldSegment?.Length ?? 0, newSegment, value, scope);

        // Ab hier ist die Datei ersetzt: nichts darf mehr werfen, sonst fehlten Katalog und Rückgängig-Daten.
        var newModified = DateTime.UtcNow;
        try
        {
            if (keepModifiedTime)
            {
                File.SetLastWriteTimeUtc(path, oldModified);
                newModified = oldModified;
            }
            else
            {
                newModified = File.GetLastWriteTimeUtc(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Änderungsdatum nicht zurückgesetzt — die Datei ist trotzdem richtig geschrieben.
        }

        return new GeoFileWrite(
            GeoTagWriteKind.JpegExif,
            path,
            oldSegment ?? [],
            SHA256.HashData(newSegment),
            location.Offset,
            oldModified.Ticks,
            oldSize,
            oldSize - (oldSegment?.Length ?? 0) + newSegment.Length,
            ToUnix(newModified));
    }

    /// <summary>
    /// ADR-0068: beliebige Änderung des APP1-„Exif“-Segments eines JPEG nach demselben Verfahren wie das GPS (Temp-Datei, Prüfung,
    /// Ersetzen; Rückgängig über <see cref="Restore"/>) — z. B. die Ausrichtung beim Drehen. <paramref name="build"/> bekommt das
    /// alte Segment (null = keines) und liefert das neue (null = nichts zu ändern → <see cref="GeoFileWrite.CatalogOnly"/>).
    /// <paramref name="newModifiedTime"/> bekommt die alte Änderungszeit und liefert die neue (null = Zeitpunkt des Schreibens).
    /// </summary>
    public static GeoFileWrite WriteJpegExif(string path, Func<byte[]?, byte[]?> build, Func<DateTime, DateTime?> newModifiedTime, IOwnChangeScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(newModifiedTime);
        EnsureLocal(path);
        if (!IsJpeg(path))
        {
            throw new GeoWriteException("Nur JPEG-Dateien.");
        }

        DateTime oldModified;
        long oldSize;
        byte[]? oldSegment;
        byte[]? newSegment;
        JpegExifLocation location;
        try
        {
            var info = new FileInfo(path);
            if (info.IsReadOnly)
            {
                throw new GeoWriteException("Datei ist schreibgeschützt.");
            }

            oldModified = info.LastWriteTimeUtc;
            oldSize = info.Length;
            using var source = OpenRead(path);
            location = JpegExifGpsWriter.Locate(source) ?? throw new GeoWriteException("Kein lesbares JPEG.");
            oldSegment = location.Exists ? ReadRange(source, location.Offset, location.Length) : null;
            newSegment = build(oldSegment);
        }
        catch (InvalidDataException ex)
        {
            throw new GeoWriteException($"EXIF nicht änderbar: {ex.Message}");
        }
        catch (EndOfStreamException)
        {
            throw new GeoWriteException("EXIF nicht änderbar: Datei abgeschnitten.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GeoWriteException(DescribeIo(ex));
        }

        if (newSegment is null)
        {
            return GeoFileWrite.CatalogOnly;
        }

        ReplaceSegment(path, location.Offset, oldSegment?.Length ?? 0, newSegment, null, scope, verifyGps: false);

        // Ab hier ist die Datei ersetzt: nichts darf mehr werfen.
        var newModified = DateTime.UtcNow;
        try
        {
            if (newModifiedTime(oldModified) is { } wanted)
            {
                File.SetLastWriteTimeUtc(path, wanted);
            }

            newModified = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Änderungsdatum nicht gesetzt — die Datei ist trotzdem richtig geschrieben.
        }

        return new GeoFileWrite(
            GeoTagWriteKind.JpegExif,
            path,
            oldSegment ?? [],
            SHA256.HashData(newSegment),
            location.Offset,
            oldModified.Ticks,
            oldSize,
            oldSize - (oldSegment?.Length ?? 0) + newSegment.Length,
            ToUnix(newModified));
    }

    /// <summary>
    /// Ersetzt <paramref name="oldLength"/> Bytes ab <paramref name="offset"/> durch <paramref name="segment"/> (0 = einfügen,
    /// leeres Segment = entfernen): Temp-Datei neben dem Original, Kopf + Segment + Rest unverändert kopiert, geprüft,
    /// dann eingesetzt. <paramref name="expect"/>: GPS, das die neue Datei haben muss (null = keines).
    /// </summary>
    internal static void ReplaceSegment(string path, long offset, int oldLength, byte[] segment, GeoCoordinate? expect, IOwnChangeScope? scope, bool verifyGps = true)
    {
        var temp = TempPathFor(path);
        scope?.Add(path);
        scope?.Add(temp);
        try
        {
            long expectedLength;
            using (var source = OpenRead(path))
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                CopyRange(source, target, 0, offset);
                target.Write(segment);
                CopyRange(source, target, offset + oldLength, source.Length - offset - oldLength);
                expectedLength = source.Length - oldLength + segment.Length;
                target.Flush(flushToDisk: true);
            }

            Verify(temp, offset, segment, expectedLength, expect, verifyGps);
            ReplaceFile(temp, path);
        }
        catch (GeoWriteException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GeoWriteException(DescribeIo(ex));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void Verify(string temp, long offset, byte[] segment, long expectedLength, GeoCoordinate? expect, bool verifyGps)
    {
        using var check = OpenRead(temp);
        if (check.Length != expectedLength)
        {
            throw new GeoWriteException("Prüfung der neuen Datei fehlgeschlagen (Länge).");
        }

        if (segment.Length > 0 && !ReadRange(check, offset, segment.Length).AsSpan().SequenceEqual(segment))
        {
            throw new GeoWriteException("Prüfung der neuen Datei fehlgeschlagen (EXIF).");
        }

        if (!verifyGps)
        {
            return;
        }

        var read = MediaGpsReader.Read(check);
        var ok = expect is { } e
            ? read is { } r && Math.Abs(r.Coordinate.Latitude - e.Latitude) < 1e-6 && Math.Abs(r.Coordinate.Longitude - e.Longitude) < 1e-6
            : read is null;
        if (!ok)
        {
            throw new GeoWriteException("Prüfung der neuen Datei fehlgeschlagen (GPS nicht wie erwartet).");
        }
    }

    // ---- XMP-Begleitdatei -------------------------------------------------------------------------

    private static GeoFileWrite WriteSidecar(string mediaPath, GeoCoordinate? value, IOwnChangeScope? scope)
    {
        var existing = XmpGpsSidecar.FindExisting(mediaPath);
        var target = existing ?? XmpGpsSidecar.PreferredPath(mediaPath);
        byte[]? oldBytes = null;
        try
        {
            if (existing is not null)
            {
                if (new FileInfo(existing).IsReadOnly)
                {
                    throw new GeoWriteException("XMP-Begleitdatei ist schreibgeschützt.");
                }

                oldBytes = File.ReadAllBytes(existing);
            }

            if (value is null && (oldBytes is null || !XmpGpsSidecar.ContainsGps(oldBytes)))
            {
                return GeoFileWrite.CatalogOnly; // nichts zu entfernen — fremde Begleitdatei nicht umformatieren
            }

            byte[]? newBytes;
            try
            {
                newBytes = XmpGpsSidecar.Upsert(oldBytes, value);
            }
            catch (InvalidDataException ex)
            {
                throw new GeoWriteException(ex.Message);
            }

            if (newBytes is null)
            {
                return GeoFileWrite.CatalogOnly; // nichts zu schreiben
            }

            WriteWhole(target, newBytes, scope);
            return new GeoFileWrite(GeoTagWriteKind.XmpSidecar, target, oldBytes, SHA256.HashData(newBytes));
        }
        catch (GeoWriteException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GeoWriteException(DescribeIo(ex));
        }
    }

    /// <summary>Ganze Datei atomar schreiben (Temp-Datei + Ersetzen bzw. Umbenennen).</summary>
    internal static void WriteWhole(string path, byte[] bytes, IOwnChangeScope? scope)
    {
        var temp = TempPathFor(path);
        scope?.Add(path);
        scope?.Add(temp);
        try
        {
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                target.Write(bytes);
                target.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                ReplaceFile(temp, path);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // ---- Rückgängig ---------------------------------------------------------------------------------

    /// <summary>
    /// Datei auf den Stand vor dem Schreiben (Rückgängig, Rücknahme nach gescheiterter Katalog-Transaktion): JPEG = altes
    /// APP1-Segment an derselben Stelle und alte Änderungszeit (danach Byte für Byte die alte Datei), XMP = alte
    /// Begleitdatei bzw. die von Lumora angelegte entfernen (<paramref name="removeCreatedFile"/>, produktiv Papierkorb).
    /// Nur, wenn der geschriebene Stand noch unverändert ist (SHA-256). Null bei Erfolg, sonst der Grund.
    /// </summary>
    public static string? Restore(GeoFileWrite write, IOwnChangeScope? scope = null, Action<string>? removeCreatedFile = null)
    {
        if (write.WrittenPath is not { } path)
        {
            return null;
        }

        try
        {
            return write.Kind switch
            {
                GeoTagWriteKind.JpegExif => RestoreJpeg(path, write, scope),
                GeoTagWriteKind.XmpSidecar => RestoreSidecar(path, write, scope, removeCreatedFile),
                _ => null,
            };
        }
        catch (GeoWriteException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DescribeIo(ex);
        }
    }

    private static string? RestoreJpeg(string path, GeoFileWrite write, IOwnChangeScope? scope)
    {
        if (!File.Exists(path))
        {
            return "Datei fehlt.";
        }

        if (!ThumbnailService.IsLocallyAvailable(path))
        {
            return "Datei liegt nur in der Cloud (OneDrive-Platzhalter) — bitte zuerst herunterladen.";
        }

        JpegExifLocation? location;
        byte[]? current = null;
        using (var source = OpenRead(path))
        {
            location = JpegExifGpsWriter.Locate(source);
            if (location is { Exists: true } l && l.Offset == write.SegmentOffset)
            {
                current = ReadRange(source, l.Offset, l.Length);
            }
        }

        if (current is null || write.WrittenHash is null || !SHA256.HashData(current).AsSpan().SequenceEqual(write.WrittenHash))
        {
            return "Datei wurde seit dem Setzen verändert — nicht zurückgesetzt.";
        }

        ReplaceSegment(path, location!.Value.Offset, current.Length, write.OldBytes ?? [], null, scope, verifyGps: false);
        if (write.OldModifiedTicks is { } ticks)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, new DateTime(ticks, DateTimeKind.Utc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Inhalt ist zurück; nur das Änderungsdatum nicht.
            }
        }

        return null;
    }

    private static string? RestoreSidecar(string path, GeoFileWrite write, IOwnChangeScope? scope, Action<string>? removeCreatedFile)
    {
        if (!File.Exists(path))
        {
            return write.OldBytes is null ? null : "XMP-Begleitdatei fehlt.";
        }

        var current = File.ReadAllBytes(path);
        if (write.WrittenHash is null || !SHA256.HashData(current).AsSpan().SequenceEqual(write.WrittenHash))
        {
            return "XMP-Begleitdatei wurde seit dem Setzen verändert — nicht zurückgesetzt.";
        }

        if (write.OldBytes is { } old)
        {
            WriteWhole(path, old, scope);
            return null;
        }

        // Von Lumora angelegt: entfernen (produktiv in den Papierkorb, nie endgültig löschen).
        scope?.Add(path);
        if (removeCreatedFile is not null)
        {
            removeCreatedFile(path);
        }
        else
        {
            File.Delete(path);
        }

        return null;
    }

    // ---- Hilfen -----------------------------------------------------------------------------------

    /// <summary>Temp-Datei im selben Ordner (gleiches Laufwerk für das Ersetzen), versteckt-ähnlicher Name.</summary>
    internal static string TempPathFor(string path)
        => Path.Combine(Path.GetDirectoryName(path) ?? ".", $"~{Path.GetFileName(path)}.{Guid.NewGuid():N}.lumora.tmp");

    private static void ReplaceFile(string temp, string path)
    {
        try
        {
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temp, path, overwrite: true);
        }
    }

    internal static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    internal static byte[] ReadRange(Stream stream, long offset, int length)
    {
        var buffer = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void CopyRange(Stream source, Stream target, long offset, long count)
    {
        source.Position = offset;
        var buffer = new byte[1 << 16];
        var remaining = count;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new IOException("Unerwartetes Dateiende.");
            }

            target.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp-Datei bleibt liegen — harmlos, der Name ist eindeutig.
        }
    }

    internal static string DescribeIo(Exception ex)
        => ex switch
        {
            UnauthorizedAccessException => "Zugriff verweigert (schreibgeschützt oder keine Berechtigung).",
            // 32/33 Freigabe-/Sperrverletzung, 1175–1177 ReplaceFile konnte das Original nicht ersetzen (geöffnet)
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 or 1175 or 1176 or 1177 => "Datei ist in Verwendung (von einem anderen Programm geöffnet).",
            IOException io when (io.HResult & 0xFFFF) is 112 => "Nicht genug Speicherplatz.",
            _ => $"Schreiben fehlgeschlagen: {ex.Message.TrimEnd('.')}.",
        };

    internal static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
