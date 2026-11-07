using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

/// <summary>Vin de la carte d'un restaurant, éventuellement dérivé d'une <see cref="WineReference"/>.</summary>
public class WineListItem : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int? WineReferenceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Producer { get; set; }
    public int? Vintage { get; set; }
    public string? Appellation { get; set; }
    public string Region { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public WineColor Color { get; set; }
    public string Grapes { get; set; } = string.Empty;
    public string Robe { get; set; } = string.Empty;
    public string Nez { get; set; } = string.Empty;
    public string Bouche { get; set; } = string.Empty;
    public string Garde { get; set; } = string.Empty;
    public string ServiceTemperature { get; set; } = string.Empty;
    public string GeneralPairings { get; set; } = string.Empty;
    public string? PersonalNote { get; set; }
    public decimal PriceBottle { get; set; }
    public decimal? PriceGlass { get; set; }
    public bool IsPublished { get; set; }
    public bool DescriptionIsAiDraft { get; set; }
    public int Position { get; set; }
}
