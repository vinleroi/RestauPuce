using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class WasteEntryConfiguration : IEntityTypeConfiguration<WasteEntry>
{
    public void Configure(EntityTypeBuilder<WasteEntry> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(e => e.Unit).HasConversion<string>();
        builder.Property(e => e.Reason).HasConversion<string>();
        builder.Property(e => e.Quantity).HasPrecision(18, 4);

        // Une perte porte soit un ingrédient, soit un plat, jamais les deux ni aucun.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_WasteEntry_OneTarget",
            "(\"IngredientId\" IS NULL) <> (\"RecipeId\" IS NULL)"));

        // Les pertes partent avec l'ingrédient ou le plat qu'elles concernent.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(e => e.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(e => e.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
