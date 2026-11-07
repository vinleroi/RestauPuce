using ChefOS.Core.Vitrine;
using ChefOS.Core.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class WineListItemPairingConfiguration : IEntityTypeConfiguration<WineListItemPairing>
{
    public void Configure(EntityTypeBuilder<WineListItemPairing> builder)
    {
        builder.BelongsToRestaurant();

        builder.HasIndex(p => new { p.WineListItemId, p.RecipeId })
            .IsUnique()
            .HasDatabaseName("IX_WineListItemPairings_Key");

        builder.HasOne<WineListItem>()
            .WithMany()
            .HasForeignKey(p => p.WineListItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(p => p.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
