using ChefOS.Core.Common;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class SalesServiceTests(PostgresFixture postgres)
{
    private const string UnknownDishMessage = "Plat introuvable";
    private const string WeeklyTotalMondayMessage = "Un total de semaine doit être daté du lundi";
    private const string NegativeQuantityMessage = "La quantité ne peut pas être négative";

    // Semaine du lundi 2 au dimanche 8 novembre 2026 (dates fixes, indépendantes de l'heure d'exécution).
    private static readonly DateTime Monday = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tuesday = Monday.AddDays(1);
    private static readonly DateTime Sunday = Monday.AddDays(6);
    private static readonly DateTime NextMonday = Monday.AddDays(7);
    private static readonly DateTime PreviousSunday = Monday.AddDays(-1);

    private static SalesService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    private static ChefOsDbContext OpenContext(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new TestContextFactory(options, restaurantId).CreateDbContext();

    private static async Task<int> AddRecipeAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        string name,
        string? category = "Plats",
        decimal? price = 22.40m,
        decimal? vat = 0.12m,
        bool archived = false,
        RecipeKind kind = RecipeKind.Dish)
    {
        await using var db = OpenContext(options, restaurantId);
        var recipe = new Recipe
        {
            Name = name,
            Kind = kind,
            Category = category,
            SellingPriceInclVat = price,
            VatRate = vat,
            IsArchived = archived
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    private static async Task AddEntryAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        int recipeId,
        DateTime date,
        decimal quantity,
        ServicePeriod service = ServicePeriod.Journee,
        bool weekly = false)
    {
        await using var db = OpenContext(options, restaurantId);
        db.SalesEntries.Add(new SalesEntry
        {
            RecipeId = recipeId,
            Date = date,
            Service = service,
            IsWeeklyTotal = weekly,
            Quantity = quantity,
            UnitPriceInclVat = 22.40m,
            VatRate = 0.12m,
            UpdatedAt = date
        });
        await db.SaveChangesAsync();
    }

    private static SalesCell Day(int recipeId, DateTime date, decimal quantity, ServicePeriod service = ServicePeriod.Journee) =>
        new(recipeId, date, service, false, quantity);

    private static SalesCell Week(int recipeId, DateTime monday, decimal quantity, ServicePeriod service = ServicePeriod.Journee) =>
        new(recipeId, monday, service, true, quantity);

    private static async Task<List<SalesEntry>> LoadEntriesAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId)
    {
        await using var db = OpenContext(options, restaurantId);
        return await db.SalesEntries.AsNoTracking().OrderBy(e => e.Date).ThenBy(e => e.Service).ThenBy(e => e.IsWeeklyTotal).ToListAsync();
    }

    // ── GetWeekAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWeek_ListsDishesAndExistingCells()
    {
        Assert.Equal(DayOfWeek.Monday, Monday.DayOfWeek);
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");

        var tartare = await AddRecipeAsync(options, restaurant, "Tartare", "Plats", 22.40m);
        var burger = await AddRecipeAsync(options, restaurant, "Burger", "Plats", 18m);
        var tiramisu = await AddRecipeAsync(options, restaurant, "Tiramisu", "Desserts", 9m);
        var special = await AddRecipeAsync(options, restaurant, "Plat du jour", null, 15m);
        var oldDishWithSales = await AddRecipeAsync(options, restaurant, "Ancienne carte", "Plats", 20m, archived: true);
        await AddRecipeAsync(options, restaurant, "Archivé sans vente", "Plats", 20m, archived: true);
        await AddRecipeAsync(options, restaurant, "Fond de veau", null, null, null, kind: RecipeKind.Preparation);
        await AddRecipeAsync(options, other, "Plat d'ailleurs", "Plats", 30m);

        await AddEntryAsync(options, restaurant, tartare, Monday, 4m, ServicePeriod.Midi);
        await AddEntryAsync(options, restaurant, tartare, Monday, 6m, ServicePeriod.Soir);
        await AddEntryAsync(options, restaurant, tartare, Sunday, 3m);
        await AddEntryAsync(options, restaurant, burger, Monday, 25m, ServicePeriod.Journee, weekly: true);
        await AddEntryAsync(options, restaurant, oldDishWithSales, Tuesday, 2.5m);
        // Hors semaine : la veille du lundi et le lundi suivant n'apparaissent pas.
        await AddEntryAsync(options, restaurant, tartare, PreviousSunday, 99m);
        await AddEntryAsync(options, restaurant, tartare, NextMonday, 98m);
        await AddEntryAsync(options, restaurant, burger, NextMonday, 97m, weekly: true);

        // Un mercredi quelconque donne la même semaine.
        var week = await CreateService(options, restaurant).GetWeekAsync(new DateTime(2026, 11, 4, 15, 30, 0, DateTimeKind.Utc));

        Assert.Equal(Monday, week.Monday);
        Assert.Equal(DateTimeKind.Utc, week.Monday.Kind);
        // Catégorie (sans catégorie en dernier) puis nom ; l'archivé avec ventes est là, l'archivé sans vente, la préparation et l'autre restaurant non.
        Assert.Equal(
            ["Tiramisu", "Ancienne carte", "Burger", "Tartare", "Plat du jour"],
            week.Dishes.Select(d => d.Name));

        var tartareRow = week.Dishes.Single(d => d.RecipeId == tartare);
        Assert.Equal(22.40m, tartareRow.UnitPriceInclVat);
        Assert.Equal(
            [
                Day(tartare, Monday, 4m, ServicePeriod.Midi),
                Day(tartare, Monday, 6m, ServicePeriod.Soir),
                Day(tartare, Sunday, 3m)
            ],
            tartareRow.Cells);

        Assert.Equal([Week(burger, Monday, 25m)], week.Dishes.Single(d => d.RecipeId == burger).Cells);
        Assert.Equal([Day(oldDishWithSales, Tuesday, 2.5m)], week.Dishes.Single(d => d.RecipeId == oldDishWithSales).Cells);
        Assert.Empty(week.Dishes.Single(d => d.RecipeId == tiramisu).Cells);
        Assert.Empty(week.Dishes.Single(d => d.RecipeId == special).Cells);
        Assert.All(week.Dishes.SelectMany(d => d.Cells), c => Assert.Equal(DateTimeKind.Utc, c.Date.Kind));
    }

    [Fact]
    public async Task GetWeek_AnyDayOfTheWeekGivesItsMonday()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        // Le dimanche 23:59 est encore dans la semaine ; le lundi 00:00 ouvre la suivante.
        Assert.Equal(Monday, (await service.GetWeekAsync(Monday)).Monday);
        Assert.Equal(Monday, (await service.GetWeekAsync(Sunday.AddHours(23).AddMinutes(59))).Monday);
        Assert.Equal(NextMonday, (await service.GetWeekAsync(NextMonday)).Monday);
        Assert.Equal(Monday.AddDays(-7), (await service.GetWeekAsync(PreviousSunday)).Monday);
    }

    // ── SaveCellsAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SaveCells_UpsertsFreezesPriceAndDeletesZeros()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare", price: 22.40m, vat: 0.12m);
        var burger = await AddRecipeAsync(options, restaurant, "Burger", price: 18m, vat: 0.06m);
        var service = CreateService(options, restaurant);

        // Création : prix et TVA du plat figés, y compris une demi-portion.
        await service.SaveCellsAsync(
        [
            Day(tartare, Monday, 4m, ServicePeriod.Midi),
            Day(tartare, Monday, 1.5m, ServicePeriod.Soir),
            Week(burger, Monday, 30m)
        ]);

        var created = await LoadEntriesAsync(options, restaurant);
        Assert.Equal(3, created.Count);
        var midi = created.Single(e => e.RecipeId == tartare && e.Service == ServicePeriod.Midi);
        Assert.Equal(4m, midi.Quantity);
        Assert.Equal(22.40m, midi.UnitPriceInclVat);
        Assert.Equal(0.12m, midi.VatRate);
        Assert.False(midi.IsWeeklyTotal);
        Assert.Equal(Monday, midi.Date);
        Assert.Equal(1.5m, created.Single(e => e.Service == ServicePeriod.Soir).Quantity);
        var weekly = created.Single(e => e.IsWeeklyTotal);
        Assert.Equal(30m, weekly.Quantity);
        Assert.Equal(18m, weekly.UnitPriceInclVat);
        Assert.Equal(0.06m, weekly.VatRate);

        // Mise à jour d'une saisie existante (même clé) : pas de doublon.
        await service.SaveCellsAsync([Day(tartare, Monday, 7m, ServicePeriod.Midi)]);
        var updated = await LoadEntriesAsync(options, restaurant);
        Assert.Equal(3, updated.Count);
        Assert.Equal(7m, updated.Single(e => e.RecipeId == tartare && e.Service == ServicePeriod.Midi).Quantity);

        // Zéro : la saisie disparaît ; zéro sans saisie existante ne crée rien.
        await service.SaveCellsAsync([Day(tartare, Monday, 0m, ServicePeriod.Soir), Day(tartare, Tuesday, 0m)]);
        var afterDelete = await LoadEntriesAsync(options, restaurant);
        Assert.Equal(2, afterDelete.Count);
        Assert.DoesNotContain(afterDelete, e => e.Service == ServicePeriod.Soir && !e.IsWeeklyTotal);
        Assert.DoesNotContain(afterDelete, e => e.Date == Tuesday);
    }

    [Fact]
    public async Task SaveCells_WeeklyTotalMustBeMonday()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SaveCellsAsync([Week(tartare, Tuesday, 10m)]));
        Assert.Equal(WeeklyTotalMondayMessage, error.Message);
        Assert.Empty(await LoadEntriesAsync(options, restaurant));

        // Le total d'un lundi passe, à côté des saisies par jour du même lundi (clés distinctes).
        await service.SaveCellsAsync([Week(tartare, Monday, 10m), Day(tartare, Monday, 2m)]);
        Assert.Equal(2, (await LoadEntriesAsync(options, restaurant)).Count);
    }

    [Fact]
    public async Task SaveCells_NegativeRefused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare");

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            CreateService(options, restaurant).SaveCellsAsync([Day(tartare, Monday, -1m)]));

        Assert.Equal(NegativeQuantityMessage, error.Message);
        Assert.Empty(await LoadEntriesAsync(options, restaurant));
    }

    [Fact]
    public async Task SaveCells_DishWithoutPriceRefused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var withoutPrice = await AddRecipeAsync(options, restaurant, "Plat sans prix", price: null, vat: null);
        var withoutVat = await AddRecipeAsync(options, restaurant, "Plat sans TVA", price: 10m, vat: null);
        var service = CreateService(options, restaurant);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SaveCellsAsync([Day(withoutPrice, Monday, 3m)]));
        Assert.Equal("Le plat Plat sans prix n'a pas de prix de vente", error.Message);

        error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SaveCellsAsync([Day(withoutVat, Monday, 3m)]));
        Assert.Equal("Le plat Plat sans TVA n'a pas de prix de vente", error.Message);

        // Effacer (0) reste permis : aucun prix n'est figé.
        await service.SaveCellsAsync([Day(withoutPrice, Monday, 0m)]);
        Assert.Empty(await LoadEntriesAsync(options, restaurant));
    }

    [Fact]
    public async Task SaveCells_ForeignDishRefused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");
        var foreignDish = await AddRecipeAsync(options, other, "Plat d'ailleurs");
        var preparation = await AddRecipeAsync(options, restaurant, "Fond de veau", null, null, null, kind: RecipeKind.Preparation);
        var service = CreateService(options, restaurant);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SaveCellsAsync([Day(foreignDish, Monday, 3m)]));
        Assert.Equal(UnknownDishMessage, error.Message);

        error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SaveCellsAsync([Day(preparation, Monday, 3m)]));
        Assert.Equal(UnknownDishMessage, error.Message);

        error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SaveCellsAsync([Day(999_999, Monday, 3m)]));
        Assert.Equal(UnknownDishMessage, error.Message);

        Assert.Empty(await LoadEntriesAsync(options, restaurant));
        Assert.Empty(await LoadEntriesAsync(options, other));
    }

    [Fact]
    public async Task SaveCells_LaterPriceChange_DoesNotAlterSavedEntry()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare", price: 22.40m, vat: 0.12m);
        var service = CreateService(options, restaurant);

        await service.SaveCellsAsync([Day(tartare, Monday, 4m)]);

        await using (var db = OpenContext(options, restaurant))
        {
            var dish = await db.Recipes.SingleAsync(r => r.Id == tartare);
            dish.SellingPriceInclVat = 25m;
            dish.VatRate = 0.06m;
            await db.SaveChangesAsync();
        }

        var entry = Assert.Single(await LoadEntriesAsync(options, restaurant));
        Assert.Equal(22.40m, entry.UnitPriceInclVat);
        Assert.Equal(0.12m, entry.VatRate);

        // La grille affiche le prix actuel du plat, pas celui de la saisie.
        var week = await service.GetWeekAsync(Monday);
        Assert.Equal(25m, week.Dishes.Single().UnitPriceInclVat);
    }

    [Fact]
    public async Task SaveCells_IsAllOrNothing()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare");
        var burger = await AddRecipeAsync(options, restaurant, "Burger");
        await AddEntryAsync(options, restaurant, burger, Monday, 5m);
        var service = CreateService(options, restaurant);

        // Une cellule valide (création) et une effacement valide, suivies d'une cellule invalide : rien n'est appliqué.
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveCellsAsync(
        [
            Day(tartare, Monday, 4m),
            Day(burger, Monday, 0m),
            Week(tartare, Tuesday, 10m)
        ]));

        var entries = await LoadEntriesAsync(options, restaurant);
        var remaining = Assert.Single(entries);
        Assert.Equal(burger, remaining.RecipeId);
        Assert.Equal(5m, remaining.Quantity);
    }

    [Fact]
    public async Task SaveCells_DuplicateKeysInOneCall_LastOneWins()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare");
        var burger = await AddRecipeAsync(options, restaurant, "Burger");
        var service = CreateService(options, restaurant);

        await service.SaveCellsAsync(
        [
            Day(tartare, Monday, 4m),
            Day(tartare, Monday.AddHours(13), 9m), // même jour (UTC), même clé
            Day(burger, Monday, 3m),
            Day(burger, Monday, 0m) // effacement en dernier : rien n'est créé
        ]);

        var entry = Assert.Single(await LoadEntriesAsync(options, restaurant));
        Assert.Equal(tartare, entry.RecipeId);
        Assert.Equal(9m, entry.Quantity);
    }

    [Fact]
    public async Task SaveCells_NormalizesDateToUtcDay()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);

        await service.SaveCellsAsync(
        [
            Day(tartare, new DateTime(2026, 11, 3, 17, 45, 12, DateTimeKind.Utc), 4m, ServicePeriod.Midi),
            Day(tartare, new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Unspecified), 2m, ServicePeriod.Midi)
        ]);

        var entries = await LoadEntriesAsync(options, restaurant);
        Assert.Equal([new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc)],
            entries.Select(e => e.Date));
        Assert.All(entries, e => Assert.Equal(DateTimeKind.Utc, e.Date.Kind));
    }

    [Fact]
    public async Task SaveCells_OtherRestaurantEntriesAreUntouched()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");
        var tartare = await AddRecipeAsync(options, restaurant, "Tartare");
        var otherTartare = await AddRecipeAsync(options, other, "Tartare");
        await AddEntryAsync(options, other, otherTartare, Monday, 50m);

        await CreateService(options, restaurant).SaveCellsAsync([Day(tartare, Monday, 4m)]);

        Assert.Equal(50m, Assert.Single(await LoadEntriesAsync(options, other)).Quantity);
        Assert.Equal(4m, Assert.Single(await LoadEntriesAsync(options, restaurant)).Quantity);
        Assert.DoesNotContain((await CreateService(options, other).GetWeekAsync(Monday)).Dishes.SelectMany(d => d.Cells), c => c.Quantity == 4m);
    }
}
