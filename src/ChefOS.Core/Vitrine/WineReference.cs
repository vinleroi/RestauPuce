namespace ChefOS.Core.Vitrine;

/// <summary>Fiche vin de référence partagée entre restaurants (pas de tenant).</summary>
public class WineReference
{
    public int Id { get; set; }
    /// <summary>Slug stable et unique.</summary>
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
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
    public string Pairings { get; set; } = string.Empty;
}
