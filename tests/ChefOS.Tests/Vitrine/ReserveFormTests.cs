using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;
using ChefOS.Vitrine.Components.Pages.Reserve;

namespace ChefOS.Tests.Vitrine;

public class ReserveFormTests
{
    private const string Zone = "Europe/Brussels";
    private const string Nbsp = " ";
    // Lundi 2 novembre 2026, 09:00 UTC = 10:00 à Bruxelles.
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly DateOnly Friday = new(2026, 11, 6);

    private static readonly IReadOnlyList<PublicMenuCategory> Menu =
    [
        new("Plats", [new PublicDish(1, "Waterzooi", null, 24m, EuAllergen.Fish), new PublicDish(2, "Carbonade", "Bière brune", 22.5m, EuAllergen.None)]),
        new("Desserts", [new PublicDish(3, "Dame blanche", null, 9m, EuAllergen.Milk)])
    ];

    private static VitrineProfile Rules(Action<VitrineProfile>? tweak = null)
    {
        var rules = new VitrineProfile { GroupThreshold = 5, DepositPerPerson = 10m, PrepaymentPercent = 0.30m, FreeCancellationHours = 24, MaxDaysAhead = 60, MaxPartySize = 20 };
        tweak?.Invoke(rules);
        return rules;
    }

    private static ReserveForm Form(Action<VitrineProfile>? tweak = null, IEnumerable<DateOnly>? closed = null, IEnumerable<DayOfWeek>? serviceDays = null) =>
        new(Rules(tweak), Zone, Now, Menu, closed ?? [], serviceDays ?? Enum.GetValues<DayOfWeek>());

    private static Slot SlotAt(int hour, int minute = 0, string service = "Soir", int remaining = 30, DateOnly? date = null)
    {
        var d = date ?? Friday;
        var time = new TimeOnly(hour, minute);
        return new Slot(d, time, service, RestaurantClock.ToUtc(d, time, Zone), remaining);
    }

    private static ReserveForm FormWithSlot(int party = 2, Action<VitrineProfile>? tweak = null)
    {
        var form = Form(tweak);
        form.SelectDate(Friday);
        form.SetPartySize(party);
        var slot = SlotAt(19);
        form.SetSlots([slot, SlotAt(19, 30)]);
        form.SelectSlot(slot);
        return form;
    }

    // ── Étape 1 : date, groupe, créneaux ─────────────────────────────────────

    [Fact]
    public void Dates_AreLimitedToTodayThroughMaxDaysAhead()
    {
        var form = Form();

        Assert.Equal(Today, form.FirstDate);
        Assert.Equal(Today.AddDays(60), form.LastDate);
        Assert.True(form.IsSelectable(Today));
        Assert.True(form.IsSelectable(Today.AddDays(60)));
        Assert.False(form.IsSelectable(Today.AddDays(-1)));
        Assert.False(form.IsSelectable(Today.AddDays(61)));
    }

    [Fact]
    public void Today_IsTheLocalDate_NotTheUtcOne()
    {
        // 23:30 UTC le 2 novembre = 00:30 le 3 novembre à Bruxelles.
        var form = new ReserveForm(Rules(), Zone, new DateTime(2026, 11, 2, 23, 30, 0, DateTimeKind.Utc), Menu, [], Enum.GetValues<DayOfWeek>());

        Assert.Equal(new DateOnly(2026, 11, 3), form.FirstDate);
    }

    [Fact]
    public void ClosedDays_AndDaysWithoutService_AreNotSelectable()
    {
        var form = Form(closed: [Friday], serviceDays: [DayOfWeek.Friday, DayOfWeek.Saturday]);

        Assert.True(form.IsClosed(Friday));
        Assert.False(form.IsSelectable(Friday));
        Assert.True(form.IsSelectable(Friday.AddDays(1)));       // samedi : service
        Assert.False(form.IsSelectable(Friday.AddDays(2)));      // dimanche : aucun service
        Assert.True(form.IsClosed(Friday.AddDays(2)));
    }

    [Fact]
    public void SelectDate_RefusesAnUnselectableDay_AndKeepsTheCurrentOne()
    {
        var form = Form(closed: [Friday]);
        Assert.True(form.SelectDate(Friday.AddDays(1)));

        Assert.False(form.SelectDate(Friday));

        Assert.Equal(Friday.AddDays(1), form.Date);
    }

    [Fact]
    public void SelectDate_ClearsTheSlotAndSlots()
    {
        var form = FormWithSlot();

        form.SelectDate(Friday.AddDays(1));

        Assert.Null(form.SelectedSlot);
        Assert.Empty(form.Slots);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(7, 7)]
    [InlineData(99, 20)]
    public void PartySize_IsClampedToOneThroughMax(int requested, int expected)
    {
        var form = Form();

        form.SetPartySize(requested);

        Assert.Equal(expected, form.PartySize);
    }

    [Fact]
    public void ChangingPartySize_ClearsTheSelectedSlot_ButNotTheDate()
    {
        var form = FormWithSlot(party: 2);

        form.SetPartySize(4);

        Assert.Equal(Friday, form.Date);
        Assert.Null(form.SelectedSlot);
    }

    [Fact]
    public void SameSizeAgain_KeepsTheSlot()
    {
        var form = FormWithSlot(party: 2);

        form.SetPartySize(2);

        Assert.NotNull(form.SelectedSlot);
    }

    [Fact]
    public void Slots_AreGroupedByService_InOrderOfTheirFirstSlot_WithHintWhenFewLeft()
    {
        var form = Form();
        form.SelectDate(Friday);
        form.SetSlots([SlotAt(19, 0, "Soir", 3), SlotAt(12, 0, "Midi", 10), SlotAt(12, 30, "Midi", 10), SlotAt(19, 30, "Soir", 3)]);

        Assert.Equal(["Midi", "Soir"], form.SlotGroups.Select(g => g.ServiceName));
        Assert.Equal(2, form.SlotGroups[0].Slots.Count);
        Assert.Null(form.RemainingHint(form.SlotGroups[0].Slots[0]));
        Assert.Equal("Plus que 3 places", form.RemainingHint(form.SlotGroups[1].Slots[0]));
    }

    [Theory]
    [InlineData(1, "Plus qu'une place")]
    [InlineData(2, "Plus que 2 places")]
    [InlineData(4, "Plus que 4 places")]
    [InlineData(5, null)]
    public void RemainingHint_AppearsFromFourLeftDownwards(int remaining, string? hint)
    {
        var form = Form();

        Assert.Equal(hint, form.RemainingHint(SlotAt(19, remaining: remaining)));
    }

    [Fact]
    public void SetSlots_DropsASelectionThatIsNoLongerOffered()
    {
        var form = FormWithSlot();

        form.SetSlots([SlotAt(20)]);

        Assert.Null(form.SelectedSlot);
    }

    [Fact]
    public void SetSlots_KeepsASelectionThatIsStillOffered()
    {
        var form = FormWithSlot();

        form.SetSlots([SlotAt(19, remaining: 5), SlotAt(20)]);

        Assert.Equal(new TimeOnly(19, 0), form.SelectedSlot!.Time);
        Assert.Equal(5, form.SelectedSlot.RemainingCovers);
    }

    [Fact]
    public void Advance_NeedsADateAndASlot()
    {
        var form = Form();
        Assert.False(form.TryAdvance());
        Assert.Equal(ReserveStep.When, form.Step);

        form.SelectDate(Friday);
        Assert.False(form.TryAdvance());
        Assert.NotNull(form.Error);
    }

    [Fact]
    public void Calendar_ListsMondayFirstWeeks_AndMarksSelectableDays()
    {
        var form = Form(closed: [new DateOnly(2026, 11, 10)]);

        var weeks = form.MonthWeeks(new DateOnly(2026, 11, 1));

        Assert.All(weeks, week => Assert.Equal(7, week.Count));
        Assert.Equal(new DateOnly(2026, 10, 26), weeks[0][0].Date);          // le 1er novembre 2026 est un dimanche
        Assert.False(weeks[0][0].InMonth);
        Assert.True(weeks[0][6].InMonth);                                    // dimanche 1er
        Assert.False(weeks[0][6].Selectable);                                // avant aujourd'hui (2)
        var tenth = weeks.SelectMany(w => w).Single(d => d.Date == new DateOnly(2026, 11, 10));
        Assert.True(tenth.IsClosed);
        Assert.False(tenth.Selectable);
        Assert.True(weeks.SelectMany(w => w).Single(d => d.Date == new DateOnly(2026, 11, 11)).Selectable);
    }

    [Fact]
    public void Months_CanBeBrowsedOnlyWithinTheBookableRange()
    {
        var form = Form();

        Assert.False(form.CanShowPreviousMonth(new DateOnly(2026, 11, 1)));
        Assert.True(form.CanShowNextMonth(new DateOnly(2026, 11, 1)));
        Assert.True(form.CanShowNextMonth(new DateOnly(2026, 12, 1)));      // 2 janvier est dans la plage (2 nov + 60 j = 1er janvier)
        Assert.False(form.CanShowNextMonth(new DateOnly(2027, 1, 1)));
    }

    // ── Étape 2 : convives ───────────────────────────────────────────────────

    [Fact]
    public void SmallParty_SkipsTheGuestStep()
    {
        var form = FormWithSlot(party: 5);

        Assert.False(form.IsGroup);
        Assert.True(form.TryAdvance());

        Assert.Equal(ReserveStep.Contact, form.Step);
    }

    [Fact]
    public void LargeParty_GoesThroughTheGuestStep_WithOneCardPerGuest()
    {
        var form = FormWithSlot(party: 8);

        Assert.True(form.IsGroup);
        Assert.True(form.TryAdvance());

        Assert.Equal(ReserveStep.Guests, form.Step);
        Assert.Equal(8, form.Guests.Count);
    }

    [Fact]
    public void GuestCards_FollowThePartySize_KeepingWhatWasEntered()
    {
        var form = Form();
        form.SetPartySize(6);
        form.Guests[0].FirstName = "Anne";
        form.AddDish(0, 1);

        form.SetPartySize(9);
        Assert.Equal(9, form.Guests.Count);
        Assert.Equal("Anne", form.Guests[0].FirstName);
        Assert.Single(form.Guests[0].Dishes);

        form.SetPartySize(6);
        Assert.Equal(6, form.Guests.Count);
    }

    [Fact]
    public void ThresholdFromTheRules_DecidesWhoIsAGroup()
    {
        var form = Form(r => r.GroupThreshold = 3);

        form.SetPartySize(3);
        Assert.False(form.IsGroup);
        form.SetPartySize(4);
        Assert.True(form.IsGroup);
    }

    [Fact]
    public void AddDish_CountsQuantities_AndTotalsLive()
    {
        var form = FormWithSlot(party: 6);

        form.AddDish(0, 1);
        form.AddDish(0, 1);
        form.AddDish(0, 3);
        form.AddDish(1, 2);

        Assert.Equal(2 * 24m + 9m, form.GuestTotal(0));
        Assert.Equal(22.5m, form.GuestTotal(1));
        Assert.Equal(0m, form.GuestTotal(2));
        Assert.Equal(2 * 24m + 9m + 22.5m, form.PreorderTotal);
        Assert.Equal(["Waterzooi", "Dame blanche"], form.Guests[0].Dishes.Select(d => form.DishName(d.RecipeId)));
    }

    [Fact]
    public void AddDish_IgnoresDishesNotOnTheMenu()
    {
        var form = FormWithSlot(party: 6);

        form.AddDish(0, 999);

        Assert.Empty(form.Guests[0].Dishes);
    }

    [Fact]
    public void SetQuantity_ClampsAtFifty_AndZeroRemovesTheLine()
    {
        var form = FormWithSlot(party: 6);
        form.AddDish(0, 1);

        form.SetQuantity(0, 1, 500);
        Assert.Equal(50, form.Guests[0].Dishes[0].Quantity);

        form.SetQuantity(0, 1, 0);
        Assert.Empty(form.Guests[0].Dishes);
    }

    [Fact]
    public void AGuest_CannotHaveMoreThanTenDifferentDishes()
    {
        var menu = new[] { new PublicMenuCategory("Tout", Enumerable.Range(1, 12).Select(i => new PublicDish(i, $"Plat {i}", null, 10m, EuAllergen.None)).ToList()) };
        var form = new ReserveForm(Rules(), Zone, Now, menu, [], Enum.GetValues<DayOfWeek>());
        form.SetPartySize(6);

        for (var i = 1; i <= 12; i++)
            form.AddDish(0, i);

        Assert.Equal(10, form.Guests[0].Dishes.Count);
    }

    [Fact]
    public void RemoveDish_RemovesTheLine()
    {
        var form = FormWithSlot(party: 6);
        form.AddDish(0, 1);
        form.AddDish(0, 2);

        form.RemoveDish(0, 1);

        Assert.Equal([2], form.Guests[0].Dishes.Select(d => d.RecipeId));
    }

    [Fact]
    public void CopyFromPrevious_DuplicatesThePreviousGuestsDishes_WithoutSharingThem()
    {
        var form = FormWithSlot(party: 6);
        form.AddDish(0, 1);
        form.AddDish(0, 1);
        form.AddDish(0, 3);

        form.CopyFromPrevious(1);
        form.AddDish(1, 1);

        Assert.Equal([2, 1], form.Guests[0].Dishes.Select(d => d.Quantity));
        Assert.Equal([3, 1], form.Guests[1].Dishes.Select(d => d.Quantity));
        Assert.Equal([1, 3], form.Guests[1].Dishes.Select(d => d.RecipeId));
    }

    [Fact]
    public void CopyFromPrevious_ReplacesWhatTheGuestAlreadyChose_AndIsANoOpForTheFirstGuest()
    {
        var form = FormWithSlot(party: 6);
        form.AddDish(0, 1);
        form.AddDish(1, 2);

        form.CopyFromPrevious(0);
        form.CopyFromPrevious(1);

        Assert.Equal([1], form.Guests[0].Dishes.Select(d => d.RecipeId));
        Assert.Equal([1], form.Guests[1].Dishes.Select(d => d.RecipeId));
        Assert.False(form.CanCopyFromPrevious(0));
        Assert.True(form.CanCopyFromPrevious(1));
    }

    [Fact]
    public void GuestStep_NeedsAtLeastOneDishPerGuest()
    {
        var form = FormWithSlot(party: 6);
        form.TryAdvance();
        for (var i = 0; i < 5; i++)
            form.AddDish(i, 1);

        Assert.False(form.TryAdvance());

        Assert.Equal(ReserveStep.Guests, form.Step);
        Assert.Contains("convive 6", form.Error);
        form.AddDish(5, 2);
        Assert.True(form.TryAdvance());
        Assert.Equal(ReserveStep.Contact, form.Step);
        Assert.Null(form.Error);
    }

    [Fact]
    public void GuestNames_AreOptional()
    {
        var form = FormWithSlot(party: 6);
        form.TryAdvance();
        for (var i = 0; i < 6; i++)
            form.AddDish(i, 1);

        Assert.True(form.TryAdvance());
    }

    [Fact]
    public void Back_ReturnsToThePreviousStepOfTheFlow()
    {
        var small = FormWithSlot(party: 2);
        small.TryAdvance();
        small.Back();
        Assert.Equal(ReserveStep.When, small.Step);

        var large = FormWithSlot(party: 6);
        large.TryAdvance();
        for (var i = 0; i < 6; i++)
            large.AddDish(i, 1);
        large.TryAdvance();
        large.Back();
        Assert.Equal(ReserveStep.Guests, large.Step);
        large.Back();
        Assert.Equal(ReserveStep.When, large.Step);
        large.Back();
        Assert.Equal(ReserveStep.When, large.Step);
    }

    // ── Étape 3 : montant, règle d'annulation, contact ───────────────────────

    [Fact]
    public void SmallParty_PaysADepositPerPerson()
    {
        var form = FormWithSlot(party: 2);

        Assert.Equal(20m, form.AmountDue);
        Assert.Equal($"Acompte : 10{Nbsp}€ × 2 couverts", form.NatureLabel);
        Assert.Equal($"Réserver et payer 20{Nbsp}€", form.SubmitLabel);
    }

    [Fact]
    public void SinglePerson_SaysOneCover()
    {
        var form = FormWithSlot(party: 1);

        Assert.Equal($"Acompte : 10{Nbsp}€ × 1 couvert", form.NatureLabel);
    }

    [Fact]
    public void LargeParty_PaysAPercentageOfThePreorder()
    {
        var form = FormWithSlot(party: 6);
        form.AddDish(0, 1); // 24
        form.AddDish(1, 2); // 22,50
        form.AddDish(2, 3); // 9

        Assert.Equal(Math.Round((24m + 22.5m + 9m) * 0.30m, 2), form.AmountDue);
        Assert.Equal($"Prépaiement : 30{Nbsp}% des plats pré-commandés", form.NatureLabel);
        Assert.Equal($"Réserver et payer 16,65{Nbsp}€", form.SubmitLabel);
    }

    [Fact]
    public void NothingToPay_OffersPlainReservation()
    {
        var form = FormWithSlot(party: 2, tweak: r => r.DepositPerPerson = 0m);

        Assert.Equal(0m, form.AmountDue);
        Assert.Null(form.NatureLabel);
        Assert.Equal("Réserver", form.SubmitLabel);
    }

    [Fact]
    public void AmountWithCents_ShowsTwoDecimals_WholeAmountsNone()
    {
        Assert.Equal($"12,60{Nbsp}€", Fmt.Money(12.6m));
        Assert.Equal($"10{Nbsp}€", Fmt.Money(10m));
        Assert.Equal($"1{Nbsp}234,50{Nbsp}€", Fmt.Money(1234.5m));
    }

    [Fact]
    public void CancellationRule_IsSpelledOutInLocalTime()
    {
        // Vendredi 6 novembre 19:00 (heure locale) moins 24 h = jeudi 5 novembre 19:00.
        var form = FormWithSlot(party: 2);

        Assert.Equal("Annulation gratuite jusqu'au 05/11/2026 à 19:00, ensuite l'acompte n'est pas remboursé.", form.CancellationText);
    }

    [Fact]
    public void CancellationRule_ForAPrepayment_SaysPrepayment()
    {
        var form = FormWithSlot(party: 6, tweak: r => r.FreeCancellationHours = 48);
        form.AddDish(0, 1);

        Assert.Equal("Annulation gratuite jusqu'au 04/11/2026 à 19:00, ensuite le prépaiement n'est pas remboursé.", form.CancellationText);
    }

    [Fact]
    public void CancellationRule_WithNothingPaid_OnlyStatesTheDeadline()
    {
        var form = FormWithSlot(party: 2, tweak: r => r.DepositPerPerson = 0m);

        Assert.Equal("Annulation gratuite jusqu'au 05/11/2026 à 19:00.", form.CancellationText);
    }

    [Fact]
    public void CancellationRule_AlreadyPast_SaysNoFreeCancellation()
    {
        var form = Form();
        form.SelectDate(Today);
        form.SetPartySize(2);
        var slot = SlotAt(13, date: Today); // dans 3 h, moins que les 24 h d'annulation gratuite
        form.SetSlots([slot]);
        form.SelectSlot(slot);

        Assert.Equal("Cette réservation est trop proche pour une annulation gratuite : l'acompte ne sera pas remboursé en cas d'annulation.", form.CancellationText);
    }

    [Fact]
    public void Contact_IsValidatedWithTheServiceRules()
    {
        var form = FormWithSlot(party: 2);

        Assert.Equal("Indiquez votre nom", form.ContactError());
        form.Name = "Dupont";
        Assert.Equal("L'adresse e-mail est invalide", form.ContactError());
        form.Email = "dupont@example";
        Assert.Equal("L'adresse e-mail est invalide", form.ContactError());
        form.Email = "dupont@example.com";
        Assert.Equal("Le numéro de téléphone est invalide (6 caractères minimum)", form.ContactError());
        form.Phone = "04 7";
        Assert.NotNull(form.ContactError());
        form.Phone = "0470 12 34 56";
        Assert.Null(form.ContactError());
    }

    [Fact]
    public void Submit_NeedsValidContact_AcceptedTerms_AndNothingBusy()
    {
        var form = FormWithSlot(party: 2);
        form.TryAdvance();
        form.Name = "Dupont";
        form.Email = "dupont@example.com";
        form.Phone = "0470 12 34 56";

        Assert.False(form.CanSubmit);          // « J'accepte » non coché
        form.Accepted = true;
        Assert.True(form.CanSubmit);
        form.IsBusy = true;
        Assert.False(form.CanSubmit);
        form.IsBusy = false;
        form.Email = "";
        Assert.False(form.CanSubmit);
    }

    [Fact]
    public void Submit_IsOnlyPossibleOnTheContactStep()
    {
        var form = FormWithSlot(party: 2);
        form.Name = "Dupont";
        form.Email = "dupont@example.com";
        form.Phone = "0470 12 34 56";
        form.Accepted = true;

        Assert.Equal(ReserveStep.When, form.Step);
        Assert.False(form.CanSubmit);
    }

    [Fact]
    public void Request_ForASmallParty_CarriesNoGuests_AndTrimsTheContact()
    {
        var form = FormWithSlot(party: 2);
        form.TryAdvance();
        form.Name = "  Dupont ";
        form.Email = " dupont@example.com ";
        form.Phone = " 0470 12 34 56 ";
        form.Note = "  allergie aux noix ";
        form.Accepted = true;

        var request = form.ToRequest();

        Assert.Equal(Friday, request.Date);
        Assert.Equal(new TimeOnly(19, 0), request.Time);
        Assert.Equal("Soir", request.ServiceName);
        Assert.Equal(2, request.PartySize);
        Assert.Equal("Dupont", request.CustomerName);
        Assert.Equal("dupont@example.com", request.CustomerEmail);
        Assert.Equal("0470 12 34 56", request.CustomerPhone);
        Assert.Equal("allergie aux noix", request.Note);
        Assert.Empty(request.Guests);
    }

    [Fact]
    public void Request_ForALargeParty_CarriesEachGuestWithTheirDishes()
    {
        var form = FormWithSlot(party: 6);
        form.TryAdvance();
        for (var i = 0; i < 6; i++)
            form.AddDish(i, i % 2 == 0 ? 1 : 2);
        form.Guests[0].FirstName = " Anne ";
        form.Guests[0].LastName = "Martin";
        form.AddDish(0, 3);
        form.Guests[1].FirstName = "  ";
        form.TryAdvance();
        form.Name = "Dupont";
        form.Email = "dupont@example.com";
        form.Phone = "0470 12 34 56";
        form.Note = " ";

        var request = form.ToRequest();

        Assert.Equal(6, request.Guests.Count);
        Assert.Equal("Anne", request.Guests[0].FirstName);
        Assert.Equal("Martin", request.Guests[0].LastName);
        Assert.Equal([new ReservationDishInput(1, 1), new ReservationDishInput(3, 1)], request.Guests[0].Dishes);
        Assert.Null(request.Guests[1].FirstName);
        Assert.Equal([new ReservationDishInput(2, 1)], request.Guests[1].Dishes);
        Assert.Null(request.Note);
    }

    // ── Erreurs du serveur ───────────────────────────────────────────────────

    [Theory]
    [InlineData(ValidationFields.Slot, ReserveStep.When)]
    [InlineData(ValidationFields.Guests, ReserveStep.Guests)]
    [InlineData(ValidationFields.Contact, ReserveStep.Contact)]
    [InlineData(ValidationFields.Payment, ReserveStep.Contact)]
    [InlineData(ValidationFields.Amount, ReserveStep.Contact)]
    public void ServerErrors_SendTheCustomerBackToTheZoneTheServiceNames(string field, ReserveStep expected)
    {
        Assert.Equal(expected, ReserveForm.StepFor(field));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("autre")]
    public void ErrorWithoutAKnownZone_HasNoStep_WhateverItsWording(string? field)
    {
        Assert.Null(ReserveForm.StepFor(field));
    }

    [Fact]
    public void RoutingIgnoresTheWording()
    {
        var form = FormWithSlot(party: 2);
        form.TryAdvance();

        // Un message qui parle de créneau mais classé « contact » : seul le champ compte.
        form.HandleServerError("Ce créneau est étrange", ValidationFields.Contact);

        Assert.Equal(ReserveStep.Contact, form.Step);
    }

    [Fact]
    public void ServerError_ShowsTheMessage_AndGoesBack_AskingForFreshSlotsWhenTheSlotIsGone()
    {
        var form = FormWithSlot(party: 2);
        form.TryAdvance();

        var refresh = form.HandleServerError("Complet", ValidationFields.Slot);

        Assert.True(refresh);
        Assert.Equal(ReserveStep.When, form.Step);
        Assert.Equal("Complet", form.Error);
        Assert.Null(form.SelectedSlot);
        Assert.False(form.IsBusy);
        Assert.False(form.Accepted);
    }

    [Fact]
    public void ServerError_AboutContact_StaysOnThatStep_WithoutRefreshingSlots()
    {
        var form = FormWithSlot(party: 2);
        form.TryAdvance();

        var refresh = form.HandleServerError("Invalide", ValidationFields.Contact);

        Assert.False(refresh);
        Assert.Equal(ReserveStep.Contact, form.Step);
        Assert.NotNull(form.SelectedSlot);
    }

    [Fact]
    public void ServerError_AboutGuests_GoesToTheGuestStep_OnlyForAGroup()
    {
        var group = FormWithSlot(party: 6);
        group.TryAdvance();
        for (var i = 0; i < 6; i++)
            group.AddDish(i, 1);
        group.TryAdvance();

        group.HandleServerError("Plat indisponible", ValidationFields.Guests);
        Assert.Equal(ReserveStep.Guests, group.Step);

        var small = FormWithSlot(party: 2);
        small.TryAdvance();
        small.HandleServerError("Plat indisponible", ValidationFields.Guests);
        Assert.Equal(ReserveStep.Contact, small.Step);
    }

    [Fact]
    public void UnknownServerError_StaysOnTheCurrentStep()
    {
        var form = FormWithSlot(party: 2);
        form.TryAdvance();

        form.HandleServerError("Quelque chose d'inattendu");

        Assert.Equal(ReserveStep.Contact, form.Step);
        Assert.Equal("Quelque chose d'inattendu", form.Error);
    }

    // ── Montant attendu, nom, rafraîchissement ───────────────────────────────

    [Fact]
    public void Request_CarriesTheAmountTheCustomerSaw()
    {
        var form = FormWithSlot(party: 2);
        form.TryAdvance();
        form.Name = "Dupont";
        form.Email = "dupont@example.com";
        form.Phone = "0470 12 34 56";

        Assert.Equal(20m, form.ToRequest().ExpectedAmount);
    }

    [Fact]
    public void Refresh_RecomputesTheAmountWithNewRules_KeepsTheEntries_AndAsksToAcceptAgain()
    {
        var form = FormWithSlot(party: 2);
        form.TryAdvance();
        form.Name = "Dupont";
        form.Accepted = true;
        Assert.Equal(20m, form.AmountDue);

        form.Refresh(Rules(r => r.DepositPerPerson = 15m), Menu);

        Assert.Equal(30m, form.AmountDue);
        Assert.Equal("Dupont", form.Name);
        Assert.False(form.Accepted);
        Assert.Equal(ReserveStep.Contact, form.Step);
    }

    [Fact]
    public void Refresh_UsesNewDishPrices_ForAGroupPrepayment()
    {
        var form = FormWithSlot(party: 6);
        form.AddDish(0, 1); // 24 → 7,20

        var cheaper = new[] { new PublicMenuCategory("Plats", [new PublicDish(1, "Waterzooi", null, 30m, EuAllergen.Fish)]) };
        form.Refresh(Rules(), cheaper);

        Assert.Equal(9m, form.AmountDue);
    }

    [Fact]
    public void Name_IsLimitedTo120Characters_LikeTheService()
    {
        var form = FormWithSlot(party: 2);
        form.Name = new string('a', 121);
        form.Email = "dupont@example.com";
        form.Phone = "0470 12 34 56";

        Assert.Equal("Le nom ne peut pas dépasser 120 caractères", form.ContactError());
        form.Name = new string('a', 120);
        Assert.Null(form.ContactError());
    }
}
