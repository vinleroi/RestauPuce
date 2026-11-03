using ChefOS.Core.Ingredients;
using ChefOS.Core.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class StockCountLineConfiguration : IEntityTypeConfiguration<StockCountLine>
{
    public void Configure(EntityTypeBuilder<StockCountLine> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(l => l.Unit).HasConversion<string>();
        builder.Property(l => l.Quantity).HasPrecision(18, 4);
        builder.Property(l => l.BaseQuantity).HasPrecision(18, 4);

        // Un ingrédient n'est compté qu'une fois par inventaire.
        builder.HasIndex(l => new { l.StockCountId, l.IngredientId })
            .IsUnique()
            .HasDatabaseName("IX_StockCountLines_Key");

        // La relation vers l'inventaire (cascade) est déclarée côté StockCount.Lines.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(l => l.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
