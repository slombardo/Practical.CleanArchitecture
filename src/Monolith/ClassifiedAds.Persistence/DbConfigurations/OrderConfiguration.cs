using ClassifiedAds.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClassifiedAds.Persistence.DbConfigurations;

/// <summary>
/// Entity Framework Core configuration for Order entity.
/// Defines table structure, constraints, and the unique business key index.
/// </summary>
public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        // Primary key with sequential GUID generation
        builder.Property(x => x.Id).HasDefaultValueSql("newsequentialid()");

        // Required fields
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.ExternalOrderRef)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.OrderNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.TotalAmount)
            .IsRequired()
            .HasPrecision(18, 2);

        builder.Property(x => x.Currency)
            .IsRequired()
            .HasMaxLength(3);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.Notes)
            .HasMaxLength(1000);

        // Unique constraint on business key (UserId + ExternalOrderRef)
        // This prevents duplicate orders from the same user with the same external reference
        builder.HasIndex(x => new { x.UserId, x.ExternalOrderRef })
            .IsUnique()
            .HasDatabaseName("IX_Orders_UserId_ExternalOrderRef_Unique");

        // Foreign key relationship to User
        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
