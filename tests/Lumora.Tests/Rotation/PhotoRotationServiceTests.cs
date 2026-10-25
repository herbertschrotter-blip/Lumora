using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Activity;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;
using Lumora.Services.Places;
using Lumora.Services.Rotation;
using Lumora.Tests.Fixtures;
using Lumora.Tests.Metadata;

namespace Lumora.Tests.Rotation;

/// <summary>
/// „Nach links/rechts drehen“ (ADR-0068): EXIF-Ausrichtung im JPEG wie beim GPS-Schreiben (nur das APP1-Segment), Maße und
/// Gesichtsrahmen im Katalog mitgedreht, Rückgängig Byte für Byte samt Änderungszeit; nur JPEG.
/// </summary>
public sealed class PhotoRotationServiceTests
{
    [Theory]
    [InlineData(1, true, 6)]
    [InlineData(6, true, 3)]
    [InlineData(3, true, 8)]
    [InlineData(8, true, 1)]
    [InlineData(1, false, 8)]
    [InlineData(2, true, 7)]
    [InlineData(7, true, 4)]
    [InlineData(5, false, 4)]
    public void Ausrichtung_dreht_im_Ring(int from, bool clockwise, int expected)
        => JpegExifOrientationWriter.Rotate(from, clockwise).Should().Be(expected);

    [Fact]
    public void Segment_ohne_Eintrag_bekommt_ihn_und_vorhandener_wird_an_Ort_und_Stelle_umgestellt()
    {
        var tiff = SyntheticMediaFiles.Tiff("2024:07:12 15:30:12");
        var segment = Segment(tiff);
        JpegExifOrientationWriter.ReadOrientation(segment).Should().Be(1);

        var withOrientation = JpegExifOrientationWriter.BuildSegment(segment, 6);
        JpegExifOrientationWriter.ReadOrientation(withOrientation).Should().Be(6);
        var patched = JpegExifOrientationWriter.BuildSegment(withOrientation, 3);
        patched.Length.Should().Be(withOrientation.Length, "vorhandener Eintrag: nur zwei Bytes umgestellt");
        JpegExifOrientationWriter.ReadOrientation(patched).Should().Be(3);

        var fresh = JpegExifOrientationWriter.BuildSegment(null, 8);
        JpegExifOrientationWriter.ReadOrientation(fresh).Should().Be(8);
        JpegExifOrientationWriter.ReadOrientation(JpegExifOrientationWriter.BuildSegment(Segment(SyntheticMediaFiles.Tiff(null, bigEndian: true)), 6)).Should().Be(6, "auch Big Endian");
    }

    [Fact]
    public void Ausrichtung_als_LONG_in_Big_Endian_wird_gelesen_und_als_SHORT_umgestellt()
    {
        // TIFF „MM“, IFD0 bei 8 mit einem Eintrag 0x0112, Typ LONG (4), Anzahl 1, Wert 6 (Review-Befund 0.4.44)
        byte[] tiff = [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 4, 0, 0, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0];
        var length = 2 + 6 + tiff.Length;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. "Exif\0\0"u8.ToArray(), .. tiff];

        JpegExifOrientationWriter.ReadOrientation(segment).Should().Be(6);
        JpegExifOrientationWriter.ReadOrientation(JpegExifOrientationWriter.BuildSegment(segment, 3)).Should().Be(3);
    }

    [Fact]
    public void Gesichtsrahmen_und_Landmarken_drehen_mit_und_zurueck()
    {
        var box = PhotoRotationService.RotateBox(0.1f, 0.2f, 0.3f, 0.4f, clockwise: true);
        box.Should().Be((1f - 0.2f - 0.4f, 0.1f, 0.4f, 0.3f));
        var back = PhotoRotationService.RotateBox(box.X, box.Y, box.W, box.H, clockwise: false);
        back.X.Should().BeApproximately(0.1f, 1e-6f);
        back.Y.Should().BeApproximately(0.2f, 1e-6f);
        back.W.Should().BeApproximately(0.3f, 1e-6f);
        back.H.Should().BeApproximately(0.4f, 1e-6f);

        var landmarks = Covers.FaceCoverScoringTests.Landmarks(0.35f, 0.65f, 0.5f);
        Floats(PhotoRotationService.RotateLandmarks(PhotoRotationService.RotateLandmarks(landmarks, true), false))
            .Should().Equal(Floats(landmarks), (a, b) => Math.Abs(a - b) < 1e-5f);
    }

    [Fact]
    public async Task Drehen_schreibt_die_Ausrichtung_dreht_Katalog_mit_und_Rueckgaengig_stellt_alles_wieder_her()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        var path = Path.Combine(fx.LibraryRoot, "hoch.jpg");
        File.WriteAllBytes(path, SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12")));
        var ohne = Path.Combine(fx.LibraryRoot, "ohne.jpg");
        File.WriteAllBytes(ohne, SyntheticMediaFiles.Jpeg(null));
        fx.WriteLibraryFile("bild.png");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        await fx.ScanAsync(library.Id);
        var original = File.ReadAllBytes(path);
        var originalOhne = File.ReadAllBytes(ohne);
        var originalModified = File.GetLastWriteTimeUtc(path);
        var file = (await fx.FindFileAsync("hoch.jpg"))!;
        var fileOhne = (await fx.FindFileAsync("ohne.jpg"))!;
        var png = (await fx.FindFileAsync("bild.png"))!;
        int faceId;
        await using (var ctx = fx.CreateContext())
        {
            var row = await ctx.Files.FindAsync(file.Id);
            row!.Width = 4000;
            row.Height = 3000;
            var face = new Face
            {
                FileId = file.Id, BoxX = 0.1f, BoxY = 0.2f, BoxW = 0.3f, BoxH = 0.4f,
                Landmarks = Covers.FaceCoverScoringTests.Landmarks(0.35f, 0.65f, 0.5f), Embedding = new byte[2048],
                ModelVersion = "test", DetScore = 0.9f, Size = 100, Quality = 0.9f, State = FaceState.Unconfirmed, CreatedAtUtc = DateTime.UtcNow,
            };
            ctx.Faces.Add(face);
            await ctx.SaveChangesAsync();
            faceId = face.Id;
        }

        var thumb = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(library.Id), $"{file.Id}_320_still.jpg");
        TempLibraryFixture.WriteFile(thumb, 32);
        var center = new ActivityCenter();
        using var service = new PhotoRotationService(fx.ContextFactory, fx.Paths, NullLogger<PhotoRotationService>.Instance, activities: center, messenger: fx.Messenger);
        fx.Messages.Clear();

        var result = await service.RotateAsync([file.Id, fileOhne.Id, png.Id], clockwise: true);

        result.Rotated.Should().Be(2);
        result.Skipped.Should().Be(1, "PNG wird nicht gedreht (nur JPEG)");
        result.CanUndo.Should().BeTrue();
        Orientation(path).Should().Be(6);
        Orientation(ohne).Should().Be(6, "ohne EXIF entsteht ein neues Segment");
        File.GetLastWriteTimeUtc(path).Should().BeAfter(originalModified, "die Datei hat eine neue Fassung");
        File.Exists(thumb).Should().BeFalse("altes Vorschaubild verworfen");
        await using (var ctx = fx.CreateContext())
        {
            var row = await ctx.Files.FindAsync(file.Id);
            (row!.Width, row.Height).Should().Be((3000, 4000));
            var face = await ctx.Faces.FindAsync(faceId);
            face!.BoxX.Should().BeApproximately(0.4f, 1e-6f);
            face.BoxY.Should().BeApproximately(0.1f, 1e-6f);
            (face.BoxW, face.BoxH).Should().Be((0.4f, 0.3f));
            row.ModifiedAtUtc.Should().BeCloseTo(File.GetLastWriteTimeUtc(path), TimeSpan.FromSeconds(1));
        }

        fx.Messages.OfKind(FilesChangeKind.Moved).SelectMany(m => m.FileIds).Should().BeEquivalentTo([file.Id, fileOhne.Id]);
        center.GetOverview().Recent.Should().ContainSingle().Which.Actions.Should().Contain(a => a.Kind == ActivityActionKind.Undo);
        (await service.GetUndoableBatchIdAsync()).Should().Be(result.BatchId);

        var undo = await service.UndoAsync(result.BatchId);

        undo.Rotated.Should().Be(2);
        File.ReadAllBytes(path).Should().Equal(original, "Rückgängig stellt die Datei Byte für Byte wieder her");
        File.ReadAllBytes(ohne).Should().Equal(originalOhne, "das eingefügte Segment ist wieder weg");
        File.GetLastWriteTimeUtc(path).Should().Be(originalModified);
        await using (var ctx = fx.CreateContext())
        {
            var row = await ctx.Files.FindAsync(file.Id);
            (row!.Width, row.Height).Should().Be((4000, 3000));
            var face = await ctx.Faces.FindAsync(faceId);
            face!.BoxX.Should().BeApproximately(0.1f, 1e-5f);
            face.BoxY.Should().BeApproximately(0.2f, 1e-5f);
            Floats(face.Landmarks).Should().Equal(Floats(Covers.FaceCoverScoringTests.Landmarks(0.35f, 0.65f, 0.5f)), (a, b) => Math.Abs(a - b) < 1e-5f);
        }

        (await service.GetUndoableBatchIdAsync()).Should().BeNull();
        (await fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Undone);
    }

    [Fact]
    public async Task Inzwischen_veraenderte_Datei_wird_beim_Rueckgaengig_nicht_angefasst()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        var path = Path.Combine(fx.LibraryRoot, "a.jpg");
        File.WriteAllBytes(path, SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12")));
        await fx.ScanAsync(library.Id);
        var file = (await fx.FindFileAsync("a.jpg"))!;
        using var service = new PhotoRotationService(fx.ContextFactory, fx.Paths, NullLogger<PhotoRotationService>.Instance, messenger: fx.Messenger);
        var result = await service.RotateAsync([file.Id], clockwise: false);
        Orientation(path).Should().Be(8);

        // ein anderes Programm dreht weiter
        var location = Locate(path);
        GeoFileWriter.ReplaceSegment(path, location.Offset, location.Length, JpegExifOrientationWriter.BuildSegment(ReadSegment(path), 3), null, null, verifyGps: false);

        var undo = await service.UndoAsync(result.BatchId);

        undo.Rotated.Should().Be(0);
        undo.Failed.Should().Be(1);
        undo.Errors.Should().ContainSingle().Which.Should().Contain("verändert");
        Orientation(path).Should().Be(3);
    }

    private static float[] Floats(byte[] bytes)
        => Enumerable.Range(0, bytes.Length / 4).Select(i => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4))).ToArray();

    private static byte[] Segment(byte[] tiff)
    {
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var length = payload.Length + 2;
        return new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload).ToArray();
    }

    private static JpegExifLocation Locate(string path)
    {
        using var stream = File.OpenRead(path);
        return JpegExifGpsWriter.Locate(stream)!.Value;
    }

    private static byte[]? ReadSegment(string path)
    {
        var location = Locate(path);
        if (!location.Exists)
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return GeoFileWriter.ReadRange(stream, location.Offset, location.Length);
    }

    private static int Orientation(string path) => JpegExifOrientationWriter.ReadOrientation(ReadSegment(path));
}
