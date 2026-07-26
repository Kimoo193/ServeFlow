using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class OrderEventConfiguration : IEntityTypeConfiguration<OrderEvent>
{
    public void Configure(EntityTypeBuilder<OrderEvent> builder)
    {
        builder.ToTable("order_events");

        builder.HasKey(oe => oe.Id);
        builder.Property(oe => oe.Id).HasColumnName("id");

        builder.Property(oe => oe.TenantId).HasColumnName("tenant_id");
        builder.Property(oe => oe.OrderId).HasColumnName("order_id");
        builder.Property(oe => oe.OrderLineId).HasColumnName("order_line_id");

        builder.Property(oe => oe.EventType)
            .HasColumnName("event_type")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(oe => oe.Details)
            .HasColumnName("details")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(oe => oe.Reason)
            .HasColumnName("reason")
            .HasMaxLength(500);

        builder.Property(oe => oe.PerformedBy)
            .HasColumnName("performed_by");

        builder.Property(oe => oe.PerformedAt)
            .HasColumnName("performed_at");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(oe => oe.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(oe => oe.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<OrderLine>()
            .WithMany()
            .HasForeignKey(oe => oe.OrderLineId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(oe => oe.TenantId);
        builder.HasIndex(oe => oe.OrderId);
        builder.HasIndex(oe => oe.OrderLineId);

        // Serves DashboardEndpoints.cs:18 (/dashboard/summary) and OrderEndpoints.cs:458-460
        // (/orders/history) — both filter on EventType and order by PerformedAt descending.
        builder.HasIndex(oe => new { oe.TenantId, oe.EventType, oe.PerformedAt })
            .IsDescending(false, false, true);
    }
}
