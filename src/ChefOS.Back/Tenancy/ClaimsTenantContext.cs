using System.Globalization;
using System.Security.Claims;
using ChefOS.Infrastructure.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace ChefOS.Back.Tenancy;

/// <summary>
/// Restaurant courant = claim <see cref="RestaurantClaimType"/> de l'utilisateur connecté. Dans un circuit Blazor Server il est lu
/// d'abord dans l'état d'authentification du circuit, qui est celui de la page affichée ; <c>HttpContext.User</c> ne sert que sans
/// circuit (rendu statique, endpoints). Après une reconnexion, <c>IHttpContextAccessor.HttpContext</c> appartient à la nouvelle connexion
/// et peut porter le cookie d'un changement de restaurant fait dans un autre onglet, alors que la page montre encore l'ancien.
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

    private ClaimsPrincipal? CurrentUser() => CircuitUser() ?? http.HttpContext?.User;

    // Null quand il n'y a pas d'état d'authentification de circuit exploitable : l'appelant se rabat alors sur HttpContext.
    private ClaimsPrincipal? CircuitUser()
    {
        try
        {
            // Dans un circuit, l'état d'authentification est fourni d'avance : la tâche est déjà terminée.
            // On ne bloque jamais sur une tâche en cours.
            var state = auth.GetAuthenticationStateAsync();
            return state.IsCompletedSuccessfully ? state.Result.User : null;
        }
        catch (InvalidOperationException)
        {
            // Fournisseur jamais initialisé : ni circuit ni rendu de composant (endpoint, démarrage, tâche de fond).
            return null;
        }
    }
}
