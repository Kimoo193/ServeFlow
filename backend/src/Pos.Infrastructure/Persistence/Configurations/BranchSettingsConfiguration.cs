using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class BranchSettingsConfiguration : IEntityTypeConfiguration<BranchSettings>
{
    public void Configure(EntityTypeBuilder<BranchSettings> builder)
    {
        builder.ToTable("branch_settings");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");

        builder.Property(s => s.TenantId).HasColumnName("tenant_id");
        builder.Property(s => s.BranchId).HasColumnName("branch_id");

        builder.Property(s => s.Currency)
            .HasColumnName("currency")
            .IsRequired()
            .HasMaxLength(3);

        builder.Property(s => s.TaxRate)
            .HasColumnName("tax_rate")
            .HasColumnType("numeric(5,4)");

        builder.Property(s => s.TaxApplies).HasColumnName("tax_applies");

        builder.Property(s => s.ServiceChargeRate)
            .HasColumnName("service_charge_rate")
            .HasColumnType("numeric(5,4)");

        builder.Property(s => s.ServiceApplies).HasColumnName("service_applies");
        builder.Property(s => s.TaxOnService).HasColumnName("tax_on_service");

        builder.HasOne<Branch>()
            .WithOne()
            .HasForeignKey<BranchSettings>(s => s.BranchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.BranchId).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.TenantId);
    }
}
