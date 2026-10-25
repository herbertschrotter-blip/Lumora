using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Covers;
using Lumora.Services.Faces.Assignment;
using Lumora.Tests.Stories;

namespace Lumora.Tests.Covers;

/// <summary>
/// „Als Titelbild für ▸“ und Ordner-Titelbild (ADR-0068): Ziele eines Fotos (nur passende), setzen, wieder automatisch, eigene
/// Wahl gewinnt, automatische Wahl (Person = bestes Gesicht, Album/Ordner = beste Foto-Güte), Ordner-Titelbild fällt zurück auf
/// automatisch, sobald die Datei nicht mehr im Ordner liegt.
/// </summary>
public sealed class CoverServiceTests
{
    /// <summary>Gesichter-Dienst-Fake: nur Titelbild setzen/lösen, direkt in der Tabelle (der echte Dienst hat eigene Tests).</summary>
    public class FaceCoverFake : DispatchProxy
    {
        public Lumora.Services.Database.ILibraryContextFactory? Factory { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            using var connection = Factory!.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            switch (targetMethod!.Name)
            {
                case nameof(IFaceRecognitionService.SetCoverAsync):
                    cmd.CommandText = "UPDATE Persons SET CoverFaceId = $f WHERE Id = $p;";
                    cmd.Parameters.AddWithValue("$f", (long)args![1]!);
                    cmd.Parameters.AddWithValue("$p", (int)args[0]!);
                    cmd.ExecuteNonQuery();
                    return Task.CompletedTask;
                case nameof(IFaceRecognitionService.ClearCoverAsync):
                    cmd.CommandText = "UPDATE Persons SET CoverFaceId = NULL WHERE Id = $p;";
                    cmd.Parameters.AddWithValue("$p", (int)args![0]!);
                    cmd.ExecuteNonQuery();
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    private static (CoverService Service, IFaceRecognitionService Faces) Create(StoryWorld w)
    {
        var faces = DispatchProxy.Create<IFaceRecognitionService, FaceCoverFake>();
        ((FaceCoverFake)(object)faces).Factory = w.Fx.ContextFactory;
        var service = new CoverService(w.Fx.ContextFactory, w.Selector, NullLogger<CoverService>.Instance, w.Scope, faces, w.Albums, w.Stories, w.Fx.Messenger);
        return (service, faces);
    }

    private static DateTime T(int day, int hour = 12) => new(2025, 7, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Ziele_zeigen_nur_was_zum_Foto_passt_und_setzen_trifft_das_Gesicht_im_Foto()
    {
        using var w = await StoryWorld.CreateAsync();
        var files = w.Add(StoryWorld.Burst(T(1), 4));
        var other = w.Add(StoryWorld.Burst(T(9), 1), folder: "Anderes")[0];
        var anna = await w.AddPersonAsync("Anna", [files[0], files[1]]);
        var bert = await w.AddPersonAsync("Bert", [other]);
        var album = await w.Albums.CreateAsync("Sommer");
        await w.Albums.AddFilesAsync(album.Id, [files[0], files[2]]);
        var unrelatedAlbum = await w.Albums.CreateAsync("Winter");
        await w.Albums.AddFilesAsync(unrelatedAlbum.Id, [other]);
        var story = await w.Stories.CreateFromSelectionAsync([files[0], files[3]], "Ausflug");
        var (service, _) = Create(w);

        var targets = await service.GetTargetsAsync(files[0]);

        targets.Select(t => (t.Kind, t.Name)).Should().BeEquivalentTo(new[]
        {
            (CoverTargetKind.Person, "Anna"),
            (CoverTargetKind.Album, "Sommer"),
            (CoverTargetKind.Story, "Ausflug"),
            (CoverTargetKind.Folder, "Fotos"),
        }, "Bert ist nicht im Foto, „Winter“ enthält es nicht");
        var person = targets.Single(t => t.Kind == CoverTargetKind.Person);
        var faceInPhoto = w.Scalar("SELECT Id FROM Faces WHERE FileId = $f AND PersonId = $p", ("$f", files[0]), ("$p", anna));
        person.FaceId.Should().Be(faceInPhoto);
        person.IsManual.Should().BeFalse();

        await service.SetAsync(person, files[0]);
        w.Scalar("SELECT CoverFaceId FROM Persons WHERE Id = $p", ("$p", anna)).Should().Be(faceInPhoto, "genau das Gesicht der Person in DIESEM Foto");
        var again = await service.GetTargetsAsync(files[0]);
        again.Single(t => t.Kind == CoverTargetKind.Person).Should().Match<CoverTarget>(t => t.IsCurrent && t.IsManual);
        (await service.GetTargetsAsync(files[1])).Single(t => t.Kind == CoverTargetKind.Person).IsCurrent.Should().BeFalse();

        await service.ResetAsync(CoverTargetKind.Person, anna);
        w.Scalar("SELECT COUNT(*) FROM Persons WHERE Id = $p AND CoverFaceId IS NULL", ("$p", anna)).Should().Be(1);
        _ = bert;
        _ = story;
    }

    [Fact]
    public async Task Album_und_Story_setzen_und_wieder_automatisch()
    {
        using var w = await StoryWorld.CreateAsync();
        var files = w.Add(StoryWorld.Burst(T(2), 3));
        var album = await w.Albums.CreateAsync("Garten");
        await w.Albums.AddFilesAsync(album.Id, files);
        var storyId = await w.Stories.CreateFromSelectionAsync(files, "Garten-Story");
        var (service, _) = Create(w);
        var targets = await service.GetTargetsAsync(files[1]);

        await service.SetAsync(targets.Single(t => t.Kind == CoverTargetKind.Album), files[1]);
        await service.SetAsync(targets.Single(t => t.Kind == CoverTargetKind.Story), files[1]);

        (await w.Albums.GetAsync(album.Id))!.Cover!.FileId.Should().Be(files[1]);
        var card = await w.Stories.GetAsync(storyId);
        card!.CoverFileId.Should().Be(files[1]);
        card.CoverIsManual.Should().BeTrue();
        (await service.GetTargetsAsync(files[1])).Where(t => t.Kind is CoverTargetKind.Album or CoverTargetKind.Story)
            .Should().OnlyContain(t => t.IsCurrent && t.IsManual);

        await service.ResetAsync(CoverTargetKind.Album, album.Id);
        await service.ResetAsync(CoverTargetKind.Story, storyId);
        w.Scalar("SELECT COUNT(*) FROM Albums WHERE Id = $a AND CoverFileId IS NULL", ("$a", album.Id)).Should().Be(1);
        (await w.Stories.GetAsync(storyId))!.CoverIsManual.Should().BeFalse("die Story wählt wieder automatisch");
    }

    [Fact]
    public async Task Ordner_Titelbild_eigene_Wahl_gewinnt_und_faellt_zurueck_wenn_die_Datei_den_Ordner_verlaesst()
    {
        using var w = await StoryWorld.CreateAsync();
        var files = w.Add(StoryWorld.Burst(T(3), 3));
        var outside = w.Add(StoryWorld.Burst(T(4), 1), folder: "Woanders")[0];
        // Beste Foto-Güte für die automatische Wahl (Ersatz-Auswahl: Favorit + Bewertung)
        w.Scalar("UPDATE Files SET IsFavorite = 1, Rating = 5 WHERE Id = $f; SELECT 0;", ("$f", files[2]));
        var folderId = (int)w.Scalar("SELECT FolderId FROM Files WHERE Id = $f", ("$f", files[0]));
        var (service, _) = Create(w);

        var auto = await service.GetFolderCoverAsync(folderId);
        auto.Should().Be(new FolderCoverInfo(folderId, files[2], false), "ohne eigene Wahl: beste Foto-Güte");

        var folderTarget = (await service.GetTargetsAsync(files[0])).Single(t => t.Kind == CoverTargetKind.Folder);
        await service.SetAsync(folderTarget, files[0]);
        (await service.GetFolderCoverAsync(folderId)).Should().Be(new FolderCoverInfo(folderId, files[0], true));
        await FluentActions.Invoking(() => service.SetAsync(folderTarget, outside)).Should().ThrowAsync<InvalidOperationException>("nur Fotos dieses Ordners");

        // Datei verlässt den Ordner (verschoben) → wieder automatisch, ohne dass jemand die Spalte anfasst
        var otherFolder = w.Scalar("SELECT FolderId FROM Files WHERE Id = $f", ("$f", outside));
        w.Scalar("UPDATE Files SET FolderId = $d WHERE Id = $f; SELECT 0;", ("$d", otherFolder), ("$f", files[0]));
        (await service.GetFolderCoverAsync(folderId)).Should().Be(new FolderCoverInfo(folderId, files[2], false));

        await service.ResetAsync(CoverTargetKind.Folder, folderId);
        w.Scalar("SELECT COUNT(*) FROM Folders WHERE Id = $d AND CoverFileId IS NULL", ("$d", folderId)).Should().Be(1);
    }

    [Fact]
    public async Task Album_ohne_eigenes_Titelbild_nimmt_die_beste_Foto_Guete()
    {
        using var w = await StoryWorld.CreateAsync();
        var files = w.Add(StoryWorld.Burst(T(5), 4));
        w.Scalar("UPDATE Files SET IsFavorite = 1, Rating = 5 WHERE Id = $f; SELECT 0;", ("$f", files[0]));
        var albums = new Lumora.Services.Albums.AlbumService(w.Fx.ContextFactory, NullLogger<Lumora.Services.Albums.AlbumService>.Instance, w.Fx.Messenger, w.Scope, catalog: w.Catalog, highlights: w.Selector);
        var album = await albums.CreateAsync("Beste");
        await albums.AddFilesAsync(album.Id, files);

        (await albums.GetAsync(album.Id))!.Cover!.FileId.Should().Be(files[0], "älteste, aber beste Aufnahme statt der neuesten");

        await albums.SetCoverAsync(album.Id, files[3]);
        (await albums.GetAsync(album.Id))!.Cover!.FileId.Should().Be(files[3], "eigene Wahl gewinnt immer");
    }

    [Fact]
    public async Task Person_ohne_eigene_Wahl_nimmt_das_beste_Gesicht()
    {
        using var w = await StoryWorld.CreateAsync();
        var files = w.Add(StoryWorld.Burst(T(6), 3));
        var anna = await w.AddPersonAsync("Anna", files);
        // Gesicht auf files[1]: bessere Güte; Gesicht auf files[2]: noch bessere Güte, aber im Profil
        w.Scalar("UPDATE Faces SET Quality = 0.5 WHERE FileId = $f; SELECT 0;", ("$f", files[0]));
        w.Scalar("UPDATE Faces SET Quality = 0.8, Landmarks = $lm WHERE FileId = $f; SELECT 0;", ("$f", files[1]), ("$lm", FaceCoverScoringTests.Landmarks(0.35f, 0.65f, 0.50f)));
        w.Scalar("UPDATE Faces SET Quality = 0.85, Landmarks = $lm WHERE FileId = $f; SELECT 0;", ("$f", files[2]), ("$lm", FaceCoverScoringTests.Landmarks(0.35f, 0.65f, 0.64f)));
        var best = w.Scalar("SELECT Id FROM Faces WHERE FileId = $f", ("$f", files[1]));
        var scope = await w.Scope.GetForLibraryAsync(w.LibraryId);

        using var connection = w.Fx.ContextFactory.OpenSqliteConnection();
        var person = FaceAssignmentStore.ReadPersons(connection, scope, anna).Single().Person;

        person.CoverFaceId.Should().Be(best, "frontal schlägt Profil bei fast gleicher Güte");
        person.CoverIsManual.Should().BeFalse();
        var (service, _) = Create(w);
        (await service.GetTargetsAsync(files[1])).Single(t => t.Kind == CoverTargetKind.Person).IsCurrent.Should().BeTrue("Häkchen auch bei automatischer Wahl");
    }
}
