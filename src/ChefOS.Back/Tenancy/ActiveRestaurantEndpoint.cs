using System.Diagnostics.CodeAnalysis;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace ChefOS.Back.Tenancy;

/// <summary>
/// <c>POST /account/active-restaurant</c> (formulaire : <c>restaurantId</c>, <c>returnUrl</c> facultatif) : change le restaurant actif
/// de l'utilisateur et réémet son cookie avec le nouveau claim. Antiforgery vérifié (paramètres de formulaire), utilisateur connecté requis.
/// </summary>
public static class ActiveRestaurantEndpoint
{
    public const string Route = "/account/active-restaurant";

    public static IEndpointConventionBuilder MapActiveRestaurantEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(Route, SwitchAsync).RequireAuthorization();

    /// <summary>Chemin de cette application (« / » ou « /… ») ; jamais « // » ni « /\ », que les navigateurs suivent vers un autre site.</summary>
    public static bool IsLocalUrl([NotNullWhen(true)] string? url) =>
        url is not null
        && url.StartsWith('/')
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal)
        // Les navigateurs ignorent tabulations et retours à la ligne : « /\t/site » deviendrait « //site ».
        && !url.Any(char.IsControl);

    private static async Task<IResult> SwitchAsync(
        HttpContext context,
        [FromForm] int restaurantId,
        [FromForm] string? returnUrl,
        [FromServices] UserManager<ApplicationUser> users,
        [FromServices] SignInManager<ApplicationUser> signIn,
        [FromServices] RestaurantService restaurants)
    {
        var user = await users.GetUserAsync(context.User);
        if (user is null)
            return TypedResults.Challenge();

        if (!await restaurants.IsMemberAsync(user.Id, restaurantId))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Accès refusé",
                detail: "Vous ne faites pas partie de ce restaurant.");

        user.ActiveRestaurantId = restaurantId;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Impossible d'enregistrer le restaurant actif : {string.Join(" ", result.Errors.Select(e => e.Description))}");

        await signIn.RefreshSignInAsync(user);

        return TypedResults.LocalRedirect(IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
