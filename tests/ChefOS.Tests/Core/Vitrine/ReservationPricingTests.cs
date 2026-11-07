using ChefOS.Core.Vitrine;

namespace ChefOS.Tests.Core.Vitrine;

public class ReservationPricingTests
{
    [Fact]
    public void Pricing_SmallAndLargeGroups()
    {
        var rules = new VitrineProfile(); // seuil 5, 10 €/pers., 30 %

        var small = ReservationPricing.Compute(4, 999m, rules);
        Assert.Equal(40m, small.Amount);
        Assert.Equal(PaymentKind.Deposit, small.Kind);

        // Le seuil lui-même (5) reste un acompte.
        var atThreshold = ReservationPricing.Compute(5, 999m, rules);
        Assert.Equal(50m, atThreshold.Amount);
        Assert.Equal(PaymentKind.Deposit, atThreshold.Kind);

        var large = ReservationPricing.Compute(6, 200m, rules);
        Assert.Equal(60m, large.Amount);
        Assert.Equal(PaymentKind.Prepayment, large.Kind);

        // Arrondi à 2 décimales, loin de zéro : 100.05 × 0.30 = 30.015 → 30.02.
        var rounded = ReservationPricing.Compute(6, 100.05m, rules);
        Assert.Equal(30.02m, rounded.Amount);
    }

    [Fact]
    public void Pricing_ZeroDeposit_NoPayment()
    {
        var free = new VitrineProfile { DepositPerPerson = 0m };
        var result = ReservationPricing.Compute(2, 0m, free);
        Assert.Equal(0m, result.Amount);
        Assert.Null(result.Kind);

        // Groupe sans pré-commande : rien à prépayer.
        var group = ReservationPricing.Compute(8, 0m, new VitrineProfile());
        Assert.Equal(0m, group.Amount);
        Assert.Null(group.Kind);
    }
}
