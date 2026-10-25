using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

// Foto verbessern (ADR-0071, Migration AddPhotoVariants): nur eine neue Tabelle — kein Neuaufbau bestehender
// (FK-Kaskaden-Lehre aus ADR-0045/0048). Fremdschlüssel ohne Navigationen (ADR-0004).

internal sealed class PhotoVariantConfiguration : IEntityTypeConfiguration<PhotoVariant>
{
    public void Configure(EntityTypeBuilder<PhotoVariant> b)
    {
        b.ToTable("PhotoVariants");
        b.HasKey(x => x.FileId);
        b.Property(x => x.FileId).ValueGeneratedNever();
        b.Property(x => x.Tools).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // Fassung weg oder Original weg → Verknüpfung weg; die übrige Datei bleibt.
        b.HasOne<MediaFile>()
            .WithOne()
            .HasForeignKey<PhotoVariant>(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.OriginalFileId)
            .OnDelete(DeleteBehavior.Cascade);

        // „Hat dieses Original Fassungen?“ (Galerie-Marke, „beste Fassung zeigen“) und die Kaskade über den Index.
        b.HasIndex(x => x.OriginalFileId).HasDatabaseName("IX_PhotoVariants_Original");
    }
}
