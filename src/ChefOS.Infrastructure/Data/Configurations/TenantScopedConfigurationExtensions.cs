using ChefOS.Core.Restaurants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal static class TenantScopedConfigurationExtensions
{
    /// <summary>Relie l'entité à son restaurant (sans propriété de navigation) ; supprimer le restaurant supprime ses données.</summary>
    public static void BelongsToRestaurant<T>(this EntityTypeBuilder<T> builder) where T : class, ITenantScoped
        => builder.HasOne<Restaurant>()
            .WithMany()
            .HasForeignKey(e => e.RestaurantId)
            .OnDelete(DeleteBehavior.Cascade);
}
