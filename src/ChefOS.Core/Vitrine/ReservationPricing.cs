namespace ChefOS.Core.Vitrine;

/// <summary>Montant à payer en ligne ; <paramref name="Kind"/> est null quand rien n'est dû.</summary>
public record AmountToPay(decimal Amount, PaymentKind? Kind);

public static class ReservationPricing
{
    /// <summary>
    /// Au-delà de <c>GroupThreshold</c> personnes : prépaiement = pré-commande TTC × <c>PrepaymentPercent</c>.
    /// Sinon : acompte = <c>DepositPerPerson</c> × personnes. Arrondi à 2 décimales (loin de zéro) ; 0 donne <c>Kind = null</c>.
    /// </summary>
    public static AmountToPay Compute(int partySize, decimal preorderTotalInclVat, VitrineProfile rules)
    {
        var isGroup = partySize > rules.GroupThreshold;
        var raw = isGroup ? preorderTotalInclVat * rules.PrepaymentPercent : rules.DepositPerPerson * partySize;
        var amount = Math.Round(raw, 2, MidpointRounding.AwayFromZero);
        if (amount == 0m)
            return new AmountToPay(0m, null);
        return new AmountToPay(amount, isGroup ? PaymentKind.Prepayment : PaymentKind.Deposit);
    }
}
