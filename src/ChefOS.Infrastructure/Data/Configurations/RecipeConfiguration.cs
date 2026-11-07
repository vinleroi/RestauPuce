using ChefOS.Core.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(r => r.Kind).HasConversion<string>();
        builder.Property(r => r.OutputUnit).HasConversion<string>();
        builder.Property(r => r.SellingPriceInclVat).HasPrecision(18, 2);
        builder.Property(r => r.VatRate).HasPrecision(5, 4);
        builder.Property(r => r.OutputQuantity).HasPrecision(18, 4);
        builder.Property(r => r.PublicDescription).HasColumnType("text");
        builder.Property(r => r.ShowOnVitrine).HasDefaultValue(false);

        // Relation propriétaire : les lignes disparaissent avec leur recette.
        builder.HasMany(r => r.Lines)
            .WithOne()
            .HasForeignKey(l => l.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
