using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines");

        builder.HasKey(ol => ol.Id);
        builder.Property(ol => ol.Id).HasColumnName("id");

        builder.Property(ol => ol.TenantId).HasColumnName("tenant_id");
        builder.Property(ol => ol.OrderId).HasColumnName("order_id");
        builder.Property(ol => ol.ItemId).HasColumnName("item_id");
        builder.Property(ol => ol.StationId).HasColumnName("station_id");
        builder.Property(ol => ol.FireBatchId).HasColumnName("fire_batch_id");

        builder.Property(ol => ol.Sequence)
            .HasColumnName("sequence");

        builder.Property(ol => ol.Quantity)
            .HasColumnName("quantity");

        builder.Property(ol => ol.UnitPrice)
            .HasColumnName("unit_price")
            .HasColumnType("numeric(10,2)");

        builder.Property(ol => ol.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        // Combo provenance. No foreign key to Combo on purpose: these are snapshots of an
        // expansion that already happened, so a retired combo must still be deletable.
        builder.Property(ol => ol.ComboId).HasColumnName("combo_id");
        builder.Property(ol => ol.ComboInstanceId).HasColumnName("combo_instance_id");
        builder.Property(ol => ol.ComboName)
            .HasColumnName("combo_name")
            .HasMaxLength(200);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(ol => ol.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(ol => ol.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<MenuItem>()
            .WithMany()
            .HasForeignKey(ol => ol.ItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Station>()
            .WithMany()
            .HasForeignKey(ol => ol.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(ol => ol.TenantId);
        builder.HasIndex(ol => ol.OrderId);
        builder.HasIndex(ol => ol.ItemId);
        builder.HasIndex(ol => ol.StationId);

        // Serves the idempotent-replay lookup on combo expansion.
        builder.HasIndex(ol => new { ol.TenantId, ol.ComboInstanceId });
    }
}
