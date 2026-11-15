using ChefOS.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Back.Components.Account;

/// <summary>
/// Inscription contrôlée. Ouverte en développement, si <c>Accounts:AllowRegistration</c> est vrai, ou tant qu'aucun compte n'existe
/// (création du premier compte). Sinon n'importe qui pourrait publier une vitrine et envoyer des e-mails depuis l'adresse de la plateforme.
/// </summary>
public sealed class RegistrationPolicy(UserManager<ApplicationUser> users, IConfiguration configuration, IHostEnvironment environment)
{
    public const string ClosedMessage = "Les inscriptions sont fermées. Demandez un accès à l'administrateur de ChefOS.";

    public async Task<bool> IsOpenAsync()
    {
        if (environment.IsDevelopment() || configuration.GetValue("Accounts:AllowRegistration", false))
            return true;
        return !await users.Users.AnyAsync();
    }
}
