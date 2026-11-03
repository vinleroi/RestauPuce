namespace ChefOS.Infrastructure.Receipts;

/// <summary>
/// Choix du lecteur de tickets (section de configuration <c>Receipts</c>) : <c>Claude</c> (par défaut) lit les tickets avec l'API Anthropic,
/// <c>Demo</c> renvoie un ticket factice, sans clé ni réseau. Toute autre valeur est traitée comme <c>Claude</c>.
/// </summary>
public sealed record ReceiptReaderOptions
{
    public const string SectionName = "Receipts";

    public const string DemoReader = "Demo";

    public string Reader { get; init; } = "Claude";
}
