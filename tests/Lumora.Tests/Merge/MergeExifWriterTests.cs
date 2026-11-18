using System.Text;
using Lumora.Domain.Enums;
using Lumora.Services.Enhance;
using Lumora.Services.Metadata;
using Lumora.Services.Places.Accuracy;

namespace Lumora.Tests.Merge;

/// <summary>
/// Aufnahmedaten im zusammengefügten Ergebnis (ADR-0078 Punkt 10, Punkt 18 „Metadaten-Tests“): EXIF der Zeitquelle mit
/// Ausrichtung 1 und ohne eingebettetes Vorschaubild, GANZES GPS-Verzeichnis der Ortsquelle (Höhe und GPS-Zeit — nach dem
/// Nachlesen bleibt der Ort „echtes GPS“), neuer Block für Nicht-JPEG-Quellen, XMP mit GPano bei 2 : 1.
/// </summary>
public sealed class MergeExifWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "mergeexif-" + Guid.NewGuid().ToString("N"));

    public MergeExifWriterTests() => Directory.CreateDirectory(_dir);

    private string Put(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Ort_kommt_mit_Hoehe_und_GPS_Zeit_von_der_Ortsquelle_und_bleibt_echtes_GPS(bool primaryLittle, bool gpsLittle)
    {
        var time = Put("a.jpg", ExifTestJpeg.Build(dateTimeOriginal: "2024:07:12 10:15:30", little: primaryLittle));
        var gpsTime = new DateTime(2024, 7, 12, 8, 16, 2, DateTimeKind.Utc);
        var place = Put("c.jpg", ExifTestJpeg.Build(dateTimeOriginal: "2024:07:12 10:16:00", gps: new ExifTestJpeg.Gps(47.123456, 13.654321, 1834.5, gpsTime), little: gpsLittle));
        var result = Put("r.jpg", ExifTestJpeg.Plain());

        MergeExifWriter.Write(result, new MergeExifInput(time, place, null, null, null, null, 12000, 3000, "Panorama", 3, false)).Should().BeTrue();

        // Ort: Koordinate, Höhe und GPS-Zeit vorhanden → Genauigkeit „Exact“ beim Nachlesen
        var reading = MediaGpsReader.Read(result);
        reading.Should().NotBeNull();
        reading!.Value.Coordinate.Latitude.Should().BeApproximately(47.123456, 1e-5);
        reading.Value.Coordinate.Longitude.Should().BeApproximately(13.654321, 1e-5);
        var details = ExifGpsDetailsReader.Read(result);
        details.Should().NotBeNull();
        details!.Value.HasAltitude.Should().BeTrue();
        details.Value.HasGpsTime.Should().BeTrue();
        details.Value.GpsTimeUtc.Should().Be(gpsTime);
        GeoAccuracyRules.Classify(details.Value).Accuracy.Should().Be(GeoAccuracy.Exact);

        // Zeit von der Zeitquelle, Ausrichtung 1, kein eingebettetes Vorschaubild (IFD1) des ersten Einzelbilds
        var date = MediaDateReader.Read(result);
        date.Should().NotBeNull();
        date!.Value.Local.Should().Be(new DateTime(2024, 7, 12, 10, 15, 30));
        date.Value.OffsetMinutes.Should().Be(120);
        var segment = JpegExifCopier.ReadExifSegment(File.ReadAllBytes(result));
        JpegExifOrientationWriter.ReadOrientation(segment).Should().Be(1);
        NextIfdOfIfd0(segment!).Should().Be(0u, "IFD1 mit dem Vorschaubild der ersten Quelle fällt weg");
        Encoding.UTF8.GetString(File.ReadAllBytes(result)).Should().Contain("xmp:CreatorTool=\"Lumora\"").And.Contain("lumora:SourceCount=\"3\"");
    }

    [Fact]
    public void Ohne_JPEG_als_Zeitquelle_entsteht_ein_neuer_Block_mit_Zeit_Kamera_und_GPS()
    {
        var heic = Put("a.heic", [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
        var place = Put("c.jpg", ExifTestJpeg.Build(gps: new ExifTestJpeg.Gps(-33.5, -70.25, 512, new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc)), little: true));
        var result = Put("r.jpg", ExifTestJpeg.Plain());

        MergeExifWriter.Write(result, new MergeExifInput(heic, place, null, new DateTime(2023, 1, 2, 0, 4, 5), -180, "Apple iPhone 15", 8000, 2000, "Panorama", 5, false));

        var date = MediaDateReader.Read(result);
        date!.Value.Local.Should().Be(new DateTime(2023, 1, 2, 0, 4, 5));
        date.Value.OffsetMinutes.Should().Be(-180);
        var details = ExifGpsDetailsReader.Read(result)!.Value;
        details.CameraModel.Should().Contain("iPhone 15");
        details.HasAltitude.Should().BeTrue();
        var reading = MediaGpsReader.Read(result)!.Value;
        reading.Coordinate.Latitude.Should().BeApproximately(-33.5, 1e-5);
        reading.Coordinate.Longitude.Should().BeApproximately(-70.25, 1e-5);
    }

    [Fact]
    public void In_Lumora_gesetzter_Ort_ohne_GPS_in_der_Datei_wird_als_Breite_Laenge_geschrieben()
    {
        var time = Put("a.jpg", ExifTestJpeg.Build());
        var place = Put("c.jpg", ExifTestJpeg.Build(gps: null));
        var result = Put("r.jpg", ExifTestJpeg.Plain());

        MergeExifWriter.Write(result, new MergeExifInput(time, place, new GeoCoordinate(48.2, 16.37), null, null, null, 100, 50, "Panorama", 2, false));

        MediaGpsReader.Read(result)!.Value.Coordinate.Latitude.Should().BeApproximately(48.2, 1e-5);
    }

    [Fact]
    public void Ohne_Ort_bleibt_das_GPS_weg_und_2_zu_1_bekommt_GPano()
    {
        var time = Put("a.jpg", ExifTestJpeg.Build(gps: new ExifTestJpeg.Gps(10, 10, null, null)));
        var result = Put("r.jpg", ExifTestJpeg.Plain());

        MergeExifWriter.Write(result, new MergeExifInput(time, null, null, null, null, null, 16000, 8000, "Panorama", 26, true));

        MediaGpsReader.Read(result).Should().BeNull("keine Quelle mit Ort: kein GPS — auch nicht das der Zeitquelle");
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(result));
        text.Should().Contain("GPano:ProjectionType=\"equirectangular\"").And.Contain("GPano:FullPanoWidthPixels=\"16000\"");
    }

    [Fact]
    public void Kein_JPEG_als_Ziel_bleibt_unveraendert()
    {
        var target = Put("r.png", [0x89, (byte)'P', (byte)'N', (byte)'G']);
        MergeExifWriter.Write(target, new MergeExifInput(null, null, null, null, null, null, 1, 1, "Panorama", 2, false)).Should().BeFalse();
        File.ReadAllBytes(target).Should().Equal(0x89, (byte)'P', (byte)'N', (byte)'G');
    }

    // ---- Befunde der Prüfung (0.5.48) ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("leer")]
    [InlineData("kurz")]
    [InlineData("ifd0-ueberlauf")]
    [InlineData("exif-ueberlauf")]
    public void Kaputter_EXIF_Block_der_Zeitquelle_ergibt_einen_neuen_Block_statt_einer_Ausnahme(string kind)
    {
        byte[] tiff = kind switch
        {
            "leer" => [],
            "kurz" => [(byte)'I', (byte)'I', 42, 0],
            "ifd0-ueberlauf" => [(byte)'I', (byte)'I', 42, 0, 0xFF, 0xFF, 0xFF, 0xFF],
            _ => ExifIfdPointerBeyondEnd(),
        };
        var primary = Put("a.jpg", JpegWithApp1([.. "Exif\0\0"u8.ToArray(), .. tiff]));
        var result = Put("r.jpg", ExifTestJpeg.Plain());

        var act = () => MergeExifWriter.Write(result, new MergeExifInput(primary, null, null, new DateTime(2024, 7, 12, 10, 15, 30), null, "TestCam TC-1", 100, 50, "Panorama", 2, false));

        act.Should().NotThrow("ein kaputter Block einer Quelle kostet nie das fertige Ergebnis");
        JpegExifCopier.ReadExifSegment(File.ReadAllBytes(result)).Should().NotBeNull();
        if (kind != "exif-ueberlauf")
        {
            // unlesbar: neuer Block mit der Aufnahmezeit (ein lesbares IFD0 mit Verweis ins Leere wird übernommen, ohne den Verweis zu lesen)
            MediaDateReader.Read(result)!.Value.Local.Should().Be(new DateTime(2024, 7, 12, 10, 15, 30));
        }
    }

    [Fact]
    public void Ergebnis_ist_als_sRGB_gekennzeichnet_ohne_geratenen_Zeitversatz_und_mit_neutralem_Namensraum()
    {
        var heic = Put("a.heic", [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
        var result = Put("r.jpg", ExifTestJpeg.Plain());

        MergeExifWriter.Write(result, new MergeExifInput(heic, null, null, new DateTime(2023, 1, 2, 0, 4, 5), null, "Apple iPhone 15", 8000, 2000, "Panorama", 5, false));

        var segment = JpegExifCopier.ReadExifSegment(File.ReadAllBytes(result))!;
        ExifTag(segment, 0xA001).Should().Be(1u, "ColorSpace sRGB — die Pixel sind umgerechnet");
        ExifTag(segment, 0x9011).Should().BeNull("die Quelle nennt keinen Versatz: nie die Zeitzone dieses PCs schreiben");
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(result));
        text.Should().Contain("xmlns:lumora=\"" + MergeExifWriter.XmpNamespace + "\"").And.NotContain("github", "kein Kontoname in jeder Ergebnis-Datei");
        MergeExifWriter.XmpNamespace.Should().Be("urn:lumora:merge:1.0");

        // EXIF-Block einer JPEG-Quelle: ColorSpace „Uncalibrated“ (Display P3) wird sRGB
        var p3 = Put("p3.jpg", ExifTestJpeg.Build(little: true, colorSpace: 0xFFFF));
        var fromJpeg = Put("r2.jpg", ExifTestJpeg.Plain());
        MergeExifWriter.Write(fromJpeg, new MergeExifInput(p3, null, null, null, null, null, 100, 50, "Panorama", 2, false));
        ExifTag(JpegExifCopier.ReadExifSegment(File.ReadAllBytes(fromJpeg))!, 0xA001).Should().Be(1u);
    }

    /// <summary>JPEG-förmig: SOI, ein APP1 mit genau diesem Inhalt, dann der Bildteil.</summary>
    private static byte[] JpegWithApp1(byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. ExifTestJpeg.ImageBody()];
    }

    /// <summary>Gültiges IFD0 mit einem Verweis auf ein EXIF-Verzeichnis weit hinter dem Ende.</summary>
    private static byte[] ExifIfdPointerBeyondEnd()
    {
        var t = new byte[8 + 2 + 12 + 4];
        "II"u8.CopyTo(t);
        t[2] = 42;
        t[4] = 8;
        t[8] = 1; // ein Eintrag
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(10), 0x8769);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(12), 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(14), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(18), 0xFFFFFFFE);
        return t;
    }

    /// <summary>Wert (SHORT/LONG) eines Tags im EXIF-Verzeichnis des Blocks, sonst null.</summary>
    private static uint? ExifTag(byte[] segment, ushort wanted)
    {
        var tiff = segment.AsSpan(10).ToArray();
        var little = tiff[0] == 'I';
        uint U32(ReadOnlySpan<byte> b) => little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b);
        ushort U16(ReadOnlySpan<byte> b) => little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b) : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b);
        uint? Find(int ifd, ushort tag, out ushort type)
        {
            type = 0;
            var count = U16(tiff.AsSpan(ifd));
            for (var i = 0; i < count; i++)
            {
                var e = tiff.AsSpan(ifd + 2 + i * 12, 12);
                if (U16(e) == tag)
                {
                    type = U16(e[2..]);
                    return type == 3 ? U16(e[8..]) : U32(e[8..]);
                }
            }

            return null;
        }

        var exif = Find((int)U32(tiff.AsSpan(4)), 0x8769, out _);
        if (exif is null)
        {
            return null;
        }

        var value = Find((int)exif.Value, wanted, out var t);
        return t == 2 ? 0u + (value is null ? 0u : 1u) : value; // ASCII (OffsetTimeOriginal): nur „vorhanden“
    }

    private static uint NextIfdOfIfd0(byte[] segment)
    {
        var tiff = segment.AsSpan(10);
        var little = tiff[0] == 'I';
        uint U32(ReadOnlySpan<byte> b) => little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b);
        ushort U16(ReadOnlySpan<byte> b) => little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b) : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b);
        var ifd0 = (int)U32(tiff[4..]);
        var count = U16(tiff[ifd0..]);
        return U32(tiff[(ifd0 + 2 + count * 12)..]);
    }

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
