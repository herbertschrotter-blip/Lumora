using Lumora.Services.Related;
using Lumora.Tests.Stories;

namespace Lumora.Tests.Related;

/// <summary>„Suchen ▸“ im Rechtsklick-Menü (ADR-0068): gleicher Tag (Ortszeit), gleicher Ort, Doppelte, gleiche Personen.</summary>
public sealed class RelatedPhotoSearchTests
{
    private static RelatedPhotoSearch Create(StoryWorld w) => new(w.Fx.ContextFactory, w.Scope);

    [Fact]
    public async Task Gleicher_Tag_zaehlt_in_Ortszeit_und_ohne_Datum_gibt_es_nichts()
    {
        using var w = await StoryWorld.CreateAsync();
        // 23:30 UTC mit +120 min = 01:30 am Folgetag; 22:00 UTC mit +120 min = 00:00 am Folgetag
        var late = w.Add([new StoryFile(new DateTime(2025, 7, 1, 23, 30, 0, DateTimeKind.Utc), OffsetMinutes: 120)])[0];
        var sameDay = w.Add([new StoryFile(new DateTime(2025, 7, 1, 22, 0, 0, DateTimeKind.Utc), OffsetMinutes: 120)])[0];
        var dayBefore = w.Add([new StoryFile(new DateTime(2025, 7, 1, 12, 0, 0, DateTimeKind.Utc), OffsetMinutes: 120)])[0];
        var undated = w.Add([new StoryFile(new DateTime(2025, 7, 2, 12, 0, 0, DateTimeKind.Utc))])[0];
        w.Scalar("UPDATE Files SET CapturedAt = NULL WHERE Id = $f; SELECT 0;", ("$f", undated));

        var result = await Create(w).SameDayAsync(late);

        result!.FileIds.Should().BeEquivalentTo([late, sameDay]);
        result.Label.Should().Be("am 2. Juli 2025");
        (await Create(w).SameDayAsync(undated)).Should().BeNull();
        _ = dayBefore;
    }

    [Fact]
    public async Task Gleicher_Ort_Doppelte_und_gleiche_Personen()
    {
        using var w = await StoryWorld.CreateAsync();
        var graz = w.Add(StoryWorld.Burst(new DateTime(2025, 7, 1, 10, 0, 0, DateTimeKind.Utc), 2, TestPlaces.Graz));
        var wien = w.Add(StoryWorld.Burst(new DateTime(2025, 7, 2, 10, 0, 0, DateTimeKind.Utc), 1, TestPlaces.Wien))[0];
        var all = graz.Append(wien).ToList();
        var anna = await w.AddPersonAsync("Anna", all);
        var bert = await w.AddPersonAsync("Bert", [graz[0], wien]);
        // Doppelte: graz[0] und wien haben denselben Inhalt (gültige Prüfsumme)
        foreach (var id in new[] { graz[0], wien })
        {
            w.Scalar("""
                INSERT INTO FileFingerprints (FileId, ContentHash, ContentSize, ContentModifiedAt, ImageLowDetail)
                SELECT Id, X'0102', Size, ModifiedAtUtc, 0 FROM Files WHERE Id = $f; SELECT 0;
                """, ("$f", id));
        }

        var search = Create(w);
        (await search.SamePlaceAsync(graz[0]))!.FileIds.Should().BeEquivalentTo(graz);
        (await search.DuplicatesAsync(graz[0]))!.FileIds.Should().BeEquivalentTo([graz[0], wien]);
        (await search.DuplicatesAsync(graz[1])).Should().BeNull("ohne Fingerabdruck");
        var persons = await search.SamePersonsAsync(graz[0]);
        persons!.FileIds.Should().BeEquivalentTo([graz[0], wien], "nur Fotos mit Anna UND Bert");
        persons.Label.Should().Be("mit Anna, Bert");
        _ = anna;
        _ = bert;
    }
}
