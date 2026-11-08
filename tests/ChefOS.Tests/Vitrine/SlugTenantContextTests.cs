using ChefOS.Core.Recipes;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Tests.Infrastructure;
using ChefOS.Vitrine.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Vitrine;

public sealed class SlugTenantContextTests
{
    [Fact]
    public void Context_ExposesTheRestaurantItWasGiven_AndNoneByDefault()
    {
        var context = new SlugTenantContext();
        Assert.Null(context.CurrentRestaurantId);

        context.RestaurantId = 7;

        Assert.Equal(7, ((ITenantContext)context).CurrentRestaurantId);
    }
}

[Collection("postgres")]
public sealed class SlugResolverTests(PostgresFixture postgres)
{
    private static async Task<int> AddAsync(Microsoft.EntityFrameworkCore.DbContextOptions<ChefOsDbContext> options, string slug, bool? published, string zone = "Europe/Brussels")
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        var restaurant = new Restaurant { Name = slug.ToUpperInvariant(), Slug = slug, TimeZoneId = zone, CreatedAt = DateTime.UtcNow };
        db.Restaurants.Add(restaurant);
        await db.SaveChangesAsync();
        if (published is { } flag)
        {
            await using var scoped = new ChefOsDbContext(options, new FixedTenantContext(restaurant.Id));
            scoped.VitrineProfiles.Add(new VitrineProfile { IsPublished = flag });
            await scoped.SaveChangesAsync();
        }

        return restaurant.Id;
    }

    private static (SlugResolver Resolver, SlugTenantContext Tenant) Create(Microsoft.EntityFrameworkCore.DbContextOptions<ChefOsDbContext> options)
    {
        var tenant = new SlugTenantContext();
        return (new SlugResolver(new ScopedFactory(options, tenant), tenant), tenant);
    }

    /// <summary>Comme en production : chaque contexte lit le restaurant courant du scope (ici le contexte mutable).</summary>
    private sealed class ScopedFactory(Microsoft.EntityFrameworkCore.DbContextOptions<ChefOsDbContext> options, ITenantContext tenant) : IDbContextFactory<ChefOsDbContext>
    {
        public ChefOsDbContext CreateDbContext() => new(options, tenant);
    }

    [Fact]
    public async Task Published_ResolvesToTheRestaurant_AndSetsTheTenant()
    {
        var options = await postgres.CreateDatabaseAsync();
        var id = await AddAsync(options, "le-bistro", published: true, zone: "Europe/Paris");
        var (resolver, tenant) = Create(options);

        var restaurant = await resolver.EnterAsync("le-bistro");

        Assert.NotNull(restaurant);
        Assert.Equal(id, restaurant.Id);
        Assert.Equal("Europe/Paris", restaurant.TimeZoneId);
        Assert.Equal(id, tenant.CurrentRestaurantId);
        Assert.Same(restaurant, resolver.Current);
    }

    [Theory]
    [InlineData("Le-Bistro")]
    [InlineData("  le-bistro ")]
    public async Task Slug_IsCaseAndSpaceInsensitive(string requested)
    {
        var options = await postgres.CreateDatabaseAsync();
        var id = await AddAsync(options, "le-bistro", published: true);
        var (resolver, _) = Create(options);

        Assert.Equal(id, (await resolver.ResolveAsync(requested))?.Id);
    }

    [Fact]
    public async Task UnpublishedUnknownOrWithoutProfile_ResolveToNothing_AndLeaveNoTenant()
    {
        var options = await postgres.CreateDatabaseAsync();
        await AddAsync(options, "cache", published: false);
        await AddAsync(options, "sans-profil", published: null);
        var (resolver, tenant) = Create(options);

        foreach (var slug in new[] { "cache", "sans-profil", "inconnu", "", "   ", new string('a', 101) })
        {
            Assert.Null(await resolver.EnterAsync(slug));
            Assert.Null(tenant.CurrentRestaurantId);
        }

        Assert.Null(resolver.Current);
    }

    [Fact]
    public async Task EnteringAnotherSlug_SwitchesTheTenant_AndAFailedEntryClearsIt()
    {
        var options = await postgres.CreateDatabaseAsync();
        var first = await AddAsync(options, "premier", published: true);
        var second = await AddAsync(options, "second", published: true);
        var (resolver, tenant) = Create(options);

        await resolver.EnterAsync("premier");
        Assert.Equal(first, tenant.CurrentRestaurantId);
        await resolver.EnterAsync("second");
        Assert.Equal(second, tenant.CurrentRestaurantId);
        await resolver.EnterAsync("inconnu");
        Assert.Null(tenant.CurrentRestaurantId);
    }

    [Fact]
    public async Task OnceEntered_TheContextSeesOnlyThatRestaurantsData()
    {
        var options = await postgres.CreateDatabaseAsync();
        var first = await AddAsync(options, "premier", published: true);
        var second = await AddAsync(options, "second", published: true);
        foreach (var (id, name) in new[] { (first, "Plat un"), (second, "Plat deux") })
        {
            await using var db = new ChefOsDbContext(options, new FixedTenantContext(id));
            db.Recipes.Add(new Recipe { Name = name, Kind = RecipeKind.Dish });
            await db.SaveChangesAsync();
        }

        var tenant = new SlugTenantContext();
        var resolver = new SlugResolver(new ScopedFactory(options, tenant), tenant);
        await using var scoped = new ChefOsDbContext(options, tenant);

        await resolver.EnterAsync("premier");
        Assert.Equal(["Plat un"], await scoped.Recipes.Select(r => r.Name).ToListAsync());

        await resolver.EnterAsync("second");
        Assert.Equal(["Plat deux"], await scoped.Recipes.Select(r => r.Name).ToListAsync());

        await resolver.EnterAsync("inconnu");
        Assert.Empty(await scoped.Recipes.ToListAsync());
    }
}
