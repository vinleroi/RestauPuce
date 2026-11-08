using ChefOS.Core.Recipes;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChefOS.Tests.Vitrine;

/// <summary>
/// Le site public (<c>WebApplicationFactory&lt;VitrineProgram&gt;</c>) contre une vraie base PostgreSQL de test et la passerelle Demo :
/// aucun appel réseau externe. Deux restaurants publiés, un non publié.
/// </summary>
public sealed class VitrineSite : IAsyncDisposable
{
    public required WebApplicationFactory<VitrineProgram> Factory { get; init; }
    public required DbContextOptions<ChefOsDbContext> Options { get; init; }
    public required Restaurant Bistro { get; init; }
    public required Restaurant Other { get; init; }
    public required Restaurant Hidden { get; init; }
    public int BistroImageId { get; init; }
    public int HiddenImageId { get; init; }
    public int BistroWineId { get; init; }
    public int BistroDishId { get; init; }

    public HttpClient Client => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static async Task<VitrineSite> CreateAsync(PostgresFixture postgres, string? gateway = null, Action<IServiceCollection>? configureServices = null)
    {
        var options = await postgres.CreateDatabaseAsync();
        var connection = new ChefOsDbContext(options, new FixedTenantContext(null)).Database.GetConnectionString()!;

        var bistro = await AddRestaurantAsync(options, "Le Bistro Test", published: true);
        var other = await AddRestaurantAsync(options, "Autre Table", published: true);
        var hidden = await AddRestaurantAsync(options, "Maison Cachée", published: false);

        int imageId, hiddenImageId, wineId, dishId;
        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(bistro.Id)))
        {
            var image = new VitrineImage { FileName = "salle.png", ContentType = "image/png", Data = [137, 80, 78, 71], IsCover = true };
            var dish = new Recipe
            {
                Name = "Waterzooi de poulet", Kind = RecipeKind.Dish, Category = "Plats", SellingPriceInclVat = 24m, VatRate = 0.12m,
                PublicDescription = "Poulet fermier, légumes, crème", ShowOnVitrine = true
            };
            db.AddRange(image, dish);
            await db.SaveChangesAsync();
            var wine = new WineListItem
            {
                Name = "Chablis Premier Cru", Region = "Bourgogne", Country = "France", Color = WineColor.Blanc, Vintage = 2021,
                Robe = "Or pâle aux reflets verts", Nez = "Agrumes et pierre à fusil", Bouche = "Tendue et minérale",
                IsPublished = true, PriceBottle = 58m, PriceGlass = 9m
            };
            db.WineListItems.Add(wine);
            await db.SaveChangesAsync();
            db.WineListItemPairings.Add(new WineListItemPairing { WineListItemId = wine.Id, RecipeId = dish.Id });
            db.VitrineProfiles.Single().CoverImageId = image.Id;
            await db.SaveChangesAsync();
            imageId = image.Id;
            wineId = wine.Id;
            dishId = dish.Id;
        }

        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(other.Id)))
        {
            db.Recipes.Add(new Recipe
            {
                Name = "Plat de l'autre maison", Kind = RecipeKind.Dish, SellingPriceInclVat = 18m, VatRate = 0.12m, ShowOnVitrine = true
            });
            db.WineListItems.Add(new WineListItem
            {
                Name = "Vin de l'autre maison", Region = "Rhône", Country = "France", Color = WineColor.Rouge, IsPublished = true, PriceBottle = 40m
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(hidden.Id)))
        {
            var image = new VitrineImage { FileName = "secret.png", ContentType = "image/png", Data = [1, 2, 3] };
            db.VitrineImages.Add(image);
            await db.SaveChangesAsync();
            hiddenImageId = image.Id;
        }

        var factory = new WebApplicationFactory<VitrineProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ChefOs", connection);
            builder.UseSetting("Payments:Gateway", gateway ?? "Demo");
            builder.UseSetting("Payments:BaseUrl", "http://localhost");
            if (configureServices is not null)
                builder.ConfigureTestServices(configureServices);
        });

        return new VitrineSite
        {
            Factory = factory, Options = options, Bistro = bistro, Other = other, Hidden = hidden,
            BistroImageId = imageId, HiddenImageId = hiddenImageId, BistroWineId = wineId, BistroDishId = dishId
        };
    }

    private static async Task<Restaurant> AddRestaurantAsync(DbContextOptions<ChefOsDbContext> options, string name, bool published)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        var restaurant = new Restaurant
        {
            Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-').Replace("é", "e").Replace("î", "i"), CreatedAt = DateTime.UtcNow
        };
        db.Restaurants.Add(restaurant);
        await db.SaveChangesAsync();

        await using var scoped = new ChefOsDbContext(options, new FixedTenantContext(restaurant.Id));
        scoped.VitrineProfiles.Add(new VitrineProfile
        {
            IsPublished = published, ReservationsOpen = true, Tagline = "Cuisine belge de saison", City = "Bruxelles",
            AmbianceText = "Une salle chaleureuse au cœur de la ville.", Phone = "02 123 45 67"
        });
        await scoped.SaveChangesAsync();
        return restaurant;
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}
