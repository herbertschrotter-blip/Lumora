using System.Buffers.Binary;
using System.Text;
using OpenCvSharp;
using Lumora.Imaging.Worker.Decoding;

namespace Lumora.Imaging.Tests.Decoding;

/// <summary>
/// Farbprofile der Quellen (Befund der Prüfung, 0.5.48): ein eingebettetes Nicht-sRGB-Profil wird beim Lesen nach sRGB
/// umgerechnet (das Ergebnis trägt kein Profil und gilt als sRGB), sRGB-Profile und Quellen ohne Profil bleiben unverändert.
/// Synthetisches Profil (sRGB-Primärfarben, aber lineare Tonkurve) — ein mittleres Grau 128 wird so deutlich heller (≈ 188).
/// </summary>
public sealed class ColorProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Lumora.Imaging.Tests", "icc-" + Guid.NewGuid().ToString("N"));

    public ColorProfileTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Nicht_sRGB_Profil_wird_beim_Lesen_nach_sRGB_umgerechnet()
    {
        var path = Write("linear.jpg", Profile("Lumora Test Linear RGB"));

        using var source = WicSourceDecoder.Read(0, path, 0);

        var gray = Center(source.Image);
        gray.Should().BeInRange(176, 200, "lineares 0,5 ist in sRGB ≈ 188 — ohne Umrechnung bliebe es 128");
    }

    [Fact]
    public void sRGB_Profil_und_Quelle_ohne_Profil_bleiben_unveraendert()
    {
        using var srgb = WicSourceDecoder.Read(0, Write("srgb.jpg", Profile("sRGB IEC61966-2.1 (Test)")), 0);
        using var plain = WicSourceDecoder.Read(1, Write("ohne.jpg", null), 0);

        Center(srgb.Image).Should().BeInRange(124, 132, "sRGB braucht keine Umrechnung");
        Center(plain.Image).Should().BeInRange(124, 132);
    }

    [Fact]
    public void Beschreibung_wird_aus_v2_und_v4_Profilen_gelesen()
    {
        IccProfile.Description(Profile("Display P3")).Should().Be("Display P3");
        IccProfile.IsSrgb(Profile("sRGB IEC61966-2.1")).Should().BeTrue();
        IccProfile.IsSrgb(Profile("Adobe RGB (1998)")).Should().BeFalse();
        IccProfile.Description(MlucProfile("sRGB built-in")).Should().Be("sRGB built-in");
        IccProfile.Description([1, 2, 3]).Should().BeNull("kaputt: keine Ausnahme");
    }

    private static int Center(Mat image)
    {
        var px = image.At<Vec3b>(image.Rows / 2, image.Cols / 2);
        return (px.Item0 + px.Item1 + px.Item2) / 3;
    }

    /// <summary>Farbiges JPEG (3 Kanäle) mit gleichmäßigem Grau 128, optional mit eingebettetem Profil (APP2 „ICC_PROFILE“).</summary>
    private string Write(string name, byte[]? profile)
    {
        using var image = new Mat(64, 64, MatType.CV_8UC3, new Scalar(128, 128, 128));
        Cv2.ImEncode(".jpg", image, out var jpeg, [(int)ImwriteFlags.JpegQuality, 95]);
        byte[] bytes = jpeg;
        if (profile is not null)
        {
            var payload = new byte[14 + profile.Length];
            "ICC_PROFILE\0"u8.CopyTo(payload);
            payload[12] = 1;
            payload[13] = 1;
            profile.CopyTo(payload, 14);
            var length = payload.Length + 2;
            bytes = [0xFF, 0xD8, 0xFF, 0xE2, (byte)(length >> 8), (byte)length, .. payload, .. jpeg.AsSpan(2)];
        }

        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>ICC v2, Bildschirm, RGB → XYZ: sRGB-Primärfarben (D50), lineare Tonkurven, Beschreibung als <c>desc</c>.</summary>
    private static byte[] Profile(string description)
        => Build(description, mluc: false);

    private static byte[] MlucProfile(string description) => Build(description, mluc: true);

    private static byte[] Build(string description, bool mluc)
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", mluc ? Mluc(description) : Desc(description)),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("rXYZ", Xyz(0.4360747, 0.2225045, 0.0139322)),
            ("gXYZ", Xyz(0.3850649, 0.7168786, 0.0971045)),
            ("bXYZ", Xyz(0.1430804, 0.0606169, 0.7141733)),
            ("rTRC", Curve()),
            ("gTRC", Curve()),
            ("bTRC", Curve()),
            ("cprt", Text("CC0")),
        };
        var tableSize = 4 + tags.Count * 12;
        var offset = 128 + tableSize;
        var entries = new List<(string Sig, int Offset, int Size)>();
        using var data = new MemoryStream();
        foreach (var (sig, bytes) in tags)
        {
            entries.Add((sig, offset + (int)data.Length, bytes.Length));
            data.Write(bytes);
            while (data.Length % 4 != 0)
            {
                data.WriteByte(0);
            }
        }

        var profile = new byte[offset + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x02100000);
        "mntr"u8.CopyTo(profile.AsSpan(12));
        "RGB "u8.CopyTo(profile.AsSpan(16));
        "XYZ "u8.CopyTo(profile.AsSpan(20));
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(24), 2024);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(28), 1);
        "acsp"u8.CopyTo(profile.AsSpan(36));
        "MSFT"u8.CopyTo(profile.AsSpan(40));
        S15(profile, 68, 0.9642);
        S15(profile, 72, 1.0);
        S15(profile, 76, 0.8249);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), (uint)tags.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var at = 132 + i * 12;
            Encoding.ASCII.GetBytes(entries[i].Sig).CopyTo(profile, at);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(at + 4), (uint)entries[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(at + 8), (uint)entries[i].Size);
        }

        data.ToArray().CopyTo(profile, offset);
        return profile;
    }

    private static byte[] Desc(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var b = new byte[12 + ascii.Length + 4 + 4 + 2 + 1 + 67];
        "desc"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)ascii.Length);
        ascii.CopyTo(b, 12);
        return b;
    }

    private static byte[] Mluc(string text)
    {
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var b = new byte[28 + utf16.Length];
        "mluc"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 12);
        "en"u8.CopyTo(b.AsSpan(16));
        "US"u8.CopyTo(b.AsSpan(18));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)utf16.Length);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), 28);
        utf16.CopyTo(b, 28);
        return b;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var b = new byte[20];
        "XYZ "u8.CopyTo(b);
        S15(b, 8, x);
        S15(b, 12, y);
        S15(b, 16, z);
        return b;
    }

    /// <summary>Tonkurve ohne Stützstellen = Identität (linear).</summary>
    private static byte[] Curve()
    {
        var b = new byte[12];
        "curv"u8.CopyTo(b);
        return b;
    }

    private static byte[] Text(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var b = new byte[8 + ascii.Length];
        "text"u8.CopyTo(b);
        ascii.CopyTo(b, 8);
        return b;
    }

    private static void S15(byte[] b, int at, double value) => BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(at), (int)Math.Round(value * 65536));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
