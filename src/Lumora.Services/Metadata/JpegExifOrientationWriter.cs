using System.Buffers.Binary;

namespace Lumora.Services.Metadata;

/// <summary>
/// EXIF-Ausrichtung (Tag 0x0112 in IFD0, Werte 1–8) eines JPEG verlustfrei lesen und setzen (ADR-0068, „Nach links/rechts
/// drehen“) — nach demselben Prinzip wie <see cref="JpegExifGpsWriter"/>: geändert wird nur das APP1-„Exif“-Segment, die
/// Bilddaten bleiben Byte für Byte. Hat IFD0 den Eintrag schon, werden nur seine zwei Wert-Bytes umgestellt (Segment gleich
/// lang); sonst wird eine Kopie von IFD0 mit dem zusätzlichen Eintrag angehängt und der Kopf darauf umgestellt (alte Bytes
/// bleiben als ungenutzter Rest im Segment). Ohne EXIF-Segment entsteht ein neues mit nur diesem Eintrag.
/// </summary>
public static class JpegExifOrientationWriter
{
    public const ushort TagOrientation = 0x0112;

    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private static readonly byte[] ExifIdent = "Exif\0\0"u8.ToArray();

    /// <summary>Ausrichtung im Segment (1 = normal, auch ohne Segment/Eintrag oder bei unlesbarem Wert).</summary>
    public static int ReadOrientation(byte[]? segment)
    {
        if (!TryOpen(segment, out var tiff, out var little, out var ifd0, out var count))
        {
            return 1;
        }

        for (var i = 0; i < count; i++)
        {
            var at = (int)ifd0 + 2 + (i * 12);
            if (U16(tiff.AsSpan(at), little) == TagOrientation)
            {
                var type = U16(tiff.AsSpan(at + 2), little);
                // SHORT (Standard), LONG (manche Programme), sonst BYTE — Wert steht linksbündig im Wertfeld
                var value = type switch
                {
                    TypeShort => U16(tiff.AsSpan(at + 8), little),
                    TypeLong => (int)Math.Min(U32(tiff.AsSpan(at + 8), little), 9),
                    _ => tiff[at + 8],
                };
                return value is >= 1 and <= 8 ? value : 1;
            }
        }

        return 1;
    }

    /// <summary>
    /// Neues Segment (inkl. FF E1 und Länge) mit Ausrichtung <paramref name="orientation"/>; <paramref name="oldSegment"/> null =
    /// es gibt noch kein EXIF-Segment. Wirft <see cref="InvalidDataException"/> bei unlesbarem EXIF bzw. zu großem Segment.
    /// </summary>
    public static byte[] BuildSegment(byte[]? oldSegment, int orientation)
    {
        if (orientation is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(orientation));
        }

        if (oldSegment is null)
        {
            return Wrap(NewTiff((ushort)orientation));
        }

        if (!TryOpen(oldSegment, out var tiff, out var little, out var ifd0, out var count))
        {
            throw new InvalidDataException("EXIF nicht lesbar.");
        }

        var entries = new List<byte[]>(count + 1);
        for (var i = 0; i < count; i++)
        {
            var at = (int)ifd0 + 2 + (i * 12);
            var entry = tiff.AsSpan(at, 12).ToArray();
            if (U16(entry, little) == TagOrientation)
            {
                // Vorhanden: nur den Wert (SHORT, linksbündig im Wertfeld) an Ort und Stelle umstellen.
                var patched = (byte[])tiff.Clone();
                W16(patched.AsSpan(at + 2), TypeShort, little);
                W32(patched.AsSpan(at + 4), 1, little);
                patched[at + 8] = 0;
                patched[at + 9] = 0;
                patched[at + 10] = 0;
                patched[at + 11] = 0;
                W16(patched.AsSpan(at + 8), (ushort)orientation, little);
                return Wrap(patched);
            }

            entries.Add(entry);
        }

        var entriesEnd = (int)ifd0 + 2 + (count * 12);
        var nextIfd = tiff.AsSpan(entriesEnd, 4).ToArray();
        using var output = new MemoryStream(tiff.Length + (12 * (count + 2)));
        output.Write(tiff);
        if (output.Position % 2 == 1)
        {
            output.WriteByte(0);
        }

        var newIfd0 = (uint)output.Position;
        var added = Entry((ushort)orientation, little);
        var insertAt = entries.FindIndex(e => U16(e, little) > TagOrientation);
        entries.Insert(insertAt < 0 ? entries.Count : insertAt, added);
        Span<byte> countBytes = stackalloc byte[2];
        W16(countBytes, (ushort)entries.Count, little);
        output.Write(countBytes);
        foreach (var entry in entries)
        {
            output.Write(entry);
        }

        output.Write(nextIfd);
        var bytes = output.ToArray();
        W32(bytes.AsSpan(4), newIfd0, little);
        return Wrap(bytes);
    }

    /// <summary>
    /// Ausrichtung nach einer Drehung um 90° (<paramref name="clockwise"/> = im Uhrzeigersinn). Gespiegelte Werte (2, 4, 5, 7)
    /// werden passend weitergedreht.
    /// </summary>
    public static int Rotate(int orientation, bool clockwise)
    {
        // Reihenfolge der Ausrichtungen im Uhrzeigersinn: ohne Spiegelung 1 → 6 → 3 → 8; gespiegelt 2 → 7 → 4 → 5 (Anzeige =
        // Spiegeln nach Drehen wie im WIC-Renderer: R90 ∘ S ∘ Rα = S ∘ R(α−90)).
        int[] plain = [1, 6, 3, 8];
        int[] mirrored = [2, 7, 4, 5];
        var ring = Array.IndexOf(plain, orientation) >= 0 ? plain : Array.IndexOf(mirrored, orientation) >= 0 ? mirrored : plain;
        var index = Math.Max(0, Array.IndexOf(ring, orientation));
        return ring[(index + (clockwise ? 1 : 3)) % 4];
    }

    private static bool TryOpen(byte[]? segment, out byte[] tiff, out bool little, out uint ifd0, out int count)
    {
        tiff = [];
        little = false;
        ifd0 = 0;
        count = 0;
        if (segment is null || segment.Length < 18 || segment[0] != 0xFF || segment[1] != 0xE1 || !segment.AsSpan(4, 6).SequenceEqual(ExifIdent))
        {
            return false;
        }

        tiff = segment.AsSpan(10).ToArray();
        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            little = true;
        }
        else if (tiff[0] != 'M' || tiff[1] != 'M')
        {
            return false;
        }

        ifd0 = U32(tiff.AsSpan(4), little);
        if (ifd0 < 8 || ifd0 + 2 > tiff.Length)
        {
            return false;
        }

        count = U16(tiff.AsSpan((int)ifd0), little);
        return count > 0 && ifd0 + 2 + (count * 12L) + 4 <= tiff.Length;
    }

    private static byte[] NewTiff(ushort orientation)
    {
        const bool little = false;
        using var output = new MemoryStream(32);
        output.Write("MM\0*"u8);
        Span<byte> header = stackalloc byte[4];
        W32(header, 8, little);
        output.Write(header);
        Span<byte> countBytes = stackalloc byte[2];
        W16(countBytes, 1, little);
        output.Write(countBytes);
        output.Write(Entry(orientation, little));
        output.Write(new byte[4]);
        return output.ToArray();
    }

    private static byte[] Entry(ushort orientation, bool little)
    {
        var entry = new byte[12];
        W16(entry, TagOrientation, little);
        W16(entry.AsSpan(2), TypeShort, little);
        W32(entry.AsSpan(4), 1, little);
        W16(entry.AsSpan(8), orientation, little);
        return entry;
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

    private static ushort U16(ReadOnlySpan<byte> b, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

    private static uint U32(ReadOnlySpan<byte> b, bool little)
        => little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);

    private static void W16(Span<byte> b, ushort value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
        }
    }

    private static void W32(Span<byte> b, uint value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
        }
    }
}
