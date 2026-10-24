using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Operations;
using Lumora.Services.Removal;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Operations;

/// <summary>
/// Papierkorb-Fake für „Ordner in den Papierkorb“ (ADR-0065): verschiebt Ordner in einen eigenen Temp-„Papierkorb“ und holt
/// sie von dort zurück — nie der echte Windows-Papierkorb. <see cref="UnavailableReason"/> simuliert ein Laufwerk ohne Papierkorb.
/// </summary>
public sealed class MovingRecycleBin(string binDir) : IRecycleBin
{
    private readonly Dictionary<string, string> _parked = new(StringComparer.OrdinalIgnoreCase);

    public string? UnavailableReason { get; set; }

    public Exception? ThrowOnDirectory { get; set; }

    public List<string> RecycledDirectories { get; } = [];

    public void MoveToRecycleBin(string filePath)
    {
        var target = Path.Combine(binDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(binDir);
        File.Move(filePath, target);
        _parked[filePath] = target;
    }

    public void MoveDirectoryToRecycleBin(string directoryPath)
    {
        if (ThrowOnDirectory is not null)
        {
            throw ThrowOnDirectory;
        }

        Directory.CreateDirectory(binDir);
        var target = Path.Combine(binDir, Guid.NewGuid().ToString("N"));
        Directory.Move(directoryPath, target);
        _parked[Path.TrimEndingDirectorySeparator(directoryPath)] = target;
        RecycledDirectories.Add(directoryPath);
    }

    public string? GetUnavailableReasonFor(string path) => UnavailableReason;

    /// <summary>Simuliert „die Shell hat endgültig gelöscht“ (Ordner zu groß für den Papierkorb).</summary>
    public bool NotInBin { get; set; }

    public bool ContainsDirectory(string originalPath, DateTime deletedAfterUtc) => !NotInBin && Contains(originalPath);

    public void RestoreDirectoryFromRecycleBin(string originalPath, DateTime deletedAfterUtc)
    {
        if (!_parked.Remove(Path.TrimEndingDirectorySeparator(originalPath), out var parked))
        {
            throw new DirectoryNotFoundException("Nicht im Papierkorb.");
        }

        Directory.Move(parked, originalPath);
    }

    public bool Contains(string originalPath) => _parked.ContainsKey(Path.TrimEndingDirectorySeparator(originalPath));
}

/// <summary>Gemeinsame Helfer der Tests zu ADR-0065.</summary>
internal static class RemovalTestKit
{
    public static RemovalService CreateRemovalService(TempLibraryFixture fx, IFileOperationService ops, IRecycleBin bin)
        => new(fx.ContextFactory, ops, bin, NullLogger<RemovalService>.Instance);

    /// <summary>Benutzerdaten an eine Datei hängen: Bewertung, Tag, Album, Thumbnail-Zeile + Cache-Datei; liefert (TagId, AlbumId, Cache-Pfad).</summary>
    public static async Task<(int TagId, int AlbumId, string ThumbPath)> DecorateAsync(TempLibraryFixture fx, int libraryId, int fileId, byte rating = 4)
    {
        var thumbPath = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(libraryId), $"{fileId}_320_still.jpg");
        TempLibraryFixture.WriteFile(thumbPath, 64);
        await using var ctx = fx.CreateContext();
        var file = await ctx.Files.FirstAsync(f => f.Id == fileId);
        file.Rating = rating;
        var tag = new Tag { Name = "Urlaub-" + fileId, Color = "#336699", CreatedAtUtc = DateTime.UtcNow };
        var album = new Album { Name = "Album-" + fileId, Kind = AlbumKind.Manual, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        ctx.Tags.Add(tag);
        ctx.Albums.Add(album);
        await ctx.SaveChangesAsync();
        ctx.FileTags.Add(new FileTag { FileId = fileId, TagId = tag.Id });
        ctx.AlbumItems.Add(new AlbumItem { AlbumId = album.Id, FileId = fileId, AddedAtUtc = DateTime.UtcNow });
        ctx.Thumbnails.Add(new Thumbnail
        {
            FileId = fileId,
            Size = 320,
            Kind = ThumbnailKind.Still,
            Format = "jpg",
            CachePath = thumbPath,
            SourceModifiedAtUtc = DateTime.UtcNow,
            GeneratedAtUtc = DateTime.UtcNow,
            ByteSize = 64,
            Status = ThumbnailStatus.Generated,
            AttemptCount = 1,
        });
        await ctx.SaveChangesAsync();
        return (tag.Id, album.Id, thumbPath);
    }

    /// <summary>Datei als fehlend markieren (wie es der Incremental-Scan täte).</summary>
    public static async Task MarkMissingAsync(TempLibraryFixture fx, params int[] fileIds)
    {
        await using var ctx = fx.CreateContext();
        await ctx.Files.Where(f => fileIds.Contains(f.Id)).ExecuteUpdateAsync(s => s.SetProperty(f => f.Missing, true));
    }

    public static async Task<int> CountAsync(TempLibraryFixture fx, string sql)
    {
        await using var ctx = fx.CreateContext();
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
