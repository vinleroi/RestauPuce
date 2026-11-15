using Microsoft.AspNetCore.Identity;

namespace ChefOS.Back.Components.Account;

/// <summary>
/// Message d'échec de connexion. Identity vérifie « adresse confirmée » avant le mot de passe : un message distinct pour « non autorisé »
/// révélerait qu'un compte non confirmé existe à cette adresse. Un seul message sert donc aux deux cas.
/// </summary>
public static class LoginFailure
{
    public const string Message = "Connexion impossible : adresse e-mail ou mot de passe incorrect, ou adresse pas encore confirmée.";

    public static string MessageFor(SignInResult _) => $"Erreur : {Message}";
}
