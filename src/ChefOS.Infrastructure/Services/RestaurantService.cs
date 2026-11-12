using System.Text.RegularExpressions;
using ChefOS.Core.Common;
using ChefOS.Core.Restaurants;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Création des restaurants (slug public unique sur toute la plateforme), appartenances des utilisateurs et réglages.
/// <see cref="Restaurant"/> et <see cref="Membership"/> ne sont pas liés à un restaurant courant : aucun tenant requis.
/// </summary>
public partial class RestaurantService(IDbContextFactory<ChefOsDbContext> factory, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private const string FallbackSlug = "restaurant";

    // \z plutôt que $ : $ accepterait aussi un retour à la ligne final.
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex SlugPattern();

    public async Task<Restaurant> CreateAsync(string ownerUserId, CreateRestaurantInput input)
    {
        var name = RequireName(input.Name);

        await using var db = await factory.CreateDbContextAsync();

        var restaurant = new Restaurant
        {
            Name = name,
            Slug = await GenerateUniqueSlugAsync(db, name),
            Address = NullIfBlank(input.Address),
            VatNumber = NullIfBlank(input.VatNumber),
            CreatedAt = _clock.GetUtcNow().UtcDateTime
        };

        // Restaurant et appartenance sont enregistrés dans le même SaveChanges, donc dans la même transaction.
        db.Memberships.Add(new Membership { UserId = ownerUserId, Restaurant = restaurant, Role = MembershipRole.Owner });
        await db.SaveChangesAsync();

        return restaurant;
    }

    public async Task<IReadOnlyList<MembershipSummary>> GetMembershipsAsync(string userId)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.Memberships
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Restaurant!.Name)
            .Select(m => new MembershipSummary(m.RestaurantId, m.Restaurant!.Name, m.Role))
            .ToListAsync();
    }

    public async Task<bool> IsMemberAsync(string userId, int restaurantId)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.Memberships.AnyAsync(m => m.UserId == userId && m.RestaurantId == restaurantId);
    }

    public async Task<Restaurant?> GetAsync(int restaurantId)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.Restaurants.AsNoTracking().FirstOrDefaultAsync(r => r.Id == restaurantId);
    }

    public async Task UpdateSettingsAsync(int restaurantId, RestaurantSettingsInput input)
    {
        var name = RequireName(input.Name);
        RequireRate(input.DefaultFoodVatRate, "Le taux de TVA nourriture");
        RequireRate(input.DefaultDrinkVatRate, "Le taux de TVA boissons");
        RequireRate(input.FoodCostAlertThreshold, "Le seuil d'alerte");

        if (!SlugPattern().IsMatch(input.Slug))
            throw new DomainValidationException("Le lien ne peut contenir que des minuscules, chiffres et tirets");

        await using var db = await factory.CreateDbContextAsync();

        var restaurant = await db.Restaurants.FirstOrDefaultAsync(r => r.Id == restaurantId)
            ?? throw new InvalidOperationException($"Restaurant {restaurantId} introuvable.");

        // Le slug actuel du restaurant lui-même reste autorisé.
        if (await db.Restaurants.AnyAsync(r => r.Slug == input.Slug && r.Id != restaurantId))
            throw new DomainValidationException("Ce lien est déjà utilisé par un autre restaurant");

        restaurant.Name = name;
        restaurant.Slug = input.Slug;
        restaurant.Address = NullIfBlank(input.Address);
        restaurant.VatNumber = NullIfBlank(input.VatNumber);
        restaurant.DefaultFoodVatRate = input.DefaultFoodVatRate;
        restaurant.DefaultDrinkVatRate = input.DefaultDrinkVatRate;
        restaurant.FoodCostAlertThreshold = input.FoodCostAlertThreshold;

        await db.SaveChangesAsync();
    }

    /// <summary>Slug dérivé du nom, suffixé -2, -3… jusqu'à trouver un slug libre.</summary>
    private static async Task<string> GenerateUniqueSlugAsync(ChefOsDbContext db, string name)
    {
        var baseSlug = SlugGenerator.FromName(name);
        if (baseSlug.Length == 0)
            baseSlug = FallbackSlug;

        // Une seule requête : tous les slugs qui pourraient entrer en collision (base seule ou base-N).
        var prefix = baseSlug + "-";
        var taken = (await db.Restaurants
                .Where(r => r.Slug == baseSlug || r.Slug.StartsWith(prefix))
                .Select(r => r.Slug)
                .ToListAsync())
            .ToHashSet();

        if (!taken.Contains(baseSlug))
            return baseSlug;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = prefix + suffix;
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainValidationException("Le nom du restaurant est obligatoire");

        return name.Trim();
    }

    // Les taux sont stockés en fraction (0,12), mais le chef les saisit et les lit en pourcentage : le message parle en %.
    private static void RequireRate(decimal rate, string label)
    {
        if (rate is < 0m or > 1m)
            throw new DomainValidationException($"{label} doit être compris entre 0 et 100 %");
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
