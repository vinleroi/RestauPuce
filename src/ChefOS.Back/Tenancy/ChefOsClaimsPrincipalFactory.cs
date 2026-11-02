using System.Globalization;
using System.Security.Claims;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ChefOS.Back.Tenancy;

/// <summary>
/// Ajoute au cookie d'authentification le claim <see cref="ClaimsTenantContext.RestaurantClaimType"/> : le restaurant actif de
/// l'utilisateur. Appelée à chaque connexion, à chaque <c>RefreshSignInAsync</c> et à chaque revalidation du cookie.
/// </summary>
public sealed class ChefOsClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityOptions> optionsAccessor,
    RestaurantService restaurants,
    ILogger<ChefOsClaimsPrincipalFactory> logger)
    : UserClaimsPrincipalFactory<ApplicationUser>(userManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (await ResolveActiveRestaurantAsync(user) is { } restaurantId)
            identity.AddClaim(new Claim(ClaimsTenantContext.RestaurantClaimType, restaurantId.ToString(CultureInfo.InvariantCulture)));

        return identity;
    }

    /// <summary>
    /// Le restaurant actif s'il fait toujours partie des restaurants de l'utilisateur ; sinon (jamais choisi, ou appartenance disparue)
    /// le premier par nom ; aucun s'il n'a aucun restaurant.
    /// </summary>
    private static int? ChooseActiveRestaurant(int? activeRestaurantId, IReadOnlyList<MembershipSummary> memberships) =>
        memberships.Any(m => m.RestaurantId == activeRestaurantId)
            ? activeRestaurantId
            : memberships.FirstOrDefault()?.RestaurantId;

    /// <summary><see cref="ChooseActiveRestaurant"/>, mémorisé dans <see cref="ApplicationUser.ActiveRestaurantId"/> s'il a changé.</summary>
    private async Task<int?> ResolveActiveRestaurantAsync(ApplicationUser user)
    {
        // GetMembershipsAsync trie par nom : le premier est le repli attendu.
        var memberships = await restaurants.GetMembershipsAsync(user.Id);
        var active = ChooseActiveRestaurant(user.ActiveRestaurantId, memberships);

        if (active != user.ActiveRestaurantId)
        {
            user.ActiveRestaurantId = active;
            var result = await UserManager.UpdateAsync(user);

            // Le claim reste juste même si l'enregistrement échoue : le choix sera simplement refait à la prochaine connexion.
            if (!result.Succeeded)
                logger.LogWarning(
                    "Impossible d'enregistrer le restaurant actif {RestaurantId} de l'utilisateur {UserId} : {Errors}",
                    active, user.Id, string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        return active;
    }
}
