using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

internal sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> b)
    {
        b.ToTable("Folders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.LibraryId).IsRequired();
        b.Property(x => x.Path).IsRequired();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.ScannedAt).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // ADR-0068: Titelbild des Ordners ohne Fremdschlüssel (ADD COLUMN, kein Neuaufbau); Leser prüfen „liegt noch im Ordner“
        b.Property(x => x.CoverFileId);

        // Pflicht-Indizes laut PLAN Kap. 4
        b.HasIndex(x => new { x.LibraryId, x.Path })
            .IsUnique()
            .HasDatabaseName("UX_Folders_Library_Path");

        // Status-Filter der Galerie (Eingang/Archiv) läuft über Folders.RootId → LibraryRoots.Role (ADR-0020)
        b.HasIndex(x => x.RootId)
            .HasDatabaseName("IX_Folders_Root");
    }
}
