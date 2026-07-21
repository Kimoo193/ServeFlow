using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class ModifierGroupConfiguration : IEntityTypeConfiguration<ModifierGroup>
{
    public void Configure(EntityTypeBuilder<ModifierGroup> builder)
    {
        builder.ToTable("modifier_groups");

        builder.HasKey(mg => mg.Id);
        builder.Property(mg => mg.Id).HasColumnName("id");

        builder.Property(mg => mg.TenantId).HasColumnName("tenant_id");

        builder.Property(mg => mg.Name)
            .HasColumnName("name")
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(mg => mg.MinSelect)
            .HasColumnName("min_select");

        builder.Property(mg => mg.MaxSelect)
            .HasColumnName("max_select");

        builder.Property(mg => mg.Required)
            .HasColumnName("required");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(mg => mg.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(mg => mg.TenantId);
    }
}
