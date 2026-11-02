using ChefOS.Core.Common;
using ChefOS.Core.Restaurants;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages;

/// <summary>
/// Valeurs saisies dans les réglages du restaurant. Les taux et le seuil d'alerte y sont exprimés en pourcentage (12 pour 12 %) ;
/// la base les stocke en fraction (0,12), la conversion se fait sans arrondi dans <see cref="From"/> et <see cref="ToInput"/>.
/// </summary>
public sealed class SettingsForm
{
    public string Name { get; set; } = "";

    public string Slug { get; set; } = "";

    public string? Address { get; set; }

    public string? VatNumber { get; set; }

    /// <summary>TVA par défaut des plats, en % ; null si le champ est vidé.</summary>
    public decimal? FoodVatPercent { get; set; }

    /// <summary>TVA par défaut des boissons, en % ; null si le champ est vidé.</summary>
    public decimal? DrinkVatPercent { get; set; }

    /// <summary>Food cost au-delà duquel un plat passe en alerte, en % ; null si le champ est vidé.</summary>
    public decimal? AlertThresholdPercent { get; set; }

    public static SettingsForm From(Restaurant restaurant) => new()
    {
        Name = restaurant.Name,
        Slug = restaurant.Slug,
        Address = restaurant.Address,
        VatNumber = restaurant.VatNumber,
        FoodVatPercent = restaurant.DefaultFoodVatRate * 100m,
        DrinkVatPercent = restaurant.DefaultDrinkVatRate * 100m,
        AlertThresholdPercent = restaurant.FoodCostAlertThreshold * 100m
    };

    /// <summary>
    /// Un taux vidé n'est pas un taux de 0 % : on refuse plutôt que d'enregistrer 0 sans que le chef l'ait écrit.
    /// Les bornes (0 à 100 %) sont celles du service.
    /// </summary>
    /// <exception cref="DomainValidationException">Un des trois pourcentages est vide.</exception>
    public RestaurantSettingsInput ToInput() => new(
        Name,
        Slug.Trim(),
        Address,
        VatNumber,
        Fraction(FoodVatPercent, "Le taux de TVA nourriture"),
        Fraction(DrinkVatPercent, "Le taux de TVA boissons"),
        Fraction(AlertThresholdPercent, "Le seuil d'alerte"));

    /// <summary>Lien public affiché sous le champ : « /le-petit-bistro » ; rien tant que le lien est vide.</summary>
    public string? PublicPath => Slug.Trim() is { Length: > 0 } slug ? "/" + slug : null;

    private static decimal Fraction(decimal? percent, string label) =>
        percent is { } value ? value / 100m : throw new DomainValidationException($"{label} est obligatoire");
}
