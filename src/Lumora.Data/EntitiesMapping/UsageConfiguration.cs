using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

// Nutzungs-Signale (ADR-0072, Migration AddUsageSignals). Fremdschlüssel ohne Navigation (ADR-0004).

internal sealed class FileUsageDayConfiguration : IEntityTypeConfiguration<FileUsageDay>
{
    public void Configure(EntityTypeBuilder<FileUsageDay> b)
    {
        b.ToTable("FileUsageDays");
        b.HasKey(x => new { x.FileId, x.Day });

        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Property(x => x.Opens).HasDefaultValue(0);
        b.Property(x => x.Seconds).HasDefaultValue(0);
        b.Property(x => x.Zooms).HasDefaultValue(0);
        b.Property(x => x.SoftOpens).HasDefaultValue(0);
        b.Property(x => x.SoftSeconds).HasDefaultValue(0);
        b.Property(x => x.Uses).HasDefaultValue(0);
        b.Property(x => x.Shares).HasDefaultValue(0);

        // „Seit einem Jahr nicht angesehen“ (Vergessene Schätze): Tage ab … → FileIds, nur aus dem Index.
        b.HasIndex(x => new { x.Day, x.FileId }).HasDatabaseName("IX_FileUsageDays_Day");
    }
}
