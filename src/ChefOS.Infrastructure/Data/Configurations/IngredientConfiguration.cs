using ChefOS.Core.Ingredients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class IngredientConfiguration : IEntityTypeConfiguration<Ingredient>
{
    public void Configure(EntityTypeBuilder<Ingredient> builder)
    {
        builder.BelongsToRestaurant();

        // Allergens (EuAllergen, [Flags]) est stocké en int : mapping par défaut de l'enum.
        builder.Property(i => i.BaseUnit).HasConversion<string>();
        builder.Property(i => i.CurrentPricePerBaseUnit).HasPrecision(18, 6);
        builder.Property(i => i.YieldPercent).HasPrecision(5, 2);
        builder.Property(i => i.AveragePieceWeightGrams).HasPrecision(18, 4);

        builder.HasOne(i => i.Supplier)
            .WithMany()
            .HasForeignKey(i => i.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
