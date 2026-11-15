using ChefOS.Core.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(o => o.Source).HasConversion<string>();
        builder.Property(o => o.Status).HasConversion<string>();

        // Jeton de version : colonne système xmin (aucune colonne ajoutée).
        builder.Property(o => o.Version).IsRowVersion();

        // Le numéro de commande est séquentiel par restaurant.
        builder.HasIndex(o => new { o.RestaurantId, o.Number })
            .IsUnique()
            .HasDatabaseName("IX_Orders_Number");

        // Les lignes disparaissent avec leur commande.
        builder.HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey(l => l.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
