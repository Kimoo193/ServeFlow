using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id");

        builder.Property(o => o.TenantId).HasColumnName("tenant_id");
        builder.Property(o => o.BranchId).HasColumnName("branch_id");
        builder.Property(o => o.TableId).HasColumnName("table_id");
        builder.Property(o => o.WaiterId).HasColumnName("waiter_id");

        builder.Property(o => o.OrderType)
            .HasColumnName("order_type")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(o => o.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(o => o.DailyNo)
            .HasColumnName("daily_no");

        builder.Property(o => o.CustomerName)
            .HasColumnName("customer_name")
            .HasMaxLength(200);

        builder.Property(o => o.SubtotalAmount).HasColumnName("subtotal_amount");

        builder.Property(o => o.ServiceAmount).HasColumnName("service_amount");

        builder.Property(o => o.TaxAmount).HasColumnName("tax_amount");

        builder.Property(o => o.DiscountAmount).HasColumnName("discount_amount");

        builder.Property(o => o.TotalAmount).HasColumnName("total_amount");

        builder.Property(o => o.RowVersion)
            .HasColumnName("row_version")
            .IsConcurrencyToken();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(o => o.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(o => o.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DiningTable>()
            .WithMany()
            .HasForeignKey(o => o.TableId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(o => o.WaiterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(o => o.TenantId);
        builder.HasIndex(o => o.BranchId);
        builder.HasIndex(o => o.TableId);
        builder.HasIndex(o => o.WaiterId);

        builder.HasIndex(o => o.TableId)
            .IsUnique()
            .HasFilter("table_id IS NOT NULL AND status IN ('Open', 'InProgress')");
    }
}
