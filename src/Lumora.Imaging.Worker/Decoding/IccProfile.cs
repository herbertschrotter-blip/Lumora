using System.Buffers.Binary;
using System.Text;

namespace Lumora.Imaging.Worker.Decoding;

/// <summary>
/// Kleinster Leser für ICC-Profile (ADR-0078, 0.5.48): nur die Beschreibung (<c>desc</c>, v2 <c>desc</c> bzw. v4 <c>mluc</c>), um
/// sRGB-Profile zu erkennen — sie brauchen keine Umrechnung. Fehlerhaftes ergibt null bzw. „nicht sRGB“, nie eine Ausnahme.
/// </summary>
internal static class IccProfile
{
    /// <summary>true, wenn die Beschreibung „sRGB“ nennt (z. B. „sRGB IEC61966-2.1“).</summary>
    public static bool IsSrgb(byte[] profile) => Description(profile)?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Text des Tags <c>desc</c> oder null.</summary>
    public static string? Description(byte[] profile)
    {
        try
        {
            if (profile.Length < 132)
            {
                return null;
            }

            var count = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
            for (long i = 0; i < count && 132 + (i + 1) * 12 <= profile.Length; i++)
            {
                var at = (int)(132 + i * 12);
                if (!profile.AsSpan(at, 4).SequenceEqual("desc"u8))
                {
                    continue;
                }

                long offset = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(at + 4));
                long size = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(at + 8));
                if (offset + size > profile.Length || size < 12)
                {
                    return null;
                }

                var tag = profile.AsSpan((int)offset, (int)size);
                if (tag[..4].SequenceEqual("desc"u8))
                {
                    var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tag[8..]), size - 12);
                    return Encoding.ASCII.GetString(tag.Slice(12, Math.Max(0, length))).TrimEnd('\0');
                }

                if (tag[..4].SequenceEqual("mluc"u8) && size >= 28)
                {
                    // erster Eintrag ab 16: Sprache (2), Land (2), Länge (4), Versatz (4)
                    var recordLength = BinaryPrimitives.ReadUInt32BigEndian(tag[20..]);
                    var recordOffset = BinaryPrimitives.ReadUInt32BigEndian(tag[24..]);
                    if (recordOffset + (long)recordLength <= size)
                    {
                        return Encoding.BigEndianUnicode.GetString(tag.Slice((int)recordOffset, (int)recordLength)).TrimEnd('\0');
                    }
                }

                return null;
            }

            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
