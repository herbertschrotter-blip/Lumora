using System.Buffers.Binary;
using System.Globalization;
using System.Security;
using System.Text;
using Lumora.Services.Enhance;

namespace Lumora.Services.Metadata;

/// <summary>Was in den EXIF-/XMP-Block eines zusammengefügten Ergebnisses kommt (ADR-0078 Punkt 10).</summary>
/// <param name="PrimarySourcePath">Quelle, deren EXIF-Block die Grundlage ist (Zeitquelle; nur JPEG wird byteweise übernommen).</param>
/// <param name="GpsSourcePath">Ortsquelle: ihr GANZES GPS-Verzeichnis (Breite/Länge, Höhe, GPS-Zeit …) wird eingesetzt; jeder Container.</param>
/// <param name="FallbackLocation">Ort aus dem Katalog, wenn die Ortsquelle selbst kein GPS trägt (in Lumora gesetzt).</param>
/// <param name="CapturedAtLocal">Aufnahmezeit als Wanduhrzeit (nur für einen neuen Block, wenn die Grundlage kein JPEG-EXIF ist).</param>
/// <param name="OffsetMinutes">Zeitversatz zu UTC (OffsetTimeOriginal), null ohne.</param>
/// <param name="Camera">„Hersteller Modell“ aus dem Katalog (nur für einen neuen Block).</param>
/// <param name="Method">Verfahren als Text für XMP („Panorama“).</param>
/// <param name="Equirectangular">Kugel 360° als 2 : 1-Rundum-Bild: GPano-Angaben für 360°-Betrachter.</param>
public sealed record MergeExifInput(
    string? PrimarySourcePath,
    string? GpsSourcePath,
    GeoCoordinate? FallbackLocation,
    DateTime? CapturedAtLocal,
    int? OffsetMinutes,
    string? Camera,
    int Width,
    int Height,
    string Method,
    int SourceCount,
    bool Equirectangular);

/// <summary>
/// Schreibt Aufnahmedaten in ein zusammengefügtes JPEG (ADR-0078 Punkt 10, Bau Stufe 1): Grundlage ist der EXIF-Block der
/// Zeitquelle (wie <see cref="JpegExifCopier"/>: Ausrichtung 1, Bilddaten unverändert), ohne dessen eingebettetes Vorschaubild
/// (IFD1 zeigte sonst das erste Einzelbild) und mit den Maßen des Ergebnisses. Das GPS-Verzeichnis kommt GANZ von der Ortsquelle
/// (auch Höhe 5/6 und GPS-Zeit 7/29 — sonst stufte jedes Nachlesen den Ort als „geschätzt“ ein, ADR-0062), aus jedem Container
/// (JPEG, HEIC, PNG, TIFF), Byte-Reihenfolge angeglichen. Ist die Grundlage kein JPEG, entsteht ein neuer Block mit Kamera,
/// Aufnahmezeit, Zeitversatz und GPS. Dazu XMP: <c>CreatorTool</c> „Lumora“, Verfahren, Quellenzahl; bei 2 : 1 GPano.
/// Nur der Kopf der Zieldatei ändert sich, nie die Bilddaten.
/// </summary>
public static class MergeExifWriter
{
    private const ushort TagOrientation = 0x0112;
    private const ushort TagMake = 0x010F;
    private const ushort TagModel = 0x0110;
    private const ushort TagSoftware = 0x0131;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagGpsIfd = 0x8825;
    private const ushort TagDateTimeOriginal = 0x9003;
    private const ushort TagOffsetTimeOriginal = 0x9011;
    private const ushort TagPixelX = 0xA002;
    private const ushort TagPixelY = 0xA003;
    private const ushort TypeByte = 1, TypeAscii = 2, TypeShort = 3, TypeLong = 4, TypeRational = 5;
    private static readonly byte[] ExifIdent = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpIdent = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

    private readonly record struct GpsDirectory(List<Entry> Entries);

    /// <summary>Schreibt EXIF und XMP in <paramref name="targetJpeg"/>; false, wenn die Datei kein JPEG ist.</summary>
    public static bool Write(string targetJpeg, MergeExifInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var target = File.ReadAllBytes(targetJpeg);
        if (target.Length < 4 || target[0] != 0xFF || target[1] != 0xD8)
        {
            return false;
        }

        var exif = BuildExifSegment(input);
        var merged = exif is null ? target : JpegExifCopier.Insert(target, exif) ?? target;
        merged = InsertXmp(merged, BuildXmp(input));
        var temp = targetJpeg + ".meta.tmp";
        File.WriteAllBytes(temp, merged);
        File.Move(temp, targetJpeg, overwrite: true);
        return true;
    }

    /// <summary>Der neue EXIF-Block (FF E1 …) oder null, wenn es nichts zu schreiben gibt.</summary>
    public static byte[]? BuildExifSegment(MergeExifInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var gps = ReadGpsDirectory(input.GpsSourcePath);
        if (gps is null && input.FallbackLocation is { } fallback)
        {
            gps = new GpsDirectory(LatLonEntries(fallback));
        }

        byte[]? primary = null;
        if (input.PrimarySourcePath is { } p && IsJpeg(p))
        {
            try
            {
                primary = JpegExifCopier.ReadExifSegment(File.ReadAllBytes(p));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                primary = null;
            }
        }

        if (primary is not null)
        {
            try
            {
                var segment = FromPrimary(primary, gps, input.Width, input.Height);
                if (segment is not null)
                {
                    return segment;
                }
            }
            catch (InvalidDataException)
            {
                // unlesbarer Block: neuer Block
            }
        }

        return Wrap(NewTiff(input, gps));
    }

    // ------------------------------------------------------------------ Grundlage: EXIF der Zeitquelle

    private static byte[]? FromPrimary(byte[] segment, GpsDirectory? gps, int width, int height)
    {
        var tiff = segment.AsSpan(10).ToArray();
        var little = tiff[0] == 'I' && tiff[1] == 'I';
        if (!little && !(tiff[0] == 'M' && tiff[1] == 'M'))
        {
            throw new InvalidDataException("EXIF: unbekannte Byte-Reihenfolge.");
        }

        var ifd0 = U32(tiff, 4, little);
        if (ifd0 < 8 || ifd0 + 2 > tiff.Length)
        {
            throw new InvalidDataException("EXIF: IFD0 außerhalb.");
        }

        var count = U16(tiff, (int)ifd0, little);
        if (count == 0 || ifd0 + 2 + count * 12L + 4 > tiff.Length)
        {
            throw new InvalidDataException("EXIF: IFD0 abgeschnitten.");
        }

        var entries = new List<byte[]>(count + 1);
        for (var i = 0; i < count; i++)
        {
            var e = tiff.AsSpan((int)ifd0 + 2 + i * 12, 12).ToArray();
            var tag = U16(e, 0, little);
            if (tag == TagGpsIfd)
            {
                continue; // wird neu gesetzt
            }

            if (tag == TagOrientation)
            {
                W16(e, 8, 1, little);
                e[10] = 0;
                e[11] = 0;
            }

            if (tag == TagExifIfd)
            {
                PatchPixelSize(tiff, U32(e, 8, little), width, height, little);
            }

            entries.Add(e);
        }

        using var output = new MemoryStream(tiff.Length + 512);
        output.Write(tiff);
        Pad(output);
        if (gps is { } g)
        {
            var gpsOffset = (uint)output.Position;
            WriteDirectory(output, Reorder(g.Entries, little), little);
            var pointer = new byte[12];
            W16(pointer, 0, TagGpsIfd, little);
            W16(pointer, 2, TypeLong, little);
            W32(pointer, 4, 1, little);
            W32(pointer, 8, gpsOffset, little);
            var at = entries.FindIndex(x => U16(x, 0, little) > TagGpsIfd);
            entries.Insert(at < 0 ? entries.Count : at, pointer);
            Pad(output);
        }

        // neues IFD0 ohne Verweis auf IFD1 (eingebettetes Vorschaubild des ersten Einzelbilds)
        var newIfd0 = (uint)output.Position;
        Span<byte> buf = stackalloc byte[4];
        W16(buf, 0, (ushort)entries.Count, little);
        output.Write(buf[..2]);
        foreach (var e in entries)
        {
            output.Write(e);
        }

        output.Write(new byte[4]);
        var bytes = output.ToArray();
        W32(bytes, 4, newIfd0, little);
        return bytes.Length + 2 + ExifIdent.Length <= JpegExifGpsWriter.MaxSegmentPayload ? Wrap(bytes) : null;
    }

    /// <summary>PixelXDimension/PixelYDimension im EXIF-Verzeichnis auf die Maße des Ergebnisses (in place, wenn inline).</summary>
    private static void PatchPixelSize(byte[] tiff, uint exifOffset, int width, int height, bool little)
    {
        if (width <= 0 || height <= 0 || exifOffset < 8 || exifOffset + 2 > tiff.Length)
        {
            return;
        }

        var count = U16(tiff, (int)exifOffset, little);
        for (var i = 0; i < count; i++)
        {
            var at = (int)exifOffset + 2 + i * 12;
            if (at + 12 > tiff.Length)
            {
                return;
            }

            var tag = U16(tiff, at, little);
            if (tag is not (TagPixelX or TagPixelY))
            {
                continue;
            }

            var value = (uint)(tag == TagPixelX ? width : height);
            var type = U16(tiff, at + 2, little);
            if (type == TypeLong)
            {
                W32(tiff, at + 8, value, little);
            }
            else if (type == TypeShort && value <= ushort.MaxValue)
            {
                W16(tiff, at + 8, (ushort)value, little);
            }
        }
    }

    // ------------------------------------------------------------------ Neuer Block (HEIC/PNG/TIFF als Zeitquelle)

    private static byte[] NewTiff(MergeExifInput input, GpsDirectory? gps)
    {
        const bool little = false;
        var ifd0 = new List<Entry>();
        var exif = new List<Entry>();
        if (!string.IsNullOrWhiteSpace(input.Camera))
        {
            // Katalog: „Hersteller Modell“ → Make = erstes Wort, Model = Rest
            var parts = input.Camera.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            ifd0.Add(Ascii(TagMake, parts[0]));
            ifd0.Add(Ascii(TagModel, parts.Length == 2 ? parts[1] : parts[0]));
        }

        ifd0.Add(new Entry(TagOrientation, TypeShort, 1, Short(1, little)));
        ifd0.Add(Ascii(TagSoftware, "Lumora"));
        if (input.CapturedAtLocal is { } t)
        {
            exif.Add(Ascii(TagDateTimeOriginal, t.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)));
            if (input.OffsetMinutes is { } off)
            {
                var sign = off < 0 ? '-' : '+';
                var a = Math.Abs(off);
                exif.Add(Ascii(TagOffsetTimeOriginal, string.Create(CultureInfo.InvariantCulture, $"{sign}{a / 60:00}:{a % 60:00}")));
            }
        }

        if (input.Width > 0 && input.Height > 0)
        {
            exif.Add(new Entry(TagPixelX, TypeLong, 1, Long((uint)input.Width, little)));
            exif.Add(new Entry(TagPixelY, TypeLong, 1, Long((uint)input.Height, little)));
        }

        using var output = new MemoryStream(512);
        output.Write("MM\0*"u8);
        output.Write([0, 0, 0, 8]);
        // IFD0 an Stelle 8; Zeiger auf EXIF/GPS werden nach dem Schreiben eingesetzt
        var ifd0Entries = ifd0.Count + (exif.Count > 0 ? 1 : 0) + (gps is not null ? 1 : 0);
        var ifd0Size = 2 + ifd0Entries * 12 + 4;
        var data = new MemoryStream();
        var dataStart = 8 + ifd0Size;
        var placed = new List<byte[]>();
        foreach (var e in ifd0)
        {
            placed.Add(Place(e, data, dataStart, little));
        }

        var exifPointerIndex = -1;
        var gpsPointerIndex = -1;
        if (exif.Count > 0)
        {
            exifPointerIndex = placed.Count;
            placed.Add(new byte[12]);
        }

        if (gps is not null)
        {
            gpsPointerIndex = placed.Count;
            placed.Add(new byte[12]);
        }

        // Datenbereich von IFD0, danach EXIF- und GPS-Verzeichnis
        output.Position = 8;
        var ifd0Bytes = new byte[ifd0Size];
        output.Write(ifd0Bytes);
        output.Write(data.ToArray());
        Pad(output);
        if (exif.Count > 0)
        {
            var exifOffset = (uint)output.Position;
            WriteDirectory(output, exif, little);
            Pad(output);
            placed[exifPointerIndex] = Pointer(TagExifIfd, exifOffset, little);
        }

        if (gps is { } g)
        {
            var gpsOffset = (uint)output.Position;
            WriteDirectory(output, g.Entries, little);
            Pad(output);
            placed[gpsPointerIndex] = Pointer(TagGpsIfd, gpsOffset, little);
        }

        var bytes = output.ToArray();
        var sorted = placed.OrderBy(e => U16(e, 0, little)).ToList();
        W16(bytes, 8, (ushort)sorted.Count, little);
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].CopyTo(bytes, 10 + i * 12);
        }

        return bytes;
    }

    private static byte[] Place(Entry e, MemoryStream data, int dataStart, bool little)
    {
        var entry = new byte[12];
        W16(entry, 0, e.Tag, little);
        W16(entry, 2, e.Type, little);
        W32(entry, 4, e.Count, little);
        if (e.Value.Length <= 4)
        {
            e.Value.CopyTo(entry, 8);
        }
        else
        {
            if (data.Position % 2 == 1)
            {
                data.WriteByte(0);
            }

            W32(entry, 8, (uint)(dataStart + data.Position), little);
            data.Write(e.Value);
        }

        return entry;
    }

    private static byte[] Pointer(ushort tag, uint offset, bool little)
    {
        var e = new byte[12];
        W16(e, 0, tag, little);
        W16(e, 2, TypeLong, little);
        W32(e, 4, 1, little);
        W32(e, 8, offset, little);
        return e;
    }

    private static Entry Ascii(ushort tag, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\0");
        return new Entry(tag, TypeAscii, (uint)bytes.Length, bytes);
    }

    private static byte[] Short(ushort v, bool little)
    {
        var b = new byte[2];
        W16(b, 0, v, little);
        return b;
    }

    private static byte[] Long(uint v, bool little)
    {
        var b = new byte[4];
        W32(b, 0, v, little);
        return b;
    }

    /// <summary>Verzeichnis samt Werten über 4 Byte direkt dahinter (Versätze relativ zum TIFF-Anfang = Stream-Position).</summary>
    private static void WriteDirectory(MemoryStream output, List<Entry> entries, bool little)
    {
        var start = output.Position;
        var sorted = entries.OrderBy(e => e.Tag).ToList();
        var dataStart = start + 2 + sorted.Count * 12 + 4;
        var data = new MemoryStream();
        var raw = new List<byte[]>();
        foreach (var e in sorted)
        {
            var entry = new byte[12];
            W16(entry, 0, e.Tag, little);
            W16(entry, 2, e.Type, little);
            W32(entry, 4, e.Count, little);
            if (e.Value.Length <= 4)
            {
                e.Value.CopyTo(entry, 8);
            }
            else
            {
                if (data.Position % 2 == 1)
                {
                    data.WriteByte(0);
                }

                W32(entry, 8, (uint)(dataStart + data.Position), little);
                data.Write(e.Value);
            }

            raw.Add(entry);
        }

        Span<byte> count = stackalloc byte[2];
        W16(count, 0, (ushort)sorted.Count, little);
        output.Write(count);
        foreach (var e in raw)
        {
            output.Write(e);
        }

        output.Write(new byte[4]);
        output.Write(data.ToArray());
    }

    // ------------------------------------------------------------------ GPS der Ortsquelle

    /// <summary>
    /// Alle Einträge des GPS-Verzeichnisses der Ortsquelle (Werte als Bytes in Big Endian, d. h. passend für jeden Zielblock nach
    /// <see cref="Reorder"/>), aus jedem Container über <see cref="MediaDateReader.ReadContainer{T}"/>; null ohne GPS.
    /// </summary>
    private static GpsDirectory? ReadGpsDirectory(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return MediaDateReader.ReadContainer<GpsDirectory>(stream, ReadGpsTiff, static (_, _) => null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static GpsDirectory? ReadGpsTiff(Stream stream, long tiffStart)
    {
        var header = new byte[8];
        if (!ReadAt(stream, tiffStart, header))
        {
            return null;
        }

        var little = header[0] == 'I' && header[1] == 'I';
        if (!little && !(header[0] == 'M' && header[1] == 'M'))
        {
            return null;
        }

        var ifd0 = ReadIfd(stream, tiffStart, U32(header, 4, little), little);
        var at = ifd0?.FindIndex(e => e.Tag == TagGpsIfd) ?? -1;
        if (at < 0 || ifd0![at].Value is not { Length: >= 4 } pointer)
        {
            return null;
        }

        var gpsOffset = little ? BinaryPrimitives.ReadUInt32LittleEndian(pointer) : BinaryPrimitives.ReadUInt32BigEndian(pointer);
        var gps = ReadIfd(stream, tiffStart, gpsOffset, little);
        if (gps is null || gps.Count == 0)
        {
            return null;
        }

        // Werte in Big Endian bringen; beim Schreiben in einen Intel-Block wieder drehen (Reorder)
        var list = gps.Select(e => e with { Value = little ? Swap(e.Type, e.Value) : e.Value }).ToList();
        return list.Any(e => e.Tag is 2 or 4) ? new GpsDirectory(list) : null;
    }

    private static List<Entry>? ReadIfd(Stream stream, long tiffStart, uint offset, bool little)
    {
        var countBytes = new byte[2];
        if (offset < 8 || !ReadAt(stream, tiffStart + offset, countBytes))
        {
            return null;
        }

        var count = U16(countBytes, 0, little);
        if (count == 0 || count > 512)
        {
            return null;
        }

        var raw = new byte[count * 12];
        if (!ReadAt(stream, tiffStart + offset + 2, raw))
        {
            return null;
        }

        var list = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var tag = U16(raw, i * 12, little);
            var type = U16(raw, i * 12 + 2, little);
            var n = U32(raw, i * 12 + 4, little);
            var size = TypeSize(type) * (long)n;
            if (size <= 0 || size > 4096)
            {
                continue;
            }

            byte[] value;
            if (size <= 4)
            {
                value = raw.AsSpan(i * 12 + 8, (int)size).ToArray();
            }
            else
            {
                value = new byte[size];
                if (!ReadAt(stream, tiffStart + U32(raw, i * 12 + 8, little), value))
                {
                    continue;
                }
            }

            list.Add(new Entry(tag, type, n, value));
        }

        return list;
    }

    private static List<Entry> LatLonEntries(GeoCoordinate value)
    {
        static byte[] Dms(double degrees)
        {
            var milli = (long)Math.Round(Math.Abs(degrees) * 3_600_000.0);
            var b = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)(milli / 3_600_000));
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)(milli % 3_600_000 / 60_000));
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)(milli % 60_000));
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 1000);
            return b;
        }

        return
        [
            new Entry(0, TypeByte, 4, [2, 3, 0, 0]),
            new Entry(1, TypeAscii, 2, [(byte)(value.Latitude < 0 ? 'S' : 'N'), 0]),
            new Entry(2, TypeRational, 3, Dms(value.Latitude)),
            new Entry(3, TypeAscii, 2, [(byte)(value.Longitude < 0 ? 'W' : 'E'), 0]),
            new Entry(4, TypeRational, 3, Dms(value.Longitude)),
        ];
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    /// <summary>Werte eines Eintrags zwischen Intel und Motorola drehen (je Element nach Typgröße).</summary>
    private static byte[] Swap(ushort type, byte[] value)
    {
        var size = TypeSize(type);
        var unit = size == 8 ? 4 : size; // RATIONAL = zwei LONG
        if (unit <= 1)
        {
            return value;
        }

        var copy = (byte[])value.Clone();
        for (var i = 0; i + unit <= copy.Length; i += unit)
        {
            Array.Reverse(copy, i, unit);
        }

        return copy;
    }

    // ------------------------------------------------------------------ XMP

    private static byte[] BuildXmp(MergeExifInput input)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:lumora=\"https://github.com/herbertschrotter-blip/Lumora/ns/merge/1.0/\"");
        if (input.Equirectangular)
        {
            sb.Append(" xmlns:GPano=\"http://ns.google.com/photos/1.0/panorama/\"");
        }

        sb.Append(" xmp:CreatorTool=\"Lumora\"");
        sb.Append(" lumora:Method=\"").Append(SecurityElement.Escape(input.Method)).Append('"');
        sb.Append(" lumora:SourceCount=\"").Append(input.SourceCount.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (input.Equirectangular && input.Width > 0 && input.Height > 0)
        {
            var w = input.Width.ToString(CultureInfo.InvariantCulture);
            var h = input.Height.ToString(CultureInfo.InvariantCulture);
            sb.Append(" GPano:ProjectionType=\"equirectangular\" GPano:UsePanoramaViewer=\"True\"");
            sb.Append(" GPano:FullPanoWidthPixels=\"").Append(w).Append("\" GPano:FullPanoHeightPixels=\"").Append(h).Append('"');
            sb.Append(" GPano:CroppedAreaImageWidthPixels=\"").Append(w).Append("\" GPano:CroppedAreaImageHeightPixels=\"").Append(h).Append('"');
            sb.Append(" GPano:CroppedAreaLeftPixels=\"0\" GPano:CroppedAreaTopPixels=\"0\"");
        }

        sb.Append("/></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
        var packet = Encoding.UTF8.GetBytes(sb.ToString());
        var payload = 2 + XmpIdent.Length + packet.Length;
        var segment = new byte[2 + payload];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)payload);
        XmpIdent.CopyTo(segment, 4);
        packet.CopyTo(segment, 4 + XmpIdent.Length);
        return segment;
    }

    /// <summary>XMP-Segment nach den APP0/APP1-Segmenten am Anfang einfügen; ein vorhandenes XMP-Segment wird ersetzt.</summary>
    private static byte[] InsertXmp(byte[] jpeg, byte[] xmp)
    {
        var i = 2;
        var insertAt = 2;
        var removeAt = -1;
        var removeLength = 0;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            if (marker is 0xDA or 0xD9)
            {
                break;
            }

            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length)
            {
                break;
            }

            if (marker == 0xE1 && i + 4 + XmpIdent.Length <= jpeg.Length && jpeg.AsSpan(i + 4, XmpIdent.Length).SequenceEqual(XmpIdent))
            {
                removeAt = i;
                removeLength = length + 2;
            }
            else if (marker is 0xE0 or 0xE1)
            {
                insertAt = i + length + 2;
            }

            i += length + 2;
        }

        using var output = new MemoryStream(jpeg.Length + xmp.Length);
        if (removeAt >= 0 && removeAt < insertAt)
        {
            output.Write(jpeg, 0, removeAt);
            output.Write(jpeg, removeAt + removeLength, insertAt - removeAt - removeLength);
            output.Write(xmp);
            output.Write(jpeg, insertAt, jpeg.Length - insertAt);
        }
        else if (removeAt >= 0)
        {
            output.Write(jpeg, 0, insertAt);
            output.Write(xmp);
            output.Write(jpeg, insertAt, removeAt - insertAt);
            output.Write(jpeg, removeAt + removeLength, jpeg.Length - removeAt - removeLength);
        }
        else
        {
            output.Write(jpeg, 0, insertAt);
            output.Write(xmp);
            output.Write(jpeg, insertAt, jpeg.Length - insertAt);
        }

        return output.ToArray();
    }

    // ------------------------------------------------------------------ Hilfen

    private static bool IsJpeg(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Wrap(byte[] tiff)
    {
        var payload = 2 + ExifIdent.Length + tiff.Length;
        if (payload > JpegExifGpsWriter.MaxSegmentPayload)
        {
            throw new InvalidDataException("Der EXIF-Block würde größer als 64 KB (JPEG-Grenze).");
        }

        var segment = new byte[2 + payload];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)payload);
        ExifIdent.CopyTo(segment, 4);
        tiff.CopyTo(segment, 10);
        return segment;
    }

    /// <summary>Werte, die als Big Endian vorliegen, für einen Intel-Block drehen.</summary>
    private static List<Entry> Reorder(List<Entry> entries, bool little) => little ? entries.Select(e => e with { Value = Swap(e.Type, e.Value) }).ToList() : entries;

    private static void Pad(MemoryStream output)
    {
        if (output.Position % 2 == 1)
        {
            output.WriteByte(0);
        }
    }

    private static bool ReadAt(Stream stream, long position, byte[] buffer)
    {
        if (position < 0 || position + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = position;
        stream.ReadExactly(buffer);
        return true;
    }

    private static ushort U16(ReadOnlySpan<byte> b, int at, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt16BigEndian(b[at..]);

    private static uint U32(ReadOnlySpan<byte> b, int at, bool little)
        => little ? BinaryPrimitives.ReadUInt32LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt32BigEndian(b[at..]);

    private static void W16(Span<byte> b, int at, ushort value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b[at..], value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(b[at..], value);
        }
    }

    private static void W32(Span<byte> b, int at, uint value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b[at..], value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b[at..], value);
        }
    }
}
