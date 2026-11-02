using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
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

// ── Recettes et tableau de bord ──────────────────────────────────────────────

/// <summary>Une ligne de recette : exactement un des deux identifiants est renseigné (ingrédient ou préparation).</summary>
public record RecipeLineInput(int? IngredientId, int? SubRecipeId, decimal Quantity, MeasureUnit Unit);

/// <summary>
/// <paramref name="Id"/> null = création. Un plat porte un prix de vente TTC et un taux de TVA (fraction) ;
/// une préparation porte la quantité produite, en unité de base. <paramref name="Lines"/> remplace toutes les lignes existantes.
/// </summary>
public record RecipeInput(
    int? Id,
    string Name,
    RecipeKind Kind,
    string? Category,
    decimal? SellingPriceInclVat,
    decimal? VatRate,
    decimal? OutputQuantity,
    BaseUnit? OutputUnit,
    string? Instructions,
    IReadOnlyList<RecipeLineInput> Lines);

public record RecipeSummary(
    int Id,
    string Name,
    RecipeKind Kind,
    string? Category,
    bool IsArchived,
    CostResult Cost,
    EuAllergen Allergens);

/// <summary><paramref name="FoodCostPercent"/> est une fraction ; null quand le coût est incomplet (le pourcentage ne porterait que sur les lignes chiffrées).</summary>
public record WatchItem(int RecipeId, string Name, decimal? FoodCostPercent, FoodCostLevel Level);

/// <summary>Plats et préparations non archivés ; <paramref name="AverageFoodCost"/> est une fraction, null s'il n'y a rien à moyenner.</summary>
public record DashboardSummary(
    int DishCount,
    int PreparationCount,
    decimal? AverageFoodCost,
    IReadOnlyList<WatchItem> WatchList);
