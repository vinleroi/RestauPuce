namespace ChefOS.Infrastructure.Wines;

/// <summary>
/// Choix de l'assistant de rédaction des fiches de vin (section de configuration <c>Wines</c>) : <c>Demo</c> (par défaut) renvoie un texte générique,
/// sans clé ni réseau ; <c>Claude</c> rédige avec l'API Anthropic. Toute autre valeur est traitée comme <c>Demo</c>.
/// </summary>
public sealed record WineDescriberOptions
{
    public const string SectionName = "Wines";

    public const string ClaudeDescriber = "Claude";

    public string Describer { get; init; } = "Demo";
}
