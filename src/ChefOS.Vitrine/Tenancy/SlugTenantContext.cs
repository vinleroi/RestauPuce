using ChefOS.Infrastructure.Data;

namespace ChefOS.Vitrine.Tenancy;

/// <summary>
/// Restaurant courant du site public : fixé par le lien (<c>/{slug}</c>), jamais par un utilisateur connecté. Scoped : un par requête HTTP
/// (rempli par <see cref="SlugTenantMiddleware"/>) et un par circuit interactif (rempli par la page via <see cref="SlugResolver.EnterAsync"/>).
/// Tant que le restaurant n'est pas fixé, le contexte de base de données ne voit aucune donnée de restaurant.
/// </summary>
public sealed class SlugTenantContext : ITenantContext
{
    public int? RestaurantId { get; set; }

    public int? CurrentRestaurantId => RestaurantId;
}
