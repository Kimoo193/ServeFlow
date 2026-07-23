using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");

        builder.HasKey(rp => rp.Id);
        builder.Property(rp => rp.Id).HasColumnName("id");

        builder.Property(rp => rp.TenantId).HasColumnName("tenant_id");

        builder.Property(rp => rp.Role)
            .HasColumnName("role")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(rp => rp.Permission)
            .HasColumnName("permission")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(40);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(rp => rp.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(rp => rp.TenantId);

        builder.HasIndex(rp => new { rp.TenantId, rp.Role, rp.Permission }).IsUnique();
    }
}
