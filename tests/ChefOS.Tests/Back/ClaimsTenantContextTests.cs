using System.Security.Claims;
using ChefOS.Back.Tenancy;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;

namespace ChefOS.Tests.Back;

public class ClaimsTenantContextTests
{
    private static ClaimsPrincipal UserWith(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    private static ClaimsPrincipal UserOf(int restaurantId) =>
        UserWith(new Claim(ClaimsTenantContext.RestaurantClaimType, restaurantId.ToString()));

    private static ServerAuthenticationStateProvider CircuitOf(ClaimsPrincipal user)
    {
        var auth = new ServerAuthenticationStateProvider();
        auth.SetAuthenticationState(Task.FromResult(new AuthenticationState(user)));
        return auth;
    }

    [Fact]
    public void DuringRequest_ReadsClaimFromHttpContext()
    {
        // Le fournisseur n'est pas initialisé (endpoint hors Blazor) : seul HttpContext.User compte.
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = UserOf(7) } };
        var tenant = new ClaimsTenantContext(new ServerAuthenticationStateProvider(), http);

        Assert.Equal(7, tenant.CurrentRestaurantId);
    }

    [Fact]
    public void InCircuit_ReadsClaimFromAuthenticationState()
    {
        var tenant = new ClaimsTenantContext(CircuitOf(UserOf(12)), new HttpContextAccessor());

        Assert.Equal(12, tenant.CurrentRestaurantId);
    }

    [Fact]
    public void OutsideRequestAndCircuit_ReturnsNullWithoutThrowing()
    {
        // Démarrage de l'application : ni HttpContext, ni état d'authentification (GetAuthenticationStateAsync lèverait).
        var tenant = new ClaimsTenantContext(new ServerAuthenticationStateProvider(), new HttpContextAccessor());

        Assert.Null(tenant.CurrentRestaurantId);
    }

    [Fact]
    public void PendingAuthenticationState_ReturnsNullWithoutBlocking()
    {
        var auth = new ServerAuthenticationStateProvider();
        auth.SetAuthenticationState(new TaskCompletionSource<AuthenticationState>().Task);
        var tenant = new ClaimsTenantContext(auth, new HttpContextAccessor());

        Assert.Null(tenant.CurrentRestaurantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData(" 4")]
    public void MissingOrInvalidClaim_ReturnsNull(string? value)
    {
        var user = value is null
            ? UserWith(new Claim(ClaimTypes.NameIdentifier, "u1"))
            : UserWith(new Claim(ClaimsTenantContext.RestaurantClaimType, value));
        var tenant = new ClaimsTenantContext(CircuitOf(user), new HttpContextAccessor());

        Assert.Null(tenant.CurrentRestaurantId);
    }
}
