using ChefOS.Core.Common;
using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class RecipeServiceTests(PostgresFixture postgres)
{
    private const string UnknownRecipeMessage = "Recette introuvable";
    private const string UnknownIngredientMessage = "Ingrédient introuvable";
    private const string UnknownPreparationMessage = "Préparation introuvable";
    private const string DishPriceMessage = "Indiquez le prix de vente et la TVA du plat";
    private const string PreparationOutputMessage = "Indiquez la quantité produite par la préparation";
    private const string LineSourceMessage = "Chaque ligne doit contenir un ingrédient ou une préparation";
    private const string QuantityMessage = "La quantité doit être supérieure à 0";
    private const string DishInOrdersMessage = "Ce plat figure dans des commandes : archivez-le";
    private const string InReservationsMessage = "Cette recette figure dans des réservations : archivez-la";
    private const string InSalesOrWasteMessage = "Cette recette figure dans des ventes ou des pertes : archivez-la";

    private static RecipeService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    private static IngredientService CreateIngredientService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    private static ChefOsDbContext OpenContext(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new TestContextFactory(options, restaurantId).CreateDbContext();

    /// <summary>Ingrédient en grammes (rendement 100 %), prix par gramme.</summary>
    private static async Task<int> AddIngredientAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        string name,
        decimal? pricePerGram,
        EuAllergen allergens = EuAllergen.None)
    {
        var ingredient = await CreateIngredientService(options, restaurantId).SaveAsync(new IngredientInput(
            null, name, "Divers", allergens, BaseUnit.Gram, pricePerGram, 100m, null, null));
        return ingredient.Id;
    }

    private static RecipeLineInput Grams(int ingredientId, decimal quantity) =>
        new(ingredientId, null, quantity, MeasureUnit.G);

    private static RecipeLineInput Millilitres(int subRecipeId, decimal quantity) =>
        new(null, subRecipeId, quantity, MeasureUnit.Ml);

    private static RecipeInput Dish(
        string name,
        IReadOnlyList<RecipeLineInput> lines,
        int? id = null,
        decimal? price = 22.40m,
        decimal? vat = 0.12m,
        string? category = "Plats") =>
        new(id, name, RecipeKind.Dish, category, price, vat, null, null, null, lines);

    /// <summary>Préparation produisant 1 000 ml (1 l) sauf indication contraire.</summary>
    private static RecipeInput Preparation(
        string name,
        IReadOnlyList<RecipeLineInput>? lines = null,
        int? id = null,
        decimal? outputQuantity = 1000m,
        BaseUnit? outputUnit = BaseUnit.Milliliter) =>
        new(id, name, RecipeKind.Preparation, null, null, null, outputQuantity, outputUnit, null, lines ?? []);

    // ── Enregistrement et coût ───────────────────────────────────────────────

    [Fact]
    public async Task Save_DishWithLines_ListReturnsCost()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", 0.02m);
        var moutarde = await AddIngredientAsync(options, restaurant, "Moutarde", 0.004m, EuAllergen.Mustard);

        var saved = await service.SaveAsync(Dish("Carbonnade", [Grams(boeuf, 200m), Grams(moutarde, 100m)]));

        Assert.True(saved.Id > 0);
        Assert.Equal(restaurant, saved.RestaurantId);
        var summary = Assert.Single(await service.ListAsync(RecipeKind.Dish));
        Assert.Equal(saved.Id, summary.Id);
        Assert.Equal("Carbonnade", summary.Name);
        Assert.Equal(RecipeKind.Dish, summary.Kind);
        Assert.Equal("Plats", summary.Category);
        Assert.False(summary.IsArchived);
        Assert.Equal(EuAllergen.Mustard, summary.Allergens);
        Assert.True(summary.Cost.IsComplete);
        Assert.Equal(4.40m, summary.Cost.TotalCost);                 // 200 g × 0,02 + 100 g × 0,004
        Assert.Equal(20.00m, summary.Cost.PriceExclVat);             // 22,40 / 1,12
        Assert.Equal(0.22m, summary.Cost.FoodCostPercent);
        Assert.Equal(15.60m, summary.Cost.Margin);
    }

    [Fact]
    public async Task Save_PersistsFieldsAndKeepsLineOrder()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var a = await AddIngredientAsync(options, restaurant, "A", 0.01m);
        var b = await AddIngredientAsync(options, restaurant, "B", 0.01m);
        var c = await AddIngredientAsync(options, restaurant, "C", 0.01m);

        var saved = await service.SaveAsync(new RecipeInput(
            null, "  Stoemp  ", RecipeKind.Dish, "  Plats  ", 18m, 0.12m, null, null, "Éplucher puis écraser.",
            [Grams(c, 30m), Grams(a, 10m), Grams(b, 20m)]));

        var stored = await service.GetAsync(saved.Id);
        Assert.NotNull(stored);
        Assert.Equal("Stoemp", stored.Name);
        Assert.Equal("Plats", stored.Category);
        Assert.Equal(18m, stored.SellingPriceInclVat);
        Assert.Equal(0.12m, stored.VatRate);
        Assert.Equal("Éplucher puis écraser.", stored.Instructions);
        Assert.False(stored.IsArchived);
        Assert.Equal([c, a, b], stored.Lines.Select(l => l.IngredientId!.Value));
        Assert.Equal([30m, 10m, 20m], stored.Lines.Select(l => l.Quantity));
        Assert.All(stored.Lines, l => Assert.Equal(restaurant, l.RestaurantId));
    }

    [Fact]
    public async Task Save_ReplacesLines()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", 0.02m);
        var oignon = await AddIngredientAsync(options, restaurant, "Oignon", 0.004m);
        var bière = await AddIngredientAsync(options, restaurant, "Bière", 0.003m);
        var saved = await service.SaveAsync(Dish("Carbonnade", [Grams(boeuf, 200m), Grams(oignon, 100m)]));

        var updated = await service.SaveAsync(Dish("Carbonnade flamande", [Grams(bière, 50m)], id: saved.Id, price: 25m));

        Assert.Equal(saved.Id, updated.Id);
        var stored = await service.GetAsync(saved.Id);
        Assert.NotNull(stored);
        Assert.Equal("Carbonnade flamande", stored.Name);
        Assert.Equal(25m, stored.SellingPriceInclVat);
        var line = Assert.Single(stored.Lines);
        Assert.Equal(bière, line.IngredientId);
        Assert.Equal(50m, line.Quantity);
        await using var db = OpenContext(options, restaurant);
        Assert.Equal(1, await db.RecipeLines.CountAsync()); // les anciennes lignes sont supprimées, pas orphelines
        Assert.Single(await service.ListAsync(RecipeKind.Dish));
    }

    [Fact]
    public async Task Save_AllowsRecipeWithoutLines()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var saved = await service.SaveAsync(Dish("Brouillon", []));

        Assert.Empty((await service.GetAsync(saved.Id))!.Lines);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Theory, InlineData(0), InlineData(-1)]
    public async Task Save_RejectsNonPositiveQuantity(decimal quantity)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", 0.02m);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Carbonnade", [Grams(boeuf, 100m), Grams(boeuf, quantity)])));

        Assert.Equal(QuantityMessage, ex.Message);
        Assert.Empty(await service.ListAsync(RecipeKind.Dish, includeArchived: true));
    }

    [Fact]
    public async Task Save_QuantityRoundingToZero_Throws() // la colonne est numeric(18,4) : 0,00004 serait stocké comme 0
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", 0.02m);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Carbonnade", [Grams(boeuf, 0.00004m)])));

        Assert.Equal(QuantityMessage, ex.Message);
    }

    [Theory, InlineData(""), InlineData("   ")]
    public async Task Save_BlankName_Throws(string name)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Dish(name, [])));

        Assert.Equal("Le nom est obligatoire", ex.Message);
    }

    [Fact]
    public async Task Save_LineWithBothOrNeitherSource_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", 0.02m);
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(boeuf, 500m)]));

        var neither = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Plat", [new RecipeLineInput(null, null, 10m, MeasureUnit.G)])));
        var both = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Plat", [new RecipeLineInput(boeuf, fond.Id, 10m, MeasureUnit.G)])));

        Assert.Equal(LineSourceMessage, neither.Message);
        Assert.Equal(LineSourceMessage, both.Message);
        Assert.Empty(await service.ListAsync(RecipeKind.Dish, includeArchived: true));
    }

    public static TheoryData<decimal?, decimal?> InvalidDishPricing => new()
    {
        { null, 0.12m },
        { 18m, null },
        { -1m, 0.12m },
        { 18m, -0.01m },
        { 18m, 1.01m }
    };

    [Theory, MemberData(nameof(InvalidDishPricing))]
    public async Task Save_DishWithoutValidPriceOrVat_Throws(decimal? price, decimal? vat)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Plat", [], price: price, vat: vat)));

        Assert.Equal(DishPriceMessage, ex.Message);
        Assert.Empty(await service.ListAsync(RecipeKind.Dish, includeArchived: true));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("0", "1")]
    public async Task Save_DishPriceAndVatBounds_AreInclusive(string price, string vat)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var saved = await service.SaveAsync(Dish("Offert", [], price: decimal.Parse(price), vat: decimal.Parse(vat)));

        var stored = await service.GetAsync(saved.Id);
        Assert.Equal(decimal.Parse(price), stored!.SellingPriceInclVat);
        Assert.Equal(decimal.Parse(vat), stored.VatRate);
    }

    public static TheoryData<decimal?, BaseUnit?> InvalidPreparationOutput => new()
    {
        { null, BaseUnit.Milliliter },
        { 0m, BaseUnit.Milliliter },
        { -5m, BaseUnit.Milliliter },
        { 1000m, null }
    };

    [Theory, MemberData(nameof(InvalidPreparationOutput))]
    public async Task Save_PreparationWithoutOutput_Throws(decimal? quantity, BaseUnit? unit)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Preparation("Fond", [], outputQuantity: quantity, outputUnit: unit)));

        Assert.Equal(PreparationOutputMessage, ex.Message);
        Assert.Empty(await service.ListAsync(RecipeKind.Preparation, includeArchived: true));
    }

    [Fact]
    public async Task Save_PreparationNeedsNoPrice()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var saved = await service.SaveAsync(Preparation("Fond", [], outputQuantity: 2000m, outputUnit: BaseUnit.Milliliter));

        var stored = await service.GetAsync(saved.Id);
        Assert.Equal(2000m, stored!.OutputQuantity);
        Assert.Equal(BaseUnit.Milliliter, stored.OutputUnit);
        Assert.Null(stored.SellingPriceInclVat);
    }

    // ── Cycles ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_Cycle_ThrowsWithMessage()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(os, 500m)]));
        var sauce = await service.SaveAsync(Preparation("Sauce", [Millilitres(fond.Id, 250m)]));

        // Le fond contiendrait la sauce, qui contient déjà le fond.
        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Preparation("Fond", [Grams(os, 500m), Millilitres(sauce.Id, 100m)], id: fond.Id)));

        Assert.Equal("Sauce contient déjà Fond (directement ou indirectement)", ex.Message);
        var stored = await service.GetAsync(fond.Id);
        Assert.Equal([os], stored!.Lines.Select(l => l.IngredientId!.Value)); // rien n'a été écrit
    }

    [Fact]
    public async Task Save_SelfReference_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var fond = await service.SaveAsync(Preparation("Fond", []));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Preparation("Fond", [Millilitres(fond.Id, 100m)], id: fond.Id)));

        Assert.Equal("Fond contient déjà Fond (directement ou indirectement)", ex.Message);
    }

    // ── Références ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_IngredientOfOtherRestaurant_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreignIngredient = await AddIngredientAsync(options, b, "Poivron", 0.01m);
        var service = CreateService(options, a);

        var onCreate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Plat", [Grams(foreignIngredient, 100m)])));
        var unknown = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Plat", [Grams(999_999, 100m)])));

        Assert.Equal(UnknownIngredientMessage, onCreate.Message);
        Assert.Equal(UnknownIngredientMessage, unknown.Message);
        Assert.Empty(await service.ListAsync(RecipeKind.Dish, includeArchived: true));
        await using var db = OpenContext(options, b);
        Assert.Empty(await db.RecipeLines.ToListAsync());
    }

    [Fact]
    public async Task Save_IngredientOfOtherRestaurantOnUpdate_KeepsExistingLines()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var own = await AddIngredientAsync(options, a, "Boeuf", 0.02m);
        var foreign = await AddIngredientAsync(options, b, "Poivron", 0.01m);
        var service = CreateService(options, a);
        var saved = await service.SaveAsync(Dish("Plat", [Grams(own, 100m)]));

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Plat", [Grams(foreign, 100m)], id: saved.Id)));

        var line = Assert.Single((await service.GetAsync(saved.Id))!.Lines);
        Assert.Equal(own, line.IngredientId);
    }

    [Fact]
    public async Task Save_SubRecipeOfOtherRestaurantOrUnknown_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreignPreparation = await CreateService(options, b).SaveAsync(Preparation("Fond"));
        var service = CreateService(options, a);

        var foreign = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Plat", [Millilitres(foreignPreparation.Id, 100m)])));
        var unknown = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Plat", [Millilitres(999_999, 100m)])));

        Assert.Equal(UnknownPreparationMessage, foreign.Message);
        Assert.Equal(UnknownPreparationMessage, unknown.Message);
        Assert.Empty(await service.ListAsync(RecipeKind.Dish, includeArchived: true));
    }

    [Fact]
    public async Task Save_DishAsSubRecipe_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var dish = await service.SaveAsync(Dish("Carbonnade", []));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Preparation("Fond", [Millilitres(dish.Id, 100m)])));

        Assert.Equal(UnknownPreparationMessage, ex.Message);
    }

    [Fact]
    public async Task Save_UsedPreparationToDish_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(os, 500m)]));
        await service.SaveAsync(Preparation("Sauce", [Millilitres(fond.Id, 250m)]));
        await service.SaveAsync(Dish("Carbonnade", [Millilitres(fond.Id, 100m)]));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Fond", [Grams(os, 100m)], id: fond.Id)));

        Assert.Equal("Cette préparation est utilisée dans d'autres recettes : Carbonnade, Sauce", ex.Message);
        var stored = await service.GetAsync(fond.Id);
        Assert.Equal(RecipeKind.Preparation, stored!.Kind);
        var line = Assert.Single(stored.Lines);
        Assert.Equal(500m, line.Quantity);
        Assert.Equal(1000m, stored.OutputQuantity);
    }

    [Fact]
    public async Task Save_UnusedPreparationToDish_Allowed()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(os, 500m)]));

        await service.SaveAsync(Dish("Fond", [Grams(os, 500m)], id: fond.Id));

        var stored = await service.GetAsync(fond.Id);
        Assert.Equal(RecipeKind.Dish, stored!.Kind);
        Assert.Equal(22.40m, stored.SellingPriceInclVat);
        Assert.Empty(await service.ListAsync(RecipeKind.Preparation, includeArchived: true));
        Assert.Single(await service.ListAsync(RecipeKind.Dish));
    }

    [Fact]
    public async Task Save_ArchivedReferences_AreStillAccepted() // les recettes existantes continuent d'utiliser un élément archivé
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", 0.02m);
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(boeuf, 500m)]));
        var dish = await service.SaveAsync(Dish("Carbonnade", [Grams(boeuf, 100m), Millilitres(fond.Id, 100m)]));
        await CreateIngredientService(options, restaurant).SetArchivedAsync(boeuf, true);
        await service.SetArchivedAsync(fond.Id, true);

        await service.SaveAsync(Dish("Carbonnade", [Grams(boeuf, 150m), Millilitres(fond.Id, 100m)], id: dish.Id));

        Assert.Equal(2, (await service.GetAsync(dish.Id))!.Lines.Count);
    }

    [Fact]
    public async Task UnknownId_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreign = await CreateService(options, b).SaveAsync(Dish("Piraté", []));
        var service = CreateService(options, a);

        var saveUnknown = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Plat", [], id: 999_999)));
        var saveForeign = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Dish("Volé", [], id: foreign.Id)));
        var archive = await Assert.ThrowsAsync<DomainValidationException>(() => service.SetArchivedAsync(foreign.Id, true));
        var delete = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(foreign.Id));
        var deleteUnknown = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(999_999));

        Assert.All(
            new[] { saveUnknown, saveForeign, archive, delete, deleteUnknown },
            ex => Assert.Equal(UnknownRecipeMessage, ex.Message));
        var untouched = await CreateService(options, b).GetAsync(foreign.Id);
        Assert.NotNull(untouched);
        Assert.Equal("Piraté", untouched.Name);
        Assert.False(untouched.IsArchived);
        Assert.Empty(await service.ListAsync(RecipeKind.Dish, includeArchived: true));
    }

    [Fact]
    public async Task Get_OtherRestaurantOrUnknown_ReturnsNull()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreign = await CreateService(options, b).SaveAsync(Dish("Piraté", []));
        var service = CreateService(options, a);

        Assert.Null(await service.GetAsync(foreign.Id));
        Assert.Null(await service.GetAsync(999_999));
    }

    // ── Liste, archivage, suppression ────────────────────────────────────────

    [Fact]
    public async Task List_FiltersByKindAndArchived_OrdersByName()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        await service.SaveAsync(Dish("Stoemp", []));
        var carbonnade = await service.SaveAsync(Dish("carbonnade", []));
        await service.SaveAsync(Dish("Boulets", []));
        await service.SaveAsync(Preparation("Fond"));

        await service.SetArchivedAsync(carbonnade.Id, true);

        Assert.Equal(["Boulets", "Stoemp"], (await service.ListAsync(RecipeKind.Dish)).Select(r => r.Name));
        Assert.Equal(
            ["Boulets", "carbonnade", "Stoemp"],
            (await service.ListAsync(RecipeKind.Dish, includeArchived: true)).Select(r => r.Name));
        Assert.Equal(["Fond"], (await service.ListAsync(RecipeKind.Preparation)).Select(r => r.Name));

        await service.SetArchivedAsync(carbonnade.Id, false);

        Assert.Equal(3, (await service.ListAsync(RecipeKind.Dish)).Count);
    }

    [Fact]
    public async Task List_ReportsIncompleteCostAndPreparationAllergens()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var oeuf = await AddIngredientAsync(options, restaurant, "Oeuf", 0.2m, EuAllergen.Eggs);
        var safran = await AddIngredientAsync(options, restaurant, "Safran", null, EuAllergen.None);
        var mayo = await service.SaveAsync(Preparation("Mayonnaise", [Grams(oeuf, 100m)]));
        await service.SaveAsync(Dish("Plat", [Grams(safran, 1m), Millilitres(mayo.Id, 50m)]));

        var dish = Assert.Single(await service.ListAsync(RecipeKind.Dish));

        Assert.False(dish.Cost.IsComplete);
        Assert.Contains(dish.Cost.Issues, i => i.Kind == CostIssueKind.MissingPrice && i.ItemName == "Safran");
        Assert.Equal(EuAllergen.Eggs, dish.Allergens);
    }

    [Fact]
    public async Task LoadBook_IncludesArchivedRecipesAndIngredients()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", 0.02m);
        var dish = await service.SaveAsync(Dish("Carbonnade", [Grams(boeuf, 200m)]));
        await service.SetArchivedAsync(dish.Id, true);
        await CreateIngredientService(options, restaurant).SetArchivedAsync(boeuf, true);

        var book = await service.LoadBookAsync();

        var recipe = Assert.Single(book.Recipes);
        Assert.True(recipe.IsArchived);
        Assert.Single(recipe.Lines);
        Assert.Single(book.Ingredients);
        Assert.Equal(4.00m, book.Cost(dish.Id).TotalCost);
    }

    [Fact]
    public async Task Delete_PreparationUsedAsSubRecipe_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(os, 500m)]));
        await service.SaveAsync(Preparation("Sauce", [Millilitres(fond.Id, 250m)]));
        await service.SaveAsync(Dish("Carbonnade", [Millilitres(fond.Id, 100m)]));

        var result = await service.DeleteAsync(fond.Id);

        Assert.False(result.Deleted);
        Assert.Equal(["Carbonnade", "Sauce"], result.UsedInRecipes);
        Assert.NotNull(await service.GetAsync(fond.Id));
        Assert.Single((await service.GetAsync(fond.Id))!.Lines);
    }

    [Fact]
    public async Task Delete_UnusedRecipe_DeletesItsLinesToo()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(os, 500m)]));
        var sauce = await service.SaveAsync(Preparation("Sauce", [Millilitres(fond.Id, 250m)]));
        var other = await service.SaveAsync(Dish("Carbonnade", [Grams(os, 100m)]));

        var result = await service.DeleteAsync(sauce.Id);

        Assert.True(result.Deleted);
        Assert.Empty(result.UsedInRecipes);
        Assert.Null(await service.GetAsync(sauce.Id));
        Assert.NotNull(await service.GetAsync(other.Id));
        await using var db = OpenContext(options, restaurant);
        Assert.Equal(2, await db.RecipeLines.CountAsync()); // les lignes de Fond et de Carbonnade restent

        // Une fois la sauce supprimée, le fond n'est plus utilisé par une préparation : il est supprimable.
        Assert.True((await service.DeleteAsync(fond.Id)).Deleted);
        Assert.Equal(1, await db.RecipeLines.CountAsync());
    }

    [Fact]
    public async Task DeleteRecipeInPreorders_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var dish = await service.SaveAsync(Dish("Carbonnade", [Grams(os, 100m)]));
        await using (var db = OpenContext(options, restaurant))
        {
            var reservation = new Reservation
            {
                PublicToken = Guid.NewGuid().ToString("N"),
                StartsAtUtc = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc),
                ServiceName = "Dîner",
                PartySize = 2,
                CustomerName = "Claire Martin",
                CustomerEmail = "claire@example.org",
                CustomerPhone = "+32470000000",
                Status = ReservationStatus.Confirmed,
                CreatedAt = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)
            };
            var guest = new ReservationGuest { Position = 1 };
            guest.Dishes.Add(new ReservationGuestDish { RecipeId = dish.Id, Quantity = 1, UnitPriceInclVat = 22.40m, VatRate = 0.12m });
            reservation.Guests.Add(guest);
            db.Reservations.Add(reservation);
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(dish.Id));

        Assert.Equal(InReservationsMessage, ex.Message);
        Assert.NotNull(await service.GetAsync(dish.Id));
    }

    [Fact]
    public async Task DeleteDishUsedInOrders_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var dish = await service.SaveAsync(Dish("Carbonnade", [Grams(os, 100m)]));
        var unordered = await service.SaveAsync(Dish("Stoemp", [Grams(os, 50m)]));
        var orderedAt = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        await using (var db = OpenContext(options, restaurant))
        {
            var order = new Order
            {
                Number = 1,
                CreatedAt = orderedAt,
                Source = OrderSource.Salle,
                Status = OrderStatus.Servie,
                StatusChangedAt = orderedAt
            };
            order.Lines.Add(new OrderLine { RecipeId = dish.Id, Quantity = 2, UnitPriceInclVat = 22.40m, VatRate = 0.12m });
            db.Orders.Add(order);
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(dish.Id));

        Assert.Equal(DishInOrdersMessage, ex.Message);
        Assert.NotNull(await service.GetAsync(dish.Id));
        await using var check = OpenContext(options, restaurant);
        Assert.Single(await check.OrderLines.ToListAsync());

        // Archiver reste possible, et un plat jamais commandé se supprime toujours.
        await service.SetArchivedAsync(dish.Id, true);
        Assert.True((await service.DeleteAsync(unordered.Id)).Deleted);
    }

    [Fact]
    public async Task Delete_DishWithSalesEntries_Refused_AndKeepsItsSalesHistory()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var sold = await service.SaveAsync(Dish("Carbonnade", [Grams(os, 100m)]));
        var unsold = await service.SaveAsync(Dish("Stoemp", [Grams(os, 50m)]));
        var soldAt = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = OpenContext(options, restaurant))
        {
            db.SalesEntries.Add(new SalesEntry
            {
                RecipeId = sold.Id, Date = soldAt, Service = ServicePeriod.Midi, Quantity = 12m, UnitPriceInclVat = 22.40m, VatRate = 0.12m, UpdatedAt = soldAt
            });
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(sold.Id));

        // Le plat et l'historique de ses ventes sont intacts (la cascade de la base ne les a pas effacés).
        Assert.Equal(InSalesOrWasteMessage, ex.Message);
        Assert.NotNull(await service.GetAsync(sold.Id));
        await using var check = OpenContext(options, restaurant);
        Assert.Equal(12m, Assert.Single(await check.SalesEntries.ToListAsync()).Quantity);

        // Archiver reste possible, et un plat sans vente se supprime toujours.
        await service.SetArchivedAsync(sold.Id, true);
        Assert.True((await service.DeleteAsync(unsold.Id)).Deleted);
        Assert.Single(await check.SalesEntries.ToListAsync());
    }

    [Fact]
    public async Task Delete_RecipeWithWasteEntries_Refused_ButIngredientWasteDoesNotBlock()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var wastedDish = await service.SaveAsync(Dish("Carbonnade", [Grams(os, 100m)]));
        var wastedPreparation = await service.SaveAsync(Preparation("Fond", [Grams(os, 500m)]));
        var other = await service.SaveAsync(Dish("Stoemp", [Grams(os, 50m)]));
        var day = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = OpenContext(options, restaurant))
        {
            db.WasteEntries.AddRange(
                new WasteEntry { Date = day, RecipeId = wastedDish.Id, Quantity = 1m, Unit = MeasureUnit.Piece, Reason = WasteReason.Erreur, CreatedAt = day },
                new WasteEntry { Date = day, RecipeId = wastedPreparation.Id, Quantity = 200m, Unit = MeasureUnit.Ml, Reason = WasteReason.Perime, CreatedAt = day },
                // Une perte d'ingrédient n'a pas de recette : elle ne retient aucun plat.
                new WasteEntry { Date = day, IngredientId = os, Quantity = 100m, Unit = MeasureUnit.G, Reason = WasteReason.Casse, CreatedAt = day });
            await db.SaveChangesAsync();
        }

        var dishError = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(wastedDish.Id));
        var preparationError = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(wastedPreparation.Id));

        Assert.Equal(InSalesOrWasteMessage, dishError.Message);
        Assert.Equal(InSalesOrWasteMessage, preparationError.Message);
        await using var check = OpenContext(options, restaurant);
        Assert.Equal(3, await check.WasteEntries.CountAsync());
        Assert.NotNull(await service.GetAsync(wastedDish.Id));

        Assert.True((await service.DeleteAsync(other.Id)).Deleted);
        Assert.Equal(3, await check.WasteEntries.CountAsync());
    }

    [Fact]
    public async Task Delete_PreparationUsedInRecipes_StillReportsTheRecipes_BeforeTheHistoryCheck()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(os, 500m)]));
        await service.SaveAsync(Dish("Carbonnade", [Millilitres(fond.Id, 500m)]));
        var day = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = OpenContext(options, restaurant))
        {
            db.WasteEntries.Add(new WasteEntry { Date = day, RecipeId = fond.Id, Quantity = 200m, Unit = MeasureUnit.Ml, Reason = WasteReason.Perime, CreatedAt = day });
            await db.SaveChangesAsync();
        }

        // La préparation sert encore à un plat ET figure dans des pertes : le refus existant (liste des recettes) reste celui qu'on obtient.
        var result = await service.DeleteAsync(fond.Id);

        Assert.False(result.Deleted);
        Assert.Equal(["Carbonnade"], result.UsedInRecipes);
    }

    [Fact]
    public async Task Archive_PreparationInUse_KeepsCostingDishes()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(os, 500m)]));
        await service.SaveAsync(Dish("Carbonnade", [Millilitres(fond.Id, 500m)]));

        await service.SetArchivedAsync(fond.Id, true);

        var dish = Assert.Single(await service.ListAsync(RecipeKind.Dish));
        Assert.True(dish.Cost.IsComplete);
        Assert.Equal(1.00m, dish.Cost.TotalCost);
        Assert.Empty(await service.ListAsync(RecipeKind.Preparation));
    }

    // ── Propagation des prix ─────────────────────────────────────────────────

    [Fact]
    public async Task PriceChange_PropagatesThroughPreparation()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var ingredients = CreateIngredientService(options, restaurant);
        var os = await AddIngredientAsync(options, restaurant, "Os de veau", 0.004m);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", 0.02m);

        // Fond : 500 g d'os (2,00 €) pour 1 l ; sauce : 250 ml de fond pour 500 ml ; plat : 100 ml de sauce + 200 g de boeuf.
        var fond = await service.SaveAsync(Preparation("Fond", [Grams(os, 500m)]));
        var sauce = await service.SaveAsync(Preparation("Sauce", [Millilitres(fond.Id, 250m)], outputQuantity: 500m));
        var dish = await service.SaveAsync(Dish("Carbonnade", [Millilitres(sauce.Id, 100m), Grams(boeuf, 200m)]));
        var before = Assert.Single(await service.ListAsync(RecipeKind.Dish)).Cost;
        Assert.Equal(4.10m, before.TotalCost);                       // 0,10 de sauce + 4,00 de boeuf

        await ingredients.SetPriceAsync(os, 0.008m, PriceSource.Receipt, null, DateTime.UtcNow);

        var after = Assert.Single(await service.ListAsync(RecipeKind.Dish)).Cost;
        Assert.Equal(4.20m, after.TotalCost);                        // le fond coûte 4,00 € : la sauce 1,00 €, le plat 0,20 €
        Assert.Equal(0.10m, after.TotalCost - before.TotalCost);
        Assert.Equal(4.20m, (await service.LoadBookAsync()).Cost(dish.Id).TotalCost);
    }
}
