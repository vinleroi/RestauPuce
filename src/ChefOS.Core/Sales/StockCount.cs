using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Sales;

/// <summary>Inventaire physique : un brouillon se corrige, un inventaire clôturé fait foi pour les écarts.</summary>
public class StockCount : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    /// <summary>Jour de l'inventaire, en UTC.</summary>
    public DateTime Date { get; set; }
    public StockCountStatus Status { get; set; }
    public string? Note { get; set; }
    public DateTime? ClosedAt { get; set; }
    public List<StockCountLine> Lines { get; set; } = new();
}
