using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class StatsServiceTests(PostgresFixture postgres)
{
    private const string InvalidPeriodMessage = "La période est invalide";
    private const string PeriodTooLongMessage = "Période trop longue (366 jours maximum)";

    // Dates fixes (UTC). Le 5 octobre 2026 est un lundi.
    private static readonly DateTime Sep1 = Day(9, 1);
    private static readonly DateTime Sep20 = Day(9, 20);
    private static readonly DateTime Oct1 = Day(10, 1);
    private static readonly DateTime Oct4 = Day(10, 4);
    private static readonly DateTime Oct5 = Day(10, 5);
    private static readonly DateTime Oct6 = Day(10, 6);
    private static readonly DateTime Oct7 = Day(10, 7);
    private static readonly DateTime Oct8 = Day(10, 8);
    private static readonly DateTime Oct10 = Day(10, 10);
    private static readonly DateTime Oct11 = Day(10, 11);
    private static readonly DateTime Oct12 = Day(10, 12);
    private static readonly DateTime Oct15 = Day(10, 15);
    private static readonly DateTime Oct20 = Day(10, 20);
    private static readonly DateTime Oct22 = Day(10, 22);
    private static readonly DateTime Oct25 = Day(10, 25);
    private static readonly DateTime Oct28 = Day(10, 28);
    private static readonly DateTime Oct31 = Day(10, 31);
    private static readonly DateTime Nov1 = Day(11, 1);
    private static readonly DateTime Nov2 = Day(11, 2);
    private static readonly DateTime Nov10 = Day(11, 10);

    // ── Chiffre d'affaires ───────────────────────────────────────────────────

    [Fact]
    public async Task Revenue_SumsEntriesAndOrders_WithSourcesSplit()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro", "UTC");
        var steak = await AddDishAsync(options, restaurant, "Steak");
        var service = CreateService(options, restaurant);

        // Prix figés : 22,40 TTC à 12 % = 20,00 HT ; 11,00 TTC à 10 % = 10,00 HT.
        await AddEntryAsync(options, restaurant, steak, Oct5, 10m);                          // 10 × 20 = 200
        await AddEntryAsync(options, restaurant, steak, Oct6, 5m, ServicePeriod.Soir);        // 5 × 20 = 100
        await AddEntryAsync(options, restaurant, steak, Oct4, 100m);                          // la veille : hors période
        await AddEntryAsync(options, restaurant, steak, Oct8, 100m);                          // le lendemain : hors période
        await AddOrderAsync(options, restaurant, Day(10, 5, 12, 30), OrderStatus.Servie, (steak, 2, 22.40m, 0.12m));     // 2 × 20 = 40
        await AddOrderAsync(options, restaurant, Day(10, 6, 19, 0), OrderStatus.Nouvelle, (steak, 3, 11.00m, 0.10m));    // 3 × 10 = 30
        await AddOrderAsync(options, restaurant, Day(10, 7, 23, 59, 59), OrderStatus.Prete, (steak, 1, 11.00m, 0.10m));  // 10 : dernière seconde du 7
        await AddOrderAsync(options, restaurant, Day(10, 8, 0, 0, 0), OrderStatus.Servie, (steak, 1, 11.00m, 0.10m));    // minuit du 8 : borne exclue
        await AddOrderAsync(options, restaurant, Day(10, 4, 23, 59, 59), OrderStatus.Servie, (steak, 1, 11.00m, 0.10m)); // avant le 5

        // Les bornes sont ramenées au jour UTC : l'heure est ignorée.
        var stats = await service.GetAsync(Day(10, 5, 17, 30), Day(10, 7, 8, 0));

        Assert.Equal(Oct5, stats.From);
        Assert.Equal(Oct7, stats.To);
        Assert.Equal(380m, stats.RevenueExclVat);          // 300 + 80
        Assert.Equal(300m, stats.RevenueFromEntries);
        Assert.Equal(80m, stats.RevenueFromOrders);        // 40 + 30 + 10
        Assert.Equal(0m, stats.WeeklyTotalsRevenue);

        Assert.Equal(3, stats.RevenueByDay.Count);
        Assert.Equal([Oct5, Oct6, Oct7], stats.RevenueByDay.Select(d => d.Day));
        Assert.Equal((200m, 40m), (stats.RevenueByDay[0].FromEntries, stats.RevenueByDay[0].FromOrders));
        Assert.Equal((100m, 30m), (stats.RevenueByDay[1].FromEntries, stats.RevenueByDay[1].FromOrders));
        Assert.Equal((0m, 10m), (stats.RevenueByDay[2].FromEntries, stats.RevenueByDay[2].FromOrders));

        var dish = Assert.Single(stats.Dishes);
        Assert.Equal(steak, dish.RecipeId);
        Assert.Equal("Steak", dish.Name);
        Assert.Equal(21m, dish.QuantitySold);              // 10 + 5 + 2 + 3 + 1
        Assert.Equal(15m, dish.QuantityFromEntries);
        Assert.Equal(6m, dish.QuantityFromOrders);
        Assert.Equal(380m, dish.RevenueExclVat);
    }

    [Fact]
    public async Task Sales_CancelledOrdersExcluded()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m));            // 100 g × 0,02 = 2,00
        var service = CreateService(options, restaurant);

        await AddOrderAsync(options, restaurant, Day(10, 5, 12, 0), OrderStatus.Servie, (burger, 1, 11.00m, 0.10m));      // 10
        await AddOrderAsync(options, restaurant, Day(10, 5, 13, 0), OrderStatus.Annulee, (burger, 5, 11.00m, 0.10m));     // annulée : ni CA, ni coût
        await AddOrderAsync(options, restaurant, Day(10, 6, 13, 0), OrderStatus.EnPreparation, (burger, 2, 11.00m, 0.10m)); // 20

        var stats = await service.GetAsync(Oct5, Oct6);

        Assert.Equal(30m, stats.RevenueExclVat);
        Assert.Equal(30m, stats.RevenueFromOrders);
        var dish = Assert.Single(stats.Dishes);
        Assert.Equal(3m, dish.QuantitySold);
        Assert.Equal(3m, dish.QuantityFromOrders);
        Assert.Equal(6m, dish.TotalCost);                  // 3 portions × 2,00, sans les 5 annulées
        Assert.Equal(6m, stats.TheoreticalCost);
        Assert.Equal(24m, stats.GrossMargin);
    }

    [Fact]
    public async Task RevenueByDay_WeeklyTotalsSeparated()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m));            // coût 2,00
        var service = CreateService(options, restaurant);

        // Total de semaine daté du lundi 5 (20 portions à 20 HT = 400) et une vente du mardi 6 (5 × 20 = 100).
        await AddEntryAsync(options, restaurant, burger, Oct5, 20m, ServicePeriod.Journee, weeklyTotal: true);
        await AddEntryAsync(options, restaurant, burger, Oct6, 5m);

        // Période qui contient le lundi : le total de semaine compte dans le CA, les coûts et les portions, mais pas dans le CA par jour.
        var week = await service.GetAsync(Oct5, Day(10, 11));
        Assert.Equal(7, week.RevenueByDay.Count);
        Assert.Equal(500m, week.RevenueExclVat);
        Assert.Equal(500m, week.RevenueFromEntries);       // le total de semaine est une saisie
        Assert.Equal(400m, week.WeeklyTotalsRevenue);
        Assert.Equal(0m, week.RevenueByDay[0].FromEntries);  // lundi 5 : le total de semaine n'y est pas
        Assert.Equal(100m, week.RevenueByDay[1].FromEntries);
        Assert.Equal(100m, week.RevenueByDay.Sum(d => d.FromEntries));
        var dish = Assert.Single(week.Dishes);
        Assert.Equal(25m, dish.QuantitySold);
        Assert.Equal(50m, dish.TotalCost);                 // 25 × 2,00
        Assert.Equal(50m, week.TheoreticalCost);

        // Période qui ne contient pas le lundi : le total de semaine n'y est pas.
        var midWeek = await service.GetAsync(Oct6, Day(10, 11));
        Assert.Equal(100m, midWeek.RevenueExclVat);
        Assert.Equal(0m, midWeek.WeeklyTotalsRevenue);
        Assert.Equal(5m, Assert.Single(midWeek.Dishes).QuantitySold);
    }

    // ── Coûts et rentabilité ─────────────────────────────────────────────────

    [Fact]
    public async Task Costs_UsePricesInForceAtPeriodEnd()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.05m);       // prix courant, en vigueur depuis le 11
        await AddPriceAsync(options, restaurant, boeuf, Sep1, 0.01m);
        await AddPriceAsync(options, restaurant, boeuf, Day(10, 10, 18, 0), 0.02m);              // le 10 à 18 h : compte pour le 10
        await AddPriceAsync(options, restaurant, boeuf, Oct11, 0.05m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 200m));
        var service = CreateService(options, restaurant);
        await AddEntryAsync(options, restaurant, burger, Oct5, 10m);                             // 10 × 20 = 200 de CA

        // Période close le 10 : 200 g × 0,02 = 4,00 par portion ; le prix du 11 est ignoré.
        var stats = await service.GetAsync(Oct5, Oct10);
        var dish = Assert.Single(stats.Dishes);
        Assert.Equal(4m, dish.UnitCost);
        Assert.Equal(40m, dish.TotalCost);
        Assert.Equal(160m, dish.Margin);                   // 200 − 40
        Assert.Equal(0.2m, dish.FoodCostPercent);          // 40 ÷ 200
        Assert.False(dish.CostIncomplete);
        Assert.Equal(40m, stats.TheoreticalCost);
        Assert.Equal(160m, stats.GrossMargin);
        Assert.Equal(0.2m, stats.TheoreticalFoodCost);
        Assert.False(stats.CostIncomplete);

        // La même vente, période close le 12 : 200 g × 0,05 = 10,00 par portion.
        var later = await service.GetAsync(Oct5, Oct12);
        Assert.Equal(10m, Assert.Single(later.Dishes).UnitCost);
        Assert.Equal(100m, later.TheoreticalCost);
    }

    [Fact]
    public async Task Profitability_IncompleteDish_FlaggedAndExcludedFromMatrix()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var truffe = await AddIngredientAsync(options, restaurant, "Truffe");                    // aucun prix
        var complet = await AddDishAsync(options, restaurant, "Complet", Line(boeuf, 100m));      // coût 2,00
        var incomplet = await AddDishAsync(options, restaurant, "Incomplet", Line(boeuf, 100m), Line(truffe, 5m));   // 2,00 chiffrés, la truffe non
        var service = CreateService(options, restaurant);

        await AddEntryAsync(options, restaurant, complet, Oct5, 1m);                              // CA 20
        await AddEntryAsync(options, restaurant, incomplet, Oct5, 99m, ServicePeriod.Soir);        // CA 1980

        var stats = await service.GetAsync(Oct5, Oct5);

        var incompleteDish = stats.Dishes.Single(d => d.RecipeId == incomplet);
        Assert.True(incompleteDish.CostIncomplete);
        Assert.Null(incompleteDish.Class);                 // hors matrice
        Assert.Equal(2m, incompleteDish.UnitCost);         // coût partiel
        Assert.Equal(198m, incompleteDish.TotalCost);
        Assert.Equal(1782m, incompleteDish.Margin);

        // Le plat complet est seul dans la matrice (n = 1) : populaire, et sa marge égale la moyenne -> Étoile.
        // Si le plat incomplet (99 portions) y entrait, le plat complet (1 portion sur 100) ne serait plus populaire.
        var completeDish = stats.Dishes.Single(d => d.RecipeId == complet);
        Assert.False(completeDish.CostIncomplete);
        Assert.Equal(MenuClass.Etoile, completeDish.Class);

        // Le coût partiel entre quand même dans le total, qui est marqué incomplet.
        Assert.True(stats.CostIncomplete);
        Assert.Equal(200m, stats.TheoreticalCost);         // 2 + 198
        Assert.Equal(2000m, stats.RevenueExclVat);
        Assert.Equal(1800m, stats.GrossMargin);
        Assert.Equal(0.1m, stats.TheoreticalFoodCost);
    }

    [Fact]
    public async Task Profitability_RecipeWithoutLines_IsIncompleteNotFree()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var complet = await AddDishAsync(options, restaurant, "Complet", Line(boeuf, 100m));       // coût 2,00
        var vide = await AddDishAsync(options, restaurant, "Recette vide");                        // aucune ligne : son coût est inconnu, pas nul
        var fondVide = await AddPreparationAsync(options, restaurant, "Fond vide", 1000m, BaseUnit.Milliliter);
        var surFondVide = await AddDishAsync(options, restaurant, "Sur fond vide",
            Line(boeuf, 50m), new RecipeLine { SubRecipeId = fondVide, Quantity = 200m, Unit = MeasureUnit.Ml });   // une préparation sans ligne le rend incomplet aussi
        var service = CreateService(options, restaurant);

        await AddEntryAsync(options, restaurant, complet, Oct5, 1m);                               // CA 20
        await AddEntryAsync(options, restaurant, vide, Oct5, 50m, ServicePeriod.Soir);             // CA 1 000
        await AddEntryAsync(options, restaurant, surFondVide, Oct5, 10m, ServicePeriod.Soir);      // CA 200

        var stats = await service.GetAsync(Oct5, Oct5);

        var emptyDish = stats.Dishes.Single(d => d.RecipeId == vide);
        Assert.True(emptyDish.CostIncomplete);
        Assert.Null(emptyDish.Class);                      // hors matrice
        Assert.Equal(0m, emptyDish.TotalCost);             // le montant reste celui des lignes chiffrées (aucune)
        var onEmptyPreparation = stats.Dishes.Single(d => d.RecipeId == surFondVide);
        Assert.True(onEmptyPreparation.CostIncomplete);
        Assert.Null(onEmptyPreparation.Class);
        Assert.Equal(10m, onEmptyPreparation.TotalCost);   // 10 × 50 g × 0,02 : la partie chiffrée

        // Le plat complet reste seul dans la matrice.
        var completeDish = stats.Dishes.Single(d => d.RecipeId == complet);
        Assert.False(completeDish.CostIncomplete);
        Assert.Equal(MenuClass.Etoile, completeDish.Class);

        // Le total est marqué incomplet : sans cela, une recette sans ligne ferait passer sa marge pour 100 %.
        Assert.True(stats.CostIncomplete);
    }

    [Fact]
    public async Task Profitability_OnlyCompleteRecipes_AreNotFlagged()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var fond = await AddPreparationAsync(options, restaurant, "Fond", 1000m, BaseUnit.Milliliter, Line(boeuf, 500m));
        var plat = await AddDishAsync(options, restaurant, "Plat", Line(boeuf, 100m), new RecipeLine { SubRecipeId = fond, Quantity = 100m, Unit = MeasureUnit.Ml });
        var service = CreateService(options, restaurant);

        await AddEntryAsync(options, restaurant, plat, Oct5, 4m);

        var stats = await service.GetAsync(Oct5, Oct5);

        Assert.False(Assert.Single(stats.Dishes).CostIncomplete);
        Assert.False(stats.CostIncomplete);
    }

    [Fact]
    public async Task MenuEngineering_ClassesAssigned()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var base1 = await AddIngredientAsync(options, restaurant, "Base", price: 1m);            // 1 par gramme : le coût d'une portion est son poids
        var star = await AddDishAsync(options, restaurant, "A Etoile", Line(base1, 5m));
        var cow = await AddDishAsync(options, restaurant, "B Vache", Line(base1, 8m));
        var puzzle = await AddDishAsync(options, restaurant, "C Enigme", Line(base1, 10m));
        var dog = await AddDishAsync(options, restaurant, "D Poids mort", Line(base1, 8m));
        var service = CreateService(options, restaurant);

        // Prix HT = TTC (TVA 0) : (quantité, prix, coût) -> marge unitaire.
        await AddEntryAsync(options, restaurant, star, Oct5, 40m, price: 20m, vat: 0m);          // marge 15
        await AddEntryAsync(options, restaurant, cow, Oct5, 40m, price: 10m, vat: 0m);           // marge 2
        await AddEntryAsync(options, restaurant, puzzle, Oct5, 10m, price: 30m, vat: 0m);        // marge 20
        await AddEntryAsync(options, restaurant, dog, Oct5, 10m, price: 9m, vat: 0m);            // marge 1

        var stats = await service.GetAsync(Oct5, Oct5);

        // 100 portions, 4 plats : populaire à partir de 0,70 × 100 / 4 = 17,5 portions.
        // Marge moyenne pondérée : (40×15 + 40×2 + 10×20 + 10×1) ÷ 100 = 8,9.
        var classes = stats.Dishes.ToDictionary(d => d.RecipeId, d => d.Class);
        Assert.Equal(MenuClass.Etoile, classes[star]);         // populaire, 15 ≥ 8,9
        Assert.Equal(MenuClass.VacheALait, classes[cow]);      // populaire, 2 < 8,9
        Assert.Equal(MenuClass.Enigme, classes[puzzle]);       // 10 < 17,5, 20 ≥ 8,9
        Assert.Equal(MenuClass.PoidsMort, classes[dog]);       // 10 < 17,5, 1 < 8,9

        // Plats triés par CA décroissant : 800, 400, 300, 90.
        Assert.Equal([star, cow, puzzle, dog], stats.Dishes.Select(d => d.RecipeId));
        Assert.Equal(1590m, stats.RevenueExclVat);
        Assert.Equal(700m, stats.TheoreticalCost);         // 200 + 320 + 100 + 80
        Assert.Equal(890m, stats.GrossMargin);
        Assert.Equal(700m / 1590m, stats.TheoreticalFoodCost);
    }

    [Fact]
    public async Task MenuEngineering_EqualMarginsStayEqual_DespiteYieldDecimals()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");

        // Rendement 65 % : 100 g utiles = 153,846… g bruts à 0,003 = 0,461538461538… de coût par portion, jamais exact en décimal.
        // Les deux plats ont donc la même marge (19,538461…), mais calculée sur 7 puis 37 portions elle diffère au 28e chiffre
        // (…539 contre …538) et la moyenne pondérée tombe entre les deux : sans arrondi, le plat à …538 perdrait l'égalité au seuil.
        var base1 = await AddIngredientAsync(options, restaurant, "Base", price: 0.003m, yieldPercent: 65m);
        var petit = await AddDishAsync(options, restaurant, "Petit", Line(base1, 100m));
        var grand = await AddDishAsync(options, restaurant, "Grand", Line(base1, 100m));
        await AddEntryAsync(options, restaurant, petit, Oct5, 7m, price: 20m, vat: 0m);
        await AddEntryAsync(options, restaurant, grand, Oct5, 37m, price: 20m, vat: 0m);

        var stats = await CreateService(options, restaurant).GetAsync(Oct5, Oct5);

        // 44 portions, 2 plats : populaire à partir de 0,70 × 44 ÷ 2 = 15,4 portions. Même marge : tous deux à marge élevée.
        Assert.Equal(MenuClass.Etoile, stats.Dishes.Single(d => d.RecipeId == grand).Class);
        Assert.Equal(MenuClass.Enigme, stats.Dishes.Single(d => d.RecipeId == petit).Class);
    }

    // ── Pertes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Waste_GroupedByReasonAtDatePrices()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var beurre = await AddIngredientAsync(options, restaurant, "Beurre", price: 0.02m);      // 0,01 jusqu'au 9, 0,02 depuis le 10
        await AddPriceAsync(options, restaurant, beurre, Oct1, 0.01m);
        await AddPriceAsync(options, restaurant, beurre, Oct10, 0.02m);
        var sel = await AddIngredientAsync(options, restaurant, "Sel");                          // aucun prix
        var tartare = await AddDishAsync(options, restaurant, "Tartare", Line(beurre, 100m));
        var sauce = await AddPreparationAsync(options, restaurant, "Sauce", 1000m, BaseUnit.Gram, Line(beurre, 400m));
        var service = CreateService(options, restaurant);

        await AddWasteAsync(options, restaurant, Oct5, beurre, null, 500m, MeasureUnit.G, WasteReason.Perime);      // 500 × 0,01 = 5
        await AddWasteAsync(options, restaurant, Oct12, beurre, null, 500m, MeasureUnit.G, WasteReason.Perime);     // 500 × 0,02 = 10
        await AddWasteAsync(options, restaurant, Oct6, null, tartare, 2m, MeasureUnit.Piece, WasteReason.Casse);    // 2 × (100 × 0,01) = 2
        await AddWasteAsync(options, restaurant, Oct7, beurre, null, 1m, MeasureUnit.Kg, WasteReason.Erreur);       // 1000 × 0,01 = 10
        await AddWasteAsync(options, restaurant, Oct7, null, sauce, 500m, MeasureUnit.G, WasteReason.Surproduction); // 500 × (400 × 0,01 ÷ 1000) = 2
        await AddWasteAsync(options, restaurant, Oct8, sel, null, 100m, MeasureUnit.G, WasteReason.Autre);          // coût inconnu
        await AddWasteAsync(options, restaurant, Oct25, beurre, null, 1000m, MeasureUnit.G, WasteReason.Perime);    // hors période

        var stats = await service.GetAsync(Oct1, Oct15);

        // Du plus coûteux au moins coûteux ; à coût égal, par motif (Casse avant Surproduction).
        // Seul « Autre » est incomplet : le sel n'a aucun prix, son coût est inconnu (0 dans la somme).
        Assert.Equal(
            [
                (WasteReason.Perime, 15m, 2, false), (WasteReason.Erreur, 10m, 1, false), (WasteReason.Casse, 2m, 1, false),
                (WasteReason.Surproduction, 2m, 1, false), (WasteReason.Autre, 0m, 1, true)
            ],
            stats.Waste.Select(w => (w.Reason, w.Cost, w.Count, w.CostIncomplete)));
        Assert.Equal(29m, stats.WasteCost);                // 15 + 10 + 2 + 2 + 0
        Assert.True(stats.WasteCostIncomplete);
        Assert.Empty(stats.Dishes);                        // aucune vente
    }

    // ── Écart d'inventaire ───────────────────────────────────────────────────

    [Fact]
    public async Task Variance_BetweenTwoClosedCounts()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");

        // Boeuf : rendement 75 %, 0,015 jusqu'au 14, 0,02 du 15 au 24, 0,03 depuis le 25 (prix courant).
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.03m, yieldPercent: 75m);
        await AddPriceAsync(options, restaurant, boeuf, Sep1, 0.015m);
        await AddPriceAsync(options, restaurant, boeuf, Oct15, 0.02m);
        await AddPriceAsync(options, restaurant, boeuf, Oct25, 0.03m);
        var sel = await AddIngredientAsync(options, restaurant, "Sel");                          // aucun prix
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 150m));        // 150 g utiles = 200 g bruts
        var fond = await AddPreparationAsync(options, restaurant, "Fond", 2000m, BaseUnit.Gram, Line(boeuf, 300m));
        var service = CreateService(options, restaurant);

        // Inventaires : le 1er (ouverture A) et le 31 (clôture B) encadrent la période du 10 au 20.
        await AddCountAsync(options, restaurant, Sep20, StockCountStatus.Cloture, (boeuf, 99999m));                        // plus ancien que A
        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 20000m), (sel, 1000m));           // A
        await AddCountAsync(options, restaurant, Oct15, StockCountStatus.Cloture, (boeuf, 1m));                            // dans la période : ni A ni B
        await AddCountAsync(options, restaurant, Oct22, StockCountStatus.Brouillon, (boeuf, 7m));                          // brouillon : ignoré
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 18300m), (sel, 900m));           // B
        await AddCountAsync(options, restaurant, Nov10, StockCountStatus.Cloture, (boeuf, 5m));                            // plus récent que B

        // Achats (tickets appliqués) dans ]1er, 31] : 10 kg + 2000 g = 12 000 g.
        await AddReceiptAsync(options, restaurant, Oct1, ReceiptStatus.Applied, Item(boeuf, 5m, MeasureUnit.Kg, 75m));       // jour de A : exclu
        await AddReceiptAsync(options, restaurant, Day(10, 8), ReceiptStatus.Applied, Item(boeuf, 10m, MeasureUnit.Kg, 180m));
        await AddReceiptAsync(options, restaurant, Oct12, ReceiptStatus.Extracted, Item(boeuf, 5m, MeasureUnit.Kg, 75m));    // pas appliqué
        await AddReceiptAsync(options, restaurant, Oct31, ReceiptStatus.Applied, Item(boeuf, 2000m, MeasureUnit.G, 40m));     // jour de B : inclus
        await AddReceiptAsync(options, restaurant, Nov2, ReceiptStatus.Applied, Item(boeuf, 1m, MeasureUnit.Kg, 15m));        // après B

        // Ventes (prix figés : 20,00 HT la portion) : 30 + 20 + 10 = 60 portions dans ]1er, 31].
        await AddEntryAsync(options, restaurant, burger, Oct1, 100m);                              // jour de A : exclu
        await AddEntryAsync(options, restaurant, burger, Oct12, 30m);                              // dans la période
        await AddEntryAsync(options, restaurant, burger, Oct25, 20m, ServicePeriod.Soir);
        await AddEntryAsync(options, restaurant, burger, Nov1, 40m);                               // après B
        await AddEntryAsync(options, restaurant, burger, Day(10, 14), 0m);                         // saisies non positives : ignorées
        await AddEntryAsync(options, restaurant, burger, Day(10, 14), -10m, ServicePeriod.Soir);
        await AddOrderAsync(options, restaurant, Day(10, 31, 20, 0), OrderStatus.Servie, (burger, 10, 22.40m, 0.12m));       // jour de B : inclus
        await AddOrderAsync(options, restaurant, Day(10, 15, 12, 0), OrderStatus.Annulee, (burger, 50, 22.40m, 0.12m));      // annulée : ignorée

        // Pertes dans ]1er, 31] : 1000 g de boeuf + 1000 g de fond + 2 burgers.
        await AddWasteAsync(options, restaurant, Oct1, boeuf, null, 5m, MeasureUnit.Kg, WasteReason.Perime);                  // jour de A : exclu
        await AddWasteAsync(options, restaurant, Oct20, boeuf, null, 1m, MeasureUnit.Kg, WasteReason.Perime);                 // 1000 g, sans rendement
        await AddWasteAsync(options, restaurant, Oct22, null, fond, 1000m, MeasureUnit.G, WasteReason.Surproduction);          // moitié du fond : 150 g utiles = 200 g bruts
        await AddWasteAsync(options, restaurant, Oct28, null, burger, 2m, MeasureUnit.Piece, WasteReason.Casse);               // 2 × 200 g bruts
        await AddWasteAsync(options, restaurant, Nov2, boeuf, null, 1m, MeasureUnit.Kg, WasteReason.Perime);                   // après B

        var stats = await service.GetAsync(Oct10, Oct20);

        // Boeuf, dans ]1er, 31] :
        //   ouverture 20 000 ; achats 10 000 + 2 000 = 12 000 ; clôture 18 300
        //   consommation réelle   = 20 000 + 12 000 − 18 300 = 13 700
        //   consommation théorique = 60 portions × 200 g (150 × 100 ÷ 75) = 12 000
        //                            + 1 000 (perte de boeuf) + 200 (perte de fond) + 2 × 200 = 400 (perte de burgers) = 13 600
        //   écart = 13 700 − 13 600 = 100 g, valorisé au prix du 31 (0,03) : 3,00
        var variance = stats.Variance;
        Assert.NotNull(variance);
        Assert.Equal(Oct1, variance.OpeningDate);
        Assert.Equal(Oct31, variance.ClosingDate);
        Assert.Equal(2, variance.Rows.Count);

        var boeufRow = variance.Rows[0];                   // valeur connue d'abord
        Assert.Equal(boeuf, boeufRow.IngredientId);
        Assert.Equal("Boeuf", boeufRow.Name);
        Assert.Equal(BaseUnit.Gram, boeufRow.BaseUnit);
        Assert.Equal(20000m, boeufRow.Opening);
        Assert.Equal(12000m, boeufRow.Purchases);
        Assert.Equal(18300m, boeufRow.Closing);
        Assert.Equal(13700m, boeufRow.ActualUse);
        Assert.Equal(13600m, boeufRow.TheoreticalUse);
        Assert.Equal(100m, boeufRow.VarianceQuantity);
        Assert.Equal(3m, boeufRow.VarianceValue);

        // Sel : compté seulement. Réel 1 000 + 0 − 900 = 100, théorique 0, prix inconnu.
        var selRow = variance.Rows[1];
        Assert.Equal(sel, selRow.IngredientId);
        Assert.Equal(100m, selRow.ActualUse);
        Assert.Equal(0m, selRow.TheoreticalUse);
        Assert.Equal(100m, selRow.VarianceQuantity);
        Assert.Null(selRow.VarianceValue);
        Assert.Equal(3m, variance.TotalVarianceValue);     // seules les valeurs connues
        Assert.Empty(variance.NotCountedIngredients);      // tout ce qui a bougé a été compté aux deux inventaires
        Assert.Empty(variance.Warnings);                   // et toutes les recettes se décomposent

        // Coût réel : (13 700 × 0,03) ÷ CA HT de ]1er, 31] (30 + 20 + 10 portions × 20 = 1 200) = 411 ÷ 1 200 = 0,3425.
        Assert.Equal(0.3425m, stats.ActualFoodCost);

        // La période demandée (10 → 20) reste calculée à part : 30 portions le 12, au prix du 20 (200 g × 0,02 = 4,00).
        Assert.Equal(600m, stats.RevenueExclVat);
        Assert.Equal(120m, stats.TheoreticalCost);
        Assert.Equal(0.2m, stats.TheoreticalFoodCost);
        Assert.Equal(20m, stats.WasteCost);                // 1 kg de boeuf le 20, à 0,02 ; la perte de fond du 22 est hors période
        Assert.False(stats.WasteCostIncomplete);
    }

    [Fact]
    public async Task Variance_IgnoredReceiptLinesNotPurchases()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var service = CreateService(options, restaurant);

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 1000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 1500m));
        await AddReceiptAsync(options, restaurant, Oct10, ReceiptStatus.Applied,
            Item(boeuf, 5m, MeasureUnit.Kg, 90m),                          // 5 000 g : seul achat réel
            Item(boeuf, 3m, MeasureUnit.Kg, 54m, ignored: true),           // ligne ignorée : pas un achat
            Item(null, 2m, MeasureUnit.Kg, 36m));                          // sans ingrédient : pas un achat

        var stats = await service.GetAsync(Oct10, Oct20);

        var variance = stats.Variance;
        Assert.NotNull(variance);
        var row = Assert.Single(variance.Rows);
        Assert.Equal(5000m, row.Purchases);
        Assert.Equal(4500m, row.ActualUse);                // 1 000 + 5 000 − 1 500
        Assert.Equal(4500m, row.VarianceQuantity);         // rien n'a été vendu ni perdu
        Assert.Equal(90m, row.VarianceValue);              // 4 500 × 0,02
    }

    [Fact]
    public async Task Variance_NullWithoutBracketingCounts()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var service = CreateService(options, restaurant);

        // Aucun inventaire.
        AssertNoVariance(await service.GetAsync(Oct10, Oct20));

        // Une ouverture clôturée, mais rien après la fin de la période.
        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 1000m));
        AssertNoVariance(await service.GetAsync(Oct10, Oct20));

        // Un brouillon de clôture ne compte pas ; un inventaire clôturé au milieu de la période n'encadre rien.
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Brouillon, (boeuf, 800m));
        await AddCountAsync(options, restaurant, Oct15, StockCountStatus.Cloture, (boeuf, 900m));
        AssertNoVariance(await service.GetAsync(Oct10, Oct20));

        // Avec une clôture clôturée le 31, l'écart existe. Sans vente, il n'y a pas de CA : pas de coût réel.
        await AddCountAsync(options, restaurant, Day(10, 30), StockCountStatus.Cloture, (boeuf, 700m));
        var framed = await service.GetAsync(Oct10, Oct20);
        var variance = framed.Variance;
        Assert.NotNull(variance);
        Assert.Equal(Oct1, variance.OpeningDate);
        Assert.Equal(Day(10, 30), variance.ClosingDate);       // le premier inventaire clôturé à partir de la fin de période
        Assert.Null(framed.ActualFoodCost);

        // Une période antérieure au premier inventaire n'a pas d'ouverture.
        AssertNoVariance(await service.GetAsync(Sep1, Day(9, 5)));

        // Un seul jour d'inventaire : ouverture et clôture sont le même jour, l'intervalle est vide.
        AssertNoVariance(await service.GetAsync(Oct1, Oct1));

        static void AssertNoVariance(PeriodStats stats)
        {
            Assert.Null(stats.Variance);
            Assert.Null(stats.ActualFoodCost);
        }
    }

    [Fact]
    public async Task Variance_UndatedAppliedReceipt_UsesCreatedAt()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro", "UTC");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var service = CreateService(options, restaurant);

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 1000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 1000m));

        // Tickets appliqués sans date lue (restaurant en UTC) : le jour local de leur création les situe (comme pour la date de leur prix dans l'historique).
        await AddUndatedReceiptAsync(options, restaurant, Day(10, 10, 9, 30), Item(boeuf, 2m, MeasureUnit.Kg, 36m));       // dans ]1er, 31] : 2 000 g
        await AddUndatedReceiptAsync(options, restaurant, Day(10, 31, 23, 0), Item(boeuf, 500m, MeasureUnit.G, 9m));        // 23 h le jour de B : 500 g
        await AddUndatedReceiptAsync(options, restaurant, Day(10, 1, 12, 0), Item(boeuf, 7m, MeasureUnit.Kg, 126m));        // jour de A : exclu
        await AddUndatedReceiptAsync(options, restaurant, Nov2, Item(boeuf, 9m, MeasureUnit.Kg, 162m));                     // après B : exclu
        // Un ticket daté : sa date lue l'emporte sur sa création (créé le 12, daté du 5 novembre -> hors intervalle).
        await using (var db = OpenContext(options, restaurant))
        {
            db.Receipts.Add(new Receipt
            {
                ReceiptDate = Day(11, 5),
                Status = ReceiptStatus.Applied,
                CreatedAt = Oct12,
                AppliedAt = Oct12,
                Lines = [new ReceiptLine { Position = 1, RawLabel = "ligne", IngredientId = boeuf, Quantity = 3m, Unit = MeasureUnit.Kg, LineTotalExclVat = 54m }]
            });
            await db.SaveChangesAsync();
        }

        var stats = await service.GetAsync(Oct10, Oct20);

        var variance = stats.Variance;
        Assert.NotNull(variance);
        var row = Assert.Single(variance.Rows);
        Assert.Equal(2500m, row.Purchases);                // 2 000 + 500
        Assert.Equal(2500m, row.ActualUse);                // 1 000 + 2 500 − 1 000
        Assert.Equal(50m, row.VarianceValue);              // 2 500 × 0,02 (rien n'a été vendu ni perdu)
    }

    [Fact]
    public async Task Variance_IngredientMissingFromClosingCount_ListedAsNotCounted()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var fromage = await AddIngredientAsync(options, restaurant, "Fromage", price: 0.01m);
        var moutarde = await AddIngredientAsync(options, restaurant, "Moutarde", price: 0.005m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m));                   // coût 2,00, CA 20
        var service = CreateService(options, restaurant);

        // Le fromage est compté à l'ouverture, oublié à la clôture, et il en est entré 3 kg : sans règle, sa clôture à 0 ferait « disparaître » 3 kg + le stock.
        // La moutarde est comptée à l'ouverture seulement, sans aucun mouvement.
        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m), (fromage, 500m), (moutarde, 200m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 9000m));
        await AddReceiptAsync(options, restaurant, Oct10, ReceiptStatus.Applied, Item(fromage, 3m, MeasureUnit.Kg, 30m));
        await AddEntryAsync(options, restaurant, burger, Oct12, 10m);                                          // 10 × 100 g = 1 000 g de boeuf, CA 200

        var stats = await service.GetAsync(Oct10, Oct20);

        var variance = stats.Variance;
        Assert.NotNull(variance);
        var row = Assert.Single(variance.Rows);            // pas de ligne pour le fromage ni la moutarde
        Assert.Equal(boeuf, row.IngredientId);
        Assert.Equal(1000m, row.ActualUse);                // 10 000 − 9 000
        Assert.Equal(1000m, row.TheoreticalUse);
        Assert.Equal(0m, row.VarianceQuantity);
        Assert.Equal(["Fromage", "Moutarde"], variance.NotCountedIngredients);
        Assert.Equal(0m, variance.TotalVarianceValue);     // lignes comptées seulement
        // Coût réel sur les lignes comptées : 1 000 g × 0,02 = 20 ÷ CA 200 = 0,1 (ni le fromage ni la moutarde n'y entrent).
        Assert.Equal(0.1m, stats.ActualFoodCost);
    }

    [Fact]
    public async Task Variance_BoughtButNeverCounted_ListedAsNotCounted()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var truffe = await AddIngredientAsync(options, restaurant, "Truffe", price: 1m);                     // achetée, jamais comptée
        var ail = await AddIngredientAsync(options, restaurant, "Ail", price: 0.004m);                       // utilisé par une recette vendue, jamais compté
        var poivre = await AddIngredientAsync(options, restaurant, "Poivre", price: 0.01m);                  // rien de rien : ne figure nulle part
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m), Line(ail, 5m));
        var service = CreateService(options, restaurant);

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 9000m));
        await AddReceiptAsync(options, restaurant, Oct10, ReceiptStatus.Applied, Item(truffe, 100m, MeasureUnit.G, 100m));
        await AddEntryAsync(options, restaurant, burger, Oct12, 10m);

        var stats = await service.GetAsync(Oct10, Oct20);

        var variance = stats.Variance;
        Assert.NotNull(variance);
        Assert.Equal(boeuf, Assert.Single(variance.Rows).IngredientId);
        Assert.Equal(["Ail", "Truffe"], variance.NotCountedIngredients);   // triés ; le poivre n'y est pas
        Assert.DoesNotContain("Poivre", variance.NotCountedIngredients);
        Assert.Empty(variance.Warnings);
    }

    [Fact]
    public async Task Variance_WeeklyTotalInWindow_CountsInRevenueAndTheoreticalUse()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 150m));
        var service = CreateService(options, restaurant);

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 7000m));

        // Totaux de semaine (datés du lundi, hors période demandée du 10 au 20) : seul celui du lundi 5, dans ]1er, 31], compte.
        await AddEntryAsync(options, restaurant, burger, Oct5, 20m, ServicePeriod.Journee, weeklyTotal: true);
        await AddEntryAsync(options, restaurant, burger, Day(9, 28), 100m, ServicePeriod.Journee, weeklyTotal: true);   // lundi 28/9 : avant A
        await AddEntryAsync(options, restaurant, burger, Day(11, 2), 100m, ServicePeriod.Journee, weeklyTotal: true);   // lundi 2/11 : après B

        var stats = await service.GetAsync(Oct10, Oct20);

        // 20 portions × 150 g = 3 000 g théoriques = 10 000 − 7 000 réels : écart nul ; CA de la fenêtre 20 × 20 = 400.
        var variance = stats.Variance;
        Assert.NotNull(variance);
        var row = Assert.Single(variance.Rows);
        Assert.Equal(3000m, row.TheoreticalUse);
        Assert.Equal(3000m, row.ActualUse);
        Assert.Equal(0m, row.VarianceQuantity);
        Assert.Equal(0.15m, stats.ActualFoodCost);         // 3 000 × 0,02 = 60 ÷ 400
        Assert.Equal(0m, stats.RevenueExclVat);            // la période du 10 au 20 ne contient aucun de ces lundis
    }

    [Fact]
    public async Task Variance_IncompleteRecipe_AddsWarning()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var lait = await AddIngredientAsync(options, restaurant, "Lait", price: 0.001m);
        var service = CreateService(options, restaurant);

        // Une ligne en millilitres sur un ingrédient en grammes : la décomposition l'ignore et le signale.
        var soupe = await AddDishAsync(options, restaurant, "Soupe", Line(boeuf, 100m), Line(boeuf, 50m, MeasureUnit.Ml));
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m));
        var gratin = await AddDishAsync(options, restaurant, "Gratin", Line(lait, 100m, MeasureUnit.G), Line(lait, 1m, MeasureUnit.Piece));

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m), (lait, 5000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 8000m), (lait, 4000m));

        // La soupe est vendue plusieurs fois (saisies et commande) : un seul avertissement. Une recette complète n'en produit aucun.
        await AddEntryAsync(options, restaurant, soupe, Oct12, 5m);
        await AddEntryAsync(options, restaurant, soupe, Oct12, 5m, ServicePeriod.Soir);
        await AddOrderAsync(options, restaurant, Day(10, 14, 12, 0), OrderStatus.Servie, (soupe, 2, 22.40m, 0.12m));
        await AddEntryAsync(options, restaurant, burger, Oct12, 5m);
        // Un plat perdu dont la décomposition est incomplète donne aussi son avertissement.
        await AddWasteAsync(options, restaurant, Oct20, null, gratin, 1m, MeasureUnit.Piece, WasteReason.Casse);

        var stats = await service.GetAsync(Oct10, Oct20);

        var variance = stats.Variance;
        Assert.NotNull(variance);
        Assert.Equal(
            [
                "Recette « Gratin » incomplète : consommation théorique sous-estimée",
                "Recette « Soupe » incomplète : consommation théorique sous-estimée"
            ],
            variance.Warnings);
        // La partie décomposable compte quand même : soupe 12 × 100 g + burger 5 × 100 g = 1 700 g de boeuf ; gratin 1 × 100 g de lait.
        Assert.Equal(1700m, variance.Rows.Single(r => r.IngredientId == boeuf).TheoreticalUse);
        Assert.Equal(100m, variance.Rows.Single(r => r.IngredientId == lait).TheoreticalUse);
    }

    [Fact]
    public async Task Variance_RecipeWithoutLines_AddsWarning_NamingTheEmptyRecipe()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m));
        var vide = await AddDishAsync(options, restaurant, "Recette vide");
        var fondVide = await AddPreparationAsync(options, restaurant, "Fond vide", 1000m, BaseUnit.Milliliter);
        var surFond = await AddDishAsync(options, restaurant, "Sur fond", Line(boeuf, 50m), new RecipeLine { SubRecipeId = fondVide, Quantity = 200m, Unit = MeasureUnit.Ml });
        var jamaisVendue = await AddDishAsync(options, restaurant, "Jamais vendue");               // sans ligne, mais hors de l'intervalle : aucun avertissement
        var service = CreateService(options, restaurant);

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 9000m));
        await AddEntryAsync(options, restaurant, burger, Oct12, 5m);
        await AddEntryAsync(options, restaurant, vide, Oct12, 3m);                                  // vendue plusieurs fois : un seul avertissement
        await AddEntryAsync(options, restaurant, vide, Oct12, 2m, ServicePeriod.Soir);
        await AddEntryAsync(options, restaurant, surFond, Oct15, 2m);
        await AddEntryAsync(options, restaurant, jamaisVendue, Oct1, 9m);                           // le jour de A : hors de ]A, B]

        var stats = await service.GetAsync(Oct10, Oct20);

        var variance = stats.Variance;
        Assert.NotNull(variance);
        Assert.Equal(
            [
                "Recette « Fond vide » sans ligne : consommation théorique inconnue",
                "Recette « Recette vide » sans ligne : consommation théorique inconnue"
            ],
            variance.Warnings);
        // Ce qui est décomposable compte quand même : burger 5 × 100 g + « Sur fond » 2 × 50 g.
        Assert.Equal(600m, Assert.Single(variance.Rows).TheoreticalUse);
    }

    [Fact]
    public async Task Variance_PreparationWithoutLinesLostAsWaste_AddsWarning()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var fondVide = await AddPreparationAsync(options, restaurant, "Fond vide", 1000m, BaseUnit.Milliliter);
        var service = CreateService(options, restaurant);

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 10000m));
        await AddWasteAsync(options, restaurant, Oct20, null, fondVide, 200m, MeasureUnit.Ml, WasteReason.Perime);

        var variance = (await service.GetAsync(Oct10, Oct20)).Variance;

        Assert.NotNull(variance);
        Assert.Equal(["Recette « Fond vide » sans ligne : consommation théorique inconnue"], variance.Warnings);
    }

    [Fact]
    public async Task Variance_UsedButNotCountedAtBothCounts_MarksTheActualFoodCostPartial()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var fromage = await AddIngredientAsync(options, restaurant, "Fromage", price: 0.01m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m));
        var service = CreateService(options, restaurant);

        // Le fromage est acheté mais oublié à la clôture : sa consommation réelle est inconnue, le coût réel l'ignore.
        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m), (fromage, 500m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 9000m));
        await AddReceiptAsync(options, restaurant, Oct10, ReceiptStatus.Applied, Item(fromage, 3m, MeasureUnit.Kg, 30m));
        await AddEntryAsync(options, restaurant, burger, Oct12, 10m);

        var stats = await service.GetAsync(Oct10, Oct20);

        Assert.NotNull(stats.ActualFoodCost);
        Assert.True(stats.ActualFoodCostPartial);
        Assert.True(stats.Variance!.ActualFoodCostPartial);
    }

    [Fact]
    public async Task Variance_UsedTheoreticallyButNeverCounted_MarksTheActualFoodCostPartial()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var ail = await AddIngredientAsync(options, restaurant, "Ail", price: 0.004m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m), Line(ail, 5m));    // l'ail est vendu mais jamais compté
        var service = CreateService(options, restaurant);

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 9000m));
        await AddEntryAsync(options, restaurant, burger, Oct12, 10m);

        var stats = await service.GetAsync(Oct10, Oct20);

        Assert.Equal(["Ail"], stats.Variance!.NotCountedIngredients);
        Assert.True(stats.ActualFoodCostPartial);
    }

    [Fact]
    public async Task Variance_NotCountedButUnused_DoesNotMarkTheActualFoodCostPartial()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var moutarde = await AddIngredientAsync(options, restaurant, "Moutarde", price: 0.005m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m));
        var service = CreateService(options, restaurant);

        // La moutarde n'est comptée qu'à l'ouverture, sans achat ni consommation : elle est nommée à part, mais le coût réel n'a rien perdu.
        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m), (moutarde, 200m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 9000m));
        await AddEntryAsync(options, restaurant, burger, Oct12, 10m);

        var stats = await service.GetAsync(Oct10, Oct20);

        Assert.Equal(["Moutarde"], stats.Variance!.NotCountedIngredients);
        Assert.NotNull(stats.ActualFoodCost);
        Assert.False(stats.ActualFoodCostPartial);
        Assert.False(stats.Variance.ActualFoodCostPartial);
    }

    [Fact]
    public async Task Variance_EverythingCountedAtBoth_OrNoWindow_IsNotPartial()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var burger = await AddDishAsync(options, restaurant, "Burger", Line(boeuf, 100m));
        var service = CreateService(options, restaurant);

        await AddEntryAsync(options, restaurant, burger, Oct12, 10m);
        Assert.False((await service.GetAsync(Oct10, Oct20)).ActualFoodCostPartial);        // pas d'inventaires : pas de coût réel, rien de partiel

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 9000m));

        var stats = await service.GetAsync(Oct10, Oct20);
        Assert.NotNull(stats.ActualFoodCost);
        Assert.False(stats.ActualFoodCostPartial);
    }

    [Fact]
    public async Task Variance_ReceiptLineWithAnUnconvertibleUnit_AddsWarning_AndIsLeftOutOfPurchases()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var service = CreateService(options, restaurant);

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 11000m));

        // Une ligne de ticket dont l'unité ne se convertit plus (l'ingrédient est en grammes) est écartée et signalée ; une ligne convertible compte ;
        // une ligne ignorée à la validation n'est pas un achat et ne fait pas d'avertissement ; deux lignes identiques ne donnent qu'un avertissement.
        await AddReceiptAsync(options, restaurant, Oct12, ReceiptStatus.Applied,
            Labeled("BOEUF HACHE 1L", boeuf, 1m, MeasureUnit.L, 18m),
            Labeled("BOEUF HACHE 1L", boeuf, 1m, MeasureUnit.L, 18m),
            Labeled("BOEUF PIECE", boeuf, 2m, MeasureUnit.Kg, 36m),
            Labeled("SACS POUBELLE", boeuf, 5m, MeasureUnit.L, 9m, ignored: true));
        await AddReceiptAsync(options, restaurant, Oct10, ReceiptStatus.Applied, Labeled("BOEUF A LA PIECE", boeuf, 4m, MeasureUnit.Piece, 40m));
        await AddReceiptAsync(options, restaurant, Oct12, ReceiptStatus.Extracted, Labeled("BROUILLON", boeuf, 1m, MeasureUnit.L, 5m));   // pas appliqué : hors achats

        var stats = await service.GetAsync(Oct10, Oct20);

        var variance = stats.Variance;
        Assert.NotNull(variance);
        Assert.Equal(
            [
                "Ligne de ticket « BOEUF A LA PIECE » du 10/10/2026 ignorée : unité non convertible",
                "Ligne de ticket « BOEUF HACHE 1L » du 12/10/2026 ignorée : unité non convertible"
            ],
            variance.Warnings);
        var row = Assert.Single(variance.Rows);
        Assert.Equal(2000m, row.Purchases);                // seule la ligne en kilos compte
    }

    [Fact]
    public async Task Variance_UnconvertibleIngredientWaste_AddsWarning()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var service = CreateService(options, restaurant);

        await AddCountAsync(options, restaurant, Oct1, StockCountStatus.Cloture, (boeuf, 10000m));
        await AddCountAsync(options, restaurant, Oct31, StockCountStatus.Cloture, (boeuf, 9000m));

        // Perte saisie en millilitres sur un ingrédient en grammes (l'ingrédient a changé d'unité depuis) : ignorée, signalée une fois par jour et ingrédient.
        await AddWasteAsync(options, restaurant, Oct20, boeuf, null, 1m, MeasureUnit.Ml, WasteReason.Perime);
        await AddWasteAsync(options, restaurant, Oct20, boeuf, null, 2m, MeasureUnit.L, WasteReason.Casse);
        await AddWasteAsync(options, restaurant, Oct22, boeuf, null, 1m, MeasureUnit.Ml, WasteReason.Perime);
        await AddWasteAsync(options, restaurant, Oct25, boeuf, null, 1m, MeasureUnit.Kg, WasteReason.Perime);          // convertible : 1 000 g

        var stats = await service.GetAsync(Oct10, Oct20);

        var variance = stats.Variance;
        Assert.NotNull(variance);
        Assert.Equal(
            [
                "Perte du 20/10/2026 sur « Boeuf » ignorée : unité non convertible",
                "Perte du 22/10/2026 sur « Boeuf » ignorée : unité non convertible"
            ],
            variance.Warnings);
        Assert.Equal(1000m, Assert.Single(variance.Rows).TheoreticalUse);     // seule la perte convertible compte
        // Les pertes du 20 (période du 10 au 20) ont un coût inconnu, leur unité ne se convertit plus : les motifs Perime et Casse sont incomplets.
        Assert.All(stats.Waste, w => Assert.True(w.CostIncomplete));
        Assert.Equal(0m, stats.WasteCost);
        Assert.True(stats.WasteCostIncomplete);
    }

    [Fact]
    public async Task Waste_IncompleteRecipeCost_FlaggedInStats()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        var truffe = await AddIngredientAsync(options, restaurant, "Truffe");                                  // aucun prix
        var complet = await AddDishAsync(options, restaurant, "Complet", Line(boeuf, 100m));                    // 2,00
        var incomplet = await AddDishAsync(options, restaurant, "Incomplet", Line(boeuf, 100m), Line(truffe, 5m)); // 2,00 chiffrés seulement
        var service = CreateService(options, restaurant);

        await AddWasteAsync(options, restaurant, Oct5, null, complet, 1m, MeasureUnit.Piece, WasteReason.Casse);      // 2,00, complet
        await AddWasteAsync(options, restaurant, Oct6, null, incomplet, 3m, MeasureUnit.Piece, WasteReason.Erreur);   // 3 × 2,00 = 6,00 partiel
        await AddWasteAsync(options, restaurant, Oct7, boeuf, null, 100m, MeasureUnit.G, WasteReason.Perime);         // 2,00, complet

        var stats = await service.GetAsync(Oct1, Oct15);

        Assert.Equal(
            [(WasteReason.Erreur, 6m, 1, true), (WasteReason.Perime, 2m, 1, false), (WasteReason.Casse, 2m, 1, false)],   // à coût égal, par motif
            stats.Waste.Select(w => (w.Reason, w.Cost, w.Count, w.CostIncomplete)));
        Assert.Equal(10m, stats.WasteCost);                // le coût partiel entre dans le total
        Assert.True(stats.WasteCostIncomplete);

        // Sans la perte incomplète, rien n'est signalé.
        var complete = await service.GetAsync(Oct1, Oct5);
        Assert.Equal(2m, complete.WasteCost);
        Assert.False(complete.WasteCostIncomplete);
        Assert.False(Assert.Single(complete.Waste).CostIncomplete);
    }

    // ── Isolation et saisie ──────────────────────────────────────────────────

    [Fact]
    public async Task OtherRestaurant_DataNeverCounted()
    {
        var options = await postgres.CreateDatabaseAsync();
        var mine = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");

        // L'autre restaurant a des ventes, des commandes, des pertes, deux inventaires et un ticket sur la même période.
        var otherBeef = await AddIngredientAsync(options, other, "Boeuf", price: 0.02m);
        var otherDish = await AddDishAsync(options, other, "Burger", Line(otherBeef, 100m));
        await AddEntryAsync(options, other, otherDish, Oct12, 50m);
        await AddOrderAsync(options, other, Day(10, 12, 12, 0), OrderStatus.Servie, (otherDish, 5, 22.40m, 0.12m));
        await AddWasteAsync(options, other, Oct12, otherBeef, null, 1m, MeasureUnit.Kg, WasteReason.Perime);
        await AddCountAsync(options, other, Oct1, StockCountStatus.Cloture, (otherBeef, 1000m));
        await AddCountAsync(options, other, Oct31, StockCountStatus.Cloture, (otherBeef, 500m));
        await AddReceiptAsync(options, other, Oct10, ReceiptStatus.Applied, Item(otherBeef, 5m, MeasureUnit.Kg, 90m));

        // Le mien : une seule vente.
        var myBeef = await AddIngredientAsync(options, mine, "Boeuf", price: 0.02m);
        var myDish = await AddDishAsync(options, mine, "Pâtes", Line(myBeef, 50m));
        await AddEntryAsync(options, mine, myDish, Oct12, 3m);                                   // 3 × 20 = 60, coût 3 × 1 = 3

        var stats = await CreateService(options, mine).GetAsync(Oct10, Oct20);

        Assert.Equal(60m, stats.RevenueExclVat);
        Assert.Equal(0m, stats.RevenueFromOrders);
        Assert.Equal(3m, stats.TheoreticalCost);
        var dish = Assert.Single(stats.Dishes);
        Assert.Equal(myDish, dish.RecipeId);
        Assert.Empty(stats.Waste);
        Assert.Equal(0m, stats.WasteCost);
        Assert.Null(stats.Variance);                       // les inventaires de l'autre restaurant n'encadrent rien chez moi
        Assert.Null(stats.ActualFoodCost);

        // Et chez l'autre restaurant, rien des miens : 50 × 20 + 5 × 20 = 1 100.
        var otherStats = await CreateService(options, other).GetAsync(Oct10, Oct20);
        Assert.Equal(1100m, otherStats.RevenueExclVat);
        Assert.Equal(otherDish, Assert.Single(otherStats.Dishes).RecipeId);
        Assert.NotNull(otherStats.Variance);
    }

    [Fact]
    public async Task Period_InvalidOrTooLong_Rejected()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var reversed = await Assert.ThrowsAsync<DomainValidationException>(() => service.GetAsync(Oct10, Oct5));
        Assert.Equal(InvalidPeriodMessage, reversed.Message);

        // 366 jours (bornes comprises) : accepté ; 367 : refusé.
        var januaryFirst = Day(1, 1);
        var year = await service.GetAsync(januaryFirst, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(366, year.RevenueByDay.Count);

        var tooLong = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.GetAsync(januaryFirst, new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(PeriodTooLongMessage, tooLong.Message);

        // Même jour : une période d'un jour, sans vente.
        var oneDay = await service.GetAsync(Oct10, Day(10, 10, 23, 59, 59));
        Assert.Single(oneDay.RevenueByDay);
        Assert.Equal(0m, oneDay.RevenueExclVat);
        Assert.Null(oneDay.TheoreticalFoodCost);           // pas de CA
        Assert.Empty(oneDay.Dishes);
        Assert.Empty(oneDay.Waste);
    }

    // ── Aides ────────────────────────────────────────────────────────────────

    private static DateTime Day(int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(2026, month, day, hour, minute, second, DateTimeKind.Utc);

    private static StatsService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    private static ChefOsDbContext OpenContext(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new TestContextFactory(options, restaurantId).CreateDbContext();

    private static async Task<int> AddIngredientAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name, decimal? price = null, decimal yieldPercent = 100m)
    {
        await using var db = OpenContext(options, restaurantId);
        var ingredient = new Ingredient { Name = name, BaseUnit = BaseUnit.Gram, CurrentPricePerBaseUnit = price, YieldPercent = yieldPercent };
        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync();
        return ingredient.Id;
    }

    private static async Task AddPriceAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, int ingredientId, DateTime date, decimal price)
    {
        await using var db = OpenContext(options, restaurantId);
        db.IngredientPrices.Add(new IngredientPrice { IngredientId = ingredientId, Date = date, PricePerBaseUnit = price, Source = PriceSource.Manual });
        await db.SaveChangesAsync();
    }

    private static RecipeLine Line(int ingredientId, decimal quantity, MeasureUnit unit = MeasureUnit.G) =>
        new() { IngredientId = ingredientId, Quantity = quantity, Unit = unit };

    private static async Task<int> AddDishAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name, params RecipeLine[] lines)
    {
        await using var db = OpenContext(options, restaurantId);
        var recipe = new Recipe
        {
            Name = name,
            Kind = RecipeKind.Dish,
            SellingPriceInclVat = 22.40m,
            VatRate = 0.12m,
            Lines = lines.ToList()
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    private static async Task<int> AddPreparationAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name, decimal outputQuantity, BaseUnit outputUnit, params RecipeLine[] lines)
    {
        await using var db = OpenContext(options, restaurantId);
        var recipe = new Recipe
        {
            Name = name,
            Kind = RecipeKind.Preparation,
            OutputQuantity = outputQuantity,
            OutputUnit = outputUnit,
            Lines = lines.ToList()
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    /// <summary>Une saisie de ventes ; par défaut 22,40 TTC à 12 %, soit 20,00 HT la portion.</summary>
    private static async Task AddEntryAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        int recipeId,
        DateTime date,
        decimal quantity,
        ServicePeriod service = ServicePeriod.Midi,
        bool weeklyTotal = false,
        decimal price = 22.40m,
        decimal vat = 0.12m)
    {
        await using var db = OpenContext(options, restaurantId);
        db.SalesEntries.Add(new SalesEntry
        {
            RecipeId = recipeId,
            Date = date,
            Service = service,
            IsWeeklyTotal = weeklyTotal,
            Quantity = quantity,
            UnitPriceInclVat = price,
            VatRate = vat,
            UpdatedAt = date
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddOrderAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        DateTime createdAt,
        OrderStatus status,
        params (int RecipeId, int Quantity, decimal PriceInclVat, decimal VatRate)[] lines)
    {
        await using var db = OpenContext(options, restaurantId);
        db.Orders.Add(new Order
        {
            Number = await db.Orders.CountAsync() + 1,
            CreatedAt = createdAt,
            StatusChangedAt = createdAt,
            Source = OrderSource.Salle,
            Status = status,
            Lines = lines.Select(l => new OrderLine { RecipeId = l.RecipeId, Quantity = l.Quantity, UnitPriceInclVat = l.PriceInclVat, VatRate = l.VatRate }).ToList()
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddWasteAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        DateTime date,
        int? ingredientId,
        int? recipeId,
        decimal quantity,
        MeasureUnit unit,
        WasteReason reason)
    {
        await using var db = OpenContext(options, restaurantId);
        db.WasteEntries.Add(new WasteEntry
        {
            Date = date,
            IngredientId = ingredientId,
            RecipeId = recipeId,
            Quantity = quantity,
            Unit = unit,
            Reason = reason,
            CreatedAt = date
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Un inventaire avec, pour chaque ingrédient, la quantité de base comptée (en grammes).</summary>
    private static async Task AddCountAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        DateTime date,
        StockCountStatus status,
        params (int IngredientId, decimal BaseQuantity)[] lines)
    {
        await using var db = OpenContext(options, restaurantId);
        db.StockCounts.Add(new StockCount
        {
            Date = date,
            Status = status,
            ClosedAt = status == StockCountStatus.Cloture ? date : null,
            Lines = lines.Select(l => new StockCountLine
            {
                IngredientId = l.IngredientId,
                Quantity = l.BaseQuantity,
                Unit = MeasureUnit.G,
                BaseQuantity = l.BaseQuantity
            }).ToList()
        });
        await db.SaveChangesAsync();
    }

    private static ReceiptLine Item(int? ingredientId, decimal quantity, MeasureUnit unit, decimal totalExclVat, bool ignored = false) =>
        new()
        {
            RawLabel = "ligne",
            IngredientId = ingredientId,
            Quantity = quantity,
            Unit = unit,
            LineTotal = totalExclVat,
            LineTotalExclVat = totalExclVat,
            IsIgnored = ignored
        };

    private static ReceiptLine Labeled(string label, int? ingredientId, decimal quantity, MeasureUnit unit, decimal totalExclVat, bool ignored = false)
    {
        var line = Item(ingredientId, quantity, unit, totalExclVat, ignored);
        line.RawLabel = label;
        return line;
    }

    /// <summary>Un ticket appliqué sans date lue : seule sa date de création (<paramref name="createdAt"/>) le situe, comme pour le prix qu'il a posé.</summary>
    private static async Task AddUndatedReceiptAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, DateTime createdAt, params ReceiptLine[] lines)
    {
        await using var db = OpenContext(options, restaurantId);
        for (var index = 0; index < lines.Length; index++)
            lines[index].Position = index + 1;

        db.Receipts.Add(new Receipt
        {
            ReceiptDate = null,
            Status = ReceiptStatus.Applied,
            CreatedAt = createdAt,
            AppliedAt = createdAt,
            Lines = lines.ToList()
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddReceiptAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, DateTime date, ReceiptStatus status, params ReceiptLine[] lines)
    {
        await using var db = OpenContext(options, restaurantId);
        for (var index = 0; index < lines.Length; index++)
            lines[index].Position = index + 1;

        db.Receipts.Add(new Receipt
        {
            ReceiptDate = date,
            Status = status,
            CreatedAt = date,
            AppliedAt = status == ReceiptStatus.Applied ? date : null,
            Lines = lines.ToList()
        });
        await db.SaveChangesAsync();
    }
}
