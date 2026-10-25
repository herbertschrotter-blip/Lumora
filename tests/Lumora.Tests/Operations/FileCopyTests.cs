using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Operations;

/// <summary>
/// „Kopieren nach…“ (ADR-0068): Copy-Batch mit Protokoll, neue Katalogzeilen ohne Benutzer-Angaben der Vorlage, freier Name bei
/// Gleichnamigem, Ziel außerhalb ohne Katalog, Rückgängig (Kopie in den Papierkorb, Zeile weg), Galerie-Taste (UndoLast).
/// </summary>
public sealed class FileCopyTests
{
    [Fact]
    public async Task Kopie_in_der_Bibliothek_bekommt_eine_neue_Zeile_ohne_Sterne_und_Tags_und_laesst_sich_zuruecknehmen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("Quelle", "sommer.jpg"), 3000);
        fx.WriteLibraryFile(Path.Combine("Ziel", "vorhanden.jpg"));
        await fx.ScanAsync(library.Id);
        var source = (await fx.FindFileAsync("sommer.jpg"))!;
        await RemovalTestKit.DecorateAsync(fx, library.Id, source.Id); // Bewertung, Tag, Album, Vorschaubild
        var bin = new FakeRecycleBin();
        using var ops = fx.CreateFileOperationService(bin);
        var target = Path.Combine(fx.LibraryRoot, "Ziel");
        fx.Messages.Clear();

        var result = await ops.CopyAsync([source.Id], target);

        result.Type.Should().Be(OperationType.Copy);
        result.Succeeded.Should().Be(1);
        var copyPath = Path.Combine(target, "sommer.jpg");
        File.Exists(copyPath).Should().BeTrue();
        File.Exists(Path.Combine(fx.LibraryRoot, "Quelle", "sommer.jpg")).Should().BeTrue("das Original bleibt");
        await using (var ctx = fx.CreateContext())
        {
            var copies = await ctx.Files.Where(f => f.Name == "sommer.jpg").ToListAsync();
            copies.Should().HaveCount(2);
            var copy = copies.Single(f => f.Id != source.Id);
            result.Items[0].FileId.Should().Be(copy.Id);
            copy.Rating.Should().BeNull("Sterne werden nicht übernommen");
            copy.IsFavorite.Should().BeFalse();
            (await ctx.FileTags.CountAsync(t => t.FileId == copy.Id)).Should().Be(0, "Tags werden nicht übernommen");
            (await ctx.AlbumItems.CountAsync(a => a.FileId == copy.Id)).Should().Be(0);
        }

        fx.FtsRowIds("sommer").Should().HaveCount(2, "die Kopie ist durchsuchbar");
        fx.Messages.OfKind(FilesChangeKind.Added).SelectMany(m => m.FileIds).Should().Contain(result.Items[0].FileId);
        (await fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Applied);

        var undo = await ops.UndoLastAsync();

        undo!.Type.Should().Be(OperationType.Copy, "Kopieren ist über die Galerie-Taste rückgängig zu machen");
        undo.Succeeded.Should().Be(1);
        File.Exists(copyPath).Should().BeFalse();
        bin.Recycled.Should().ContainSingle().Which.Should().Be(copyPath, "Kopie in den Papierkorb, kein Hard-Delete");
        (await fx.FindFileByIdAsync(result.Items[0].FileId)).Should().BeNull();
        (await fx.FindFileByIdAsync(source.Id))!.Rating.Should().Be(4, "das Original bleibt unberührt");
        fx.FtsRowIds("sommer").Should().ContainSingle();
        (await fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Undone);
    }

    [Fact]
    public async Task Gleichnamige_Datei_im_Ziel_ergibt_einen_freien_Namen_und_nie_ein_Ueberschreiben()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("A", "bild.jpg"), 1111);
        fx.WriteLibraryFile(Path.Combine("B", "bild.jpg"), 2222);
        await fx.ScanAsync(library.Id);
        await using var ctx = fx.CreateContext();
        var ids = await ctx.Files.Where(f => f.Name == "bild.jpg").Select(f => f.Id).ToListAsync();
        using var ops = fx.CreateFileOperationService(new FakeRecycleBin());
        var target = Path.Combine(fx.LibraryRoot, "B");

        var result = await ops.CopyAsync(ids, target);

        result.Succeeded.Should().Be(2, "auch die Datei aus B selbst wird neben sich kopiert");
        File.Exists(Path.Combine(target, "bild (2).jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "bild (3).jpg")).Should().BeTrue();
        new FileInfo(Path.Combine(target, "bild.jpg")).Length.Should().Be(2222, "nichts überschrieben");
        result.Items.Select(i => Path.GetFileName(i.DestPath)).Should().BeEquivalentTo(["bild (2).jpg", "bild (3).jpg"]);
    }

    [Fact]
    public async Task Ziel_ausserhalb_der_Bibliothek_kopiert_ohne_Katalog()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("draussen.jpg");
        await fx.ScanAsync(library.Id);
        var source = (await fx.FindFileAsync("draussen.jpg"))!;
        using var ops = fx.CreateFileOperationService(new FakeRecycleBin());

        var result = await ops.CopyAsync([source.Id], fx.OutsideDir);

        result.Succeeded.Should().Be(1);
        File.Exists(Path.Combine(fx.OutsideDir, "draussen.jpg")).Should().BeTrue();
        await using var ctx = fx.CreateContext();
        (await ctx.Files.CountAsync(f => f.Name == "draussen.jpg")).Should().Be(1, "außerhalb der Bibliothek entsteht keine Zeile");
    }
}
