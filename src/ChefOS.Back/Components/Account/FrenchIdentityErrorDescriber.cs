using Microsoft.AspNetCore.Identity;

namespace ChefOS.Back.Components.Account;

/// <summary>
/// Messages d'erreur d'ASP.NET Core Identity en français (création de compte, mot de passe). Chaque méthode de
/// <see cref="IdentityErrorDescriber"/> est redéfinie : un test échoue si une version future d'Identity en ajoute une que l'on oublierait.
/// Les codes d'erreur restent ceux d'Identity.
/// </summary>
public sealed class FrenchIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => Error(nameof(DefaultError), "Une erreur inconnue est survenue.");

    public override IdentityError ConcurrencyFailure() =>
        Error(nameof(ConcurrencyFailure), "L'élément a été modifié entre-temps : rechargez la page puis recommencez.");

    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), "Mot de passe incorrect.");

    public override IdentityError InvalidToken() => Error(nameof(InvalidToken), "Jeton invalide.");

    public override IdentityError RecoveryCodeRedemptionFailed() =>
        Error(nameof(RecoveryCodeRedemptionFailed), "Le code de récupération n'a pas pu être utilisé.");

    public override IdentityError LoginAlreadyAssociated() =>
        Error(nameof(LoginAlreadyAssociated), "Un utilisateur est déjà associé à cette connexion.");

    public override IdentityError InvalidUserName(string? userName) =>
        Error(nameof(InvalidUserName), $"Le nom d'utilisateur « {userName} » n'est pas valide : lettres et chiffres uniquement.");

    public override IdentityError InvalidEmail(string? email) =>
        Error(nameof(InvalidEmail), $"L'adresse e-mail « {email} » n'est pas valide.");

    public override IdentityError DuplicateUserName(string userName) =>
        Error(nameof(DuplicateUserName), $"Le nom d'utilisateur « {userName} » est déjà pris.");

    public override IdentityError DuplicateEmail(string email) =>
        Error(nameof(DuplicateEmail), $"L'adresse e-mail « {email} » est déjà utilisée.");

    public override IdentityError InvalidRoleName(string? role) =>
        Error(nameof(InvalidRoleName), $"Le nom de rôle « {role} » n'est pas valide.");

    public override IdentityError DuplicateRoleName(string role) =>
        Error(nameof(DuplicateRoleName), $"Le nom de rôle « {role} » est déjà pris.");

    public override IdentityError UserAlreadyHasPassword() =>
        Error(nameof(UserAlreadyHasPassword), "Cet utilisateur a déjà un mot de passe.");

    public override IdentityError UserLockoutNotEnabled() =>
        Error(nameof(UserLockoutNotEnabled), "Le verrouillage n'est pas activé pour cet utilisateur.");

    public override IdentityError UserAlreadyInRole(string role) =>
        Error(nameof(UserAlreadyInRole), $"L'utilisateur a déjà le rôle « {role} ».");

    public override IdentityError UserNotInRole(string role) =>
        Error(nameof(UserNotInRole), $"L'utilisateur n'a pas le rôle « {role} ».");

    public override IdentityError PasswordTooShort(int length) =>
        Error(nameof(PasswordTooShort), $"Le mot de passe doit contenir au moins {length} caractères.");

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        Error(nameof(PasswordRequiresUniqueChars), $"Le mot de passe doit contenir au moins {uniqueChars} caractères différents.");

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        Error(nameof(PasswordRequiresNonAlphanumeric), "Le mot de passe doit contenir au moins un caractère spécial (ni lettre ni chiffre).");

    public override IdentityError PasswordRequiresDigit() =>
        Error(nameof(PasswordRequiresDigit), "Le mot de passe doit contenir au moins un chiffre (0-9).");

    public override IdentityError PasswordRequiresLower() =>
        Error(nameof(PasswordRequiresLower), "Le mot de passe doit contenir au moins une minuscule (a-z).");

    public override IdentityError PasswordRequiresUpper() =>
        Error(nameof(PasswordRequiresUpper), "Le mot de passe doit contenir au moins une majuscule (A-Z).");

    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };
}
