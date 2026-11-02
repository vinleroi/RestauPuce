namespace ChefOS.Core.Restaurants;

public class Restaurant
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? VatNumber { get; set; }
    public decimal DefaultFoodVatRate { get; set; } = 0.12m;
    public decimal DefaultDrinkVatRate { get; set; } = 0.21m;
    public decimal FoodCostAlertThreshold { get; set; } = 0.35m;
    public DateTime CreatedAt { get; set; }
}
