using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class WasteServiceTests(PostgresFixture postgres)
{
    private const string TargetMessage = "Choisissez un ingrédient ou une recette";
    private const string QuantityMessage = "La quantité doit être supérieure à 0";
    private const string QuantityTooHighMessage = "Quantité trop élevée";
    private const string FutureMessage = "La date ne peut pas être dans le futur";
    private const string IncompatibleUnitMessage = "Unité incompatible";
    private const string UnknownIngredientMessage = "Ingrédient introuvable";
    private const string UnknownRecipeMessage = "Recette introuvable";
    private const string UnknownWasteMessage = "Perte introuvable";
    private const string ReasonMessage = "Motif invalide";

    private static readonly DateTime Oct15 = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Oct31 = new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Nov1 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Nov2 = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Nov3 = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static WasteService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    private static ChefOsDbContext OpenContext(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new TestContextFactory(options, restaurantId).CreateDbContext();

    private static async Task<int> AddIngredientAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        string name,
        BaseUnit baseUnit = BaseUnit.Gram,
        decimal? price = null,
        decimal yieldPercent = 100m,
        decimal? pieceWeight = null,
        bool archived = false)
    {
        await using var db = OpenContext(options, restaurantId);
        var ingredient = new Ingredient
        {
            Name = name,
            BaseUnit = baseUnit,
            CurrentPricePerBaseUnit = price,
            YieldPercent = yieldPercent,
            AveragePieceWeightGrams = pieceWeight,
            IsArchived = archived
        };
        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync();
        return ingredient.Id;
    }

    private static async Task AddPriceAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, int ingredientId, DateTime date, decimal price)
    {
        await using var db = OpenContext(options, restaurantId);
        db.IngredientPrices.Add(new IngredientPrice
        {
            IngredientId = ingredientId,
            Date = date,
            PricePerBaseUnit = price,
            Source = PriceSource.Manual
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> AddRecipeAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        string name,
        RecipeKind kind,
        decimal? outputQuantity = null,
        BaseUnit? outputUnit = null,
        params RecipeLine[] lines)
    {
        await using var db = OpenContext(options, restaurantId);
        var recipe = new Recipe
        {
            Name = name,
            Kind = kind,
            OutputQuantity = outputQuantity,
            OutputUnit = outputUnit,
            Lines = lines.ToList()
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    private static RecipeLine IngredientLine(int ingredientId, decimal quantity, MeasureUnit unit = MeasureUnit.G) =>
        new() { IngredientId = ingredientId, Quantity = quantity, Unit = unit };

    private static RecipeLine PreparationLine(int subRecipeId, decimal quantity, MeasureUnit unit = MeasureUnit.G) =>
        new() { SubRecipeId = subRecipeId, Quantity = quantity, Unit = unit };

    private static WasteInput Waste(
        DateTime date,
        int? ingredientId = null,
        int? recipeId = null,
        decimal quantity = 1m,
        MeasureUnit unit = MeasureUnit.G,
        WasteReason reason = WasteReason.Perime,
        string? note = null) =>
        new(date, ingredientId, recipeId, quantity, unit, reason, note);

    private static async Task<List<WasteEntry>> LoadEntriesAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId)
    {
        await using var db = OpenContext(options, restaurantId);
        return await db.WasteEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    // ── AddAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Waste_Add_ValidatesTargetUnitDate()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");
        var service = CreateService(options, restaurant);

        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf");                                  // en g, sans poids de pièce
        var oeuf = await AddIngredientAsync(options, restaurant, "Oeuf", pieceWeight: 60m);                  // en g, 60 g la pièce
        var lait = await AddIngredientAsync(options, restaurant, "Lait", BaseUnit.Milliliter);
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare", RecipeKind.Dish);
        var sauce = await AddRecipeAsync(options, restaurant, "Sauce", RecipeKind.Preparation, 1000m, BaseUnit.Gram);
        var foreignIngredient = await AddIngredientAsync(options, other, "Homard");
        var foreignDish = await AddRecipeAsync(options, other, "Plat d'ailleurs", RecipeKind.Dish);

        async Task Refused(string message, WasteInput input)
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.AddAsync(input));
            Assert.Equal(message, ex.Message);
        }

        // Exactement une cible.
        await Refused(TargetMessage, Waste(Oct15));
        await Refused(TargetMessage, Waste(Oct15, ingredientId: boeuf, recipeId: tartare, unit: MeasureUnit.Piece));

        // Quantité strictement positive, plafonnée à 100 000.
        await Refused(QuantityMessage, Waste(Oct15, ingredientId: boeuf, quantity: 0m));
        await Refused(QuantityMessage, Waste(Oct15, ingredientId: boeuf, quantity: -1m));
        await Refused(QuantityMessage, Waste(Oct15, ingredientId: boeuf, quantity: 0.00001m)); // arrondie à 0 par la colonne
        await Refused(QuantityTooHighMessage, Waste(Oct15, ingredientId: boeuf, quantity: 100_000.0001m));

        // Pas dans le futur : demain reste permis (fuseau du chef), après-demain non.
        var tomorrow = DateTime.UtcNow.Date.AddDays(1);
        await Refused(FutureMessage, Waste(tomorrow.AddDays(1), ingredientId: boeuf));
        await Refused(FutureMessage, Waste(DateTime.UtcNow.AddYears(1), recipeId: tartare, unit: MeasureUnit.Piece));

        // Ingrédient : l'unité doit se convertir dans l'unité de base.
        await Refused(IncompatibleUnitMessage, Waste(Oct15, ingredientId: boeuf, unit: MeasureUnit.Ml));
        await Refused(IncompatibleUnitMessage, Waste(Oct15, ingredientId: boeuf, unit: MeasureUnit.Piece));   // pas de poids de pièce
        await Refused(IncompatibleUnitMessage, Waste(Oct15, ingredientId: lait, unit: MeasureUnit.G));

        // Plat : des portions, donc Piece uniquement.
        await Refused(IncompatibleUnitMessage, Waste(Oct15, recipeId: tartare, unit: MeasureUnit.Kg));
        await Refused(IncompatibleUnitMessage, Waste(Oct15, recipeId: tartare, unit: MeasureUnit.G));

        // Préparation : l'unité doit se convertir dans son unité de production (grammes).
        await Refused(IncompatibleUnitMessage, Waste(Oct15, recipeId: sauce, unit: MeasureUnit.L));
        await Refused(IncompatibleUnitMessage, Waste(Oct15, recipeId: sauce, unit: MeasureUnit.Piece));

        // La cible appartient au restaurant.
        await Refused(UnknownIngredientMessage, Waste(Oct15, ingredientId: foreignIngredient));
        await Refused(UnknownIngredientMessage, Waste(Oct15, ingredientId: 999_999));
        await Refused(UnknownRecipeMessage, Waste(Oct15, recipeId: foreignDish, unit: MeasureUnit.Piece));
        await Refused(UnknownRecipeMessage, Waste(Oct15, recipeId: 999_999, unit: MeasureUnit.Piece));

        // Motif connu.
        await Refused(ReasonMessage, Waste(Oct15, ingredientId: boeuf, reason: (WasteReason)99));

        Assert.Empty(await LoadEntriesAsync(options, restaurant));
        Assert.Empty(await LoadEntriesAsync(options, other));

        // Les cas valides passent : unités compatibles, demain, plafond exact.
        await service.AddAsync(Waste(Oct15, ingredientId: boeuf, unit: MeasureUnit.Kg));
        await service.AddAsync(Waste(Oct15, ingredientId: oeuf, quantity: 3m, unit: MeasureUnit.Piece));      // 3 × 60 g
        await service.AddAsync(Waste(Oct15, ingredientId: lait, unit: MeasureUnit.Cl));
        await service.AddAsync(Waste(Oct15, recipeId: tartare, quantity: 2m, unit: MeasureUnit.Piece));
        await service.AddAsync(Waste(Oct15, recipeId: sauce, unit: MeasureUnit.Kg));
        await service.AddAsync(Waste(tomorrow, ingredientId: boeuf));
        await service.AddAsync(Waste(Oct15, ingredientId: boeuf, quantity: 100_000m));
        Assert.Equal(7, (await LoadEntriesAsync(options, restaurant)).Count);
    }

    [Fact]
    public async Task Waste_Add_StoresEntryOnTheUtcDay()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.01m);

        var before = DateTime.UtcNow;
        var id = await service.AddAsync(Waste(
            new DateTime(2026, 11, 3, 15, 30, 0, DateTimeKind.Unspecified),
            ingredientId: boeuf,
            quantity: 1.23456m,
            unit: MeasureUnit.Kg,
            reason: WasteReason.Casse,
            note: "  tombé du plan de travail  "));
        var after = DateTime.UtcNow;

        var entry = Assert.Single(await LoadEntriesAsync(options, restaurant));
        Assert.Equal(id, entry.Id);
        Assert.Equal(restaurant, entry.RestaurantId);
        Assert.Equal(Nov3, entry.Date);
        Assert.Equal(DateTimeKind.Utc, entry.Date.Kind);
        Assert.Equal(boeuf, entry.IngredientId);
        Assert.Null(entry.RecipeId);
        Assert.Equal(1.2346m, entry.Quantity);   // arrondie à l'échelle de la colonne
        Assert.Equal(MeasureUnit.Kg, entry.Unit);
        Assert.Equal(WasteReason.Casse, entry.Reason);
        Assert.Equal("tombé du plan de travail", entry.Note);
        Assert.InRange(entry.CreatedAt, before, after);

        // Une note vide n'est pas conservée.
        await service.AddAsync(Waste(Nov3, ingredientId: boeuf, note: "   "));
        Assert.Null((await LoadEntriesAsync(options, restaurant))[1].Note);
    }

    [Fact]
    public async Task Waste_Add_AcceptsArchivedTargets()
    {
        // Un produit retiré de la carte peut encore périmer au frigo : on peut déclarer sa perte.
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var archived = await AddIngredientAsync(options, restaurant, "Truffe", archived: true);

        await service.AddAsync(Waste(Nov3, ingredientId: archived));

        Assert.Single(await LoadEntriesAsync(options, restaurant));
    }

    // ── DeleteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Waste_Delete_RemovesTheEntry_AndRefusesUnknownId()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf");
        var first = await service.AddAsync(Waste(Nov3, ingredientId: boeuf));
        var second = await service.AddAsync(Waste(Nov3, ingredientId: boeuf));

        await service.DeleteAsync(first);

        Assert.Equal([second], (await LoadEntriesAsync(options, restaurant)).Select(e => e.Id));

        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(999_999));
        var again = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(first));
        Assert.Equal(UnknownWasteMessage, unknown.Message);
        Assert.Equal(UnknownWasteMessage, again.Message);
    }

    // ── ListAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Waste_List_CostsIngredientAtDatePrice_NoYield()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        // Rendement 65 % : une perte se chiffre sur le produit tel qu'acheté, le rendement n'y entre pas.
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m, yieldPercent: 65m);
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), 0.01m);
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc), 0.02m);
        var sel = await AddIngredientAsync(options, restaurant, "Sel");                                      // aucun prix
        var oeuf = await AddIngredientAsync(options, restaurant, "Oeuf", BaseUnit.Piece, 0.30m, pieceWeight: 60m); // prix courant, sans historique

        await service.AddAsync(Waste(Oct15, ingredientId: boeuf, quantity: 500m, reason: WasteReason.Perime));
        await service.AddAsync(Waste(Oct31, ingredientId: boeuf, quantity: 1m, unit: MeasureUnit.Kg, reason: WasteReason.Casse, note: "bac renversé"));
        await service.AddAsync(Waste(Nov1, ingredientId: boeuf, quantity: 2m, unit: MeasureUnit.Kg));        // prix de 18 h le jour même
        await service.AddAsync(Waste(Nov3, ingredientId: sel, quantity: 200m));
        await service.AddAsync(Waste(Nov3, ingredientId: oeuf, quantity: 4m, unit: MeasureUnit.Piece));
        await service.AddAsync(Waste(Nov2, ingredientId: oeuf, quantity: 120m, unit: MeasureUnit.G));       // 2 pièces

        var items = await service.ListAsync(Oct15, Nov3);

        Assert.Equal(6, items.Count);
        // Date décroissante.
        Assert.Equal([Nov3, Nov3, Nov2, Nov1, Oct31, Oct15], items.Select(i => i.Date));
        Assert.All(items, i => Assert.Equal(DateTimeKind.Utc, i.Date.Kind));
        Assert.All(items, i => Assert.False(i.IsRecipe));
        Assert.All(items, i => Assert.False(i.CostIncomplete));

        // À dates égales, la dernière saisie d'abord.
        Assert.Equal(["Oeuf", "Sel", "Oeuf", "Boeuf", "Boeuf", "Boeuf"], items.Select(i => i.ItemName));

        Assert.Equal(1.2m, items[0].Cost);          // 4 pièces × 0,30
        Assert.Null(items[1].Cost);                 // sel : aucun prix connu
        Assert.Equal(0.6m, items[2].Cost);          // 120 g = 2 pièces × 0,30
        Assert.Equal(40m, items[3].Cost);           // 2 000 g × 0,02 (entrée de 18 h le jour même)
        Assert.Equal(10m, items[4].Cost);           // 1 000 g × 0,01
        Assert.Equal(5m, items[5].Cost);            // 500 g × 0,01, sans rendement (avec 65 % : 7,69)

        Assert.Equal(WasteReason.Casse, items[4].Reason);
        Assert.Equal("bac renversé", items[4].Note);
        Assert.Equal(1m, items[4].Quantity);
        Assert.Equal(MeasureUnit.Kg, items[4].Unit);
        Assert.Equal(MeasureUnit.Piece, items[0].Unit);
    }

    [Fact]
    public async Task Waste_List_FiltersByInclusiveDayRange_NewestFirst()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.01m);

        var onOct31 = await service.AddAsync(Waste(Oct31, ingredientId: boeuf));
        var firstOnNov1 = await service.AddAsync(Waste(Nov1, ingredientId: boeuf));
        var secondOnNov1 = await service.AddAsync(Waste(Nov1, ingredientId: boeuf));
        var onNov2 = await service.AddAsync(Waste(Nov2, ingredientId: boeuf));
        var onNov3 = await service.AddAsync(Waste(Nov3, ingredientId: boeuf));

        // Les deux bornes sont des jours inclus ; l'heure éventuelle est ignorée.
        var items = await service.ListAsync(
            new DateTime(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 2, 23, 59, 0, DateTimeKind.Utc));

        Assert.Equal([onNov2, secondOnNov1, firstOnNov1], items.Select(i => i.Id));

        Assert.Equal([onNov3], (await service.ListAsync(Nov3, Nov3)).Select(i => i.Id));
        Assert.Empty(await service.ListAsync(Nov3, Nov1));   // bornes inversées : rien
        Assert.Empty(await service.ListAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(5, (await service.ListAsync(Oct31, Nov3)).Count);
        Assert.Contains(onOct31, (await service.ListAsync(Oct31, Oct31)).Select(i => i.Id));
    }

    [Fact]
    public async Task Waste_List_DishCostIsRecipeCostTimesPortions()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 0.01m);
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), 0.02m);
        // Sauce : 1 000 g produits pour 500 g de bœuf, soit 5 € au premier prix et 10 € au second.
        var sauce = await AddRecipeAsync(options, restaurant, "Sauce", RecipeKind.Preparation, 1000m, BaseUnit.Gram,
            IngredientLine(boeuf, 500m));
        // Tartare : 150 g de bœuf + 100 g de sauce = 1,5 + 0,5 = 2,0 € au premier prix ; 3,0 + 1,0 = 4,0 € au second.
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare", RecipeKind.Dish, null, null,
            IngredientLine(boeuf, 150m), PreparationLine(sauce, 100m));

        await service.AddAsync(Waste(Oct15, recipeId: tartare, quantity: 3m, unit: MeasureUnit.Piece));
        await service.AddAsync(Waste(Nov3, recipeId: tartare, quantity: 2.5m, unit: MeasureUnit.Piece));

        var items = await service.ListAsync(Oct15, Nov3);

        Assert.Equal(2, items.Count);
        var recent = items[0];
        Assert.Equal("Tartare", recent.ItemName);
        Assert.True(recent.IsRecipe);
        Assert.Equal(2.5m, recent.Quantity);
        Assert.Equal(MeasureUnit.Piece, recent.Unit);
        Assert.Equal(10m, recent.Cost);            // 2,5 portions × 4,0 €
        Assert.False(recent.CostIncomplete);
        Assert.Equal(6m, items[1].Cost);           // 3 portions × 2,0 € : le coût du plat suit le prix de la date de la perte
        Assert.False(items[1].CostIncomplete);
    }

    [Fact]
    public async Task Waste_List_PreparationCostIsCostPerOutputUnitTimesQuantity()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 0.01m);
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), 0.02m);
        // 1 000 g produits : 5 € au premier prix, 10 € au second, soit 0,005 €/g puis 0,01 €/g.
        var sauce = await AddRecipeAsync(options, restaurant, "Sauce", RecipeKind.Preparation, 1000m, BaseUnit.Gram,
            IngredientLine(boeuf, 500m));
        // Fond : 2 l produits pour 400 g de bœuf à 0,01 € = 4 €, soit 0,002 €/ml.
        var fond = await AddRecipeAsync(options, restaurant, "Fond", RecipeKind.Preparation, 2000m, BaseUnit.Milliliter,
            IngredientLine(boeuf, 400m));

        await service.AddAsync(Waste(Oct15, recipeId: sauce, quantity: 200m, unit: MeasureUnit.G));
        await service.AddAsync(Waste(Nov3, recipeId: sauce, quantity: 0.25m, unit: MeasureUnit.Kg));
        await service.AddAsync(Waste(Oct15, recipeId: fond, quantity: 50m, unit: MeasureUnit.Cl));

        var items = await service.ListAsync(Oct15, Nov3);

        Assert.Equal(["Sauce", "Fond", "Sauce"], items.Select(i => i.ItemName));
        Assert.All(items, i => Assert.True(i.IsRecipe));
        Assert.Equal(2.5m, items[0].Cost);   // 250 g × 0,01
        Assert.Equal(1m, items[1].Cost);     // 500 ml × 0,002
        Assert.Equal(1m, items[2].Cost);     // 200 g × 0,005
        Assert.All(items, i => Assert.False(i.CostIncomplete));
        Assert.Equal(MeasureUnit.Kg, items[0].Unit);
        Assert.Equal(MeasureUnit.Cl, items[1].Unit);
    }

    [Fact]
    public async Task Waste_List_IncompleteRecipeFlagged()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.01m);
        var sel = await AddIngredientAsync(options, restaurant, "Sel");                    // sans prix : la recette est incomplète
        var plat = await AddRecipeAsync(options, restaurant, "Plat", RecipeKind.Dish, null, null,
            IngredientLine(boeuf, 100m), IngredientLine(sel, 10m));
        var complet = await AddRecipeAsync(options, restaurant, "Complet", RecipeKind.Dish, null, null,
            IngredientLine(boeuf, 100m));
        // Une préparation dont la quantité produite est nulle : son coût par unité n'existe pas.
        var invalide = await AddRecipeAsync(options, restaurant, "Fond sans rendement", RecipeKind.Preparation, 0m, BaseUnit.Gram,
            IngredientLine(boeuf, 100m));

        await service.AddAsync(Waste(Nov3, recipeId: plat, quantity: 2m, unit: MeasureUnit.Piece));
        await service.AddAsync(Waste(Nov3, recipeId: complet, quantity: 2m, unit: MeasureUnit.Piece));
        await service.AddAsync(Waste(Nov3, recipeId: invalide, quantity: 100m, unit: MeasureUnit.G));

        var items = (await service.ListAsync(Nov3, Nov3)).ToDictionary(i => i.ItemName);

        // Coût partiel (les lignes chiffrées seulement), signalé comme incomplet.
        Assert.Equal(2m, items["Plat"].Cost);
        Assert.True(items["Plat"].CostIncomplete);

        Assert.Equal(2m, items["Complet"].Cost);
        Assert.False(items["Complet"].CostIncomplete);

        Assert.Null(items["Fond sans rendement"].Cost);
    }

    // ── Isolation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task OtherRestaurant_CannotSeeOrModify()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");
        var mine = CreateService(options, restaurant);
        var theirs = CreateService(options, other);

        var myIngredient = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.01m);
        var theirIngredient = await AddIngredientAsync(options, other, "Homard", price: 0.05m);
        var theirDish = await AddRecipeAsync(options, other, "Plat d'ailleurs", RecipeKind.Dish);
        var myWaste = await mine.AddAsync(Waste(Nov3, ingredientId: myIngredient));
        var theirWaste = await theirs.AddAsync(Waste(Nov3, ingredientId: theirIngredient));

        // Chacun ne voit que ses pertes.
        Assert.Equal([myWaste], (await mine.ListAsync(Nov3, Nov3)).Select(i => i.Id));
        Assert.Equal([theirWaste], (await theirs.ListAsync(Nov3, Nov3)).Select(i => i.Id));

        // On ne supprime pas la perte d'un autre restaurant.
        var delete = await Assert.ThrowsAsync<DomainValidationException>(() => mine.DeleteAsync(theirWaste));
        Assert.Equal(UnknownWasteMessage, delete.Message);
        Assert.Single(await LoadEntriesAsync(options, other));

        // On ne déclare pas de perte sur l'ingrédient ou le plat d'un autre restaurant.
        var ingredient = await Assert.ThrowsAsync<DomainValidationException>(() => mine.AddAsync(Waste(Nov3, ingredientId: theirIngredient)));
        var dish = await Assert.ThrowsAsync<DomainValidationException>(
            () => mine.AddAsync(Waste(Nov3, recipeId: theirDish, unit: MeasureUnit.Piece)));
        Assert.Equal(UnknownIngredientMessage, ingredient.Message);
        Assert.Equal(UnknownRecipeMessage, dish.Message);
        Assert.Single(await LoadEntriesAsync(options, restaurant));
    }
}
