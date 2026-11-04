using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Sales;
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

/// <summary>
/// Variation récente du prix d'un ingrédient : comparaison de ses deux dernières entrées d'historique.
/// Les prix sont par unité de base (<paramref name="BaseUnit"/>) ; <paramref name="ChangePercent"/> est une fraction non arrondie
/// ((courant − précédent) ÷ précédent, négative pour une baisse) ; <paramref name="Date"/> est celle de la dernière entrée (UTC).
/// </summary>
public record PriceChange(
    int IngredientId,
    string Name,
    BaseUnit BaseUnit,
    decimal PreviousPrice,
    decimal CurrentPrice,
    decimal ChangePercent,
    DateTime Date);

// ── Tickets fournisseurs ─────────────────────────────────────────────────────

/// <summary><paramref name="SupplierName"/> est le fournisseur rapproché s'il y en a un, sinon le nom lu sur le ticket ; <paramref name="Total"/> est le total lu.</summary>
public record ReceiptSummary(
    int Id,
    DateTime CreatedAt,
    DateTime? ReceiptDate,
    string SupplierName,
    decimal? Total,
    ReceiptStatus Status,
    int LineCount);

/// <summary>
/// Une ligne du brouillon. <paramref name="Id"/> null = ligne ajoutée à la main ; sinon c'est une ligne du ticket, qui garde son identifiant.
/// <paramref name="VatRate"/> est une fraction (0,06 = 6 %) ; <paramref name="LineTotalExclVat"/> est le total hors TVA (corrigé ou saisi par le chef).
/// </summary>
public record ReceiptLineInput(
    int? Id,
    string RawLabel,
    decimal? Quantity,
    MeasureUnit? Unit,
    decimal? LineTotalExclVat,
    decimal? VatRate,
    int? IngredientId,
    bool IsIgnored);

/// <summary><paramref name="Lines"/> remplace les lignes du ticket, dans cet ordre. <paramref name="ReceiptDate"/> : seul le jour du calendrier compte (enregistré à minuit UTC).</summary>
public record ReceiptDraftInput(
    int ReceiptId,
    int? SupplierId,
    DateTime? ReceiptDate,
    string? InvoiceNumber,
    IReadOnlyList<ReceiptLineInput> Lines);

/// <summary>
/// Prix appliqué à un ingrédient. <paramref name="ReplacedCurrent"/> est faux quand le ticket est plus ancien que le dernier prix connu :
/// il n'enrichit alors que l'historique.
/// </summary>
public record AppliedPrice(
    int IngredientId,
    string IngredientName,
    BaseUnit BaseUnit,
    decimal? OldPricePerBaseUnit,
    decimal NewPricePerBaseUnit,
    bool ReplacedCurrent);

public record ApplyResult(IReadOnlyList<AppliedPrice> Prices);

// ── Ventes saisies et commandes ──────────────────────────────────────────────

/// <summary>
/// Une case de la grille hebdomadaire des ventes. <paramref name="Date"/> est un jour UTC (seul le jour compte) ;
/// pour un total de semaine (<paramref name="IsWeeklyTotal"/>), c'est le lundi. <paramref name="Quantity"/> est un nombre de portions
/// (une demi-portion est permise) ; 0 efface la saisie.
/// </summary>
public record SalesCell(int RecipeId, DateTime Date, ServicePeriod Service, bool IsWeeklyTotal, decimal Quantity);

/// <summary>Un plat de la grille et les saisies qui existent pour lui cette semaine-là ; <paramref name="UnitPriceInclVat"/> est son prix de vente actuel.</summary>
public record SalesWeekDish(int RecipeId, string Name, decimal? UnitPriceInclVat, IReadOnlyList<SalesCell> Cells);

/// <summary>Une semaine de ventes : <paramref name="Monday"/> est le lundi à 00:00 UTC.</summary>
public record SalesWeek(DateTime Monday, IReadOnlyList<SalesWeekDish> Dishes);

public record OrderLineInput(int RecipeId, int Quantity, string? Note);

public record OrderInput(OrderSource Source, string? TableOrCustomer, string? Note, IReadOnlyList<OrderLineInput> Lines);

public record OrderLineSummary(string DishName, int Quantity, string? Note);

/// <summary><paramref name="TotalInclVat"/> : somme des quantités × prix figés des lignes (non arrondie).</summary>
public record OrderSummary(
    int Id,
    int Number,
    DateTime CreatedAt,
    OrderSource Source,
    OrderStatus Status,
    string? TableOrCustomer,
    decimal TotalInclVat,
    IReadOnlyList<OrderLineSummary> Lines);

// ── Pertes et inventaires ────────────────────────────────────────────────────

/// <summary>
/// Une perte à déclarer : exactement un des deux identifiants est renseigné (ingrédient ou recette). <paramref name="Date"/> est un jour
/// (seul le jour UTC compte). Pour un ingrédient, <paramref name="Unit"/> doit se convertir dans son unité de base ; pour un plat,
/// <paramref name="Quantity"/> est un nombre de portions et l'unité est <c>Piece</c> ; pour une préparation, l'unité doit se convertir dans son unité de production.
/// </summary>
public record WasteInput(
    DateTime Date,
    int? IngredientId,
    int? RecipeId,
    decimal Quantity,
    MeasureUnit Unit,
    WasteReason Reason,
    string? Note);

/// <summary>
/// Une perte avec son coût, valorisé aux prix en vigueur à sa date (non arrondi). <paramref name="Cost"/> est null quand il est inconnu
/// (ingrédient sans prix, préparation sans quantité produite valide) ; <paramref name="CostIncomplete"/> signale une recette dont le coût
/// ne porte que sur les lignes chiffrées : <paramref name="Cost"/> est alors un coût partiel.
/// </summary>
public record WasteItem(
    int Id,
    DateTime Date,
    string ItemName,
    bool IsRecipe,
    decimal Quantity,
    MeasureUnit Unit,
    WasteReason Reason,
    string? Note,
    decimal? Cost,
    bool CostIncomplete);

/// <summary>
/// <paramref name="Value"/> : somme des quantités de base × prix en vigueur à la date de l'inventaire ; les ingrédients sans prix n'y comptent pas.
/// Null quand aucune ligne n'a de prix (inventaire vide, ou ingrédients tous sans prix).
/// </summary>
public record StockCountSummary(int Id, DateTime Date, StockCountStatus Status, int LineCount, decimal? Value);

/// <summary>Une quantité comptée, dans l'unité saisie.</summary>
public record StockCountLineInput(int IngredientId, decimal Quantity, MeasureUnit Unit);

/// <summary>La feuille de comptage : tous les ingrédients à compter, avec les valeurs déjà saisies.</summary>
public record StockCountSheet(int Id, DateTime Date, StockCountStatus Status, string? Note, IReadOnlyList<StockCountSheetLine> Lines);

/// <summary><paramref name="Quantity"/> et <paramref name="Unit"/> : la valeur saisie, telle que saisie ; null tant que l'ingrédient n'est pas compté.</summary>
public record StockCountSheetLine(
    int IngredientId,
    string Name,
    string Category,
    BaseUnit BaseUnit,
    decimal? Quantity,
    MeasureUnit? Unit);
