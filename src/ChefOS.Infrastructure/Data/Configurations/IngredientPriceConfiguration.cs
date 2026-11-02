using ChefOS.Core.Ingredients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class IngredientPriceConfiguration : IEntityTypeConfiguration<IngredientPrice>
{
    public void Configure(EntityTypeBuilder<IngredientPrice> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(p => p.PricePerBaseUnit).HasPrecision(18, 6);
        builder.Property(p => p.Source).HasConversion<string>();

        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(p => p.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
