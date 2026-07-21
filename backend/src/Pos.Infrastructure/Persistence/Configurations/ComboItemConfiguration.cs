using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class ComboItemConfiguration : IEntityTypeConfiguration<ComboItem>
{
    public void Configure(EntityTypeBuilder<ComboItem> builder)
    {
        builder.ToTable("combo_items");

        builder.HasKey(ci => ci.Id);
        builder.Property(ci => ci.Id).HasColumnName("id");

        builder.Property(ci => ci.TenantId).HasColumnName("tenant_id");
        builder.Property(ci => ci.ComboId).HasColumnName("combo_id");
        builder.Property(ci => ci.ItemId).HasColumnName("item_id");

        builder.Property(ci => ci.Quantity)
            .HasColumnName("quantity");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(ci => ci.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Combo>()
            .WithMany()
            .HasForeignKey(ci => ci.ComboId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<MenuItem>()
            .WithMany()
            .HasForeignKey(ci => ci.ItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(ci => ci.TenantId);
        builder.HasIndex(ci => ci.ComboId);
        builder.HasIndex(ci => new { ci.ComboId, ci.ItemId }).IsUnique();
    }
}
