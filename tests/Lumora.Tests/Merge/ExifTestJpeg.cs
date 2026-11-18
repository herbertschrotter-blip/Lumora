using System.Buffers.Binary;
using System.Text;

namespace Lumora.Tests.Merge;

/// <summary>
/// Baut JPEG-förmige Testdateien mit vollständigem EXIF (IFD0 mit Kamera und Ausrichtung, EXIF-Verzeichnis mit Aufnahmezeit
/// und Maßen, GPS-Verzeichnis mit Breite/Länge, Höhe und GPS-Zeit, IFD1 als eingebettetes Vorschaubild) — in Intel- oder
/// Motorola-Byte-Reihenfolge. Keine echten Fotos.
/// </summary>
public static class ExifTestJpeg
{
    public sealed record Gps(double Latitude, double Longitude, double? Altitude, DateTime? TimeUtc);

    private sealed record E(ushort Tag, ushort Type, uint Count, byte[] Value);

    public static byte[] Build(
        string? make = "TestCam",
        string? model = "TC-1",
        ushort orientation = 6,
        string? dateTimeOriginal = "2024:07:12 10:15:30",
        string? offset = "+02:00",
        Gps? gps = null,
        bool little = true,
        bool withThumbnailIfd = true,
        int pixelX = 4000,
        int pixelY = 3000)
    {
        var ifd0 = new List<E>();
        if (make is not null)
        {
            ifd0.Add(Ascii(0x010F, make));
        }

        if (model is not null)
        {
            ifd0.Add(Ascii(0x0110, model));
        }

        ifd0.Add(new E(0x0112, 3, 1, Short(orientation, little)));
        var exif = new List<E>();
        if (dateTimeOriginal is not null)
        {
            exif.Add(Ascii(0x9003, dateTimeOriginal));
        }

        if (offset is not null)
        {
            exif.Add(Ascii(0x9011, offset));
        }

        exif.Add(new E(0xA002, 4, 1, Long((uint)pixelX, little)));
        exif.Add(new E(0xA003, 4, 1, Long((uint)pixelY, little)));
        List<E>? gpsEntries = null;
        if (gps is not null)
        {
            gpsEntries =
            [
                new E(0, 1, 4, [2, 3, 0, 0]),
                Ascii(1, gps.Latitude < 0 ? "S" : "N"),
                new E(2, 5, 3, Dms(gps.Latitude, little)),
                Ascii(3, gps.Longitude < 0 ? "W" : "E"),
                new E(4, 5, 3, Dms(gps.Longitude, little)),
            ];
            if (gps.Altitude is { } alt)
            {
                gpsEntries.Add(new E(5, 1, 1, [(byte)(alt < 0 ? 1 : 0)]));
                gpsEntries.Add(new E(6, 5, 1, Rational((uint)Math.Round(Math.Abs(alt) * 10), 10, little)));
            }

            if (gps.TimeUtc is { } t)
            {
                gpsEntries.Add(new E(7, 5, 3, [.. Rational((uint)t.Hour, 1, little), .. Rational((uint)t.Minute, 1, little), .. Rational((uint)t.Second, 1, little)]));
                gpsEntries.Add(Ascii(29, t.ToString("yyyy:MM:dd", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        // Aufbau: Kopf, IFD0, EXIF, GPS, IFD1, Daten hinten
        using var ms = new MemoryStream();
        ms.Write(little ? "II"u8 : "MM"u8);
        ms.Write(Short(42, little));
        ms.Write(Long(8, little));
        var ifd0Count = ifd0.Count + 1 + (gpsEntries is not null ? 1 : 0);
        var ifd0Size = 2 + ifd0Count * 12 + 4;
        var exifStart = 8 + ifd0Size;
        var exifSize = 2 + exif.Count * 12 + 4;
        var gpsStart = exifStart + exifSize;
        var gpsSize = gpsEntries is null ? 0 : 2 + gpsEntries.Count * 12 + 4;
        var ifd1Start = gpsStart + gpsSize;
        var ifd1Size = withThumbnailIfd ? 2 + 2 * 12 + 4 : 0;
        var dataStart = ifd1Start + ifd1Size;
        var data = new MemoryStream();

        byte[] Entry(E e)
        {
            var b = new byte[12];
            Put16(b, 0, e.Tag, little);
            Put16(b, 2, e.Type, little);
            Put32(b, 4, e.Count, little);
            if (e.Value.Length <= 4)
            {
                e.Value.CopyTo(b, 8);
            }
            else
            {
                if (data.Length % 2 == 1)
                {
                    data.WriteByte(0);
                }

                Put32(b, 8, (uint)(dataStart + data.Length), little);
                data.Write(e.Value);
            }

            return b;
        }

        var all0 = ifd0.ToList();
        all0.Add(new E(0x8769, 4, 1, Long((uint)exifStart, little)));
        if (gpsEntries is not null)
        {
            all0.Add(new E(0x8825, 4, 1, Long((uint)gpsStart, little)));
        }

        WriteIfd(ms, all0.OrderBy(e => e.Tag).Select(Entry).ToList(), withThumbnailIfd ? (uint)ifd1Start : 0, little);
        WriteIfd(ms, exif.OrderBy(e => e.Tag).Select(Entry).ToList(), 0, little);
        if (gpsEntries is not null)
        {
            WriteIfd(ms, gpsEntries.OrderBy(e => e.Tag).Select(Entry).ToList(), 0, little);
        }

        if (withThumbnailIfd)
        {
            var thumb = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
            var t1 = new E(0x0201, 4, 1, Long((uint)(dataStart + data.Length + (data.Length % 2)), little));
            var t2 = new E(0x0202, 4, 1, Long((uint)thumb.Length, little));
            var entries = new List<byte[]> { Entry(t1), Entry(t2) };
            WriteIfd(ms, entries, 0, little);
            if (data.Length % 2 == 1)
            {
                data.WriteByte(0);
            }

            data.Write(thumb);
        }

        ms.Write(data.ToArray());
        var tiff = ms.ToArray();
        var payload = 2 + 6 + tiff.Length;
        var segment = new byte[2 + payload];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)payload);
        "Exif\0\0"u8.CopyTo(segment.AsSpan(4));
        tiff.CopyTo(segment, 10);
        return [0xFF, 0xD8, .. segment, .. ImageBody()];
    }

    /// <summary>Kleinster „Bildteil“ (APP0 + SOS + Rohdaten + EOI) — genug für Segment-Leser.</summary>
    public static byte[] ImageBody()
        => [0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xDA, 0x00, 0x02, 1, 2, 3, 4, 0xFF, 0xD9];

    /// <summary>JPEG ohne EXIF.</summary>
    public static byte[] Plain() => [0xFF, 0xD8, .. ImageBody()];

    private static void WriteIfd(MemoryStream ms, List<byte[]> entries, uint next, bool little)
    {
        ms.Write(Short((ushort)entries.Count, little));
        foreach (var e in entries)
        {
            ms.Write(e);
        }

        ms.Write(Long(next, little));
    }

    private static E Ascii(ushort tag, string text)
    {
        var b = Encoding.ASCII.GetBytes(text + "\0");
        return new E(tag, 2, (uint)b.Length, b);
    }

    private static byte[] Short(ushort v, bool little)
    {
        var b = new byte[2];
        Put16(b, 0, v, little);
        return b;
    }

    private static byte[] Long(uint v, bool little)
    {
        var b = new byte[4];
        Put32(b, 0, v, little);
        return b;
    }

    private static byte[] Rational(uint n, uint d, bool little) => [.. Long(n, little), .. Long(d, little)];

    private static byte[] Dms(double degrees, bool little)
    {
        var milli = (long)Math.Round(Math.Abs(degrees) * 3_600_000.0);
        return [.. Rational((uint)(milli / 3_600_000), 1, little), .. Rational((uint)(milli % 3_600_000 / 60_000), 1, little), .. Rational((uint)(milli % 60_000), 1000, little)];
    }

    private static void Put16(byte[] b, int at, ushort v, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v);
        }
    }

    private static void Put32(byte[] b, int at, uint v, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
        }
    }
}
