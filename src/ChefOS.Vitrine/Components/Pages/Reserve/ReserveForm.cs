using System.Text.RegularExpressions;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Vitrine.Components.Pages.Reserve;

public enum ReserveStep { When = 1, Guests = 2, Contact = 3 }

/// <summary>Un plat choisi par un convive.</summary>
public sealed class DishPick(int recipeId, int quantity)
{
    public int RecipeId { get; } = recipeId;
    public int Quantity { get; set; } = quantity;
}

/// <summary>Un convive d'un grand groupe : nom facultatif et plats pré-commandés.</summary>
public sealed class GuestDraft
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public List<DishPick> Dishes { get; } = [];
}

public sealed record SlotGroup(string ServiceName, IReadOnlyList<Slot> Slots);

public sealed record CalendarDay(DateOnly Date, bool InMonth, bool Selectable, bool IsClosed);

/// <summary>
/// Logique pure du formulaire de réservation (aucun accès à la base ni au réseau) : date dans la plage réservable, taille du groupe, créneaux,
/// convives et plats des grands groupes, montant à payer et règle d'annulation affichés en clair, coordonnées, demande à envoyer.
/// Le service revalide tout (le montant est recalculé côté serveur) : ce formulaire guide le client et lui épargne les allers-retours.
/// </summary>
public sealed partial class ReserveForm
{
    public const int MaxDishQuantity = 50;
    public const int MaxDishLinesPerGuest = 10;
    public const int FewLeftThreshold = 4;

    private const string Nbsp = " ";

    private readonly string _timeZone;
    private readonly DateTime _utcNow;
    private readonly Dictionary<int, PublicDish> _dishes;
    private readonly HashSet<DateOnly> _closedDates;
    private readonly HashSet<DayOfWeek> _serviceDays;
    private IReadOnlyList<Slot> _slots = [];

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+\z")]
    private static partial Regex EmailPattern();

    public ReserveForm(VitrineProfile rules, string timeZoneId, DateTime utcNow, IEnumerable<PublicMenuCategory> menu,
        IEnumerable<DateOnly> closedDates, IEnumerable<DayOfWeek> serviceDays)
    {
        Rules = rules;
        _timeZone = timeZoneId;
        _utcNow = utcNow;
        _dishes = menu.SelectMany(c => c.Dishes).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        _closedDates = closedDates.ToHashSet();
        _serviceDays = serviceDays.ToHashSet();
        FirstDate = RestaurantClock.Today(utcNow, timeZoneId);
        LastDate = FirstDate.AddDays(rules.MaxDaysAhead);
        PartySize = Math.Min(2, Math.Max(1, rules.MaxPartySize));
    }

    public VitrineProfile Rules { get; }
    public ReserveStep Step { get; private set; } = ReserveStep.When;
    public string? Error { get; private set; }
    public bool IsBusy { get; set; }

    // ── Étape 1 ──────────────────────────────────────────────────────────────

    public DateOnly FirstDate { get; }
    public DateOnly LastDate { get; }
    public DateOnly? Date { get; private set; }
    public int PartySize { get; private set; }
    public Slot? SelectedSlot { get; private set; }
    public IReadOnlyList<Slot> Slots => _slots;

    public bool IsGroup => PartySize > Rules.GroupThreshold;

    /// <summary>Jour fermé (fermeture exceptionnelle) ou sans service ce jour de la semaine : grisé dans le calendrier.</summary>
    public bool IsClosed(DateOnly date) => _closedDates.Contains(date) || !_serviceDays.Contains(date.DayOfWeek);

    public bool IsSelectable(DateOnly date) => date >= FirstDate && date <= LastDate && !IsClosed(date);

    /// <summary>Choisit le jour ; refusé (false) s'il est hors plage ou fermé. Les créneaux sont à recharger.</summary>
    public bool SelectDate(DateOnly date)
    {
        if (!IsSelectable(date))
            return false;

        Date = date;
        _slots = [];
        SelectedSlot = null;
        Error = null;
        return true;
    }

    /// <summary>Change le nombre de couverts (borné de 1 au maximum du restaurant). Les créneaux dépendent de la taille : la sélection est à refaire.</summary>
    public void SetPartySize(int size)
    {
        var clamped = Math.Clamp(size, 1, Math.Max(1, Rules.MaxPartySize));
        if (clamped != PartySize)
        {
            PartySize = clamped;
            _slots = [];
            SelectedSlot = null;
        }

        ResizeGuests();
    }

    public void SetSlots(IReadOnlyList<Slot> slots)
    {
        _slots = slots;
        SelectedSlot = SelectedSlot is { } kept
            ? slots.FirstOrDefault(s => s.Date == kept.Date && s.Time == kept.Time && s.ServiceName == kept.ServiceName)
            : null;
    }

    public void SelectSlot(Slot slot)
    {
        var offered = _slots.FirstOrDefault(s => s.Date == slot.Date && s.Time == slot.Time && s.ServiceName == slot.ServiceName);
        if (offered is null)
            return;

        SelectedSlot = offered;
        Error = null;
    }

    /// <summary>Créneaux regroupés par service (« Midi », « Soir »), dans l'ordre de leur premier créneau.</summary>
    public IReadOnlyList<SlotGroup> SlotGroups => _slots
        .GroupBy(s => s.ServiceName)
        .Select(g => new SlotGroup(g.Key, g.OrderBy(s => s.Time).ToList()))
        .OrderBy(g => g.Slots[0].Time)
        .ToList();

    /// <summary>« Plus que 3 places » quand il en reste 4 ou moins dans le service, sinon rien.</summary>
    public string? RemainingHint(Slot slot) => slot.RemainingCovers switch
    {
        <= 0 => null,
        1 => "Plus qu'une place",
        <= FewLeftThreshold => $"Plus que {slot.RemainingCovers} places",
        _ => null
    };

    /// <summary>Semaines (lundi d'abord) du mois contenant <paramref name="month"/>, complétées par les jours voisins.</summary>
    public IReadOnlyList<IReadOnlyList<CalendarDay>> MonthWeeks(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        var end = last.AddDays(6 - (((int)last.DayOfWeek + 6) % 7));

        var weeks = new List<IReadOnlyList<CalendarDay>>();
        for (var day = start; day <= end; day = day.AddDays(7))
        {
            weeks.Add(Enumerable.Range(0, 7).Select(i =>
            {
                var d = day.AddDays(i);
                return new CalendarDay(d, d.Month == first.Month, IsSelectable(d), IsClosed(d));
            }).ToList());
        }

        return weeks;
    }

    public bool CanShowPreviousMonth(DateOnly month) =>
        new DateOnly(month.Year, month.Month, 1) > new DateOnly(FirstDate.Year, FirstDate.Month, 1);

    public bool CanShowNextMonth(DateOnly month) => new DateOnly(month.Year, month.Month, 1).AddMonths(1) <= LastDate;

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>Passe à l'étape suivante si l'étape courante est complète ; sinon <see cref="Error"/> dit ce qui manque.</summary>
    public bool TryAdvance()
    {
        switch (Step)
        {
            case ReserveStep.When:
                if (Date is null)
                    return Fail("Choisissez une date");
                if (SelectedSlot is null)
                    return Fail("Choisissez un créneau");
                Error = null;
                Step = IsGroup ? ReserveStep.Guests : ReserveStep.Contact;
                return true;

            case ReserveStep.Guests:
                for (var i = 0; i < Guests.Count; i++)
                {
                    if (Guests[i].Dishes.Count == 0)
                        return Fail($"Choisissez au moins un plat pour le convive {i + 1}");
                }

                Error = null;
                Step = ReserveStep.Contact;
                return true;

            default:
                return false;
        }
    }

    public void Back()
    {
        Error = null;
        Step = Step switch
        {
            ReserveStep.Contact => IsGroup ? ReserveStep.Guests : ReserveStep.When,
            _ => ReserveStep.When
        };
    }

    private bool Fail(string message)
    {
        Error = message;
        return false;
    }

    // ── Étape 2 : convives et plats ──────────────────────────────────────────

    private readonly List<GuestDraft> _guests = [];

    /// <summary>Un convive par couvert dès que le groupe dépasse le seuil ; vide sinon.</summary>
    public IReadOnlyList<GuestDraft> Guests => _guests;

    private void ResizeGuests()
    {
        var wanted = IsGroup ? PartySize : 0;
        while (_guests.Count < wanted)
            _guests.Add(new GuestDraft());
        // Ce qui a été saisi est gardé tant que le groupe reste grand : repasser sous le seuil l'ignore sans le perdre.
        if (_guests.Count > wanted && IsGroup)
            _guests.RemoveRange(wanted, _guests.Count - wanted);
    }

    public string DishName(int recipeId) => _dishes.TryGetValue(recipeId, out var dish) ? dish.Name : "Plat indisponible";

    public decimal DishPrice(int recipeId) => _dishes.TryGetValue(recipeId, out var dish) ? dish.PriceInclVat : 0m;

    public void AddDish(int guestIndex, int recipeId)
    {
        if (!_dishes.ContainsKey(recipeId) || !ValidGuest(guestIndex))
            return;

        var guest = _guests[guestIndex];
        if (guest.Dishes.FirstOrDefault(d => d.RecipeId == recipeId) is { } line)
            line.Quantity = Math.Min(MaxDishQuantity, line.Quantity + 1);
        else if (guest.Dishes.Count < MaxDishLinesPerGuest)
            guest.Dishes.Add(new DishPick(recipeId, 1));
    }

    /// <summary>Fixe la quantité d'un plat (au plus 50) ; zéro ou moins retire la ligne.</summary>
    public void SetQuantity(int guestIndex, int recipeId, int quantity)
    {
        if (!ValidGuest(guestIndex))
            return;

        var guest = _guests[guestIndex];
        var line = guest.Dishes.FirstOrDefault(d => d.RecipeId == recipeId);
        if (line is null)
            return;

        if (quantity <= 0)
            guest.Dishes.Remove(line);
        else
            line.Quantity = Math.Min(quantity, MaxDishQuantity);
    }

    public void RemoveDish(int guestIndex, int recipeId) => SetQuantity(guestIndex, recipeId, 0);

    public bool CanCopyFromPrevious(int guestIndex) => guestIndex >= 1 && ValidGuest(guestIndex);

    /// <summary>Reprend les plats du convive précédent (copie indépendante) en remplaçant ceux déjà choisis.</summary>
    public void CopyFromPrevious(int guestIndex)
    {
        if (!CanCopyFromPrevious(guestIndex))
            return;

        var target = _guests[guestIndex];
        target.Dishes.Clear();
        target.Dishes.AddRange(_guests[guestIndex - 1].Dishes.Select(d => new DishPick(d.RecipeId, d.Quantity)));
    }

    public decimal GuestTotal(int guestIndex) =>
        ValidGuest(guestIndex) ? _guests[guestIndex].Dishes.Sum(d => d.Quantity * DishPrice(d.RecipeId)) : 0m;

    public decimal PreorderTotal => IsGroup ? _guests.Take(PartySize).Sum(g => g.Dishes.Sum(d => d.Quantity * DishPrice(d.RecipeId))) : 0m;

    private bool ValidGuest(int index) => index >= 0 && index < _guests.Count;

    // ── Étape 3 : montant, règle d'annulation, coordonnées ───────────────────

    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Accepted { get; set; }

    private AmountToPay Pricing => ReservationPricing.Compute(PartySize, PreorderTotal, Rules);

    public decimal AmountDue => Pricing.Amount;

    /// <summary>« Acompte : 10 € × 2 couverts » ou « Prépaiement : 30 % des plats pré-commandés » ; null quand rien n'est dû.</summary>
    public string? NatureLabel => Pricing.Kind switch
    {
        PaymentKind.Deposit => $"Acompte : {Fmt.Money(Rules.DepositPerPerson)} × {Fmt.Covers(PartySize)}",
        PaymentKind.Prepayment => $"Prépaiement : {Math.Round(Rules.PrepaymentPercent * 100, 0, MidpointRounding.AwayFromZero)}{Nbsp}% des plats pré-commandés",
        _ => null
    };

    public string SubmitLabel => AmountDue > 0m ? $"Réserver et payer {Fmt.Money(AmountDue)}" : "Réserver";

    /// <summary>La règle d'annulation en toutes lettres, avec la date et l'heure locales de la limite.</summary>
    public string? CancellationText
    {
        get
        {
            if (SelectedSlot is not { } slot)
                return null;

            var what = Pricing.Kind == PaymentKind.Prepayment ? "le prépaiement" : "l'acompte";
            var free = CancellationPolicy.IsFreeCancellation(slot.StartsAtUtc, _utcNow, Rules.FreeCancellationHours);
            if (!free)
            {
                return Pricing.Kind is null
                    ? "Cette réservation est trop proche pour une annulation gratuite."
                    : $"Cette réservation est trop proche pour une annulation gratuite : {what} ne sera pas remboursé en cas d'annulation.";
            }

            var (date, time) = RestaurantClock.ToLocal(slot.StartsAtUtc.AddHours(-Rules.FreeCancellationHours), _timeZone);
            var limit = Fmt.LocalDateTime(date, time);
            return Pricing.Kind is null
                ? $"Annulation gratuite jusqu'au {limit}."
                : $"Annulation gratuite jusqu'au {limit}, ensuite {what} n'est pas remboursé.";
        }
    }

    /// <summary>Le premier défaut des coordonnées, ou null si elles sont valides (mêmes règles que le service).</summary>
    public string? ContactError()
    {
        if (Name.Trim().Length == 0)
            return "Indiquez votre nom";
        if (!EmailPattern().IsMatch(Email.Trim()))
            return "L'adresse e-mail est invalide";
        if (Phone.Trim().Replace(" ", "").Length < 6)
            return "Le numéro de téléphone est invalide (6 caractères minimum)";
        return null;
    }

    public bool CanSubmit => Step == ReserveStep.Contact && SelectedSlot is not null && ContactError() is null && Accepted && !IsBusy;

    /// <summary>La demande à envoyer au service : convives et plats seulement pour un grand groupe.</summary>
    public ReservationRequest ToRequest()
    {
        var slot = SelectedSlot ?? throw new InvalidOperationException("Aucun créneau choisi");
        var guests = IsGroup
            ? _guests.Take(PartySize)
                .Select(g => new ReservationGuestInput(Blank(g.FirstName), Blank(g.LastName),
                    g.Dishes.Select(d => new ReservationDishInput(d.RecipeId, d.Quantity)).ToList()))
                .ToList()
            : new List<ReservationGuestInput>();

        return new ReservationRequest(slot.Date, slot.Time, slot.ServiceName, PartySize, Name.Trim(), Email.Trim(), Phone.Trim(), Blank(Note), guests);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── Erreurs du serveur ───────────────────────────────────────────────────

    /// <summary>L'étape à laquelle renvoyer le client pour un message du service ; null si le message ne dit pas où corriger.</summary>
    public static ReserveStep? StepFor(string message)
    {
        var text = message.ToLowerInvariant();
        if (text.Contains("créneau") || text.Contains("fermé") || text.Contains("couverts") || text.Contains("réservations en ligne"))
            return ReserveStep.When;
        if (text.Contains("plat") || text.Contains("convive"))
            return ReserveStep.Guests;
        if (text.Contains("e-mail") || text.Contains("nom") || text.Contains("téléphone") || text.Contains("trop longue") || text.Contains("paiement"))
            return ReserveStep.Contact;
        return null;
    }

    /// <summary>
    /// Affiche le message du service et renvoie le client à l'étape concernée. Retourne true quand les créneaux sont à recharger
    /// (le créneau choisi n'est plus bon : il est désélectionné).
    /// </summary>
    public bool HandleServerError(string message)
    {
        Error = message;
        IsBusy = false;
        Accepted = false;

        var target = StepFor(message);
        if (target == ReserveStep.Guests && !IsGroup)
            target = ReserveStep.Contact;
        if (target is null)
            return false;

        Step = target.Value;
        if (target != ReserveStep.When)
            return false;

        SelectedSlot = null;
        return true;
    }
}
