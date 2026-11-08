using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;
using static ChefOS.Tests.Infrastructure.ReservationHarness;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Les erreurs de réservation en ligne disent où les corriger (<see cref="DomainValidationException.Field"/>), et le montant attendu est contrôlé.</summary>
[Collection("postgres")]
public class ReservationValidationFieldTests(PostgresFixture postgres)
{
    private static async Task<DomainValidationException> RefusedAsync(Task task) => await Assert.ThrowsAnyAsync<DomainValidationException>(() => task);

    [Fact]
    public async Task GuestErrors_AreTaggedGuests()
    {
        var h = await CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak", 25m);
        ReservationGuestInput[] Six(Func<int, ReservationGuestInput> guest) => Enumerable.Range(0, 6).Select(guest).ToArray();

        var count = await RefusedAsync(h.CreateAsync(Request(6, guests: Six(_ => Guest("A", "B", steak)).Take(5).ToArray())));
        var noDish = await RefusedAsync(h.CreateAsync(Request(6, guests: Six(i => i == 3 ? Guest("A", "B") : Guest("A", "B", steak)))));
        var unavailable = await RefusedAsync(h.CreateAsync(Request(6, guests: Six(i => Guest("A", "B", i == 2 ? 999999 : steak)))));
        var longName = await RefusedAsync(h.CreateAsync(Request(6, guests: Six(i => Guest(i == 1 ? new string('x', 101) : "A", "B", steak)))));
        var quantity = await RefusedAsync(h.CreateAsync(Request(6, guests: Six(i => i == 0
            ? new ReservationGuestInput("A", "B", [new ReservationDishInput(steak, 0)])
            : Guest("A", "B", steak)))));

        Assert.All(new[] { count, noDish, unavailable, longName, quantity }, e => Assert.Equal(ValidationFields.Guests, e.Field));
    }

    [Fact]
    public async Task SlotAndContactErrors_AreTaggedAccordingly()
    {
        var h = await CreateAsync(postgres);

        Assert.Equal(ValidationFields.Slot, (await RefusedAsync(h.CreateAsync(Request(time: new TimeOnly(19, 10))))).Field);
        Assert.Equal(ValidationFields.Slot, (await RefusedAsync(h.CreateAsync(Request(partySize: 21)))).Field);
        Assert.Equal(ValidationFields.Contact, (await RefusedAsync(h.CreateAsync(Request(email: "pas-un-mail")))).Field);
        Assert.Equal(ValidationFields.Contact, (await RefusedAsync(h.CreateAsync(Request(name: " ")))).Field);
        Assert.Equal(ValidationFields.Contact, (await RefusedAsync(h.CreateAsync(Request(phone: "12")))).Field);
        Assert.Equal(ValidationFields.Contact, (await RefusedAsync(h.CreateAsync(Request(name: new string('a', 121))))).Field);
    }

    [Fact]
    public async Task GatewayFailure_IsTaggedPayment()
    {
        var h = await CreateAsync(postgres);
        h.Gateway.FailCreate = true;

        Assert.Equal(ValidationFields.Payment, (await RefusedAsync(h.CreateAsync(Request()))).Field);
    }

    [Fact]
    public async Task ExpectedAmount_MatchingTheServer_IsAccepted_AndAbsentIsNotChecked()
    {
        var h = await CreateAsync(postgres);

        var withAmount = await h.CreateAsync(Request(2) with { ExpectedAmount = 20m });
        var without = await h.CreateAsync(Request(2));

        Assert.Equal(20m, withAmount.AmountDue);
        Assert.Equal(20m, without.AmountDue);
    }

    [Fact]
    public async Task ExpectedAmount_DifferentFromTheServer_IsRefused_BeforeAnythingIsSaved()
    {
        var h = await CreateAsync(postgres);
        await using (var db = h.Open())
        {
            var profile = db.VitrineProfiles.Single();
            profile.DepositPerPerson = 15m; // le restaurateur a changé l'acompte après l'affichage
            await db.SaveChangesAsync();
        }

        var refused = await RefusedAsync(h.CreateAsync(Request(2) with { ExpectedAmount = 20m }));

        Assert.Equal("Le montant a changé (20,00 € → 30,00 €) : vérifiez votre récapitulatif", refused.Message);
        Assert.Equal(ValidationFields.Amount, refused.Field);
        await using var verify = h.Open();
        Assert.Empty(verify.Reservations);
        Assert.Empty(verify.Payments);
        // Avec le montant à jour, la réservation passe.
        Assert.Equal(30m, (await h.CreateAsync(Request(2) with { ExpectedAmount = 30m })).AmountDue);
    }

    [Fact]
    public async Task ExpectedAmount_ForAPrepayment_FollowsTheCurrentDishPrices()
    {
        var h = await CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak", 25m);
        var guests = Enumerable.Range(0, 6).Select(_ => Guest("A", "B", steak)).ToArray(); // 150 € → 45 €
        await using (var db = h.Open())
        {
            var recipe = db.Recipes.Single(r => r.Id == steak);
            recipe.SellingPriceInclVat = 30m;
            await db.SaveChangesAsync();
        }

        var refused = await RefusedAsync(h.CreateAsync(Request(6, guests: guests) with { ExpectedAmount = 45m }));

        Assert.Equal(ValidationFields.Amount, refused.Field);
        Assert.Contains("45,00", refused.Message);
        Assert.Contains("54,00", refused.Message);
    }
}
