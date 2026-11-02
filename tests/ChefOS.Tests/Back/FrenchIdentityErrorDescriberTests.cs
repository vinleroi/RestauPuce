using System.Reflection;
using ChefOS.Back.Components.Account;
using Microsoft.AspNetCore.Identity;

namespace ChefOS.Tests.Back;

public class FrenchIdentityErrorDescriberTests
{
    private static readonly FrenchIdentityErrorDescriber Describer = new();

    [Fact]
    public void EveryErrorOfIdentity_IsOverridden()
    {
        // Une méthode d'IdentityErrorDescriber ajoutée par une version future d'Identity resterait en anglais sans ce garde-fou.
        static IEnumerable<string> ErrorMethods(Type type, BindingFlags flags) => type.GetMethods(flags)
            .Where(m => m.ReturnType == typeof(IdentityError))
            .Select(m => m.Name);

        var inherited = ErrorMethods(typeof(IdentityErrorDescriber), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).ToHashSet();
        var overridden = ErrorMethods(typeof(FrenchIdentityErrorDescriber), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).ToHashSet();

        Assert.NotEmpty(inherited);
        Assert.Empty(inherited.Except(overridden));
    }

    [Fact]
    public void PasswordRules_ExplainWhatIsMissing_InFrench()
    {
        Assert.Equal("Le mot de passe doit contenir au moins 6 caractères.", Describer.PasswordTooShort(6).Description);
        Assert.Equal("Le mot de passe doit contenir au moins un chiffre (0-9).", Describer.PasswordRequiresDigit().Description);
        Assert.Equal("Le mot de passe doit contenir au moins une minuscule (a-z).", Describer.PasswordRequiresLower().Description);
        Assert.Equal("Le mot de passe doit contenir au moins une majuscule (A-Z).", Describer.PasswordRequiresUpper().Description);
        Assert.Equal("Le mot de passe doit contenir au moins un caractère spécial (ni lettre ni chiffre).", Describer.PasswordRequiresNonAlphanumeric().Description);
    }

    [Fact]
    public void DuplicateEmail_NamesTheAddress_AndKeepsIdentityCode()
    {
        var error = Describer.DuplicateEmail("chef@chefos.test");

        Assert.Equal("L'adresse e-mail « chef@chefos.test » est déjà utilisée.", error.Description);
        Assert.Equal(new IdentityErrorDescriber().DuplicateEmail("chef@chefos.test").Code, error.Code);
    }
}
