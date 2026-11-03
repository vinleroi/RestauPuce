using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Sales;

/// <summary>Ventes d'un plat sur un jour et un service, saisies à la main ou cumulées sur la semaine.</summary>
public class SalesEntry : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int RecipeId { get; set; }
    /// <summary>Jour de la vente, en UTC ; le lundi de la semaine pour un total hebdomadaire.</summary>
    public DateTime Date { get; set; }
    public ServicePeriod Service { get; set; }
    /// <summary>Vrai quand la quantité est un total de semaine saisi d'un bloc, sans détail par jour.</summary>
    public bool IsWeeklyTotal { get; set; }
    /// <summary>Nombre de portions vendues.</summary>
    public decimal Quantity { get; set; }
    public decimal UnitPriceInclVat { get; set; }
    /// <summary>Taux de TVA en fraction (0.12m pour 12 %).</summary>
    public decimal VatRate { get; set; }
    public DateTime UpdatedAt { get; set; }
}
