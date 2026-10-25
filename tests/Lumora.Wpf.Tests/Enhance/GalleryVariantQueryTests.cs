using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Services.Database;
using Lumora.Services.Enhance;

namespace Lumora.Wpf.Tests.Enhance;

/// <summary>
/// „Beste Fassung zeigen“ und Fassungs-Gruppen (ADR-0071) gegen eine echte, migrierte DB: C:\Fotos\Urlaub (o1, v1 = Fassung
/// von o1, o2, o3) und C:\Fotos\Urlaub\verbessert (v2 = Fassung von o2).
/// </summary>
public sealed class GalleryVariantQueryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, int> _ids = new();
    private readonly Dictionary<string, int> _folders = new();
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _lib;

    public async Task InitializeAsync()
    {
        var paths = new TempPaths(_root);
        paths.EnsureDirectoriesExist();
        _factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        await using (var ctx = _factory.Create())
        {
            var t = new Library { Name = "T", RootPath = @"C:\Fotos", CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
            ctx.Libraries.Add(t);
            await ctx.SaveChangesAsync();
            _lib = t.Id;
            foreach (var (key, path) in new[] { ("urlaub", @"C:\Fotos\Urlaub"), ("verbessert", @"C:\Fotos\Urlaub\verbessert") })
            {
                var folder = new Folder { LibraryId = t.Id, Path = path, Name = Path.GetFileName(path), ScannedAt = DateTime.UtcNow };
                ctx.Folders.Add(folder);
                await ctx.SaveChangesAsync();
                _folders[key] = folder.Id;
            }

            var minute = 0;
            foreach (var (name, folder) in new[] { ("o1", "urlaub"), ("v1", "urlaub"), ("o2", "urlaub"), ("o3", "urlaub"), ("v2", "verbessert") })
            {
                var file = new MediaFile
                {
                    LibraryId = t.Id, FolderId = _folders[folder], Name = name + ".jpg", Extension = ".jpg", Size = 1000, MediaType = MediaType.Image,
                    CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = new DateTime(2024, 1, 1, 12, minute++, 0, DateTimeKind.Utc),
                };
                ctx.Files.Add(file);
                await ctx.SaveChangesAsync();
                _ids[name] = file.Id;
            }

            ctx.PhotoVariants.Add(new PhotoVariant { FileId = _ids["v1"], OriginalFileId = _ids["o1"], Tools = "upscale", CreatedAtUtc = DateTime.UtcNow });
            ctx.PhotoVariants.Add(new PhotoVariant { FileId = _ids["v2"], OriginalFileId = _ids["o2"], Tools = "colorize", CreatedAtUtc = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        using var connection = _factory.OpenSqliteConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files; INSERT INTO LibraryGroups (Id, Name, CreatedAtUtc) SELECT Id, Name, 0 FROM Libraries; UPDATE Libraries SET GroupId = Id;";
            cmd.ExecuteNonQuery();
        }

        _source = new GalleryPageSource(_factory);
        _source.Open();
    }

    public Task DisposeAsync()
    {
        _source.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private GalleryQuery Query(bool best) => new GalleryQuery(_lib, GalleryFilterState.None, null, Sort: GallerySort.NameAsc) with { HideVariantOriginals = best };

    private async Task<IReadOnlyList<string>> NamesAsync(GalleryQuery query)
        => (await _source.LoadPageAsync(query, 0, 50, 0, CancellationToken.None)).Select(r => r.Name).ToList();

    [Fact]
    public async Task Beste_Fassung_blendet_Originale_mit_Fassung_aus()
    {
        (await NamesAsync(Query(best: false))).Should().Equal("o1.jpg", "o2.jpg", "o3.jpg", "v1.jpg", "v2.jpg");
        (await NamesAsync(Query(best: true))).Should().Equal("o3.jpg", "v1.jpg", "v2.jpg");
        (await _source.GetTotalCountAsync(Query(best: true), CancellationToken.None)).Should().Be(3);
    }

    [Fact]
    public async Task Im_Ordner_nur_Originale_deren_Fassung_im_selben_Ordner_liegt()
    {
        var folder = Query(best: true) with { FolderId = _folders["urlaub"], IncludeSubfolders = false };
        // die Fassung von o2 liegt im Unterordner — o2 bleibt sichtbar
        (await NamesAsync(folder)).Should().Equal("o2.jpg", "o3.jpg", "v1.jpg");
    }

    [Fact]
    public async Task Fehlende_Fassung_blendet_nichts_aus()
    {
        using (var connection = _factory.OpenSqliteConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"UPDATE Files SET Missing = 1 WHERE Id = {_ids["v1"]};";
            cmd.ExecuteNonQuery();
        }

        (await NamesAsync(Query(best: true))).Should().Contain("o1.jpg");
    }

    [Fact]
    public async Task Gruppen_fuer_Marke_und_Lightbox()
    {
        var store = new PhotoVariantStore(_factory);
        var groups = await store.GetGroupsAsync([_ids["o1"], _ids["v1"], _ids["o3"], _ids["v2"]]);

        groups.Keys.Should().BeEquivalentTo([_ids["o1"], _ids["v1"], _ids["v2"]]);
        groups[_ids["v1"]].AllFileIds.Should().Equal(_ids["o1"], _ids["v1"]);
        groups[_ids["v2"]].OriginalFileId.Should().Be(_ids["o2"]);
        (await store.GetGroupsAsync([])).Should().BeEmpty();
    }

    private sealed class TempPaths(string root) : Lumora.Services.Infrastructure.IAppDataPaths
    {
        public string AppDataRoot => root;
        public string LibraryDatabasePath => Path.Combine(root, "library.db");
        public string ThumbnailsRoot => Path.Combine(root, "thumbs");
        public string LogsRoot => Path.Combine(root, "logs");
        public string GetLibraryThumbnailsPath(long libraryId) => Path.Combine(ThumbnailsRoot, libraryId.ToString());
        public void EnsureDirectoriesExist() => Directory.CreateDirectory(root);
    }
}
