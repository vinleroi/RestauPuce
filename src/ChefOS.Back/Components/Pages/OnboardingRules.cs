using ChefOS.Core.Restaurants;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages;

/// <summary>Décisions de la page d'onboarding, hors du composant pour rester testables.</summary>
public static class OnboardingRules
{
    /// <summary>
    /// Restaurant que l'utilisateur a déjà créé (rôle propriétaire) sous le nom soumis, casse et espaces de bord ignorés ; null s'il n'y en a pas.
    /// Un second envoi du même formulaire (double clic, renvoi après une réponse lente) retrouve ainsi le restaurant du premier envoi
    /// au lieu d'en créer un deuxième.
    /// </summary>
    public static MembershipSummary? FindOwnedRestaurant(IReadOnlyList<MembershipSummary> memberships, string? submittedName)
    {
        var name = submittedName?.Trim();
        if (string.IsNullOrEmpty(name))
            return null;

        return memberships.FirstOrDefault(m =>
            m.Role == MembershipRole.Owner
            && string.Equals(m.RestaurantName.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }
}
