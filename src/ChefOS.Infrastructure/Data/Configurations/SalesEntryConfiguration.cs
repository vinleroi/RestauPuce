using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class SalesEntryConfiguration : IEntityTypeConfiguration<SalesEntry>
{
    public void Configure(EntityTypeBuilder<SalesEntry> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(e => e.Service).HasConversion<string>();
        builder.Property(e => e.Quantity).HasPrecision(18, 4);
        builder.Property(e => e.UnitPriceInclVat).HasPrecision(18, 2);
        builder.Property(e => e.VatRate).HasPrecision(5, 4);

        // Une seule saisie par plat, jour et service ; le total hebdomadaire (lundi) est une saisie à part.
        builder.HasIndex(e => new { e.RestaurantId, e.RecipeId, e.Date, e.Service, e.IsWeeklyTotal })
            .IsUnique()
            .HasDatabaseName("IX_SalesEntries_Key");

        // Les ventes saisies suivent la suppression du plat ; l'archivage reste la voie recommandée.
        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(e => e.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
