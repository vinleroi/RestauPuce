using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class InventoryServiceTests(PostgresFixture postgres)
{
    private const string UnknownCountMessage = "Inventaire introuvable";
    private const string ClosedMessage = "Inventaire clôturé";
    private const string FutureMessage = "La date ne peut pas être dans le futur";
    private const string NegativeQuantityMessage = "La quantité ne peut pas être négative";
    private const string QuantityTooHighMessage = "Quantité trop élevée";
    private const string UnknownIngredientMessage = "Ingrédient introuvable";
    private const string EmptyCloseMessage = "Saisissez au moins une quantité avant de clôturer";
    private const string ClosedCannotBeDeletedMessage = "Un inventaire clôturé ne peut pas être supprimé";

    private static readonly DateTime Oct15 = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Nov2 = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Nov3 = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static InventoryService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    private static ChefOsDbContext OpenContext(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new TestContextFactory(options, restaurantId).CreateDbContext();

    private static async Task<int> AddIngredientAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        string name,
        string category = "Divers",
        BaseUnit baseUnit = BaseUnit.Gram,
        decimal? price = null,
        decimal? pieceWeight = null,
        bool archived = false)
    {
        await using var db = OpenContext(options, restaurantId);
        var ingredient = new Ingredient
        {
            Name = name,
            Category = category,
            BaseUnit = baseUnit,
            CurrentPricePerBaseUnit = price,
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

    private static StockCountLineInput Line(int ingredientId, decimal quantity, MeasureUnit unit = MeasureUnit.G) =>
        new(ingredientId, quantity, unit);

    private static async Task<StockCount> LoadCountAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId, int id)
    {
        await using var db = OpenContext(options, restaurantId);
        return await db.StockCounts.AsNoTracking().Include(c => c.Lines.OrderBy(l => l.Id)).SingleAsync(c => c.Id == id);
    }

    /// <summary>Inventaire clôturé, avec une seule ligne, créé à la date donnée.</summary>
    private static async Task<int> CreateClosedCountAsync(InventoryService service, DateTime day, int ingredientId, decimal grams)
    {
        var id = await service.CreateAsync(day, null);
        await service.SaveLinesAsync(id, [Line(ingredientId, grams)]);
        await service.CloseAsync(id);
        return id;
    }

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Inventory_Create_StartsADraftOnTheUtcDay()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var id = await service.CreateAsync(new DateTime(2026, 11, 3, 17, 45, 0, DateTimeKind.Unspecified), "  fin de mois  ");
        var blank = await service.CreateAsync(Nov2, "   ");

        var count = await LoadCountAsync(options, restaurant, id);
        Assert.Equal(restaurant, count.RestaurantId);
        Assert.Equal(Nov3, count.Date);
        Assert.Equal(DateTimeKind.Utc, count.Date.Kind);
        Assert.Equal(StockCountStatus.Brouillon, count.Status);
        Assert.Equal("fin de mois", count.Note);
        Assert.Null(count.ClosedAt);
        Assert.Empty(count.Lines);
        Assert.Null((await LoadCountAsync(options, restaurant, blank)).Note);
    }

    [Fact]
    public async Task Inventory_Create_RefusesFutureDays_ButAllowsTomorrow()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var tomorrow = DateTime.UtcNow.Date.AddDays(1);
        await service.CreateAsync(tomorrow, null);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(tomorrow.AddDays(1), null));
        Assert.Equal(FutureMessage, ex.Message);
        Assert.Single(await service.ListAsync());
    }

    // ── GetSheetAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task Inventory_GetSheet_CarriesTheAveragePieceWeight_SoTheSheetCanOfferPieces()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var citron = await AddIngredientAsync(options, restaurant, "Citron", "Fruits", pieceWeight: 90m);
        var farine = await AddIngredientAsync(options, restaurant, "Farine", "Épicerie");
        var id = await service.CreateAsync(Nov3, null);

        var sheet = await service.GetSheetAsync(id);

        Assert.Equal(90m, sheet.Lines.Single(l => l.IngredientId == citron).AveragePieceWeightGrams);
        Assert.Null(sheet.Lines.Single(l => l.IngredientId == farine).AveragePieceWeightGrams);
    }


    [Fact]
    public async Task Inventory_GetSheet_ListsActiveAndCountedArchivedIngredients_ByCategoryThenName()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");
        var service = CreateService(options, restaurant);

        var farine = await AddIngredientAsync(options, restaurant, "Farine", "Épicerie");
        var sucre = await AddIngredientAsync(options, restaurant, "sucre", "Épicerie");
        var beurre = await AddIngredientAsync(options, restaurant, "Beurre", "Crémerie");
        var lait = await AddIngredientAsync(options, restaurant, "Lait", "Crémerie", BaseUnit.Milliliter);
        var oeuf = await AddIngredientAsync(options, restaurant, "Oeuf", "Crémerie", BaseUnit.Piece);
        var truffe = await AddIngredientAsync(options, restaurant, "Truffe", "Épicerie", archived: true);          // archivée et comptée
        await AddIngredientAsync(options, restaurant, "Caviar", "Épicerie", archived: true);                       // archivée, pas comptée
        await AddIngredientAsync(options, other, "Homard", "Poissons");

        var id = await service.CreateAsync(Nov3, "Fin de mois");
        await service.SaveLinesAsync(id, [Line(lait, 2m, MeasureUnit.L), Line(truffe, 150m), Line(oeuf, 12m, MeasureUnit.Piece)]);

        var sheet = await service.GetSheetAsync(id);

        Assert.Equal(id, sheet.Id);
        Assert.Equal(Nov3, sheet.Date);
        Assert.Equal(DateTimeKind.Utc, sheet.Date.Kind);
        Assert.Equal(StockCountStatus.Brouillon, sheet.Status);
        Assert.Equal("Fin de mois", sheet.Note);
        Assert.Equal(
            ["Beurre", "Lait", "Oeuf", "Farine", "sucre", "Truffe"],
            sheet.Lines.Select(l => l.Name));
        Assert.Equal(
            ["Crémerie", "Crémerie", "Crémerie", "Épicerie", "Épicerie", "Épicerie"],
            sheet.Lines.Select(l => l.Category));

        // Les valeurs saisies sont rendues telles que saisies (quantité et unité), null pour un ingrédient non compté.
        var byName = sheet.Lines.ToDictionary(l => l.Name);
        Assert.Equal(new StockCountSheetLine(beurre, "Beurre", "Crémerie", BaseUnit.Gram, null, null), byName["Beurre"]);
        Assert.Equal(new StockCountSheetLine(lait, "Lait", "Crémerie", BaseUnit.Milliliter, 2m, MeasureUnit.L), byName["Lait"]);
        Assert.Equal(new StockCountSheetLine(oeuf, "Oeuf", "Crémerie", BaseUnit.Piece, 12m, MeasureUnit.Piece), byName["Oeuf"]);
        Assert.Equal(new StockCountSheetLine(farine, "Farine", "Épicerie", BaseUnit.Gram, null, null), byName["Farine"]);
        Assert.Null(byName["sucre"].Quantity);
        Assert.Equal(150m, byName["Truffe"].Quantity);
        Assert.Equal(MeasureUnit.G, byName["Truffe"].Unit);
        Assert.Equal(sucre, byName["sucre"].IngredientId);

        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => service.GetSheetAsync(999_999));
        Assert.Equal(UnknownCountMessage, unknown.Message);
    }

    // ── SaveLinesAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Inventory_SaveLines_ConvertsToBase()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var farine = await AddIngredientAsync(options, restaurant, "Farine");
        var lait = await AddIngredientAsync(options, restaurant, "Lait", baseUnit: BaseUnit.Milliliter);
        var huile = await AddIngredientAsync(options, restaurant, "Huile", baseUnit: BaseUnit.Milliliter);
        var oeuf = await AddIngredientAsync(options, restaurant, "Oeuf", baseUnit: BaseUnit.Piece, pieceWeight: 60m);
        var citron = await AddIngredientAsync(options, restaurant, "Citron", baseUnit: BaseUnit.Piece);
        var id = await service.CreateAsync(Nov3, null);

        await service.SaveLinesAsync(id,
        [
            Line(farine, 2.5m, MeasureUnit.Kg),
            Line(lait, 1.5m, MeasureUnit.L),
            Line(huile, 33m, MeasureUnit.Cl),
            Line(oeuf, 720m, MeasureUnit.G),
            Line(citron, 5m, MeasureUnit.Piece)
        ]);

        var lines = (await LoadCountAsync(options, restaurant, id)).Lines.ToDictionary(l => l.IngredientId);
        Assert.Equal(5, lines.Count);
        Assert.All(lines.Values, l => Assert.Equal(restaurant, l.RestaurantId));
        Assert.Equal((2.5m, MeasureUnit.Kg, 2500m), (lines[farine].Quantity, lines[farine].Unit, lines[farine].BaseQuantity));
        Assert.Equal((1.5m, MeasureUnit.L, 1500m), (lines[lait].Quantity, lines[lait].Unit, lines[lait].BaseQuantity));
        Assert.Equal((33m, MeasureUnit.Cl, 330m), (lines[huile].Quantity, lines[huile].Unit, lines[huile].BaseQuantity));
        Assert.Equal((720m, MeasureUnit.G, 12m), (lines[oeuf].Quantity, lines[oeuf].Unit, lines[oeuf].BaseQuantity));
        Assert.Equal((5m, MeasureUnit.Piece, 5m), (lines[citron].Quantity, lines[citron].Unit, lines[citron].BaseQuantity));
    }

    [Fact]
    public async Task Inventory_SaveLines_ReplacesTheCountedLines_UpdatingInPlace()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var farine = await AddIngredientAsync(options, restaurant, "Farine");
        var sucre = await AddIngredientAsync(options, restaurant, "Sucre");
        var sel = await AddIngredientAsync(options, restaurant, "Sel");
        var id = await service.CreateAsync(Nov3, null);

        await service.SaveLinesAsync(id, [Line(farine, 1000m), Line(sucre, 500m)]);
        var farineLineId = (await LoadCountAsync(options, restaurant, id)).Lines.Single(l => l.IngredientId == farine).Id;

        // Une quantité de 0 est une vraie saisie ; un ingrédient absent de la liste n'est plus compté (sa ligne disparaît) ;
        // un ingrédient répété garde sa dernière valeur ; la ligne existante est modifiée sur place.
        await service.SaveLinesAsync(id, [Line(farine, 2m, MeasureUnit.Kg), Line(sel, 0m), Line(farine, 3m, MeasureUnit.Kg)]);

        var lines = (await LoadCountAsync(options, restaurant, id)).Lines;
        Assert.Equal([farine, sel], lines.Select(l => l.IngredientId).Order());
        var farineLine = lines.Single(l => l.IngredientId == farine);
        Assert.Equal(farineLineId, farineLine.Id);
        Assert.Equal((3m, MeasureUnit.Kg, 3000m), (farineLine.Quantity, farineLine.Unit, farineLine.BaseQuantity));
        var selLine = lines.Single(l => l.IngredientId == sel);
        Assert.Equal((0m, 0m), (selLine.Quantity, selLine.BaseQuantity));

        // Sauvegarder une liste vide vide l'inventaire.
        await service.SaveLinesAsync(id, []);
        Assert.Empty((await LoadCountAsync(options, restaurant, id)).Lines);
    }

    [Fact]
    public async Task Inventory_SaveLines_IsAllOrNothing()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");
        var service = CreateService(options, restaurant);
        var farine = await AddIngredientAsync(options, restaurant, "Farine");
        var lait = await AddIngredientAsync(options, restaurant, "Lait", baseUnit: BaseUnit.Milliliter);
        var foreign = await AddIngredientAsync(options, other, "Homard");
        var id = await service.CreateAsync(Nov3, null);
        await service.SaveLinesAsync(id, [Line(farine, 1000m)]);

        async Task Refused(string message, params StockCountLineInput[] lines)
        {
            // Une ligne valide en tête ne doit rien écrire si une autre est refusée.
            var ex = await Assert.ThrowsAsync<DomainValidationException>(
                () => service.SaveLinesAsync(id, [Line(farine, 7m), .. lines]));
            Assert.Equal(message, ex.Message);

            var saved = Assert.Single((await LoadCountAsync(options, restaurant, id)).Lines);
            Assert.Equal((farine, 1000m), (saved.IngredientId, saved.Quantity));
        }

        await Refused("Unité incompatible pour Lait", Line(lait, 1m, MeasureUnit.Kg));
        await Refused("Unité incompatible pour Farine", Line(farine, 1m, MeasureUnit.Piece)); // pas de poids de pièce
        await Refused(NegativeQuantityMessage, Line(lait, -1m, MeasureUnit.L));
        await Refused(QuantityTooHighMessage, Line(lait, 1_000_000.0001m, MeasureUnit.Ml));
        await Refused(UnknownIngredientMessage, Line(foreign, 1m));
        await Refused(UnknownIngredientMessage, Line(999_999, 1m));

        // Le plafond lui-même est accepté.
        await service.SaveLinesAsync(id, [Line(lait, 1_000_000m, MeasureUnit.Ml)]);
        Assert.Equal(1_000_000m, Assert.Single((await LoadCountAsync(options, restaurant, id)).Lines).Quantity);
    }

    [Fact]
    public async Task Inventory_SaveLines_UnknownCount_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var farine = await AddIngredientAsync(options, restaurant, "Farine");

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => CreateService(options, restaurant).SaveLinesAsync(999_999, [Line(farine, 1m)]));

        Assert.Equal(UnknownCountMessage, ex.Message);
    }

    // ── CloseAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Inventory_Close_ThenReadOnly()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var farine = await AddIngredientAsync(options, restaurant, "Farine");
        var id = await service.CreateAsync(Nov3, null);
        await service.SaveLinesAsync(id, [Line(farine, 1000m)]);

        var before = DateTime.UtcNow;
        await service.CloseAsync(id);
        var after = DateTime.UtcNow;

        var count = await LoadCountAsync(options, restaurant, id);
        Assert.Equal(StockCountStatus.Cloture, count.Status);
        Assert.NotNull(count.ClosedAt);
        Assert.InRange(count.ClosedAt.Value, before, after);

        // Plus aucune écriture : ni lignes, ni nouvelle clôture, ni suppression.
        var save = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveLinesAsync(id, [Line(farine, 5m)]));
        var clear = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveLinesAsync(id, []));
        var close = await Assert.ThrowsAsync<DomainValidationException>(() => service.CloseAsync(id));
        var delete = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(id));
        Assert.Equal(ClosedMessage, save.Message);
        Assert.Equal(ClosedMessage, clear.Message);
        Assert.Equal(ClosedMessage, close.Message);
        Assert.Equal(ClosedCannotBeDeletedMessage, delete.Message);

        var unchanged = await LoadCountAsync(options, restaurant, id);
        Assert.Equal(count.ClosedAt, unchanged.ClosedAt);
        var line = Assert.Single(unchanged.Lines);
        Assert.Equal(1000m, line.Quantity);

        // La feuille d'un inventaire clôturé reste lisible.
        var sheet = await service.GetSheetAsync(id);
        Assert.Equal(StockCountStatus.Cloture, sheet.Status);
        Assert.Equal(1000m, Assert.Single(sheet.Lines).Quantity);
    }

    [Fact]
    public async Task Inventory_Close_EmptyRefused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Nov3, null);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.CloseAsync(id));

        Assert.Equal(EmptyCloseMessage, ex.Message);
        var count = await LoadCountAsync(options, restaurant, id);
        Assert.Equal(StockCountStatus.Brouillon, count.Status);   // le refus laisse le brouillon intact
        Assert.Null(count.ClosedAt);

        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => service.CloseAsync(999_999));
        Assert.Equal(UnknownCountMessage, unknown.Message);
    }

    [Fact]
    public async Task Inventory_Close_ConcurrentCalls_OnlyOneWins()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var farine = await AddIngredientAsync(options, restaurant, "Farine");
        var id = await CreateService(options, restaurant).CreateAsync(Nov3, null);
        await CreateService(options, restaurant).SaveLinesAsync(id, [Line(farine, 1000m)]);

        async Task<Exception?> TryClose()
        {
            try
            {
                await CreateService(options, restaurant).CloseAsync(id);
                return null;
            }
            catch (DomainValidationException ex)
            {
                return ex;
            }
        }

        var results = await Task.WhenAll(TryClose(), TryClose(), TryClose());

        Assert.Equal(1, results.Count(r => r is null));
        Assert.All(results.OfType<DomainValidationException>(), ex => Assert.Equal(ClosedMessage, ex.Message));
        Assert.Equal(StockCountStatus.Cloture, (await LoadCountAsync(options, restaurant, id)).Status);
    }

    [Fact]
    public async Task Inventory_SaveLines_WaitsForAConcurrentClose_ThenRefuses()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var farine = await AddIngredientAsync(options, restaurant, "Farine");
        var sucre = await AddIngredientAsync(options, restaurant, "Sucre");
        var id = await service.CreateAsync(Nov3, null);
        await service.SaveLinesAsync(id, [Line(farine, 1000m)]);

        // Une clôture en cours (transaction ouverte, non validée) tient la ligne de l'inventaire.
        await using var closing = OpenContext(options, restaurant);
        await using var transaction = await closing.Database.BeginTransactionAsync();
        await closing.StockCounts
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Status, StockCountStatus.Cloture));

        var save = service.SaveLinesAsync(id, [Line(farine, 1m), Line(sucre, 2m)]);
        await Task.Delay(500);
        Assert.False(save.IsCompleted);   // la sauvegarde attend l'issue de la clôture au lieu d'écrire dans un inventaire qui se ferme

        await transaction.CommitAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => save);
        Assert.Equal(ClosedMessage, ex.Message);
        var line = Assert.Single((await LoadCountAsync(options, restaurant, id)).Lines);
        Assert.Equal((farine, 1000m), (line.IngredientId, line.Quantity));
    }

    // ── DeleteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Inventory_Delete_DraftOnly()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var farine = await AddIngredientAsync(options, restaurant, "Farine");

        var draft = await service.CreateAsync(Nov2, null);
        await service.SaveLinesAsync(draft, [Line(farine, 1000m)]);
        var closed = await CreateClosedCountAsync(service, Nov3, farine, 500m);

        await service.DeleteAsync(draft);

        await using (var db = OpenContext(options, restaurant))
        {
            Assert.Equal([closed], await db.StockCounts.Select(c => c.Id).ToListAsync());
            Assert.Single(await db.StockCountLines.ToListAsync());   // les lignes du brouillon sont parties avec lui
        }

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(closed));
        Assert.Equal(ClosedCannotBeDeletedMessage, refused.Message);
        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(draft));
        Assert.Equal(UnknownCountMessage, unknown.Message);
    }

    // ── ListAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inventory_Value_UsesPriceAtDate()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.02m);
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), 0.01m);
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc), 0.02m);
        var oeuf = await AddIngredientAsync(options, restaurant, "Oeuf", baseUnit: BaseUnit.Piece, price: 0.30m);   // prix courant, sans historique
        var sel = await AddIngredientAsync(options, restaurant, "Sel");                                              // aucun prix

        // 15 octobre : 2 kg de bœuf à 0,01 + 10 œufs à 0,30 + du sel sans prix (ignoré) = 20 + 3.
        var october = await service.CreateAsync(Oct15, null);
        await service.SaveLinesAsync(october, [Line(boeuf, 2m, MeasureUnit.Kg), Line(oeuf, 10m, MeasureUnit.Piece), Line(sel, 500m)]);
        await service.CloseAsync(october);

        // 3 novembre : les mêmes 2 kg de bœuf, au prix de 18 h le jour même (0,02) = 40, en brouillon.
        var november = await service.CreateAsync(Nov3, null);
        await service.SaveLinesAsync(november, [Line(boeuf, 2000m)]);

        // Aucune ligne : pas de valeur ; que du sel sans prix : pas de valeur non plus.
        var empty = await service.CreateAsync(Nov2, null);
        var unpriced = await service.CreateAsync(Nov2, null);
        await service.SaveLinesAsync(unpriced, [Line(sel, 100m)]);

        var list = await service.ListAsync();

        // Date décroissante, puis le plus récent d'abord à date égale.
        Assert.Equal([november, unpriced, empty, october], list.Select(s => s.Id));
        Assert.Equal(
            new StockCountSummary(november, Nov3, StockCountStatus.Brouillon, 1, 40m), list[0]);
        Assert.Equal(
            new StockCountSummary(unpriced, Nov2, StockCountStatus.Brouillon, 1, null), list[1]);
        Assert.Equal(
            new StockCountSummary(empty, Nov2, StockCountStatus.Brouillon, 0, null), list[2]);
        Assert.Equal(
            new StockCountSummary(october, Oct15, StockCountStatus.Cloture, 3, 23m), list[3]);
        Assert.All(list, s => Assert.Equal(DateTimeKind.Utc, s.Date.Kind));
    }

    [Fact]
    public async Task Inventory_Value_IsFrozenToTheCountDate_NotTheCurrentPrice()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var boeuf = await AddIngredientAsync(options, restaurant, "Boeuf", price: 0.01m);
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 0.01m);
        var id = await CreateClosedCountAsync(service, Oct15, boeuf, 1000m);

        // Le prix monte après l'inventaire : sa valeur ne bouge pas.
        await AddPriceAsync(options, restaurant, boeuf, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), 0.05m);

        Assert.Equal(10m, Assert.Single(await service.ListAsync(), s => s.Id == id).Value);
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

        var myIngredient = await AddIngredientAsync(options, restaurant, "Farine", price: 0.01m);
        var theirIngredient = await AddIngredientAsync(options, other, "Homard", price: 0.05m);
        var myCount = await CreateClosedCountAsync(mine, Nov2, myIngredient, 100m);
        var theirCount = await theirs.CreateAsync(Nov3, null);
        await theirs.SaveLinesAsync(theirCount, [Line(theirIngredient, 5m)]);

        // Chacun ne voit que ses inventaires.
        Assert.Equal([myCount], (await mine.ListAsync()).Select(s => s.Id));
        Assert.Equal([theirCount], (await theirs.ListAsync()).Select(s => s.Id));
        var sheet = await Assert.ThrowsAsync<DomainValidationException>(() => mine.GetSheetAsync(theirCount));
        Assert.Equal(UnknownCountMessage, sheet.Message);

        // On ne modifie, ne clôture ni ne supprime l'inventaire d'un autre restaurant.
        var save = await Assert.ThrowsAsync<DomainValidationException>(() => mine.SaveLinesAsync(theirCount, [Line(myIngredient, 1m)]));
        var close = await Assert.ThrowsAsync<DomainValidationException>(() => mine.CloseAsync(theirCount));
        var delete = await Assert.ThrowsAsync<DomainValidationException>(() => mine.DeleteAsync(theirCount));
        Assert.Equal(UnknownCountMessage, save.Message);
        Assert.Equal(UnknownCountMessage, close.Message);
        Assert.Equal(UnknownCountMessage, delete.Message);

        var untouched = await LoadCountAsync(options, other, theirCount);
        Assert.Equal(StockCountStatus.Brouillon, untouched.Status);
        Assert.Equal((theirIngredient, 5m), (untouched.Lines.Single().IngredientId, untouched.Lines.Single().Quantity));

        // Ni compter l'ingrédient d'un autre restaurant dans son propre inventaire.
        var draft = await mine.CreateAsync(Nov3, null);
        var foreign = await Assert.ThrowsAsync<DomainValidationException>(() => mine.SaveLinesAsync(draft, [Line(theirIngredient, 1m)]));
        Assert.Equal(UnknownIngredientMessage, foreign.Message);
        Assert.Empty((await LoadCountAsync(options, restaurant, draft)).Lines);

        // La feuille ne propose pas les ingrédients de l'autre restaurant.
        Assert.Equal(["Farine"], (await mine.GetSheetAsync(draft)).Lines.Select(l => l.Name));
    }
}
