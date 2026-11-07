namespace ChefOS.Infrastructure.Payments;

public enum GatewayStatus { Open, Paid, Failed, Expired, Canceled }

/// <summary>Paiement créé chez la passerelle : son identifiant et l'adresse où le client règle.</summary>
public record GatewayPayment(string Id, string CheckoutUrl);

/// <summary>Passerelle de paiement (démonstration en mémoire ou Mollie). Les erreurs sont des <see cref="InvalidOperationException"/> au message français.</summary>
public interface IPaymentGateway
{
    string Name { get; }

    Task<GatewayPayment> CreateAsync(decimal amount, string description, string redirectUrl, string webhookUrl,
        IDictionary<string, string> metadata, CancellationToken ct);

    Task<GatewayStatus> GetStatusAsync(string id, CancellationToken ct);

    /// <summary>Rembourse <paramref name="amount"/>. <paramref name="idempotencyKey"/> identifie ce remboursement : le rejouer avec la même clé ne le crée pas deux fois.</summary>
    Task RefundAsync(string id, decimal amount, string idempotencyKey, CancellationToken ct);

    /// <summary>Montant déjà remboursé chez la passerelle (sert à rattraper un remboursement réussi dont l'enregistrement a échoué).</summary>
    Task<decimal> GetRefundedAmountAsync(string id, CancellationToken ct);
}
