namespace ChefOS.Infrastructure.Data;

/// <summary>Fournit le restaurant courant, utilisé par le filtre global et le contrôle d'écriture du contexte.</summary>
public interface ITenantContext
{
    int? CurrentRestaurantId { get; }
}
