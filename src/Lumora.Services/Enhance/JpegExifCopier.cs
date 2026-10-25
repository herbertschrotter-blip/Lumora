namespace Lumora.Services.Enhance;

/// <summary>
/// Übernimmt den EXIF-Block (APP1 „Exif“) eines JPEG-Originals in die verbesserte Fassung (ADR-0071): Aufnahmedatum, GPS,
/// Kamera bleiben so auch in der Datei erhalten. Die Ausrichtung (Tag 0x0112) wird auf 1 gesetzt, weil die Pixel der Fassung
/// schon gedreht sind. Ein vorhandener EXIF-Block des Ziels wird ersetzt; der Rest des Ziels (JFIF, Bilddaten) bleibt Byte
/// für Byte. Nur Bytes umkopieren — kein Neukodieren. Andere Formate als JPEG werden nicht angefasst (Ergebnis false).
/// </summary>
public static class JpegExifCopier
{
    private const ushort OrientationTag = 0x0112;

    /// <summary>Kopiert den EXIF-Block von <paramref name="sourceJpeg"/> nach <paramref name="targetJpeg"/>; false ohne EXIF bzw. ohne JPEG.</summary>
    public static bool Copy(string sourceJpeg, string targetJpeg)
    {
        var exif = ReadExifSegment(File.ReadAllBytes(sourceJpeg));
        if (exif is null)
        {
            return false;
        }

        var target = File.ReadAllBytes(targetJpeg);
        var merged = Insert(target, exif);
        if (merged is null)
        {
            return false;
        }

        var temp = targetJpeg + ".exif.tmp";
        File.WriteAllBytes(temp, merged);
        File.Move(temp, targetJpeg, overwrite: true);
        return true;
    }

    /// <summary>Der APP1-EXIF-Block (Marker, Länge, Daten) mit Ausrichtung 1 — oder null.</summary>
    public static byte[]? ReadExifSegment(byte[] jpeg)
    {
        foreach (var (offset, length, marker) in Segments(jpeg))
        {
            if (marker == 0xE1 && IsExif(jpeg, offset))
            {
                var segment = jpeg.AsSpan(offset, length).ToArray();
                ResetOrientation(segment);
                return segment;
            }
        }

        return null;
    }

    /// <summary>Fügt <paramref name="exifSegment"/> nach SOI bzw. nach APP0 (JFIF) ein und entfernt vorhandene EXIF-Blöcke.</summary>
    public static byte[]? Insert(byte[] jpeg, byte[] exifSegment)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return null;
        }

        var segments = Segments(jpeg).ToList();
        var insertAt = 2;
        if (segments.Count > 0 && segments[0].Marker == 0xE0)
        {
            insertAt = segments[0].Offset + segments[0].Length;
        }

        using var output = new MemoryStream(jpeg.Length + exifSegment.Length);
        var position = 0;
        var inserted = false;
        foreach (var (offset, length, marker) in segments)
        {
            if (!inserted && offset >= insertAt)
            {
                output.Write(jpeg, position, insertAt - position);
                output.Write(exifSegment);
                position = insertAt;
                inserted = true;
            }

            if (marker == 0xE1 && IsExif(jpeg, offset))
            {
                output.Write(jpeg, position, offset - position);
                position = offset + length; // alten EXIF-Block auslassen
            }
        }

        if (!inserted)
        {
            output.Write(jpeg, position, insertAt - position);
            output.Write(exifSegment);
            position = insertAt;
        }

        output.Write(jpeg, position, jpeg.Length - position);
        return output.ToArray();
    }

    /// <summary>Segmente vor den Bilddaten (bis SOS): Anfang (beim 0xFF), Gesamtlänge, Marker.</summary>
    private static IEnumerable<(int Offset, int Length, byte Marker)> Segments(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            yield break;
        }

        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            if (marker == 0xDA || marker == 0xD9)
            {
                yield break; // Bilddaten bzw. Ende
            }

            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length)
            {
                yield break;
            }

            yield return (i, length + 2, marker);
            i += length + 2;
        }
    }

    private static bool IsExif(byte[] jpeg, int offset)
        => offset + 10 <= jpeg.Length && jpeg[offset + 4] == (byte)'E' && jpeg[offset + 5] == (byte)'x' && jpeg[offset + 6] == (byte)'i'
           && jpeg[offset + 7] == (byte)'f' && jpeg[offset + 8] == 0 && jpeg[offset + 9] == 0;

    /// <summary>Setzt Tag 0x0112 in IFD0 auf 1 (TIFF ab Byte 10 des Segments; Intel oder Motorola).</summary>
    private static void ResetOrientation(byte[] segment)
    {
        const int tiff = 10;
        if (segment.Length < tiff + 8)
        {
            return;
        }

        var little = segment[tiff] == (byte)'I';
        int U16(int at) => little ? segment[at] | (segment[at + 1] << 8) : (segment[at] << 8) | segment[at + 1];
        int U32(int at) => little
            ? segment[at] | (segment[at + 1] << 8) | (segment[at + 2] << 16) | (segment[at + 3] << 24)
            : (segment[at] << 24) | (segment[at + 1] << 16) | (segment[at + 2] << 8) | segment[at + 3];

        var ifd = tiff + U32(tiff + 4);
        if (ifd < tiff || ifd + 2 > segment.Length)
        {
            return;
        }

        var count = U16(ifd);
        for (var e = 0; e < count; e++)
        {
            var entry = ifd + 2 + e * 12;
            if (entry + 12 > segment.Length)
            {
                return;
            }

            if (U16(entry) == OrientationTag)
            {
                // SHORT, Anzahl 1: Wert steht in den ersten zwei Bytes des Wertfelds
                if (little)
                {
                    segment[entry + 8] = 1;
                    segment[entry + 9] = 0;
                }
                else
                {
                    segment[entry + 8] = 0;
                    segment[entry + 9] = 1;
                }

                return;
            }
        }
    }
}
