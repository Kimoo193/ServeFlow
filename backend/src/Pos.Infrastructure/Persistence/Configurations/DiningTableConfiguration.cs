using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class DiningTableConfiguration : IEntityTypeConfiguration<DiningTable>
{
    public void Configure(EntityTypeBuilder<DiningTable> builder)
    {
        builder.ToTable("dining_tables");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");

        builder.Property(t => t.TenantId).HasColumnName("tenant_id");
        builder.Property(t => t.SectionId).HasColumnName("section_id");

        builder.Property(t => t.Label)
            .HasColumnName("label")
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(t => t.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(t => t.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Section>()
            .WithMany()
            .HasForeignKey(t => t.SectionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => t.TenantId);
        builder.HasIndex(t => t.SectionId);
    }
}
