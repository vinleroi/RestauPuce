using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class IngredientServiceTests(PostgresFixture postgres)
{
    private const string UnknownIngredientMessage = "Ingrédient introuvable";
    private const string UnknownSupplierMessage = "Fournisseur introuvable";
    private const string YieldMessage = "Le rendement doit être compris entre 1 et 100 %";

    private static IngredientService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    private static SupplierService CreateSupplierService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    private static ChefOsDbContext OpenContext(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new TestContextFactory(options, restaurantId).CreateDbContext();

    private static IngredientInput Input(
        string name = "Tomate",
        decimal? price = null,
        decimal yieldPercent = 100m,
        int? supplierId = null,
        int? id = null,
        string category = "Légumes",
        decimal? pieceWeight = null,
        BaseUnit baseUnit = BaseUnit.Gram) =>
        new(id, name, category, EuAllergen.None, baseUnit, price, yieldPercent, pieceWeight, supplierId);

    private static async Task<int> AddSupplierAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name = "Metro")
    {
        var supplier = await CreateSupplierService(options, restaurantId).SaveAsync(new SupplierInput(null, name, null, null, null));
        return supplier.Id;
    }

    /// <summary>RecipeService n'existe pas encore : la recette et sa ligne sont insérées directement.</summary>
    private static async Task AddRecipeUsingAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string recipeName, int ingredientId)
    {
        await using var db = OpenContext(options, restaurantId);
        var recipe = new Recipe { Name = recipeName, Kind = RecipeKind.Dish };
        recipe.Lines.Add(new RecipeLine { IngredientId = ingredientId, Quantity = 100m, Unit = MeasureUnit.G });
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
    }

    // ── Prix et historique ───────────────────────────────────────────────────

    [Fact]
    public async Task Save_NewPrice_AddsManualHistoryEntry()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var supplierId = await AddSupplierAsync(options, restaurant);

        var before = DateTime.UtcNow;
        var created = await service.SaveAsync(Input(price: 0.0025m, supplierId: supplierId));
        var afterCreate = DateTime.UtcNow;

        var history = await service.GetPriceHistoryAsync(created.Id);
        var first = Assert.Single(history);
        Assert.Equal(0.0025m, first.PricePerBaseUnit);
        Assert.Equal(PriceSource.Manual, first.Source);
        Assert.Equal(supplierId, first.SupplierId);
        Assert.Equal(DateTimeKind.Utc, first.Date.Kind);
        Assert.InRange(first.Date, before, afterCreate);
        Assert.Equal(0.0025m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);

        // Un nouveau prix sur un ingrédient existant ajoute une seconde entrée et met à jour le prix courant.
        await service.SaveAsync(Input(id: created.Id, price: 0.003m, supplierId: supplierId));

        history = await service.GetPriceHistoryAsync(created.Id);
        Assert.Equal([0.0025m, 0.003m], history.Select(p => p.PricePerBaseUnit));
        Assert.All(history, p => Assert.Equal(PriceSource.Manual, p.Source));
        Assert.Equal(0.003m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
    }

    [Fact]
    public async Task Save_SamePrice_NoHistoryEntry()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input(price: 0.0025m));

        await service.SaveAsync(Input(id: created.Id, name: "Tomate cerise", price: 0.0025m));

        Assert.Single(await service.GetPriceHistoryAsync(created.Id));
        Assert.Equal("Tomate cerise", (await service.GetAsync(created.Id))!.Name);
    }

    [Fact]
    public async Task Save_NoPrice_CreatesWithoutHistory()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var created = await service.SaveAsync(Input(price: null));

        Assert.Null((await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        Assert.Empty(await service.GetPriceHistoryAsync(created.Id));
    }

    [Fact]
    public async Task Save_NullPriceOnExisting_KeepsCurrentPriceAndHistory()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input(price: 0.0025m));

        await service.SaveAsync(Input(id: created.Id, price: null));

        Assert.Equal(0.0025m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        Assert.Single(await service.GetPriceHistoryAsync(created.Id));
    }

    [Fact]
    public async Task SetPrice_UpdatesCurrentAndHistory() // Source = Receipt conservée
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var supplierId = await AddSupplierAsync(options, restaurant);
        var created = await service.SaveAsync(Input(price: 0.0025m));
        var receiptDate = new DateTime(2099, 1, 15, 8, 30, 0, DateTimeKind.Utc);

        await service.SetPriceAsync(created.Id, 0.0031m, PriceSource.Receipt, supplierId, receiptDate);

        Assert.Equal(0.0031m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        var history = await service.GetPriceHistoryAsync(created.Id);
        Assert.Equal(2, history.Count);
        var receipt = history[1]; // la date de ticket (2099) est postérieure à l'entrée manuelle créée à l'instant
        Assert.Equal(0.0031m, receipt.PricePerBaseUnit);
        Assert.Equal(PriceSource.Receipt, receipt.Source);
        Assert.Equal(supplierId, receipt.SupplierId);
        Assert.Equal(receiptDate, receipt.Date);
    }

    [Fact]
    public async Task SetPrice_OlderDate_DoesNotReplaceCurrent() // un ticket en retard n'enrichit que l'historique
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var supplierId = await AddSupplierAsync(options, restaurant);
        var created = await service.SaveAsync(Input(price: 0.010m)); // entrée Manual datée de maintenant (jour J)
        // Date fixe : un UtcNow décalé aurait des ticks sous la microseconde que PostgreSQL ne restitue pas (selon la plateforme).
        var olderDate = new DateTime(2020, 1, 10, 8, 30, 0, DateTimeKind.Utc);

        await service.SetPriceAsync(created.Id, 0.008m, PriceSource.Receipt, supplierId, olderDate);

        Assert.Equal(0.010m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        var history = await service.GetPriceHistoryAsync(created.Id);
        Assert.Equal(2, history.Count);
        var late = history[0]; // triée par date : le ticket en retard passe avant l'entrée manuelle
        Assert.Equal(0.008m, late.PricePerBaseUnit);
        Assert.Equal(PriceSource.Receipt, late.Source);
        Assert.Equal(supplierId, late.SupplierId);
        Assert.Equal(olderDate, late.Date);
        Assert.Equal(0.010m, history[1].PricePerBaseUnit);
    }

    [Fact]
    public async Task SetPrice_BetweenExistingEntries_DoesNotReplaceCurrent() // la référence est la date la plus récente de l'historique
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input());
        var january = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var february = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var march = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        await service.SetPriceAsync(created.Id, 1m, PriceSource.Receipt, null, january);
        await service.SetPriceAsync(created.Id, 3m, PriceSource.Receipt, null, march);
        await service.SetPriceAsync(created.Id, 2m, PriceSource.Receipt, null, february);

        Assert.Equal(3m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        Assert.Equal([1m, 2m, 3m], (await service.GetPriceHistoryAsync(created.Id)).Select(p => p.PricePerBaseUnit));
    }

    [Fact]
    public async Task SetPrice_NoHistory_SetsCurrentWhateverTheDate()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input()); // ni prix courant ni historique

        await service.SetPriceAsync(created.Id, 0.008m, PriceSource.Receipt, null, new DateTime(2020, 1, 10, 8, 30, 0, DateTimeKind.Utc));

        Assert.Equal(0.008m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        Assert.Single(await service.GetPriceHistoryAsync(created.Id));
    }

    [Fact]
    public async Task SetPrice_SameOrNewerDate_ReplacesCurrent()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input(price: 0.0025m));
        var receiptDate = new DateTime(2099, 1, 15, 8, 30, 0, DateTimeKind.Utc);

        await service.SetPriceAsync(created.Id, 0.010m, PriceSource.Receipt, null, receiptDate); // plus récent
        Assert.Equal(0.010m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);

        await service.SetPriceAsync(created.Id, 0.012m, PriceSource.Receipt, null, receiptDate); // même date
        Assert.Equal(0.012m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);

        await service.SetPriceAsync(created.Id, 0.015m, PriceSource.Receipt, null, receiptDate.AddDays(1)); // encore plus récent
        Assert.Equal(0.015m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);

        Assert.Equal(4, (await service.GetPriceHistoryAsync(created.Id)).Count);
    }

    [Fact]
    public async Task SetPrice_SameDayEarlierTime_BecomesLatestEntry() // comparaison au jour pour le prix courant ; l'entrée qui le fixe est la dernière
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input());
        var manualEvening = new DateTime(2026, 1, 15, 18, 0, 0, DateTimeKind.Utc);
        var ticketMidnight = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        await service.SetPriceAsync(created.Id, 0.010m, PriceSource.Manual, null, manualEvening);
        await service.SetPriceAsync(created.Id, 0.012m, PriceSource.Receipt, null, ticketMidnight);

        Assert.Equal(0.012m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        var history = await service.GetPriceHistoryAsync(created.Id);
        // Le ticket, daté de minuit, passe 1 µs après la saisie du soir : il est la dernière entrée, comme le prix courant qu'il a fixé.
        Assert.Equal([manualEvening, manualEvening.AddTicks(TimeSpan.TicksPerMicrosecond)], history.Select(p => p.Date));
        Assert.Equal([0.010m, 0.012m], history.Select(p => p.PricePerBaseUnit));
        Assert.Equal([PriceSource.Manual, PriceSource.Receipt], history.Select(p => p.Source));
    }

    [Fact]
    public async Task SetPrice_SameDayLaterTime_KeepsItsOwnDate() // pas de décalage quand l'entrée est déjà la plus récente
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input());
        var morning = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        var evening = new DateTime(2026, 1, 15, 18, 0, 0, DateTimeKind.Utc);

        await service.SetPriceAsync(created.Id, 0.010m, PriceSource.Manual, null, morning);
        await service.SetPriceAsync(created.Id, 0.012m, PriceSource.Manual, null, evening);

        Assert.Equal([morning, evening], (await service.GetPriceHistoryAsync(created.Id)).Select(p => p.Date));
    }

    [Fact]
    public async Task SetPrice_PreviousDayLateEvening_DoesNotReplaceCurrent()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input());
        await service.SetPriceAsync(created.Id, 0.010m, PriceSource.Manual, null, new DateTime(2099, 1, 15, 0, 0, 1, DateTimeKind.Utc));

        await service.SetPriceAsync(created.Id, 0.008m, PriceSource.Receipt, null, new DateTime(2099, 1, 14, 23, 59, 59, DateTimeKind.Utc));

        Assert.Equal(0.010m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        var history = await service.GetPriceHistoryAsync(created.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal(new DateTime(2099, 1, 14, 23, 59, 59, DateTimeKind.Utc), history[0].Date); // en retard : sa date n'est pas décalée
    }

    [Fact]
    public async Task SetPrice_NegativePrice_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input(price: 0.0025m));

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SetPriceAsync(created.Id, -1m, PriceSource.Receipt, null, DateTime.UtcNow));

        Assert.Single(await service.GetPriceHistoryAsync(created.Id));
    }

    [Theory, InlineData(DateTimeKind.Local), InlineData(DateTimeKind.Unspecified)]
    public async Task SetPrice_NonUtcDate_Throws(DateTimeKind kind)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input(price: 0.0025m));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetPriceAsync(
            created.Id, 1m, PriceSource.Receipt, null, new DateTime(2026, 1, 15, 8, 30, 0, kind)));

        Assert.Single(await service.GetPriceHistoryAsync(created.Id));
    }

    [Fact]
    public async Task SetPrice_UnknownIngredient_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreign = await CreateService(options, b).SaveAsync(Input(name: "Poivron"));
        var service = CreateService(options, a);

        var unknown = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SetPriceAsync(999_999, 1m, PriceSource.Manual, null, DateTime.UtcNow));
        var otherRestaurant = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SetPriceAsync(foreign.Id, 1m, PriceSource.Manual, null, DateTime.UtcNow));

        Assert.Equal(UnknownIngredientMessage, unknown.Message);
        Assert.Equal(UnknownIngredientMessage, otherRestaurant.Message);
        await using var db = OpenContext(options, b);
        Assert.Empty(await db.IngredientPrices.ToListAsync());
        Assert.Null((await db.Ingredients.SingleAsync()).CurrentPricePerBaseUnit);
    }

    [Fact]
    public async Task SetPrice_SupplierOfOtherRestaurant_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreignSupplierId = await AddSupplierAsync(options, b);
        var service = CreateService(options, a);
        var created = await service.SaveAsync(Input(price: 0.0025m));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SetPriceAsync(created.Id, 0.004m, PriceSource.Receipt, foreignSupplierId, DateTime.UtcNow));

        Assert.Equal(UnknownSupplierMessage, ex.Message);
        Assert.Equal(0.0025m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        Assert.Single(await service.GetPriceHistoryAsync(created.Id));
    }

    [Fact]
    public async Task GetPriceHistory_OrderedByDateAscending()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input());
        var march = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var january = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var february = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        await service.SetPriceAsync(created.Id, 3m, PriceSource.Receipt, null, march);
        await service.SetPriceAsync(created.Id, 1m, PriceSource.Receipt, null, january);
        await service.SetPriceAsync(created.Id, 2m, PriceSource.Receipt, null, february);

        var history = await service.GetPriceHistoryAsync(created.Id);
        Assert.Equal([1m, 2m, 3m], history.Select(p => p.PricePerBaseUnit));
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Theory, InlineData(0), InlineData(101)]
    public async Task Save_InvalidYield_Throws(decimal y)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Input(yieldPercent: y)));

        Assert.Equal(YieldMessage, ex.Message);
        Assert.Empty(await service.ListAsync(includeArchived: true));
    }

    [Fact]
    public async Task Save_InvalidYieldOnUpdate_Throws() // RecipeBook divise par YieldPercent / 100 : contrôle à chaque enregistrement
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var created = await service.SaveAsync(Input(yieldPercent: 80m));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Input(id: created.Id, yieldPercent: 0m)));

        Assert.Equal(YieldMessage, ex.Message);
        Assert.Equal(80m, (await service.GetAsync(created.Id))!.YieldPercent);
    }

    [Theory, InlineData(1), InlineData(100)]
    public async Task Save_YieldBounds_AreInclusive(decimal y)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var created = await service.SaveAsync(Input(yieldPercent: y));

        Assert.Equal(y, (await service.GetAsync(created.Id))!.YieldPercent);
    }

    [Theory, InlineData(""), InlineData("   ")]
    public async Task Save_BlankName_Throws(string name)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Input(name: name)));

        Assert.Equal("Le nom est obligatoire", ex.Message);
    }

    [Fact]
    public async Task Save_NegativePrice_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Input(price: -0.01m)));

        Assert.Empty(await service.ListAsync(includeArchived: true));
    }

    [Fact]
    public async Task Save_ZeroPrice_IsAllowed()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var created = await service.SaveAsync(Input(price: 0m));

        Assert.Equal(0m, (await service.GetAsync(created.Id))!.CurrentPricePerBaseUnit);
        Assert.Single(await service.GetPriceHistoryAsync(created.Id));
    }

    [Theory, InlineData(0), InlineData(-5)]
    public async Task Save_NonPositivePieceWeight_Throws(decimal weight)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Input(pieceWeight: weight)));
    }

    [Fact]
    public async Task Save_PersistsAllFields_AndNormalizesText()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var supplierId = await AddSupplierAsync(options, restaurant);

        var created = await service.SaveAsync(new IngredientInput(
            null, "  Oeuf  ", "Crémerie", EuAllergen.Eggs, BaseUnit.Piece, null, 95m, 60m, supplierId));
        var blankCategory = await service.SaveAsync(Input(name: "Sel", category: "  "));

        var stored = await service.GetAsync(created.Id);
        Assert.NotNull(stored);
        Assert.Equal("Oeuf", stored.Name);
        Assert.Equal("Crémerie", stored.Category);
        Assert.Equal(EuAllergen.Eggs, stored.Allergens);
        Assert.Equal(BaseUnit.Piece, stored.BaseUnit);
        Assert.Equal(95m, stored.YieldPercent);
        Assert.Equal(60m, stored.AveragePieceWeightGrams);
        Assert.Equal(supplierId, stored.SupplierId);
        Assert.False(stored.IsArchived);
        Assert.Equal("Divers", (await service.GetAsync(blankCategory.Id))!.Category);
    }

    // ── Changement d'unité de base ───────────────────────────────────────────
    // Un prix par unité de base n'a de sens qu'avec son unité : un changement d'unité ne doit jamais le réinterpréter en silence.

    [Fact]
    public async Task Save_UnitChangeOnUsedIngredient_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var oeuf = await service.SaveAsync(Input(name: "Oeuf", baseUnit: BaseUnit.Piece, price: 0.30m));
        await AddRecipeUsingAsync(options, restaurant, "Quiche", oeuf.Id);
        await AddRecipeUsingAsync(options, restaurant, "Brioche", oeuf.Id);

        // Le renommage voyage avec le changement d'unité : rien ne doit être enregistré.
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SaveAsync(Input(id: oeuf.Id, name: "Oeuf frais", baseUnit: BaseUnit.Gram, price: 0.0003m)));

        Assert.Equal("Impossible de changer l'unité : ingrédient utilisé dans Brioche, Quiche", error.Message);
        var stored = await service.GetAsync(oeuf.Id);
        Assert.NotNull(stored);
        Assert.Equal("Oeuf", stored.Name);
        Assert.Equal(BaseUnit.Piece, stored.BaseUnit);
        Assert.Equal(0.30m, stored.CurrentPricePerBaseUnit);
        var history = Assert.Single(await service.GetPriceHistoryAsync(oeuf.Id));
        Assert.Equal(0.30m, history.PricePerBaseUnit);
    }

    [Fact]
    public async Task Save_UsedIngredient_SameUnit_StillSaves()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var oeuf = await service.SaveAsync(Input(name: "Oeuf", baseUnit: BaseUnit.Piece, price: 0.30m));
        await AddRecipeUsingAsync(options, restaurant, "Quiche", oeuf.Id);

        await service.SaveAsync(Input(id: oeuf.Id, name: "Oeuf frais", baseUnit: BaseUnit.Piece, price: 0.32m));

        var stored = await service.GetAsync(oeuf.Id);
        Assert.Equal("Oeuf frais", stored!.Name);
        Assert.Equal(0.32m, stored.CurrentPricePerBaseUnit);
        Assert.Equal(2, (await service.GetPriceHistoryAsync(oeuf.Id)).Count);
    }

    [Fact]
    public async Task Save_UnitChangeWithoutPrice_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var oeuf = await service.SaveAsync(Input(name: "Oeuf", baseUnit: BaseUnit.Piece, price: 0.30m));

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SaveAsync(Input(id: oeuf.Id, baseUnit: BaseUnit.Gram, price: null)));

        Assert.Equal("Indiquez le prix dans la nouvelle unité", error.Message);
        var stored = await service.GetAsync(oeuf.Id);
        Assert.Equal(BaseUnit.Piece, stored!.BaseUnit);
        Assert.Equal(0.30m, stored.CurrentPricePerBaseUnit);
        Assert.Single(await service.GetPriceHistoryAsync(oeuf.Id));
    }

    [Fact]
    public async Task Save_UnitChangeOnUnusedIngredient_ResetsHistory()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var supplierId = await AddSupplierAsync(options, restaurant);
        var oeuf = await service.SaveAsync(Input(name: "Oeuf", baseUnit: BaseUnit.Piece, price: 0.30m));
        await service.SetPriceAsync(oeuf.Id, 0.35m, PriceSource.Receipt, null, DateTime.UtcNow);
        var otherIngredient = await service.SaveAsync(Input(name: "Sel", price: 0.001m));
        Assert.Equal(2, (await service.GetPriceHistoryAsync(oeuf.Id)).Count);

        var before = DateTime.UtcNow;
        await service.SaveAsync(Input(id: oeuf.Id, baseUnit: BaseUnit.Gram, price: 0.0003m, supplierId: supplierId));
        var after = DateTime.UtcNow;

        var stored = await service.GetAsync(oeuf.Id);
        Assert.Equal(BaseUnit.Gram, stored!.BaseUnit);
        Assert.Equal(0.0003m, stored.CurrentPricePerBaseUnit);
        var entry = Assert.Single(await service.GetPriceHistoryAsync(oeuf.Id)); // les lignes en pièce ont disparu
        Assert.Equal(0.0003m, entry.PricePerBaseUnit);
        Assert.Equal(PriceSource.Manual, entry.Source);
        Assert.Equal(supplierId, entry.SupplierId);
        Assert.InRange(entry.Date, before, after);
        // L'historique des autres ingrédients n'est pas touché.
        Assert.Single(await service.GetPriceHistoryAsync(otherIngredient.Id));
    }

    [Fact]
    public async Task Save_UnitChangeWithSameNumericPrice_StillRecordsTheNewUnitPrice()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var oeuf = await service.SaveAsync(Input(name: "Oeuf", baseUnit: BaseUnit.Piece, price: 0.30m));
        var oldEntryId = Assert.Single(await service.GetPriceHistoryAsync(oeuf.Id)).Id;

        // 0,30 pour une pièce et 0,30 pour un gramme n'ont rien à voir : l'ancienne entrée ne doit pas être gardée.
        await service.SaveAsync(Input(id: oeuf.Id, baseUnit: BaseUnit.Gram, price: 0.30m));

        var entry = Assert.Single(await service.GetPriceHistoryAsync(oeuf.Id));
        Assert.NotEqual(oldEntryId, entry.Id);
        Assert.Equal(0.30m, entry.PricePerBaseUnit);
        Assert.Equal(PriceSource.Manual, entry.Source);
        var stored = await service.GetAsync(oeuf.Id);
        Assert.Equal(BaseUnit.Gram, stored!.BaseUnit);
        Assert.Equal(0.30m, stored.CurrentPricePerBaseUnit);
    }

    // ── Références et identifiants inconnus ──────────────────────────────────

    [Fact]
    public async Task Save_SupplierOfOtherRestaurant_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreignSupplierId = await AddSupplierAsync(options, b);
        var service = CreateService(options, a);

        var onCreate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Input(supplierId: foreignSupplierId)));
        var existing = await service.SaveAsync(Input(name: "Carotte"));
        var onUpdate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Input(id: existing.Id, name: "Carotte", supplierId: foreignSupplierId)));

        Assert.Equal(UnknownSupplierMessage, onCreate.Message);
        Assert.Equal(UnknownSupplierMessage, onUpdate.Message);
        Assert.Equal(["Carotte"], (await service.ListAsync()).Select(i => i.Name));
        Assert.Null((await service.GetAsync(existing.Id))!.SupplierId);
    }

    [Fact]
    public async Task Save_UnknownId_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreign = await CreateService(options, b).SaveAsync(Input(name: "Poivron"));
        var service = CreateService(options, a);

        var unknown = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Input(id: 999_999)));
        var otherRestaurant = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Input(id: foreign.Id, name: "Piraté")));

        Assert.Equal(UnknownIngredientMessage, unknown.Message);
        Assert.Equal(UnknownIngredientMessage, otherRestaurant.Message);
        Assert.Equal("Poivron", (await CreateService(options, b).GetAsync(foreign.Id))!.Name);
        Assert.Empty(await service.ListAsync(includeArchived: true));
    }

    [Fact]
    public async Task Get_OtherRestaurant_ReturnsNull()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreign = await CreateService(options, b).SaveAsync(Input(name: "Poivron"));
        var service = CreateService(options, a);

        Assert.Null(await service.GetAsync(foreign.Id));
        Assert.Null(await service.GetAsync(999_999));
    }

    [Fact]
    public async Task SetArchivedAndDelete_UnknownId_Throw()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreign = await CreateService(options, b).SaveAsync(Input(name: "Poivron"));
        var service = CreateService(options, a);

        var archive = await Assert.ThrowsAsync<DomainValidationException>(() => service.SetArchivedAsync(foreign.Id, true));
        var delete = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(foreign.Id));
        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(999_999));

        Assert.Equal(UnknownIngredientMessage, archive.Message);
        Assert.Equal(UnknownIngredientMessage, delete.Message);
        Assert.Equal(UnknownIngredientMessage, unknown.Message);
        var untouched = await CreateService(options, b).GetAsync(foreign.Id);
        Assert.NotNull(untouched);
        Assert.False(untouched.IsArchived);
    }

    // ── Liste, archivage, suppression ────────────────────────────────────────

    [Fact]
    public async Task List_HidesArchivedByDefault()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var tomate = await service.SaveAsync(Input(name: "Tomate"));
        await service.SaveAsync(Input(name: "Carotte"));

        await service.SetArchivedAsync(tomate.Id, true);

        Assert.Equal(["Carotte"], (await service.ListAsync()).Select(i => i.Name));
        Assert.Equal(["Carotte", "Tomate"], (await service.ListAsync(includeArchived: true)).Select(i => i.Name));

        await service.SetArchivedAsync(tomate.Id, false);

        Assert.Equal(["Carotte", "Tomate"], (await service.ListAsync()).Select(i => i.Name));
    }

    [Fact]
    public async Task List_OrdersByCategoryThenName_AndIncludesSupplier()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var supplierId = await AddSupplierAsync(options, restaurant, "Metro");
        await service.SaveAsync(Input(name: "Tomate", category: "Légumes"));
        await service.SaveAsync(Input(name: "Oeuf", category: "Crémerie", supplierId: supplierId));
        await service.SaveAsync(Input(name: "Carotte", category: "Légumes"));

        var list = await service.ListAsync();

        Assert.Equal(["Oeuf", "Carotte", "Tomate"], list.Select(i => i.Name));
        Assert.Equal("Metro", list[0].Supplier?.Name);
        Assert.Null(list[1].Supplier);
    }

    [Fact]
    public async Task Delete_UsedIngredient_RefusedWithRecipeNames() // UsedInRecipes == ["Carbonnade"]
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await service.SaveAsync(Input(name: "Boeuf", price: 0.02m));
        await AddRecipeUsingAsync(options, restaurant, "Carbonnade", boeuf.Id);

        var result = await service.DeleteAsync(boeuf.Id);

        Assert.False(result.Deleted);
        Assert.Equal(["Carbonnade"], result.UsedInRecipes);
        Assert.NotNull(await service.GetAsync(boeuf.Id));
        Assert.Single(await service.GetPriceHistoryAsync(boeuf.Id));
    }

    [Fact]
    public async Task Delete_UnusedIngredient_Deletes()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var tomate = await service.SaveAsync(Input(name: "Tomate", price: 0.0025m));
        var carotte = await service.SaveAsync(Input(name: "Carotte"));

        var result = await service.DeleteAsync(tomate.Id);

        Assert.True(result.Deleted);
        Assert.Empty(result.UsedInRecipes);
        Assert.Null(await service.GetAsync(tomate.Id));
        Assert.NotNull(await service.GetAsync(carotte.Id));
        await using var db = OpenContext(options, restaurant);
        Assert.Empty(await db.IngredientPrices.ToListAsync()); // l'historique part avec l'ingrédient (cascade)
    }

    [Fact]
    public async Task GetUsage_ListsRecipeNamesOnce_OrderedByName()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await service.SaveAsync(Input(name: "Boeuf"));
        var oignon = await service.SaveAsync(Input(name: "Oignon"));
        await AddRecipeUsingAsync(options, restaurant, "Stoemp", oignon.Id);
        await AddRecipeUsingAsync(options, restaurant, "Carbonnade", boeuf.Id);
        await AddRecipeUsingAsync(options, restaurant, "Carbonnade de luxe", boeuf.Id);

        await using (var db = OpenContext(options, restaurant))
        {
            // Deux lignes du même ingrédient dans une seule recette : la recette n'est comptée qu'une fois.
            var carbonnade = await db.Recipes.SingleAsync(r => r.Name == "Carbonnade");
            db.RecipeLines.Add(new RecipeLine
            {
                RecipeId = carbonnade.Id, IngredientId = boeuf.Id, Quantity = 50m, Unit = MeasureUnit.G
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(["Carbonnade", "Carbonnade de luxe"], await service.GetUsageAsync(boeuf.Id));
        Assert.Equal(["Stoemp"], await service.GetUsageAsync(oignon.Id));
        Assert.Empty(await service.GetUsageAsync(999_999));
    }

    [Fact]
    public async Task DeleteSupplier_NullsIngredientSupplier()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var supplierId = await AddSupplierAsync(options, restaurant);
        var tomate = await service.SaveAsync(Input(price: 0.0025m, supplierId: supplierId));

        await CreateSupplierService(options, restaurant).DeleteAsync(supplierId);

        var stored = await service.GetAsync(tomate.Id);
        Assert.NotNull(stored);
        Assert.Null(stored.SupplierId);
        Assert.Null(stored.Supplier);
        var history = Assert.Single(await service.GetPriceHistoryAsync(tomate.Id));
        Assert.Null(history.SupplierId);
        Assert.Equal(0.0025m, history.PricePerBaseUnit);
    }
}
