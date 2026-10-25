using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;

namespace Lumora.Data;

/// <summary>
/// Zentraler EF Core DbContext für die Library-DB (siehe ADR-0003 + ADR-0004).
/// CRUD-Pfad. Bulk-Insert (Scanner) und FTS5 laufen über Raw Microsoft.Data.Sqlite
/// in Lumora.Data.RawSql/ — parallel zum DbContext, gleiche Connection-String.
/// </summary>
public sealed class LibraryContext : DbContext
{
    public LibraryContext(DbContextOptions<LibraryContext> options)
        : base(options)
    {
    }

    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<LibraryGroup> LibraryGroups => Set<LibraryGroup>(); // ADR-0048
    public DbSet<LibraryRoot> LibraryRoots => Set<LibraryRoot>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<MediaFile> Files => Set<MediaFile>();
    public DbSet<Thumbnail> Thumbnails => Set<Thumbnail>();
    public DbSet<OperationBatch> OperationBatches => Set<OperationBatch>();
    public DbSet<FileOperation> FileOperations => Set<FileOperation>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<FileTag> FileTags => Set<FileTag>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryValue> CategoryValues => Set<CategoryValue>();
    public DbSet<FileCategoryValue> FileCategoryValues => Set<FileCategoryValue>();
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<PersonRelation> PersonRelations => Set<PersonRelation>(); // ADR-0045
    public DbSet<Face> Faces => Set<Face>();
    public DbSet<FaceRejection> FaceRejections => Set<FaceRejection>();
    public DbSet<FaceScan> FaceScans => Set<FaceScan>();
    public DbSet<MotifTerm> MotifTerms => Set<MotifTerm>(); // Motive (ADR-0052)
    public DbSet<FileEmbedding> FileEmbeddings => Set<FileEmbedding>();
    public DbSet<FileMotif> FileMotifs => Set<FileMotif>();
    public DbSet<FileFingerprint> FileFingerprints => Set<FileFingerprint>(); // Doppelte erkennen (ADR-0053)
    public DbSet<DuplicateCleanupUndo> DuplicateCleanupUndo => Set<DuplicateCleanupUndo>();
    public DbSet<Album> Albums => Set<Album>(); // Alben und Ordner-Verknüpfungen (ADR-0054)
    public DbSet<AlbumItem> AlbumItems => Set<AlbumItem>();
    public DbSet<FolderLink> FolderLinks => Set<FolderLink>();
    public DbSet<FileLinkUndo> FileLinkUndo => Set<FileLinkUndo>();
    public DbSet<FileQuality> FileQuality => Set<FileQuality>(); // Foto-Güte (ADR-0057)
    public DbSet<Story> Stories => Set<Story>(); // Storys (ADR-0058)
    public DbSet<StoryItem> StoryItems => Set<StoryItem>();
    public DbSet<GpsTrack> GpsTracks => Set<GpsTrack>(); // GPS-Spuren (ADR-0062)
    public DbSet<AssistantHistoryEntry> AssistantHistory => Set<AssistantHistoryEntry>(); // Story-Assistent (ADR-0063)
    public DbSet<AssistantAlias> AssistantAliases => Set<AssistantAlias>();
    public DbSet<FileUsageDay> FileUsageDays => Set<FileUsageDay>(); // Nutzungs-Signale (ADR-0072)

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LibraryContext).Assembly);
    }
}
