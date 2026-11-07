using ChefOS.Core.Vitrine;
using ChefOS.Core.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class ReservationGuestDishConfiguration : IEntityTypeConfiguration<ReservationGuestDish>
{
    public void Configure(EntityTypeBuilder<ReservationGuestDish> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(d => d.UnitPriceInclVat).HasPrecision(18, 2);
        builder.Property(d => d.VatRate).HasPrecision(5, 4);

        builder.ToTable(t => t.HasCheckConstraint("CK_ReservationGuestDish_Quantity", "\"Quantity\" >= 1"));

        // Une pré-commande garde son plat : filet de sécurité, le service refuse déjà de supprimer un plat pré-commandé.
        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(d => d.RecipeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
