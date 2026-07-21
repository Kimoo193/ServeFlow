using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class SectionConfiguration : IEntityTypeConfiguration<Section>
{
    public void Configure(EntityTypeBuilder<Section> builder)
    {
        builder.ToTable("sections");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");

        builder.Property(s => s.TenantId).HasColumnName("tenant_id");
        builder.Property(s => s.BranchId).HasColumnName("branch_id");

        builder.Property(s => s.Name)
            .HasColumnName("name")
            .IsRequired()
            .HasMaxLength(200);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.TenantId);
        builder.HasIndex(s => s.BranchId);
    }
}
