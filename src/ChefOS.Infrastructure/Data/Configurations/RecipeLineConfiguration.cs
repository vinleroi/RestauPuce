using ChefOS.Core.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class RecipeLineConfiguration : IEntityTypeConfiguration<RecipeLine>
{
    public void Configure(EntityTypeBuilder<RecipeLine> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(l => l.Quantity).HasPrecision(18, 4);
        builder.Property(l => l.Unit).HasConversion<string>();

        // Une ligne porte soit un ingrédient, soit une sous-préparation, jamais les deux ni aucun.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_RecipeLine_OneSource",
            "(\"IngredientId\" IS NULL) <> (\"SubRecipeId\" IS NULL)"));

        builder.HasOne(l => l.Ingredient)
            .WithMany()
            .HasForeignKey(l => l.IngredientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.SubRecipe)
            .WithMany()
            .HasForeignKey(l => l.SubRecipeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
