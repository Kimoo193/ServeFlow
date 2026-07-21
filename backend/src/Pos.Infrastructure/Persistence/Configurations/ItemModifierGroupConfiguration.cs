using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class ItemModifierGroupConfiguration : IEntityTypeConfiguration<ItemModifierGroup>
{
    public void Configure(EntityTypeBuilder<ItemModifierGroup> builder)
    {
        builder.ToTable("item_modifier_groups");

        builder.HasKey(img => img.Id);
        builder.Property(img => img.Id).HasColumnName("id");

        builder.Property(img => img.TenantId).HasColumnName("tenant_id");
        builder.Property(img => img.ItemId).HasColumnName("item_id");
        builder.Property(img => img.GroupId).HasColumnName("group_id");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(img => img.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MenuItem>()
            .WithMany()
            .HasForeignKey(img => img.ItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ModifierGroup>()
            .WithMany()
            .HasForeignKey(img => img.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(img => img.TenantId);
        builder.HasIndex(img => new { img.ItemId, img.GroupId }).IsUnique();
    }
}
