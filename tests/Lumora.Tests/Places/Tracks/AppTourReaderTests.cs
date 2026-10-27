using Lumora.Services.Places.Tracks;

namespace Lumora.Tests.Places.Tracks;

/// <summary>
/// Galerie-Filter „Tour“ (ADR-0073 Stufe 7 Teil B, Mockup 24): welche Fotos zu einer Tour gehören — Zeitabgleich wie bei
/// „Ungenaue Orte“ (OffsetTimeOriginal, Toleranz), nur mit Aufnahmedatum, nur im Bereich; Liste neueste zuerst mit Zahlen,
/// Gipfel und Rahmen; Tour eines Fotos für die Lightbox.
/// </summary>
public sealed class AppTourReaderTests
{
    // Tour: 15.03.2020 08:00–14:00 Ortszeit (+01:00) = 07:00–13:00 UTC
    private static readonly DateTime Start = new(2020, 3, 15, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Fotos_der_Tour_nach_Zeitabgleich_mit_Toleranz_nur_im_Bereich()
    {
        using var w = await TrackWorld.CreateAsync();
        foreach (var name in new[] { "start.jpg", "knapp.jpg", "zuspaet.jpg", "ohnezeit.jpg", "andere.jpg" })
        {
            await File.WriteAllBytesAsync(Path.Combine(w.Fx.LibraryRoot, name), [0xFF, 0xD8, 0xFF, 0xE0, (byte)name.Length]);
        }

        await w.Fx.ScanAsync(w.Library.Id);
        int Id(string n) => w.FileIdAsync(n).GetAwaiter().GetResult();
        w.SetPhoto(Id("start.jpg"), new DateTime(2020, 3, 15, 9, 0, 0), 60, null, null, 0, 0); // 08:00 UTC
        w.SetPhoto(Id("knapp.jpg"), new DateTime(2020, 3, 15, 14, 1, 0), 60, null, null, 0, 0); // 1 min nach dem Ende — Toleranz 2 min
        w.SetPhoto(Id("zuspaet.jpg"), new DateTime(2020, 3, 15, 14, 5, 0), 60, null, null, 0, 0);
        w.SetPhoto(Id("andere.jpg"), new DateTime(2020, 3, 15, 10, 0, 0), 60, null, null, 0, 0);
        (await new AppTourReader(w.Fx.ContextFactory, w.Settings).AnyAsync()).Should().BeFalse("noch keine Touren");
        var other = await w.Fx.CreateLibraryAsync("Andere");
        w.Execute("UPDATE Files SET LibraryId = $lib WHERE Id = $id", ("$lib", other.Id), ("$id", Id("andere.jpg")));
        w.Execute("""
            INSERT INTO AppTours (AppId, ExternalId, Name, StartUtc, EndUtc, StartOffsetMinutes, Sport, Region, DistanceMeters, AscentMeters,
                DurationSeconds, MinLongitude, MinLatitude, MaxLongitude, MaxLatitude, HasTrack, Summits, UpdatedAtUtc)
            VALUES ('gipfelbuch', 't1', 'Handalm', $s, $e, 60, 'mountain_hiking', 'Koralpe', 14200, 912, 21600, 15.0, 46.8, 15.1, 46.9, 1,
                    '[{"name":"Handalm","elevationM":1853,"arrivalUtc":"2020-03-15T10:24:00Z","confirmed":true}]', 0),
                   ('gipfelbuch', 't2', 'Ohne Fotos', $s2, $e2, 60, 'cycling', NULL, 48000, 210, 7200, 0, 0, 0, 0, 0, '[]', 0);
            """,
            ("$s", Unix(Start)), ("$e", Unix(Start.AddHours(6))), ("$s2", Unix(Start.AddYears(6))), ("$e2", Unix(Start.AddYears(6).AddHours(2))));
        var reader = new AppTourReader(w.Fx.ContextFactory, w.Settings);
        var scope = new[] { w.Library.Id };

        var list = await reader.ListAsync(scope);

        (await reader.AnyAsync()).Should().BeTrue();

        list.Select(t => (t.Name, t.PhotoCount)).Should().Equal(("Ohne Fotos", 0), ("Handalm", 2));
        var tour = list[1];
        tour.LocalStart.Should().Be(new DateTime(2020, 3, 15, 8, 0, 0));
        tour.Summits.Should().ContainSingle().Which.Should().Be(new AppTourSummit("Handalm", 1853, new DateTime(2020, 3, 15, 10, 24, 0, DateTimeKind.Utc), true));
        tour.Bounds.Should().NotBeNull();
        list[0].Bounds.Should().BeNull("Rahmen 0/0/0/0 = unbekannt");

        var photos = await reader.PhotosAsync(tour.Id, scope);
        photos!.Value.Photos.Select(p => p.FileId).Should().Equal(Id("start.jpg"), Id("knapp.jpg"));
        photos.Value.Tour.PhotoCount.Should().Be(2);
        (await reader.PhotosAsync(tour.Id, [w.Library.Id, other.Id]))!.Value.Photos.Should().HaveCount(3, "„Alle verknüpften“ zählt die andere Bibliothek mit");
        (await reader.PhotosAsync(999, scope)).Should().BeNull();

        (await reader.ForFileAsync(Id("start.jpg")))!.Name.Should().Be("Handalm");
        (await reader.ForFileAsync(Id("zuspaet.jpg"))).Should().BeNull();
        (await reader.ForFileAsync(Id("ohnezeit.jpg"))).Should().BeNull("ohne Aufnahmedatum keine Zeit");
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();
}
