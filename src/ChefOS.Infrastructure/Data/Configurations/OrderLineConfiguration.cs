using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(l => l.UnitPriceInclVat).HasPrecision(18, 2);
        builder.Property(l => l.VatRate).HasPrecision(5, 4);

        // La relation vers la commande (cascade) est déclarée côté Order.Lines.
        // Une commande garde ses plats : filet de sécurité, le service refuse déjà de supprimer un plat commandé.
        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(l => l.RecipeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
