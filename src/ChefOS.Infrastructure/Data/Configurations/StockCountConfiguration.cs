using ChefOS.Core.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class StockCountConfiguration : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(c => c.Status).HasConversion<string>();

        // Les lignes sont triées côté requêtes, pas dans le modèle.
        builder.HasMany(c => c.Lines)
            .WithOne()
            .HasForeignKey(l => l.StockCountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
