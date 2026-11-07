using System.Collections.Concurrent;

namespace ChefOS.Infrastructure.Payments;

/// <summary>
/// Passerelle de démonstration : les paiements vivent en mémoire (singleton) et la page de démonstration de la vitrine change leur statut
/// avec <see cref="SetStatus"/>. Un redémarrage les oublie ; ne sert qu'aux démos et aux tests.
/// </summary>
public sealed class DemoPaymentGateway(string baseUrl) : IPaymentGateway
{
    private sealed class Entry(decimal amount, string redirectUrl)
    {
        public decimal Amount { get; } = amount;
        public string RedirectUrl { get; } = redirectUrl;
        public GatewayStatus Status { get; set; } = GatewayStatus.Open;
        public decimal Refunded { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _payments = new();

    public string Name => "Demo";

    public Task<GatewayPayment> CreateAsync(decimal amount, string description, string redirectUrl, string webhookUrl,
        IDictionary<string, string> metadata, CancellationToken ct)
    {
        var id = "demo_" + Guid.NewGuid().ToString("N")[..16];
        _payments[id] = new Entry(amount, redirectUrl);
        return Task.FromResult(new GatewayPayment(id, $"{baseUrl.TrimEnd('/')}/paiement-demo/{id}"));
    }

    public Task<GatewayStatus> GetStatusAsync(string id, CancellationToken ct)
    {
        var entry = Require(id);
        lock (entry)
            return Task.FromResult(entry.Status);
    }

    public Task RefundAsync(string id, decimal amount, CancellationToken ct)
    {
        var entry = Require(id);
        lock (entry)
        {
            if (entry.Status != GatewayStatus.Paid)
                throw new InvalidOperationException("Remboursement impossible : le paiement n'a pas été réglé");
            if (amount <= 0 || entry.Refunded + amount > entry.Amount)
                throw new InvalidOperationException("Remboursement impossible : montant supérieur au paiement");
            entry.Refunded += amount;
        }

        return Task.CompletedTask;
    }

    /// <summary>Change le statut d'un paiement (page de démonstration).</summary>
    public void SetStatus(string id, GatewayStatus status)
    {
        var entry = Require(id);
        lock (entry)
            entry.Status = status;
    }

    /// <summary>Adresse où ramener le client après le paiement ; null si le paiement est inconnu.</summary>
    public string? GetRedirectUrl(string id) => _payments.TryGetValue(id, out var entry) ? entry.RedirectUrl : null;

    /// <summary>Montant déjà remboursé (0 si inconnu).</summary>
    public decimal RefundedAmount(string id)
    {
        if (!_payments.TryGetValue(id, out var entry))
            return 0m;
        lock (entry)
            return entry.Refunded;
    }

    private Entry Require(string id) =>
        _payments.TryGetValue(id, out var entry) ? entry : throw new InvalidOperationException("Paiement de démonstration introuvable");
}
