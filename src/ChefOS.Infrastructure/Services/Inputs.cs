using ChefOS.Core.Ingredients;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Units;

namespace ChefOS.Infrastructure.Services;

// ── Restaurants ──────────────────────────────────────────────────────────────

public record CreateRestaurantInput(string Name, string? Address, string? VatNumber);

/// <summary>Taux exprimés en fraction (0,12 = 12 %).</summary>
public record RestaurantSettingsInput(
    string Name,
    string Slug,
    string? Address,
    string? VatNumber,
    decimal DefaultFoodVatRate,
    decimal DefaultDrinkVatRate,
    decimal FoodCostAlertThreshold);

public record MembershipSummary(int RestaurantId, string RestaurantName, MembershipRole Role);

// ── Ingrédients et fournisseurs ──────────────────────────────────────────────

/// <summary>
/// <paramref name="Id"/> null = création. <paramref name="PricePerBaseUnit"/> est exprimé par unité de base
/// (g, ml ou pièce) ; null laisse le prix courant inchangé. <paramref name="YieldPercent"/> est un pourcentage de 1 à 100.
/// </summary>
public record IngredientInput(
    int? Id,
    string Name,
    string Category,
    EuAllergen Allergens,
    BaseUnit BaseUnit,
    decimal? PricePerBaseUnit,
    decimal YieldPercent,
    decimal? AveragePieceWeightGrams,
    int? SupplierId);

public record SupplierInput(int? Id, string Name, string? ContactName, string? Email, string? Phone);

/// <summary>Suppression refusée (<c>Deleted</c> = false) tant que des recettes utilisent l'ingrédient.</summary>
public record DeleteResult(bool Deleted, IReadOnlyList<string> UsedInRecipes);
