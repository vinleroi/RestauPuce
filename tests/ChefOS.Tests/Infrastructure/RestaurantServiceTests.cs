using ChefOS.Core.Common;
using ChefOS.Core.Restaurants;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class RestaurantServiceTests(PostgresFixture postgres)
{
    private const string TakenSlugMessage = "Ce lien est déjà utilisé par un autre restaurant";
    private const string InvalidSlugMessage = "Le lien ne peut contenir que des minuscules, chiffres et tirets";

    // Restaurant et Membership ne sont pas ITenantScoped : le service fonctionne sans restaurant courant.
    private static RestaurantService CreateService(DbContextOptions<ChefOsDbContext> options) =>
        new(new TestContextFactory(options, null));

    private static async Task<string> AddUserAsync(DbContextOptions<ChefOsDbContext> options, string name)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        var user = new ApplicationUser { UserName = name, Email = $"{name}@chefos.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static RestaurantSettingsInput SettingsFor(Restaurant restaurant) => new(
        restaurant.Name, restaurant.Slug, restaurant.Address, restaurant.VatNumber,
        restaurant.DefaultFoodVatRate, restaurant.DefaultDrinkVatRate, restaurant.FoodCostAlertThreshold);

    [Fact]
    public async Task Create_MakesOwnerMembership()
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");

        var before = DateTime.UtcNow;
        var restaurant = await service.CreateAsync(userId,
            new CreateRestaurantInput("Le Petit Bistro", "1 rue des Halles, Bruxelles", "BE0123456789"));

        Assert.True(restaurant.Id > 0);
        Assert.Equal("Le Petit Bistro", restaurant.Name);
        Assert.Equal("le-petit-bistro", restaurant.Slug);
        Assert.True(restaurant.CreatedAt >= before);

        await using var db = new TestContextFactory(options, null).CreateDbContext();
        var membership = await db.Memberships.SingleAsync();
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(restaurant.Id, membership.RestaurantId);
        Assert.Equal(MembershipRole.Owner, membership.Role);

        var stored = await db.Restaurants.SingleAsync();
        Assert.Equal("1 rue des Halles, Bruxelles", stored.Address);
        Assert.Equal("BE0123456789", stored.VatNumber);
        Assert.Equal(0.12m, stored.DefaultFoodVatRate);
        Assert.Equal(0.21m, stored.DefaultDrinkVatRate);
        Assert.Equal(0.35m, stored.FoodCostAlertThreshold);
    }

    [Fact]
    public async Task Create_SameName_GetsSuffixedSlug() // Review Focus 5 : le slug est unique sur toute la plateforme
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userA = await AddUserAsync(options, "alice");
        var userB = await AddUserAsync(options, "bob");

        var first = await service.CreateAsync(userA, new CreateRestaurantInput("Le Petit Bistro", null, null));
        var second = await service.CreateAsync(userB, new CreateRestaurantInput("Le Petit Bistro", null, null));
        var third = await service.CreateAsync(userA, new CreateRestaurantInput("Le Petit Bistro", null, null));

        Assert.Equal("le-petit-bistro", first.Slug);
        Assert.Equal("le-petit-bistro-2", second.Slug);
        Assert.Equal("le-petit-bistro-3", third.Slug);
    }

    [Fact]
    public async Task Create_SymbolOnlyName_UsesFallbackSlug()
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");

        var first = await service.CreateAsync(userId, new CreateRestaurantInput("!!!", null, null));
        var second = await service.CreateAsync(userId, new CreateRestaurantInput("???", null, null));

        Assert.Equal("restaurant", first.Slug);
        Assert.Equal("restaurant-2", second.Slug);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankName_Throws(string name)
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.CreateAsync(userId, new CreateRestaurantInput(name, null, null)));

        await using var db = new TestContextFactory(options, null).CreateDbContext();
        Assert.Empty(await db.Restaurants.ToListAsync());
    }

    [Fact]
    public async Task GetMemberships_OnlyForUser()
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var alice = await AddUserAsync(options, "alice");
        var bob = await AddUserAsync(options, "bob");

        var zeste = await service.CreateAsync(alice, new CreateRestaurantInput("Zeste", null, null));
        var bistro = await service.CreateAsync(alice, new CreateRestaurantInput("Bistro", null, null));
        await service.CreateAsync(bob, new CreateRestaurantInput("Chez Bob", null, null));

        var memberships = await service.GetMembershipsAsync(alice);

        Assert.Equal(
            [
                new MembershipSummary(bistro.Id, "Bistro", MembershipRole.Owner),
                new MembershipSummary(zeste.Id, "Zeste", MembershipRole.Owner)
            ],
            memberships);
        Assert.Empty(await service.GetMembershipsAsync("inconnu"));
    }

    [Fact]
    public async Task IsMember_TrueOnlyForLinkedUserAndRestaurant()
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var alice = await AddUserAsync(options, "alice");
        var bob = await AddUserAsync(options, "bob");
        var bistro = await service.CreateAsync(alice, new CreateRestaurantInput("Bistro", null, null));
        var chezBob = await service.CreateAsync(bob, new CreateRestaurantInput("Chez Bob", null, null));

        Assert.True(await service.IsMemberAsync(alice, bistro.Id));
        Assert.False(await service.IsMemberAsync(alice, chezBob.Id));
        Assert.False(await service.IsMemberAsync(bob, bistro.Id));
    }

    [Fact]
    public async Task Get_ReturnsRestaurantOrNull()
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");
        var created = await service.CreateAsync(userId, new CreateRestaurantInput("Bistro", null, null));

        var found = await service.GetAsync(created.Id);

        Assert.NotNull(found);
        Assert.Equal("Bistro", found.Name);
        Assert.Null(await service.GetAsync(created.Id + 1000));
    }

    [Fact]
    public async Task UpdateSettings_PersistsAllFields()
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");
        var created = await service.CreateAsync(userId, new CreateRestaurantInput("Bistro", null, null));

        await service.UpdateSettingsAsync(created.Id, new RestaurantSettingsInput(
            "Bistro du Port", "bistro-du-port", "2 quai aux Briques", "BE0987654321", 0.06m, 0.21m, 0.30m));

        var stored = await service.GetAsync(created.Id);
        Assert.NotNull(stored);
        Assert.Equal("Bistro du Port", stored.Name);
        Assert.Equal("bistro-du-port", stored.Slug);
        Assert.Equal("2 quai aux Briques", stored.Address);
        Assert.Equal("BE0987654321", stored.VatNumber);
        Assert.Equal(0.06m, stored.DefaultFoodVatRate);
        Assert.Equal(0.21m, stored.DefaultDrinkVatRate);
        Assert.Equal(0.30m, stored.FoodCostAlertThreshold);
    }

    [Fact]
    public async Task UpdateSettings_OwnCurrentSlug_IsAllowed()
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");
        var created = await service.CreateAsync(userId, new CreateRestaurantInput("Bistro", null, null));

        // Le slug du restaurant lui-même n'est pas « déjà pris » : seul le nom change ici.
        await service.UpdateSettingsAsync(created.Id, SettingsFor(created) with { Name = "Bistro Rénové" });

        var stored = await service.GetAsync(created.Id);
        Assert.NotNull(stored);
        Assert.Equal("Bistro Rénové", stored.Name);
        Assert.Equal("bistro", stored.Slug);
    }

    [Fact]
    public async Task UpdateSettings_TakenSlug_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");
        var bistro = await service.CreateAsync(userId, new CreateRestaurantInput("Bistro", null, null));
        var brasserie = await service.CreateAsync(userId, new CreateRestaurantInput("Brasserie", null, null));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.UpdateSettingsAsync(brasserie.Id, SettingsFor(brasserie) with { Slug = bistro.Slug }));

        Assert.Equal(TakenSlugMessage, ex.Message);
        Assert.Equal("brasserie", (await service.GetAsync(brasserie.Id))!.Slug);
    }

    [Fact]
    public async Task UpdateSettings_InvalidSlug_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");
        var created = await service.CreateAsync(userId, new CreateRestaurantInput("Bistro", null, null));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.UpdateSettingsAsync(created.Id, SettingsFor(created) with { Slug = "Mon Resto!" }));

        Assert.Equal(InvalidSlugMessage, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-bistro")]
    [InlineData("bistro-")]
    [InlineData("le--bistro")]
    [InlineData("Le-Bistro")]
    [InlineData("le_bistro")]
    [InlineData("le-bistro\n")] // `$` accepterait le retour à la ligne final : le motif doit être ancré avec \z
    public async Task UpdateSettings_MalformedSlug_Throws(string slug)
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");
        var created = await service.CreateAsync(userId, new CreateRestaurantInput("Bistro", null, null));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.UpdateSettingsAsync(created.Id, SettingsFor(created) with { Slug = slug }));

        Assert.Equal(InvalidSlugMessage, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateSettings_BlankName_Throws(string name)
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");
        var created = await service.CreateAsync(userId, new CreateRestaurantInput("Bistro", null, null));

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.UpdateSettingsAsync(created.Id, SettingsFor(created) with { Name = name }));
    }

    [Theory]
    [InlineData(-0.01, 0.21, 0.35)]
    [InlineData(1.01, 0.21, 0.35)]
    [InlineData(0.12, -0.01, 0.35)]
    [InlineData(0.12, 1.5, 0.35)]
    [InlineData(0.12, 0.21, -0.01)]
    [InlineData(0.12, 0.21, 1.01)]
    public async Task UpdateSettings_RateOutOfRange_Throws(double food, double drink, double threshold)
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");
        var created = await service.CreateAsync(userId, new CreateRestaurantInput("Bistro", null, null));

        await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateSettingsAsync(
            created.Id,
            SettingsFor(created) with
            {
                DefaultFoodVatRate = (decimal)food,
                DefaultDrinkVatRate = (decimal)drink,
                FoodCostAlertThreshold = (decimal)threshold
            }));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    public async Task UpdateSettings_RateBounds_AreInclusive(double food, double drink, double threshold)
    {
        var options = await postgres.CreateDatabaseAsync();
        var service = CreateService(options);
        var userId = await AddUserAsync(options, "chef");
        var created = await service.CreateAsync(userId, new CreateRestaurantInput("Bistro", null, null));

        await service.UpdateSettingsAsync(created.Id, SettingsFor(created) with
        {
            DefaultFoodVatRate = (decimal)food,
            DefaultDrinkVatRate = (decimal)drink,
            FoodCostAlertThreshold = (decimal)threshold
        });

        var stored = await service.GetAsync(created.Id);
        Assert.NotNull(stored);
        Assert.Equal((decimal)food, stored.DefaultFoodVatRate);
        Assert.Equal((decimal)drink, stored.DefaultDrinkVatRate);
        Assert.Equal((decimal)threshold, stored.FoodCostAlertThreshold);
    }
}
