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

/// <summary><paramref name="RecipeId"/> permet de modifier les lignes d'une commande sans passer par le nom du plat, que deux plats peuvent partager.</summary>
public record OrderLineSummary(int RecipeId, string DishName, int Quantity, string? Note);

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

// ── Statistiques ─────────────────────────────────────────────────────────────

/// <summary>
/// Un plat vendu pendant la période (les plats sans vente n'y figurent pas). Les ventes saisies (totaux de semaine compris) et les commandes
/// se cumulent dans <paramref name="QuantitySold"/> ; <paramref name="QuantityFromEntries"/> et <paramref name="QuantityFromOrders"/> les distinguent.
/// <paramref name="UnitCost"/> est le coût de revient d'une portion aux prix du dernier jour de la période, <paramref name="TotalCost"/> = coût unitaire ×
/// quantité vendue, <paramref name="Margin"/> = CA HT − coût total (le CA suit les prix figés à la vente, pas le prix actuel), et
/// <paramref name="FoodCostPercent"/> = coût total ÷ CA HT (null si le CA est nul). Tout est non arrondi.
/// <paramref name="CostIncomplete"/> : le coût ne porte que sur les lignes chiffrées de la recette, il est partiel. <paramref name="Class"/> est la classe
/// de menu engineering ; null pour un plat au coût incomplet, qui n'entre pas dans la matrice.
/// </summary>
public record DishProfit(
    int RecipeId,
    string Name,
    decimal QuantitySold,
    decimal QuantityFromEntries,
    decimal QuantityFromOrders,
    decimal RevenueExclVat,
    decimal? UnitCost,
    decimal? TotalCost,
    decimal? Margin,
    decimal? FoodCostPercent,
    bool CostIncomplete,
    MenuClass? Class);

/// <summary>Chiffre d'affaires HT d'un jour UTC, par origine. Les totaux de semaine n'y figurent pas.</summary>
public record DayRevenue(DateTime Day, decimal FromEntries, decimal FromOrders);

/// <summary>
/// Coût (non arrondi) des pertes d'un motif ; une perte au coût inconnu compte dans <paramref name="Count"/> mais pas dans <paramref name="Cost"/>.
/// <paramref name="CostIncomplete"/> est vrai quand au moins une perte du motif a un coût inconnu ou partiel (recette dont une ligne n'est pas chiffrée) :
/// <paramref name="Cost"/> est alors un minimum.
/// </summary>
public record WasteByReason(WasteReason Reason, decimal Cost, int Count, bool CostIncomplete);

/// <summary>
/// Écart d'un ingrédient entre deux inventaires clôturés, en unité de base (<paramref name="BaseUnit"/>) : <paramref name="ActualUse"/> = ouverture + achats − clôture,
/// <paramref name="VarianceQuantity"/> = réel − théorique, valorisé au prix à la clôture (<paramref name="VarianceValue"/> est null si ce prix est inconnu).
/// </summary>
public record VarianceRow(
    int IngredientId,
    string Name,
    BaseUnit BaseUnit,
    decimal Opening,
    decimal Purchases,
    decimal Closing,
    decimal ActualUse,
    decimal TheoreticalUse,
    decimal VarianceQuantity,
    decimal? VarianceValue);

/// <summary>
/// L'écart entre l'inventaire d'ouverture et celui de clôture. <paramref name="Rows"/> ne contient que les ingrédients comptés aux deux inventaires : un
/// ingrédient absent de l'un des deux serait compté à 0 et ferait inventer des pertes. <paramref name="TotalVarianceValue"/> (Σ des valeurs connues) et le
/// coût réel (<see cref="PeriodStats.ActualFoodCost"/>) ne portent donc que sur ces lignes.
/// <paramref name="NotCountedIngredients"/> : les noms (triés) des ingrédients qui ont bougé dans l'intervalle (achats ou consommation théorique) ou qui n'ont été
/// comptés qu'à un des deux inventaires, mais pas aux deux : leur écart est inconnu. <paramref name="Warnings"/> : les approximations de la consommation
/// théorique, sans doublon (recette dont la décomposition est incomplète, perte d'ingrédient dont l'unité ne se convertit plus).
/// </summary>
public record VarianceReport(
    DateTime OpeningDate,
    DateTime ClosingDate,
    IReadOnlyList<VarianceRow> Rows,
    decimal TotalVarianceValue,
    IReadOnlyList<string> NotCountedIngredients,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Les chiffres d'une période (voir <see cref="StatsService.GetAsync"/>). <paramref name="RevenueFromEntries"/> comprend
/// <paramref name="WeeklyTotalsRevenue"/> (les totaux de semaine saisis d'un bloc) ; <paramref name="RevenueByDay"/> ne les comprend pas.
/// <paramref name="ActualFoodCost"/> et <paramref name="Variance"/> portent sur l'intervalle entre les deux inventaires (<see cref="VarianceReport.OpeningDate"/> à
/// <see cref="VarianceReport.ClosingDate"/>), pas sur la période demandée.
/// <paramref name="WasteCostIncomplete"/> : au moins une perte de la période a un coût inconnu ou partiel, <paramref name="WasteCost"/> est alors un minimum.
/// </summary>
public record PeriodStats(
    DateTime From,
    DateTime To,
    decimal RevenueExclVat,
    decimal RevenueFromEntries,
    decimal RevenueFromOrders,
    decimal WeeklyTotalsRevenue,
    decimal TheoreticalCost,
    bool CostIncomplete,
    decimal GrossMargin,
    decimal? TheoreticalFoodCost,
    decimal WasteCost,
    bool WasteCostIncomplete,
    decimal? ActualFoodCost,
    IReadOnlyList<DayRevenue> RevenueByDay,
    IReadOnlyList<DishProfit> Dishes,
    IReadOnlyList<WasteByReason> Waste,
    VarianceReport? Variance);
