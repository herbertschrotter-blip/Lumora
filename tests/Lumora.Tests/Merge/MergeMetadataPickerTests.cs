using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Merge;

namespace Lumora.Tests.Merge;

/// <summary>Wahl von Zeit, Ort, Kamera und Bewertung des Ergebnisses (ADR-0078 Punkt 10, Abnahme Nr. 12), ohne Datenbank.</summary>
public sealed class MergeMetadataPickerTests
{
    private static readonly DateTime T0 = new(2024, 7, 12, 8, 0, 0, DateTimeKind.Utc);

    private static MediaFile F(int id, int? seconds = null, double? lat = null, GeoAccuracy? acc = null, byte? rating = null, bool favorite = false, string? camera = null)
        => new()
        {
            Id = id,
            Name = $"{id}.jpg",
            Extension = ".jpg",
            CapturedAt = seconds is { } s ? T0.AddSeconds(s) : null,
            CapturedAtSource = seconds is null ? null : CaptureDateSource.Exif,
            Latitude = lat,
            Longitude = lat is null ? null : lat + 1,
            GeoSource = lat is null ? null : GeoSource.Exif,
            GeoAccuracy = acc,
            Rating = rating,
            IsFavorite = favorite,
            CameraModel = camera,
        };

    [Fact]
    public void Erstes_Bild_ohne_Ort_nimmt_den_Ort_der_naechsten_Quelle_mit_Ort()
    {
        var choice = MergeMetadataPicker.Pick([F(1, 0), F(2, 1, 47.0, GeoAccuracy.Exact), F(3, 2, 48.0, GeoAccuracy.Exact)]);
        choice.LocationSourceId.Should().Be(2);
        choice.LocationPosition.Should().Be(1);
        choice.TimeSourceId.Should().Be(1);
    }

    [Fact]
    public void Echtes_GPS_geht_vor_geschaetztem_und_in_Lumora_gesetztem()
    {
        var choice = MergeMetadataPicker.Pick([F(1, 0, 47.0, GeoAccuracy.Estimated), F(2, 1, 47.5, GeoAccuracy.Unknown), F(3, 2, 48.0, GeoAccuracy.Exact)]);
        choice.LocationSourceId.Should().Be(3);
        MergeMetadataPicker.Pick([F(1, 0, 47.0, GeoAccuracy.Estimated), F(2, 1, 47.5, GeoAccuracy.Unknown)]).LocationSourceId.Should().Be(2);
    }

    [Fact]
    public void Zeit_der_fruehesten_Quelle_mit_Zeit_auch_wenn_sie_nicht_vorne_steht()
    {
        MergeMetadataPicker.Pick([F(1), F(2, 30), F(3, 10)]).TimeSourceId.Should().Be(3);
    }

    [Fact]
    public void Ohne_Zeit_und_Ort_bleibt_die_Wahl_leer_und_die_Zeile_bekommt_kein_Datum()
    {
        var sources = new[] { F(1), F(2) };
        var choice = MergeMetadataPicker.Pick(sources);
        (choice.TimeSourceId, choice.LocationSourceId).Should().Be((null, null));
        var result = new MediaFile { Name = "r.jpg", Extension = ".jpg", CapturedAt = DateTime.UtcNow };
        MergeMetadataPicker.Apply(result, sources, choice, null, copyRating: true);
        result.CapturedAt.Should().BeNull();
        result.CapturedAtSource.Should().Be(CaptureDateSource.Unknown);
        result.GeoSource.Should().Be(GeoSource.None);
    }

    [Fact]
    public void Frisch_gelesene_Werte_fuellen_Luecken_aus_OneDrive()
    {
        var files = new Dictionary<int, MergeSourceMetadata> { [2] = new(T0.AddSeconds(-5), CaptureDateSource.Exif, Latitude: 46.0, Longitude: 12.0, GeoAccuracy: GeoAccuracy.Exact) };
        var sources = new[] { F(1, 0), F(2) };
        var choice = MergeMetadataPicker.Pick(sources, files);
        (choice.TimeSourceId, choice.LocationSourceId).Should().Be((2, 2));
        var result = new MediaFile { Name = "r.jpg", Extension = ".jpg" };
        MergeMetadataPicker.Apply(result, sources, choice, files, copyRating: true);
        result.CapturedAt.Should().Be(T0.AddSeconds(-5));
        (result.Latitude, result.GeoAccuracy, result.GeoSource).Should().Be((46.0, GeoAccuracy.Exact, GeoSource.Exif));
    }

    [Fact]
    public void Vorlaeufige_OneDrive_Zeit_weicht_der_frisch_gelesenen()
    {
        var cloud = F(1, 0);
        cloud.CapturedAtSource = CaptureDateSource.Cloud; // vorläufig, ohne Zeitzone (ADR-0077)
        var files = new Dictionary<int, MergeSourceMetadata> { [1] = new(T0.AddSeconds(30), CaptureDateSource.Exif, CapturedAtOffset: 120) };
        var sources = new[] { cloud, F(2, 10) };

        var choice = MergeMetadataPicker.Pick(sources, files);

        choice.TimeSourceId.Should().Be(2, "die frische Zeit von Bild 1 ist später als Bild 2");
        MergeMetadataPicker.EffectiveTime(cloud, files).Should().Be((T0.AddSeconds(30), 120));
        var only = MergeMetadataPicker.Pick([cloud], files);
        var result = new MediaFile { Name = "r.jpg", Extension = ".jpg" };
        MergeMetadataPicker.Apply(result, [cloud], only, files, copyRating: true);
        (result.CapturedAt, result.CapturedAtSource, result.CapturedAtOffset).Should().Be((T0.AddSeconds(30), CaptureDateSource.Exif, 120));
    }

    [Fact]
    public void Hoechste_Sterne_Favorit_wenn_eine_Quelle_Favorit_und_Kamera_der_ersten_mit_Kamera()
    {
        var sources = new[] { F(1, rating: 2), F(2, rating: 5, camera: "DJI FC3170"), F(3, favorite: true, camera: "Andere") };
        var choice = MergeMetadataPicker.Pick(sources);
        (choice.Rating, choice.IsFavorite, choice.CameraSourceId).Should().Be(((byte?)5, true, 2));
        var result = new MediaFile { Name = "r.jpg", Extension = ".jpg" };
        MergeMetadataPicker.Apply(result, sources, choice, null, copyRating: false);
        (result.Rating, result.IsFavorite).Should().Be((null, false), "ohne Haken keine Bewertung");
        result.CameraModel.Should().Be("DJI FC3170");
    }
}
