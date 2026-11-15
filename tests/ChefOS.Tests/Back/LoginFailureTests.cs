using ChefOS.Back.Components.Account;
using Microsoft.AspNetCore.Identity;

namespace ChefOS.Tests.Back;

public sealed class LoginFailureTests
{
    [Fact]
    public void WrongCredentials_AndUnconfirmedAccount_GetTheSameMessage()
    {
        // Identity vérifie la confirmation avant le mot de passe : deux messages différents révéleraient les comptes non confirmés.
        Assert.Equal(LoginFailure.MessageFor(SignInResult.Failed), LoginFailure.MessageFor(SignInResult.NotAllowed));
        Assert.Contains("pas encore confirmée", LoginFailure.MessageFor(SignInResult.NotAllowed));
    }
}
