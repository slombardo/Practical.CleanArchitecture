using ClassifiedAds.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClassifiedAds.Persistence.DbConfigurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.Property(x => x.Id).HasDefaultValueSql("newsequentialid()");

        builder.Property(x => x.UserId)
            .IsRequired();

        builder.Property(x => x.ExternalOrderRef)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Description)
            .HasMaxLength(500);

        builder.Property(x => x.TotalAmount)
            .HasPrecision(18, 2);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(50);

        // Unique index on business key to prevent duplicate orders
        builder.HasIndex(x => new { x.UserId, x.ExternalOrderRef })
            .IsUnique()
            .HasDatabaseName("IX_Orders_UserId_ExternalOrderRef");
    }
}
