using ChefOS.Core.Restaurants;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

public static class TestData
{
    /// <summary>Crée un restaurant (slug unique) directement en base et retourne son identifiant.</summary>
    public static async Task<int> CreateRestaurantAsync(DbContextOptions<ChefOsDbContext> options, string name)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        var restaurant = new Restaurant
        {
            Name = name,
            Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid().ToString("N")[..8]}",
            CreatedAt = DateTime.UtcNow
        };
        db.Restaurants.Add(restaurant);
        await db.SaveChangesAsync();
        return restaurant.Id;
    }
}
