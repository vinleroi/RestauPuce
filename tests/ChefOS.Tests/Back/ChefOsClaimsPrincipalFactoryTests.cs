using System.Security.Claims;
using ChefOS.Back.Tenancy;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Services;
using ChefOS.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChefOS.Tests.Back;

[Collection("postgres")]
public class ChefOsClaimsPrincipalFactoryTests(PostgresFixture postgres)
{
    /// <summary>Hôte minimal : infrastructure sur la base de test et Identity avec la fabrique de claims du Back.</summary>
    private static ServiceProvider BuildHost(DbContextOptions<ChefOsDbContext> options)
    {
        string connectionString;
        using (var db = new ChefOsDbContext(options, new FixedTenantContext(null)))
            connectionString = db.Database.GetConnectionString()!;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(null));
        services.AddChefOsInfrastructure(connectionString);
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<ChefOsDbContext>()
            .AddClaimsPrincipalFactory<ChefOsClaimsPrincipalFactory>();

        return services.BuildServiceProvider();
    }

    private static async Task<ApplicationUser> CreateUserAsync(IServiceProvider services, string name)
    {
        var user = new ApplicationUser { UserName = $"{name}@chefos.test", Email = $"{name}@chefos.test" };
        var result = await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user);
        Assert.True(result.Succeeded);
        return user;
    }

    private static async Task<int> CreateRestaurantAsync(IServiceProvider services, ApplicationUser owner, string name) =>
        (await services.GetRequiredService<RestaurantService>().CreateAsync(owner.Id, new CreateRestaurantInput(name, null, null))).Id;

    /// <summary>Crée le principal du cookie (comme à la connexion), puis relit l'utilisateur en base dans un nouveau scope.</summary>
    private static async Task<(string? Claim, int? Stored)> SignInAsync(ServiceProvider provider, string userId)
    {
        ClaimsPrincipal principal;
        await using (var scope = provider.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(userId);
            return (principal.FindFirst(ClaimsTenantContext.RestaurantClaimType)?.Value, stored!.ActiveRestaurantId);
        }
    }

    private static async Task SetActiveRestaurantAsync(ServiceProvider provider, string userId, int? restaurantId)
    {
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId))!;
        user.ActiveRestaurantId = restaurantId;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }

    [Fact]
    public async Task ActiveRestaurantOfTheUser_IsKept()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);
        var chef = await CreateUserAsync(provider, "chef");
        await CreateRestaurantAsync(provider, chef, "Atelier");
        var zinc = await CreateRestaurantAsync(provider, chef, "Zinc");
        await SetActiveRestaurantAsync(provider, chef.Id, zinc);

        var (claim, stored) = await SignInAsync(provider, chef.Id);

        Assert.Equal(zinc.ToString(), claim);
        Assert.Equal(zinc, stored);
    }

    [Fact]
    public async Task NoActiveRestaurant_PicksFirstByNameAndSavesIt()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);
        var chef = await CreateUserAsync(provider, "chef");
        await CreateRestaurantAsync(provider, chef, "Zinc");
        var atelier = await CreateRestaurantAsync(provider, chef, "Atelier");

        var (claim, stored) = await SignInAsync(provider, chef.Id);

        Assert.Equal(atelier.ToString(), claim);
        Assert.Equal(atelier, stored);
    }

    [Fact]
    public async Task StaleActiveRestaurant_IsReplacedByFirstMembershipByName()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);
        var chef = await CreateUserAsync(provider, "chef");
        var other = await CreateUserAsync(provider, "autre");
        var zinc = await CreateRestaurantAsync(provider, chef, "Zinc");
        var notMine = await CreateRestaurantAsync(provider, other, "Ailleurs");
        await SetActiveRestaurantAsync(provider, chef.Id, notMine);

        var (claim, stored) = await SignInAsync(provider, chef.Id);

        Assert.Equal(zinc.ToString(), claim);
        Assert.Equal(zinc, stored);
    }

    [Fact]
    public async Task NoMembership_AddsNoClaimAndClearsStaleRestaurant()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);
        var chef = await CreateUserAsync(provider, "chef");
        var other = await CreateUserAsync(provider, "autre");
        var notMine = await CreateRestaurantAsync(provider, other, "Ailleurs");
        await SetActiveRestaurantAsync(provider, chef.Id, notMine);

        var (claim, stored) = await SignInAsync(provider, chef.Id);

        Assert.Null(claim);
        Assert.Null(stored);
    }
}
