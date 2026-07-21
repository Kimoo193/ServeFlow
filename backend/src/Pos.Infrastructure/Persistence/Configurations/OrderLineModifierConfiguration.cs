using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class OrderLineModifierConfiguration : IEntityTypeConfiguration<OrderLineModifier>
{
    public void Configure(EntityTypeBuilder<OrderLineModifier> builder)
    {
        builder.ToTable("order_line_modifiers");

        builder.HasKey(olm => olm.Id);
        builder.Property(olm => olm.Id).HasColumnName("id");

        builder.Property(olm => olm.TenantId).HasColumnName("tenant_id");
        builder.Property(olm => olm.OrderLineId).HasColumnName("order_line_id");

        builder.Property(olm => olm.Name)
            .HasColumnName("name")
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(olm => olm.PriceDelta)
            .HasColumnName("price_delta")
            .HasColumnType("numeric(10,2)");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(olm => olm.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<OrderLine>()
            .WithMany()
            .HasForeignKey(olm => olm.OrderLineId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(olm => olm.TenantId);
        builder.HasIndex(olm => olm.OrderLineId);
    }
}
