using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class MenuItemConfiguration : IEntityTypeConfiguration<MenuItem>
{
    public void Configure(EntityTypeBuilder<MenuItem> builder)
    {
        builder.ToTable("menu_items");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id");

        builder.Property(m => m.TenantId).HasColumnName("tenant_id");
        builder.Property(m => m.CategoryId).HasColumnName("category_id");
        builder.Property(m => m.StationId).HasColumnName("station_id");

        builder.Property(m => m.Name)
            .HasColumnName("name")
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(m => m.Price)
            .HasColumnName("price")
            .HasColumnType("numeric(10,2)");

        builder.Property(m => m.Availability)
            .HasColumnName("availability")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(m => m.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Station>()
            .WithMany()
            .HasForeignKey(m => m.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => m.TenantId);
        builder.HasIndex(m => m.CategoryId);
        builder.HasIndex(m => m.StationId);
    }
}
