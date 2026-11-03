using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class SupplierAliasConfiguration : IEntityTypeConfiguration<SupplierAlias>
{
    public void Configure(EntityTypeBuilder<SupplierAlias> builder)
    {
        builder.BelongsToRestaurant();

        // Un libellé ne désigne qu'un ingrédient par fournisseur ; l'alias sans fournisseur (valable pour tous)
        // est unique lui aussi : NULLS NOT DISTINCT traite deux SupplierId nuls comme égaux.
        builder.HasIndex(a => new { a.RestaurantId, a.SupplierId, a.NormalizedLabel })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("IX_SupplierAliases_Key");

        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(a => a.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(a => a.SupplierId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
