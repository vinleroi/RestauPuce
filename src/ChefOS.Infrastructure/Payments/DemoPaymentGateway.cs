using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Payments;

/// <summary>
/// Passerelle de démonstration : les paiements vivent en base (table globale <c>DemoGatewayPayments</c>), donc le back et la vitrine,
/// deux processus distincts, partagent paiements et remboursements. La page de démonstration de la vitrine change leur statut
/// avec <see cref="SetStatusAsync"/>. Enregistrée scoped : elle ouvre un contexte par opération via la fabrique du scope.
/// Ne sert qu'aux démos et aux tests.
/// </summary>
public sealed class DemoPaymentGateway(string baseUrl, IDbContextFactory<ChefOsDbContext> factory) : IPaymentGateway
{
    public string Name => "Demo";

    public async Task<GatewayPayment> CreateAsync(decimal amount, string description, string redirectUrl, string webhookUrl,
        IDictionary<string, string> metadata, CancellationToken ct)
    {
        var id = "demo_" + Guid.NewGuid().ToString("N")[..16];
        await using var db = await factory.CreateDbContextAsync(ct);
        db.DemoGatewayPayments.Add(new DemoGatewayPayment
        {
            Id = id, Amount = amount, Description = Truncate(description, 500), RedirectUrl = Truncate(redirectUrl, 2000), CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return new GatewayPayment(id, $"{baseUrl.TrimEnd('/')}/paiement-demo/{id}");
    }

    public async Task<GatewayStatus> GetStatusAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await Require(db, id, ct)).Status;
    }

    public Task<decimal> GetRefundedAmountAsync(string id, CancellationToken ct) => RefundedAmountAsync(id, ct);

    public async Task RefundAsync(string id, decimal amount, string idempotencyKey, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Verrou de ligne : deux remboursements simultanés du même paiement s'exécutent l'un après l'autre.
        var payment = await db.DemoGatewayPayments
            .FromSqlInterpolated($"""SELECT * FROM "DemoGatewayPayments" WHERE "Id" = {id} FOR UPDATE""")
            .FirstOrDefaultAsync(ct) ?? throw NotFound();

        if (payment.RefundKeys.Contains(idempotencyKey))
            return; // même clé : déjà exécuté
        if (payment.Status != GatewayStatus.Paid)
            throw new InvalidOperationException("Remboursement impossible : le paiement n'a pas été réglé");
        if (amount <= 0 || payment.RefundedAmount + amount > payment.Amount)
            throw new InvalidOperationException("Remboursement impossible : montant supérieur au paiement");

        // La clé n'est retenue qu'une fois les contrôles passés : un refus ne « brûle » pas la clé.
        payment.RefundedAmount += amount;
        payment.RefundKeys = [.. payment.RefundKeys, idempotencyKey];
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Change le statut d'un paiement (page de démonstration).</summary>
    public async Task SetStatusAsync(string id, GatewayStatus status, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var updated = await db.DemoGatewayPayments.Where(p => p.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, status), ct);
        if (updated == 0)
            throw NotFound();
    }

    /// <summary>Le paiement de démonstration (montant, libellé, adresse de retour, statut), ou null s'il est inconnu.</summary>
    public async Task<DemoGatewayPayment?> FindAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DemoGatewayPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <summary>Adresse où ramener le client après le paiement ; null si le paiement est inconnu.</summary>
    public async Task<string?> GetRedirectUrlAsync(string id, CancellationToken ct = default) => (await FindAsync(id, ct))?.RedirectUrl;

    /// <summary>Montant déjà remboursé (0 si inconnu).</summary>
    public async Task<decimal> RefundedAmountAsync(string id, CancellationToken ct = default) => (await FindAsync(id, ct))?.RefundedAmount ?? 0m;

    private static async Task<DemoGatewayPayment> Require(ChefOsDbContext db, string id, CancellationToken ct) =>
        await db.DemoGatewayPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw NotFound();

    private static InvalidOperationException NotFound() => new("Paiement de démonstration introuvable");

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
