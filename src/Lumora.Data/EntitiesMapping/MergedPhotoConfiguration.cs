using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

// Zusammenfügen (ADR-0078 Punkt 11, Migration AddMergedPhotos): nur neue Tabellen — kein Neuaufbau bestehender
// (FK-Kaskaden-Lehre aus ADR-0045/0048). Fremdschlüssel ohne Navigationen (ADR-0004).

internal sealed class MergedPhotoConfiguration : IEntityTypeConfiguration<MergedPhoto>
{
    public void Configure(EntityTypeBuilder<MergedPhoto> b)
    {
        b.ToTable("MergedPhotos");
        b.HasKey(x => x.FileId);
        b.Property(x => x.FileId).ValueGeneratedNever();
        b.Property(x => x.Method).HasConversion<int>();
        b.Property(x => x.Projection).HasConversion<int?>();
        b.Property(x => x.Tools).IsRequired();
        b.Property(x => x.RecipeJson).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // Ergebnis weg → Zeile (und über sie die Quellen) weg; die Einzelbilder bleiben. SeriesId ohne Fremdschlüssel (Stufe 2).
        b.HasOne<MediaFile>()
            .WithOne()
            .HasForeignKey<MergedPhoto>(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MergedPhotoUndoConfiguration : IEntityTypeConfiguration<MergedPhotoUndo>
{
    public void Configure(EntityTypeBuilder<MergedPhotoUndo> b)
    {
        b.ToTable("MergedPhotoUndo");
        b.HasKey(x => x.FileOperationId);
        b.Property(x => x.FileOperationId).ValueGeneratedNever();
        b.Property(x => x.Method).HasConversion<int>();
        b.Property(x => x.Projection).HasConversion<int?>();
        b.Property(x => x.Tools).IsRequired();
        b.Property(x => x.RecipeJson).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // wie FileLinkUndo: verschwindet die protokollierte Operation, verfällt die Sicherung
        b.HasOne<FileOperation>()
            .WithOne()
            .HasForeignKey<MergedPhotoUndo>(x => x.FileOperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MergedPhotoSourceConfiguration : IEntityTypeConfiguration<MergedPhotoSource>
{
    public void Configure(EntityTypeBuilder<MergedPhotoSource> b)
    {
        b.ToTable("MergedPhotoSources");
        b.HasKey(x => new { x.ResultFileId, x.SourceFileId });

        // Ergebnis weg → alle Quellen-Zeilen weg; eine Quelle weg → nur ihre Zeile (SourceCount am Ergebnis bleibt).
        b.HasOne<MergedPhoto>()
            .WithMany()
            .HasForeignKey(x => x.ResultFileId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.SourceFileId)
            .OnDelete(DeleteBehavior.Cascade);

        // „Zusammengefügtes Ergebnis“ einer Quelle (Suchen ▸, Lightbox) und die Kaskade über den Index.
        b.HasIndex(x => x.SourceFileId).HasDatabaseName("IX_MergedPhotoSources_Source");
    }
}
