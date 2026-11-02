using System.Globalization;
using System.Security.Claims;
using ChefOS.Infrastructure.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace ChefOS.Back.Tenancy;

/// <summary>
/// Restaurant courant = claim <see cref="RestaurantClaimType"/> de l'utilisateur connecté. Pendant une requête HTTP (rendu statique,
/// prérendu, endpoints) il est lu dans <c>HttpContext.User</c> ; dans un circuit Blazor Server, dans l'état d'authentification du circuit.
/// Sans l'un ni l'autre (démarrage de l'application, par exemple) : aucun restaurant, jamais d'exception.
/// </summary>
public sealed class ClaimsTenantContext(AuthenticationStateProvider auth, IHttpContextAccessor http) : ITenantContext
{
    public const string RestaurantClaimType = "chefos:restaurant";

    public int? CurrentRestaurantId => GetRestaurantId(CurrentUser());

    public static int? GetRestaurantId(ClaimsPrincipal? user) =>
        int.TryParse(user?.FindFirst(RestaurantClaimType)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    private ClaimsPrincipal? CurrentUser()
    {
        if (http.HttpContext is { } context)
            return context.User;

        try
        {
            // Dans un circuit, l'état d'authentification est fourni d'avance : la tâche est déjà terminée.
            // On ne bloque jamais sur une tâche en cours.
            var state = auth.GetAuthenticationStateAsync();
            return state.IsCompletedSuccessfully ? state.Result.User : null;
        }
        catch (InvalidOperationException)
        {
            // Fournisseur jamais initialisé : ni requête ni circuit (démarrage, tâche de fond).
            return null;
        }
    }
}
