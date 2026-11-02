using Microsoft.AspNetCore.Identity;

namespace ChefOS.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    /// <summary>Restaurant actuellement sélectionné par l'utilisateur (null tant qu'il n'en a pas choisi).</summary>
    public int? ActiveRestaurantId { get; set; }
}
