using ChefOS.Core.Common;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderEntity = ChefOS.Core.Sales.Order;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class OrderServiceTests(PostgresFixture postgres)
{
    private const string NoLineMessage = "Ajoutez au moins un plat";
    private const string QuantityMessage = "La quantité doit être d'au moins 1";
    private const string QuantityTooHighMessage = "Quantité trop élevée";
    private const string UnknownDishMessage = "Plat introuvable";
    private const string UnknownOrderMessage = "Commande introuvable";
    private const string StatusChangedMessage = "La commande a déjà changé de statut";
    private const string NumberMessage = "Impossible d'attribuer un numéro de commande, réessayez";
    private const string ModifiedElsewhereMessage = "La commande a été modifiée sur un autre appareil, rechargez";

    private static readonly DateTime Day = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static OrderService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    private static ChefOsDbContext OpenContext(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new TestContextFactory(options, restaurantId).CreateDbContext();

    private static async Task<int> AddDishAsync(
        DbContextOptions<ChefOsDbContext> options,
        int restaurantId,
        string name,
        decimal? price = 22.40m,
        decimal? vat = 0.12m,
        bool archived = false,
        RecipeKind kind = RecipeKind.Dish)
    {
        await using var db = OpenContext(options, restaurantId);
        var recipe = new Recipe { Name = name, Kind = kind, SellingPriceInclVat = price, VatRate = vat, IsArchived = archived };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    private static OrderInput Order(params OrderLineInput[] lines) =>
        new(OrderSource.Salle, "Table 4", null, lines);

    private static OrderLineInput Line(int recipeId, int quantity = 1, string? note = null) => new(recipeId, quantity, note);

    private static async Task<OrderEntity> LoadOrderAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId, int orderId)
    {
        await using var db = OpenContext(options, restaurantId);
        return await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == orderId);
    }

    /// <summary>Insère une commande à une date fixe (les commandes créées par le service portent l'heure réelle).</summary>
    private static async Task<int> InsertOrderAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, int number, DateTime createdAt, int recipeId, OrderStatus status = OrderStatus.Nouvelle)
    {
        await using var db = OpenContext(options, restaurantId);
        var order = new OrderEntity
        {
            Number = number,
            CreatedAt = createdAt,
            Source = OrderSource.Emporter,
            Status = status,
            StatusChangedAt = createdAt,
            Lines = { new OrderLine { RecipeId = recipeId, Quantity = 2, UnitPriceInclVat = 10m, VatRate = 0.06m } }
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_NumbersSequentiallyPerRestaurant()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurantA = await TestData.CreateRestaurantAsync(options, "Bistro A");
        var restaurantB = await TestData.CreateRestaurantAsync(options, "Bistro B");
        var dishA = await AddDishAsync(options, restaurantA, "Tartare");
        var dishB = await AddDishAsync(options, restaurantB, "Burger");

        var serviceA = CreateService(options, restaurantA);
        var serviceB = CreateService(options, restaurantB);
        var a1 = await serviceA.CreateAsync(Order(Line(dishA)));
        var a2 = await serviceA.CreateAsync(Order(Line(dishA)));
        var b1 = await serviceB.CreateAsync(Order(Line(dishB)));

        Assert.Equal(1, (await LoadOrderAsync(options, restaurantA, a1)).Number);
        Assert.Equal(2, (await LoadOrderAsync(options, restaurantA, a2)).Number);
        Assert.Equal(1, (await LoadOrderAsync(options, restaurantB, b1)).Number);
    }

    [Fact]
    public async Task Create_FreezesPricesAndValidates()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddDishAsync(options, restaurant, "Tartare", 22.40m, 0.12m);
        var burger = await AddDishAsync(options, restaurant, "Burger", 18m, 0.06m);
        var withoutPrice = await AddDishAsync(options, restaurant, "Sans prix", null, null);
        var withoutVat = await AddDishAsync(options, restaurant, "Sans TVA", 10m, null);
        var archived = await AddDishAsync(options, restaurant, "Ancien plat", archived: true);
        var preparation = await AddDishAsync(options, restaurant, "Fond de veau", null, null, kind: RecipeKind.Preparation);
        var service = CreateService(options, restaurant);

        var before = DateTime.UtcNow;
        var id = await service.CreateAsync(new OrderInput(
            OrderSource.Telephone, "  M. Dupont  ", "  Sans oignon  ", [Line(tartare, 2, "  saignant "), Line(burger, 1, " "), Line(tartare, 1)]));
        var after = DateTime.UtcNow;

        var order = await LoadOrderAsync(options, restaurant, id);
        Assert.Equal(1, order.Number);
        Assert.Equal(OrderStatus.Nouvelle, order.Status);
        Assert.Equal(OrderSource.Telephone, order.Source);
        Assert.Equal("M. Dupont", order.TableOrCustomer);
        Assert.Equal("Sans oignon", order.Note);
        Assert.Equal(DateTimeKind.Utc, order.CreatedAt.Kind);
        Assert.InRange(order.CreatedAt, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(order.CreatedAt, order.StatusChangedAt);
        Assert.Equal(3, order.Lines.Count);
        var first = order.Lines.OrderBy(l => l.Id).First();
        Assert.Equal((tartare, 2, 22.40m, 0.12m, "saignant"), (first.RecipeId, first.Quantity, first.UnitPriceInclVat, first.VatRate, first.Note));
        var second = order.Lines.OrderBy(l => l.Id).Skip(1).First();
        Assert.Equal((burger, 1, 18m, 0.06m), (second.RecipeId, second.Quantity, second.UnitPriceInclVat, second.VatRate));
        Assert.Null(second.Note);

        // Les prix restent ceux de la prise de commande, même si le plat change ensuite.
        await using (var db = OpenContext(options, restaurant))
        {
            var dish = await db.Recipes.SingleAsync(r => r.Id == tartare);
            dish.SellingPriceInclVat = 30m;
            await db.SaveChangesAsync();
        }
        var reloaded = await LoadOrderAsync(options, restaurant, id);
        Assert.Contains(reloaded.Lines, l => l.RecipeId == tartare && l.UnitPriceInclVat == 22.40m);

        // Validations.
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Order()));
        Assert.Equal(NoLineMessage, error.Message);

        error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Order(Line(tartare, 0))));
        Assert.Equal(QuantityMessage, error.Message);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Order(Line(tartare, -2))));
        Assert.Equal(QuantityMessage, error.Message);

        error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Order(Line(withoutPrice))));
        Assert.Equal("Le plat Sans prix n'a pas de prix de vente", error.Message);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Order(Line(withoutVat))));
        Assert.Equal("Le plat Sans TVA n'a pas de prix de vente", error.Message);

        foreach (var unavailable in new[] { archived, preparation, 999_999 })
        {
            error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Order(Line(tartare), Line(unavailable))));
            Assert.Equal(UnknownDishMessage, error.Message);
        }

        // Aucune commande n'a été créée par les refus (et le numéro suivant n'est pas consommé).
        var next = await service.CreateAsync(Order(Line(burger)));
        Assert.Equal(2, (await LoadOrderAsync(options, restaurant, next)).Number);
    }

    [Fact]
    public async Task LineQuantityCeiling_AppliesToCreateAndUpdate()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Order(Line(dish, 1001))));
        Assert.Equal(QuantityTooHighMessage, error.Message);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Order(Line(dish), Line(dish, int.MaxValue))));
        Assert.Equal(QuantityTooHighMessage, error.Message);

        // Le plafond lui-même est permis.
        var id = await service.CreateAsync(Order(Line(dish, 1000)));
        Assert.Equal(1000, Assert.Single((await LoadOrderAsync(options, restaurant, id)).Lines).Quantity);

        error = await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateLinesAsync(id, [Line(dish, 1001)]));
        Assert.Equal(QuantityTooHighMessage, error.Message);
        Assert.Equal(1000, Assert.Single((await LoadOrderAsync(options, restaurant, id)).Lines).Quantity);
    }

    [Fact]
    public async Task Create_ForeignDishRefused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");
        var foreignDish = await AddDishAsync(options, other, "Plat d'ailleurs");

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            CreateService(options, restaurant).CreateAsync(Order(Line(foreignDish))));

        Assert.Equal(UnknownDishMessage, error.Message);
    }

    [Fact]
    public async Task Create_Concurrent_NoDuplicateNumbers()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");

        // Cinq services (donc cinq contextes) indépendants, lancés en même temps.
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(async () =>
            {
                var service = CreateService(options, restaurant);
                gate.Wait();
                return await service.CreateAsync(Order(Line(dish)));
            }))
            .ToList();
        gate.Set();
        var ids = await Task.WhenAll(tasks);

        Assert.Equal(5, ids.Distinct().Count());
        await using var db = OpenContext(options, restaurant);
        var numbers = await db.Orders.Select(o => o.Number).OrderBy(n => n).ToListAsync();
        Assert.Equal([1, 2, 3, 4, 5], numbers);
    }

    /// <summary>
    /// Juste avant l'insertion d'une commande, valide dans une autre transaction une commande portant le numéro que celle-ci va prendre :
    /// provoque la violation de <c>IX_Orders_Number</c>, <see cref="Conflicts"/> fois de suite.
    /// </summary>
    private sealed class CompetingOrderInterceptor(DbContextOptions<ChefOsDbContext> options, int restaurantId, int dishId, int conflicts)
        : SaveChangesInterceptor
    {
        public int Conflicts { get; } = conflicts;
        public int Attempts { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var inserting = eventData.Context!.ChangeTracker.Entries<OrderEntity>().FirstOrDefault(e => e.State == EntityState.Added);
            if (inserting is null)
                return result;

            Attempts++;
            if (Attempts <= Conflicts)
            {
                await using var db = OpenContext(options, restaurantId);
                db.Orders.Add(new OrderEntity
                {
                    Number = inserting.Entity.Number,
                    CreatedAt = Day,
                    Source = OrderSource.Salle,
                    Status = OrderStatus.Nouvelle,
                    StatusChangedAt = Day,
                    Lines = { new OrderLine { RecipeId = dishId, Quantity = 1, UnitPriceInclVat = 10m, VatRate = 0.06m } }
                });
                await db.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    private static OrderService CreateInterceptedService(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, CompetingOrderInterceptor interceptor)
    {
        var intercepted = new DbContextOptionsBuilder<ChefOsDbContext>(options).AddInterceptors(interceptor).Options;
        return CreateService(intercepted, restaurantId);
    }

    [Fact]
    public async Task Create_RetriesAfterNumberConflict()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");

        // Cinq conflits de suite : la sixième tentative (cinquième nouvel essai) réussit, avec le numéro qui suit les cinq concurrents.
        var interceptor = new CompetingOrderInterceptor(options, restaurant, dish, conflicts: 5);
        var id = await CreateInterceptedService(options, restaurant, interceptor).CreateAsync(Order(Line(dish)));

        Assert.Equal(6, interceptor.Attempts);
        Assert.Equal(6, (await LoadOrderAsync(options, restaurant, id)).Number);
        await using var db = OpenContext(options, restaurant);
        Assert.Equal(6, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Create_GivesUpAfterFiveRetries()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");

        var interceptor = new CompetingOrderInterceptor(options, restaurant, dish, conflicts: int.MaxValue);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            CreateInterceptedService(options, restaurant, interceptor).CreateAsync(Order(Line(dish))));

        Assert.Equal(NumberMessage, error.Message);
        Assert.Equal(6, interceptor.Attempts);
        // Seules les six commandes des concurrents existent : la commande refusée n'a rien laissé derrière elle (ni lignes).
        await using var db = OpenContext(options, restaurant);
        Assert.Equal(6, await db.Orders.CountAsync());
        Assert.Equal(6, await db.OrderLines.CountAsync());
    }

    // ── Statuts ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Advance_FollowsLifecycle_ThenRefused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));
        var created = await LoadOrderAsync(options, restaurant, id);

        var previousChange = created.StatusChangedAt;
        var from = OrderStatus.Nouvelle;
        foreach (var expected in new[] { OrderStatus.EnPreparation, OrderStatus.Prete, OrderStatus.Servie })
        {
            await service.AdvanceAsync(id, from);
            from = expected;

            var order = await LoadOrderAsync(options, restaurant, id);
            Assert.Equal(expected, order.Status);
            Assert.Equal(DateTimeKind.Utc, order.StatusChangedAt.Kind);
            Assert.True(order.StatusChangedAt >= previousChange);
            previousChange = order.StatusChangedAt;
        }
        Assert.True(previousChange > created.StatusChangedAt);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.AdvanceAsync(id, OrderStatus.Servie));
        Assert.Equal("Cette commande est déjà servie", error.Message);
        Assert.Equal(OrderStatus.Servie, (await LoadOrderAsync(options, restaurant, id)).Status);
    }

    [Fact]
    public async Task Advance_RefusedOnCancelledOrder()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));
        await service.CancelAsync(id, OrderStatus.Nouvelle);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.AdvanceAsync(id, OrderStatus.Annulee));

        Assert.Equal("Une commande annulée ne peut plus avancer", error.Message);
        Assert.Equal(OrderStatus.Annulee, (await LoadOrderAsync(options, restaurant, id)).Status);
    }

    private sealed record AdvanceOutcome(string? Message, bool Ok);

    [Fact]
    public async Task Advance_Concurrent_NeverSkipsOrRepeatsAStep()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var id = await CreateService(options, restaurant).CreateAsync(Order(Line(dish)));

        // Quatre « Avancer » simultanés depuis « Nouvelle » (le statut que montrent quatre écrans identiques) sur des services indépendants :
        // un seul passe, quel que soit l'entrelacement, et les autres sont refusés (sans mise à jour conditionnelle, deux appels pourraient écrire le même pas).
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                var service = CreateService(options, restaurant);
                gate.Wait();
                try
                {
                    await service.AdvanceAsync(id, OrderStatus.Nouvelle);
                    return new AdvanceOutcome(null, true);
                }
                catch (DomainValidationException ex)
                {
                    return new AdvanceOutcome(ex.Message, false);
                }
            }))
            .ToList();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(OrderStatus.EnPreparation, (await LoadOrderAsync(options, restaurant, id)).Status);
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.All(results.Where(r => !r.Ok), r => Assert.Equal(StatusChangedMessage, r.Message));
    }

    [Fact]
    public async Task Advance_FromStaleStatus_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));
        await service.AdvanceAsync(id, OrderStatus.Nouvelle);
        await service.AdvanceAsync(id, OrderStatus.EnPreparation);
        var before = await LoadOrderAsync(options, restaurant, id);

        // Un écran périmé montre encore la commande « En préparation » : son bouton « Marquer prête » ne doit pas la servir.
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.AdvanceAsync(id, OrderStatus.EnPreparation));

        Assert.Equal(StatusChangedMessage, error.Message);
        var after = await LoadOrderAsync(options, restaurant, id);
        Assert.Equal(OrderStatus.Prete, after.Status);
        Assert.Equal(before.StatusChangedAt, after.StatusChangedAt);
    }

    [Fact]
    public async Task Advance_FromStaleStatus_Refused_EvenWhenTheOrderWasServedMeanwhile()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));
        await service.AdvanceAsync(id, OrderStatus.Nouvelle);
        await service.AdvanceAsync(id, OrderStatus.EnPreparation);
        await service.AdvanceAsync(id, OrderStatus.Prete);   // servie

        // L'écran montre « Prête » mais la commande est servie : c'est un changement de statut, pas « déjà servie ».
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.AdvanceAsync(id, OrderStatus.Prete));

        Assert.Equal(StatusChangedMessage, error.Message);
        Assert.Equal(OrderStatus.Servie, (await LoadOrderAsync(options, restaurant, id)).Status);
    }

    [Fact]
    public async Task Cancel_FromStaleStatus_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));
        await service.AdvanceAsync(id, OrderStatus.Nouvelle);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CancelAsync(id, OrderStatus.Nouvelle));

        Assert.Equal(StatusChangedMessage, error.Message);
        Assert.Equal(OrderStatus.EnPreparation, (await LoadOrderAsync(options, restaurant, id)).Status);
    }

    [Fact]
    public async Task Cancel_Works_UntilServed_ThenRefused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);

        var fresh = await service.CreateAsync(Order(Line(dish)));
        await service.CancelAsync(fresh, OrderStatus.Nouvelle);
        Assert.Equal(OrderStatus.Annulee, (await LoadOrderAsync(options, restaurant, fresh)).Status);

        var ready = await service.CreateAsync(Order(Line(dish)));
        await service.AdvanceAsync(ready, OrderStatus.Nouvelle);
        await service.AdvanceAsync(ready, OrderStatus.EnPreparation);
        await service.CancelAsync(ready, OrderStatus.Prete);
        Assert.Equal(OrderStatus.Annulee, (await LoadOrderAsync(options, restaurant, ready)).Status);

        // Annuler une commande déjà annulée ne change rien.
        var cancelledAt = (await LoadOrderAsync(options, restaurant, ready)).StatusChangedAt;
        await service.CancelAsync(ready, OrderStatus.Annulee);
        var again = await LoadOrderAsync(options, restaurant, ready);
        Assert.Equal(OrderStatus.Annulee, again.Status);
        Assert.Equal(cancelledAt, again.StatusChangedAt);
    }

    [Fact]
    public async Task Cancel_RefusedWhenServed()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));
        await service.AdvanceAsync(id, OrderStatus.Nouvelle);
        await service.AdvanceAsync(id, OrderStatus.EnPreparation);
        await service.AdvanceAsync(id, OrderStatus.Prete);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.CancelAsync(id, OrderStatus.Servie));

        Assert.Equal("Une commande servie ne peut pas être annulée", error.Message);
        Assert.Equal(OrderStatus.Servie, (await LoadOrderAsync(options, restaurant, id)).Status);
    }

    // ── UpdateLinesAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateLines_ReplacesLines_KeepingFrozenPricesOfExistingDishes()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddDishAsync(options, restaurant, "Tartare", 22.40m, 0.12m);
        var burger = await AddDishAsync(options, restaurant, "Burger", 18m, 0.06m);
        var tiramisu = await AddDishAsync(options, restaurant, "Tiramisu", 9m, 0.12m);
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(tartare, 1), Line(burger, 2)));

        // Les prix changent, puis le plat du burger est archivé : la commande garde ses prix, le nouveau plat prend le prix du jour.
        await using (var db = OpenContext(options, restaurant))
        {
            (await db.Recipes.SingleAsync(r => r.Id == tartare)).SellingPriceInclVat = 25m;
            var oldBurger = await db.Recipes.SingleAsync(r => r.Id == burger);
            oldBurger.SellingPriceInclVat = 20m;
            oldBurger.IsArchived = true;
            await db.SaveChangesAsync();
        }

        await service.UpdateLinesAsync(id, [Line(tartare, 3, "bien cuit"), Line(burger, 1), Line(tiramisu, 2)]);

        var order = await LoadOrderAsync(options, restaurant, id);
        Assert.Equal(3, order.Lines.Count);
        var byDish = order.Lines.ToDictionary(l => l.RecipeId);
        Assert.Equal((3, 22.40m, 0.12m, "bien cuit"), (byDish[tartare].Quantity, byDish[tartare].UnitPriceInclVat, byDish[tartare].VatRate, byDish[tartare].Note));
        Assert.Equal((1, 18m, 0.06m), (byDish[burger].Quantity, byDish[burger].UnitPriceInclVat, byDish[burger].VatRate));
        Assert.Equal((2, 9m, 0.12m), (byDish[tiramisu].Quantity, byDish[tiramisu].UnitPriceInclVat, byDish[tiramisu].VatRate));

        // Le statut et le numéro ne bougent pas, et les anciennes lignes ont bien disparu.
        Assert.Equal(OrderStatus.Nouvelle, order.Status);
        Assert.Equal(1, order.Number);
        await using var check = OpenContext(options, restaurant);
        Assert.Equal(3, await check.OrderLines.CountAsync());

        // Un plat archivé ne peut pas être ajouté à une commande.
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateLinesAsync(id, [Line(tartare), Line(burger, 1), Line(burger, 1)]));
        Assert.Equal(UnknownDishMessage, error.Message);
        Assert.Equal(3, await check.OrderLines.CountAsync());
    }

    [Fact]
    public async Task UpdateLines_ValidatesLines()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var withoutPrice = await AddDishAsync(options, restaurant, "Sans prix", null, null);
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateLinesAsync(id, []));
        Assert.Equal(NoLineMessage, error.Message);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateLinesAsync(id, [Line(dish, 0)]));
        Assert.Equal(QuantityMessage, error.Message);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateLinesAsync(id, [Line(withoutPrice)]));
        Assert.Equal("Le plat Sans prix n'a pas de prix de vente", error.Message);

        var order = await LoadOrderAsync(options, restaurant, id);
        Assert.Equal(dish, Assert.Single(order.Lines).RecipeId);
    }

    [Fact]
    public async Task UpdateLines_RefusedWhenCancelled()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));
        await service.CancelAsync(id, OrderStatus.Nouvelle);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateLinesAsync(id, [Line(dish, 4)]));

        Assert.Equal("Une commande annulée ne peut plus être modifiée", error.Message);
        Assert.Equal(1, Assert.Single((await LoadOrderAsync(options, restaurant, id)).Lines).Quantity);
    }

    [Fact]
    public async Task UpdateLines_RefusedWhenServed_AllowedWhenReady()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));
        await service.AdvanceAsync(id, OrderStatus.Nouvelle);
        await service.AdvanceAsync(id, OrderStatus.EnPreparation);

        await service.UpdateLinesAsync(id, [Line(dish, 5)]);
        Assert.Equal(5, Assert.Single((await LoadOrderAsync(options, restaurant, id)).Lines).Quantity);

        await service.AdvanceAsync(id, OrderStatus.Prete);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateLinesAsync(id, [Line(dish, 6)]));

        Assert.Equal("Une commande servie ne peut plus être modifiée", error.Message);
        Assert.Equal(5, Assert.Single((await LoadOrderAsync(options, restaurant, id)).Lines).Quantity);
    }

    private sealed record UpdateOutcome(string? Message, int Quantity);

    [Fact]
    public async Task UpdateLines_Concurrent_NeverCrashes_AndLeavesOneCoherentSet()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddDishAsync(options, restaurant, "Tartare");
        var burger = await AddDishAsync(options, restaurant, "Burger", 18m, 0.06m);
        var id = await CreateService(options, restaurant).CreateAsync(Order(Line(tartare, 1), Line(burger, 1)));

        // Quatre appareils enregistrent en même temps des lignes différentes (sur des services, donc des contextes, indépendants) :
        // sans réservation de la commande, ils se disputeraient les mêmes lignes et la page planterait sur une erreur de concurrence.
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(2, 4)
            .Select(quantity => Task.Run(async () =>
            {
                var service = CreateService(options, restaurant);
                gate.Wait();
                try
                {
                    await service.UpdateLinesAsync(id, [Line(tartare, quantity), Line(burger, quantity)]);
                    return new UpdateOutcome(null, quantity);
                }
                catch (DomainValidationException ex)
                {
                    return new UpdateOutcome(ex.Message, quantity);
                }
            }))
            .ToList();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        // Seul le message français de conflit est permis ; au moins un appareil a enregistré.
        Assert.All(results.Where(r => r.Message is not null), r => Assert.Equal(ModifiedElsewhereMessage, r.Message));
        var winners = results.Where(r => r.Message is null).Select(r => r.Quantity).ToList();
        Assert.NotEmpty(winners);

        // Les lignes sont exactement celles d'un seul appareil : deux lignes (pas de doublons), de la même quantité, aux prix figés d'origine.
        var order = await LoadOrderAsync(options, restaurant, id);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal([tartare, burger], order.Lines.OrderBy(l => l.Id).Select(l => l.RecipeId).Order());
        var finalQuantity = order.Lines.Select(l => l.Quantity).Distinct().Single();
        Assert.Contains(finalQuantity, winners);
        Assert.Equal(22.40m, order.Lines.Single(l => l.RecipeId == tartare).UnitPriceInclVat);
        Assert.Equal(OrderStatus.Nouvelle, order.Status);
    }

    /// <summary>
    /// Juste avant d'enregistrer les nouvelles lignes d'une commande, un autre appareil remplace ses lignes (suppression et insertion validées dans une autre transaction).
    /// </summary>
    private sealed class CompetingEditInterceptor(DbContextOptions<ChefOsDbContext> options, int restaurantId, int orderId, int dishId) : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Attempts++ == 0)
            {
                await using var db = OpenContext(options, restaurantId);
                await db.OrderLines.Where(l => l.OrderId == orderId).ExecuteDeleteAsync(cancellationToken);
                db.OrderLines.Add(new OrderLine { OrderId = orderId, RecipeId = dishId, Quantity = 9, UnitPriceInclVat = 10m, VatRate = 0.06m });
                await db.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task UpdateLines_LinesReplacedByAnotherDevice_ReportedInFrench_AndNothingOfOursIsSaved()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddDishAsync(options, restaurant, "Tartare");
        var burger = await AddDishAsync(options, restaurant, "Burger", 18m, 0.06m);
        var id = await CreateService(options, restaurant).CreateAsync(Order(Line(tartare, 1)));

        var interceptor = new CompetingEditInterceptor(options, restaurant, id, burger);
        var intercepted = new DbContextOptionsBuilder<ChefOsDbContext>(options).AddInterceptors(interceptor).Options;
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            CreateService(intercepted, restaurant).UpdateLinesAsync(id, [Line(tartare, 5)]));

        Assert.Equal(ModifiedElsewhereMessage, error.Message);
        Assert.Equal(1, interceptor.Attempts);

        // Les lignes sont celles de l'autre appareil : rien de l'enregistrement refusé n'a été écrit.
        var line = Assert.Single((await LoadOrderAsync(options, restaurant, id)).Lines);
        Assert.Equal((burger, 9), (line.RecipeId, line.Quantity));
    }

    [Fact]
    public async Task UpdateLines_OrderServedWhileWaiting_RefusedAsServed()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(Order(Line(dish)));
        await service.AdvanceAsync(id, OrderStatus.Nouvelle);
        await service.AdvanceAsync(id, OrderStatus.EnPreparation);

        // Un autre appareil sert la commande (transaction ouverte) pendant que celui-ci enregistre : il attend l'issue, puis refuse.
        await using var serving = OpenContext(options, restaurant);
        await using var transaction = await serving.Database.BeginTransactionAsync();
        await serving.Orders.Where(o => o.Id == id).ExecuteUpdateAsync(set => set.SetProperty(o => o.Status, OrderStatus.Servie));

        var update = service.UpdateLinesAsync(id, [Line(dish, 4)]);
        await Task.Delay(500);
        Assert.False(update.IsCompleted);

        await transaction.CommitAsync();

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => update);
        Assert.Equal("Une commande servie ne peut plus être modifiée", error.Message);
        Assert.Equal(1, Assert.Single((await LoadOrderAsync(options, restaurant, id)).Lines).Quantity);
    }

    // ── ListForDayAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListForDay_ReturnsOrdersOfTheUtcDay_SortedByNumber()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddDishAsync(options, restaurant, "Tartare", 22.40m, 0.12m);
        var burger = await AddDishAsync(options, restaurant, "Burger", 18m, 0.06m);

        // Numéros volontairement hors de l'ordre des dates ; bornes : 00:00 inclus, 24:00 exclu.
        var lateId = await InsertOrderAsync(options, restaurant, 3, Day.AddHours(23).AddMinutes(59).AddSeconds(59), tartare);
        var firstId = await InsertOrderAsync(options, restaurant, 1, Day, tartare);
        var cancelledId = await InsertOrderAsync(options, restaurant, 2, Day.AddHours(12), burger, OrderStatus.Annulee);
        await InsertOrderAsync(options, restaurant, 4, Day.AddDays(1), tartare);
        await InsertOrderAsync(options, restaurant, 5, Day.AddTicks(-1), tartare, OrderStatus.Servie);   // la veille, déjà servie : hors du jour

        // Les lignes de la commande 2 : deux plats, prix figés différents du prix actuel.
        await using (var db = OpenContext(options, restaurant))
        {
            db.OrderLines.Add(new OrderLine
            {
                OrderId = cancelledId, RecipeId = tartare, Quantity = 3, UnitPriceInclVat = 21.50m, VatRate = 0.12m, Note = "sans oignon"
            });
            await db.SaveChangesAsync();
        }

        var orders = await CreateService(options, restaurant).ListForDayAsync(Day.AddHours(15));

        Assert.Equal([1, 2, 3], orders.Select(o => o.Number));
        Assert.Equal([firstId, cancelledId, lateId], orders.Select(o => o.Id));
        Assert.Equal([OrderStatus.Nouvelle, OrderStatus.Annulee, OrderStatus.Nouvelle], orders.Select(o => o.Status));

        var cancelled = orders[1];
        Assert.Equal(OrderSource.Emporter, cancelled.Source);
        Assert.Equal(Day.AddHours(12), cancelled.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, cancelled.CreatedAt.Kind);
        // 2 × 10,00 (ligne insérée avec la commande) + 3 × 21,50 (ligne ajoutée) : prix figés, non ceux du plat.
        Assert.Equal(2 * 10m + 3 * 21.50m, cancelled.TotalInclVat);
        Assert.Equal(
            [("Burger", 2, null), ("Tartare", 3, "sans oignon")],
            cancelled.Lines.OrderBy(l => l.DishName).Select(l => (l.DishName, l.Quantity, l.Note)));
    }

    [Fact]
    public async Task ListForDay_SummarizesCreatedOrder()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddDishAsync(options, restaurant, "Tartare", 22.40m, 0.12m);
        var burger = await AddDishAsync(options, restaurant, "Burger", 18m, 0.06m);
        var service = CreateService(options, restaurant);
        var id = await service.CreateAsync(
            new OrderInput(OrderSource.Vitrine, "Mme Martin", "Allergie aux arachides", [Line(tartare, 2, "saignant"), Line(burger, 3)]));

        // Le jour est celui de la création enregistrée (pas d'égalité sur l'heure de l'horloge).
        var createdAt = (await LoadOrderAsync(options, restaurant, id)).CreatedAt;
        var summary = Assert.Single(await service.ListForDayAsync(createdAt));

        Assert.Equal((id, 1, OrderSource.Vitrine, OrderStatus.Nouvelle, "Mme Martin"),
            (summary.Id, summary.Number, summary.Source, summary.Status, summary.TableOrCustomer));
        Assert.Equal("Allergie aux arachides", summary.Note);   // la note saisie à la prise de commande doit arriver à l'écran de la cuisine
        Assert.Equal(2 * 22.40m + 3 * 18m, summary.TotalInclVat);
        Assert.Equal([("Tartare", 2, "saignant"), ("Burger", 3, null)], summary.Lines.Select(l => (l.DishName, l.Quantity, l.Note)));
        Assert.Equal([tartare, burger], summary.Lines.Select(l => l.RecipeId));   // l'écran de modification en a besoin : deux plats peuvent porter le même nom
        Assert.Empty(await service.ListForDayAsync(createdAt.AddDays(-1)));

        // Encore à traiter, la commande reste au tableau les jours suivants ; servie, elle n'y est plus.
        Assert.Equal(id, Assert.Single(await service.ListForDayAsync(createdAt.AddDays(1))).Id);
        await service.AdvanceAsync(id, OrderStatus.Nouvelle);
        await service.AdvanceAsync(id, OrderStatus.EnPreparation);
        await service.AdvanceAsync(id, OrderStatus.Prete);
        Assert.Empty(await service.ListForDayAsync(createdAt.AddDays(1)));
    }

    [Fact]
    public async Task OrderNote_BlankIsNone_AndSurvivesEveryStatus()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);
        var withNote = await service.CreateAsync(new OrderInput(OrderSource.Salle, "T1", "  sans gluten  ", [Line(dish)]));
        var blank = await service.CreateAsync(new OrderInput(OrderSource.Salle, "T2", "   ", [Line(dish)]));
        await service.CancelAsync(withNote, OrderStatus.Nouvelle);

        var createdAt = (await LoadOrderAsync(options, restaurant, withNote)).CreatedAt;
        var orders = (await service.ListForDayAsync(createdAt)).ToDictionary(o => o.Id);

        Assert.Equal("sans gluten", orders[withNote].Note);
        Assert.Equal(OrderStatus.Annulee, orders[withNote].Status);
        Assert.Null(orders[blank].Note);
        Assert.Equal("sans gluten", (await service.GetAsync(withNote))!.Note);
        Assert.Null((await service.GetAsync(blank))!.Note);
    }

    [Fact]
    public async Task ListForDay_IncludesOpenOrdersFromEarlierDays()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddDishAsync(options, restaurant, "Tartare");

        // Hier et avant-hier : les commandes encore à traiter restent au tableau, les servies et annulées non.
        var newYesterday = await InsertOrderAsync(options, restaurant, 3, Day.AddDays(-1).AddHours(23).AddMinutes(40), tartare, OrderStatus.Nouvelle);
        var preparingBefore = await InsertOrderAsync(options, restaurant, 1, Day.AddDays(-2).AddHours(12), tartare, OrderStatus.EnPreparation);
        var readyYesterday = await InsertOrderAsync(options, restaurant, 4, Day.AddDays(-1).AddHours(9), tartare, OrderStatus.Prete);
        await InsertOrderAsync(options, restaurant, 2, Day.AddDays(-2).AddHours(13), tartare, OrderStatus.Servie);
        await InsertOrderAsync(options, restaurant, 5, Day.AddDays(-1).AddHours(18), tartare, OrderStatus.Annulee);
        // Aujourd'hui : toutes, annulée comprise, après celles d'hier. Demain n'y est pas.
        var servedToday = await InsertOrderAsync(options, restaurant, 7, Day.AddHours(11), tartare, OrderStatus.Servie);
        var cancelledToday = await InsertOrderAsync(options, restaurant, 6, Day.AddHours(18), tartare, OrderStatus.Annulee);
        await InsertOrderAsync(options, restaurant, 8, Day.AddDays(1), tartare, OrderStatus.Nouvelle);

        var orders = await CreateService(options, restaurant).ListForDayAsync(Day.AddHours(15));

        // D'abord les commandes encore à traiter d'un jour précédent, par date de création ; puis celles du jour, par numéro.
        Assert.Equal([preparingBefore, readyYesterday, newYesterday, cancelledToday, servedToday], orders.Select(o => o.Id));
        Assert.Equal([1, 4, 3, 6, 7], orders.Select(o => o.Number));
        Assert.All(orders, o => Assert.Equal(DateTimeKind.Utc, o.CreatedAt.Kind));
        Assert.Equal(Day.AddDays(-1).AddHours(23).AddMinutes(40), orders[2].CreatedAt);
    }

    // ── GetAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ReturnsAnyOrderOfTheRestaurant_WhateverItsDayOrStatus()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var tartare = await AddDishAsync(options, restaurant, "Tartare");
        var oldServed = await InsertOrderAsync(options, restaurant, 1, Day.AddDays(-30), tartare, OrderStatus.Servie);
        var service = CreateService(options, restaurant);
        var created = await service.CreateAsync(new OrderInput(OrderSource.Emporter, "M. Dupont", "Allergie", [Line(tartare, 2, "bien cuit")]));

        var old = await service.GetAsync(oldServed);
        var summary = await service.GetAsync(created);

        Assert.NotNull(old);
        Assert.Equal((oldServed, 1, OrderStatus.Servie, Day.AddDays(-30)), (old.Id, old.Number, old.Status, old.CreatedAt));
        Assert.Equal(2 * 10m, old.TotalInclVat);
        Assert.NotNull(summary);
        Assert.Equal((created, 2, OrderSource.Emporter, OrderStatus.Nouvelle, "M. Dupont", "Allergie"),
            (summary.Id, summary.Number, summary.Source, summary.Status, summary.TableOrCustomer, summary.Note));
        Assert.Equal([("Tartare", 2, "bien cuit")], summary.Lines.Select(l => (l.DishName, l.Quantity, l.Note)));
        Assert.Equal(tartare, Assert.Single(summary.Lines).RecipeId);
        Assert.Null(await service.GetAsync(999_999));
    }

    [Fact]
    public async Task GetAsync_OtherRestaurant_Null()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var id = await InsertOrderAsync(options, restaurant, 1, Day, dish);

        Assert.Null(await CreateService(options, other).GetAsync(id));
        Assert.NotNull(await CreateService(options, restaurant).GetAsync(id));
    }

    // ── Isolation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task OtherRestaurant_CannotSeeOrModify()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var other = await TestData.CreateRestaurantAsync(options, "Brasserie");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var otherDish = await AddDishAsync(options, other, "Burger");
        var id = await InsertOrderAsync(options, restaurant, 1, Day, dish);
        var intruder = CreateService(options, other);

        Assert.Empty(await intruder.ListForDayAsync(Day));
        Assert.Empty(await intruder.ListForDayAsync(Day.AddDays(3)));   // même une commande encore à traiter d'un jour précédent
        foreach (var attempt in new Func<Task>[]
                 {
                     () => intruder.AdvanceAsync(id, OrderStatus.Nouvelle),
                     () => intruder.CancelAsync(id, OrderStatus.Nouvelle),
                     () => intruder.UpdateLinesAsync(id, [Line(otherDish)])
                 })
        {
            var error = await Assert.ThrowsAsync<DomainValidationException>(attempt);
            Assert.Equal(UnknownOrderMessage, error.Message);
        }

        var untouched = await LoadOrderAsync(options, restaurant, id);
        Assert.Equal(OrderStatus.Nouvelle, untouched.Status);
        Assert.Equal(dish, Assert.Single(untouched.Lines).RecipeId);
        Assert.Single(await CreateService(options, restaurant).ListForDayAsync(Day));
    }

    [Fact]
    public async Task UnknownOrder_Refused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var dish = await AddDishAsync(options, restaurant, "Tartare");
        var service = CreateService(options, restaurant);

        foreach (var attempt in new Func<Task>[]
                 {
                     () => service.AdvanceAsync(999_999, OrderStatus.Nouvelle),
                     () => service.CancelAsync(999_999, OrderStatus.Nouvelle),
                     () => service.UpdateLinesAsync(999_999, [Line(dish)])
                 })
        {
            var error = await Assert.ThrowsAsync<DomainValidationException>(attempt);
            Assert.Equal(UnknownOrderMessage, error.Message);
        }
    }
}
