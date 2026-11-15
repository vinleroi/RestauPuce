using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Sales;

/// <summary>Commande prise en salle, à emporter, par téléphone ou depuis la vitrine.</summary>
public class Order : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    /// <summary>Numéro séquentiel, propre à chaque restaurant.</summary>
    public int Number { get; set; }
    public DateTime CreatedAt { get; set; }
    public OrderSource Source { get; set; }
    public OrderStatus Status { get; set; }
    public string? TableOrCustomer { get; set; }
    public string? Note { get; set; }
    public DateTime StatusChangedAt { get; set; }
    /// <summary>Jeton de concurrence : la colonne système <c>xmin</c> de PostgreSQL, qui change à chaque modification de la ligne.</summary>
    public uint Version { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
}
