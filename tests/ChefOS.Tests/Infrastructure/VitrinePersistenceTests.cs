using System.Reflection;
using ChefOS.Core.Recipes;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Wines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class VitrinePersistenceTests(PostgresFixture postgres)
{
    private static WineLibrarySeeder CreateSeeder(DbContextOptions<ChefOsDbContext> options) =>
        new(new TestContextFactory(options, null), NullLogger<WineLibrarySeeder>.Instance);

    private static Reservation NewReservation(string token) => new()
    {
        PublicToken = token,
        StartsAtUtc = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc),
        ServiceName = "Dîner",
        PartySize = 2,
        CustomerName = "Claire Martin",
        CustomerEmail = "claire@example.org",
        CustomerPhone = "+32470000000",
        Status = ReservationStatus.Confirmed,
        CreatedAt = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task WineLibrary_Seed_IsIdempotentAndComplete()
    {
        var options = await postgres.CreateDatabaseAsync();
        var seeder = CreateSeeder(options);

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        var wines = await db.WineReferences.ToListAsync();
        Assert.True(wines.Count >= 60, $"{wines.Count} vins seulement.");
        Assert.Equal(wines.Count, wines.Select(w => w.Key).Distinct().Count());
        Assert.Equal(WineLibrarySeeder.LoadLibrary().Count, wines.Count);

        foreach (var wine in wines)
        {
            foreach (var property in typeof(WineReference).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.PropertyType == typeof(string) && p.Name != nameof(WineReference.Appellation)))
                Assert.False(string.IsNullOrWhiteSpace((string?)property.GetValue(wine)), $"{wine.Key} : {property.Name} vide.");
        }
        foreach (var color in Enum.GetValues<WineColor>())
            Assert.Contains(wines, w => w.Color == color);

        // Une fiche modifiée en base est ramenée à la version de la bibliothèque, sans doublon.
        wines[0].Robe = "modifiée";
        await db.SaveChangesAsync();
        await seeder.SeedAsync();
        await using var check = new ChefOsDbContext(options, new FixedTenantContext(null));
        Assert.Equal(wines.Count, await check.WineReferences.CountAsync());
        Assert.NotEqual("modifiée", (await check.WineReferences.SingleAsync(w => w.Key == wines[0].Key)).Robe);
    }

    [Fact]
    public async Task WineReference_HasNoTenantFilter()
    {
        var options = await postgres.CreateDatabaseAsync();
        await CreateSeeder(options).SeedAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");

        await using var noTenant = new ChefOsDbContext(options, new FixedTenantContext(null));
        await using var tenant = new ChefOsDbContext(options, new FixedTenantContext(restaurant));

        Assert.Empty(noTenant.Model.FindEntityType(typeof(WineReference))!.GetDeclaredQueryFilters());
        var all = await noTenant.WineReferences.CountAsync();
        Assert.True(all > 0);
        Assert.Equal(all, await tenant.WineReferences.CountAsync());
    }

    [Fact]
    public async Task ServiceSchedule_InvalidTimes_Rejected()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");

        ServiceSchedule Make(TimeOnly start, TimeOnly end, int capacity = 30, int slot = 30) => new()
        {
            DayOfWeek = DayOfWeek.Monday,
            Name = Guid.NewGuid().ToString("N"),
            StartLocal = start,
            EndLocal = end,
            Capacity = capacity,
            SlotMinutes = slot
        };

        var invalid = new[]
        {
            Make(new TimeOnly(20, 0), new TimeOnly(18, 0)),
            Make(new TimeOnly(18, 0), new TimeOnly(18, 0)),
            Make(new TimeOnly(18, 0), new TimeOnly(22, 0), capacity: 0),
            Make(new TimeOnly(18, 0), new TimeOnly(22, 0), slot: 20)
        };
        foreach (var schedule in invalid)
        {
            await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurant));
            db.ServiceSchedules.Add(schedule);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("CK_ServiceSchedule_Times", ((PostgresException)ex.InnerException!).ConstraintName);
        }

        await using var ok = new ChefOsDbContext(options, new FixedTenantContext(restaurant));
        ok.ServiceSchedules.Add(Make(new TimeOnly(18, 0), new TimeOnly(22, 0)));
        await ok.SaveChangesAsync();
    }

    [Fact]
    public async Task ReservationToken_Unique()
    {
        var options = await postgres.CreateDatabaseAsync();
        var first = await TestData.CreateRestaurantAsync(options, "Bistro");
        var second = await TestData.CreateRestaurantAsync(options, "Brasserie");

        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(first)))
        {
            db.Reservations.Add(NewReservation("jeton-unique"));
            await db.SaveChangesAsync();
        }

        // L'unicité est globale : un autre restaurant ne peut pas réutiliser le jeton.
        await using var other = new ChefOsDbContext(options, new FixedTenantContext(second));
        other.Reservations.Add(NewReservation("jeton-unique"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        Assert.Equal("23505", ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task DeleteRestaurant_CascadesVitrineData()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var survivor = await TestData.CreateRestaurantAsync(options, "Brasserie");

        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(restaurant)))
        {
            var recipe = new Recipe { Name = "Salade", Kind = RecipeKind.Dish };
            db.Recipes.Add(recipe);
            var image = new VitrineImage { FileName = "a.jpg", ContentType = "image/jpeg", Data = [1] };
            db.VitrineImages.Add(image);
            await db.SaveChangesAsync();

            db.VitrineProfiles.Add(new VitrineProfile { CoverImageId = image.Id });
            db.ServiceSchedules.Add(new ServiceSchedule
            {
                DayOfWeek = DayOfWeek.Friday, Name = "Dîner", StartLocal = new TimeOnly(18, 0), EndLocal = new TimeOnly(22, 0), Capacity = 20
            });
            db.ClosureDays.Add(new ClosureDay { DateLocal = new DateOnly(2026, 12, 25) });
            var reservation = NewReservation("jeton-cascade");
            var guest = new ReservationGuest { Position = 1 };
            guest.Dishes.Add(new ReservationGuestDish { RecipeId = recipe.Id, UnitPriceInclVat = 10m, VatRate = 0.12m });
            reservation.Guests.Add(guest);
            db.Reservations.Add(reservation);
            var wine = new WineListItem { Name = "Chablis", Region = "Bourgogne", Country = "France", PriceBottle = 40m };
            db.WineListItems.Add(wine);
            await db.SaveChangesAsync();

            db.Payments.Add(new Payment { ReservationId = reservation.Id, Amount = 10m, Gateway = "mollie", CreatedAt = DateTime.UtcNow });
            db.WineListItemPairings.Add(new WineListItemPairing { WineListItemId = wine.Id, RecipeId = recipe.Id });
            await db.SaveChangesAsync();
        }
        await using (var other = new ChefOsDbContext(options, new FixedTenantContext(survivor)))
        {
            other.ClosureDays.Add(new ClosureDay { DateLocal = new DateOnly(2026, 12, 25) });
            await other.SaveChangesAsync();
        }

        await using (var admin = new ChefOsDbContext(options, new FixedTenantContext(null)))
            await admin.Restaurants.Where(r => r.Id == restaurant).ExecuteDeleteAsync();

        await using var check = new ChefOsDbContext(options, new FixedTenantContext(restaurant));
        Assert.Empty(await check.VitrineProfiles.ToListAsync());
        Assert.Empty(await check.VitrineImages.ToListAsync());
        Assert.Empty(await check.ServiceSchedules.ToListAsync());
        Assert.Empty(await check.ClosureDays.ToListAsync());
        Assert.Empty(await check.Reservations.ToListAsync());
        Assert.Empty(await check.ReservationGuests.ToListAsync());
        Assert.Empty(await check.ReservationGuestDishes.ToListAsync());
        Assert.Empty(await check.Payments.ToListAsync());
        Assert.Empty(await check.WineListItems.ToListAsync());
        Assert.Empty(await check.WineListItemPairings.ToListAsync());
        await using var survivorDb = new ChefOsDbContext(options, new FixedTenantContext(survivor));
        Assert.Single(await survivorDb.ClosureDays.ToListAsync());
    }
}
