using ChefOS.Core.Vitrine;

namespace ChefOS.Infrastructure.Wines;

/// <summary>Ce que l'on sait d'un vin à décrire, et les noms des plats publiés parmi lesquels proposer des accords.</summary>
public record WineDescribeRequest(string Name, int? Vintage, string? Producer, string? Appellation, IReadOnlyList<string> DishNames);

/// <summary>
/// Fiche rédigée par l'assistant : les champs de la fiche d'un <see cref="WineListItem"/> et les accords proposés par nom de plat.
/// <see cref="SuggestedPairingRecipeIds"/> est rempli par <see cref="Services.WineService"/> (qui connaît les plats) ; un describer le laisse vide.
/// </summary>
public record WineDraft(
    string? Appellation,
    string Region,
    string Country,
    WineColor Color,
    string Grapes,
    string Robe,
    string Nez,
    string Bouche,
    string Garde,
    string ServiceTemperature,
    string GeneralPairings,
    IReadOnlyList<string> SuggestedPairingDishNames)
{
    public IReadOnlyList<int> SuggestedPairingRecipeIds { get; init; } = [];
}

/// <summary>Rédige la fiche d'un vin. Une fiche rédigée est toujours un brouillon : le chef la relit avant de la publier.</summary>
public interface IWineDescriber
{
    /// <exception cref="InvalidOperationException">L'assistant est indisponible ou sa réponse est inutilisable ; le message est rédigé en français.</exception>
    Task<WineDraft> DescribeAsync(WineDescribeRequest request, CancellationToken ct = default);
}
