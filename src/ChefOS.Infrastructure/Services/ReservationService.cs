using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ChefOS.Core.Common;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChefOS.Infrastructure.Services;

public record ReservationDishInput(int RecipeId, int Quantity);

public record ReservationGuestInput(string? FirstName, string? LastName, IReadOnlyList<ReservationDishInput> Dishes);

/// <summary>
/// Demande de réservation : <paramref name="ServiceName"/> et l'heure viennent d'un <see cref="Slot"/> proposé par <see cref="ReservationService.GetSlotsAsync"/>.
/// <paramref name="ExpectedAmount"/> : le montant que le client a vu ; s'il diffère de celui du serveur, la réservation est refusée (réservation en ligne seulement).
/// </summary>
public record ReservationRequest(
    DateOnly Date, TimeOnly Time, string ServiceName, int PartySize,
    string CustomerName, string CustomerEmail, string CustomerPhone, string? Note,
    IReadOnlyList<ReservationGuestInput> Guests, decimal? ExpectedAmount = null);

public record ReservationStart(string Token, string? CheckoutUrl, decimal AmountDue, ReservationStatus Status);

public record ManualReservationResult(int ReservationId, string? Warning);

/// <summary>
/// Une réservation manuelle qui dépasse la capacité (ou tombe un jour de fermeture) demande une confirmation : <see cref="Prompt"/> est la question à poser au chef,
/// et la même demande avec <c>force</c> l'enregistre. Reste une <see cref="DomainValidationException"/> pour qui n'a pas de boîte de confirmation.
/// </summary>
public sealed class ConfirmationRequiredException(string prompt, string message) : DomainValidationException(message)
{
    public string Prompt { get; } = prompt;
}

public record PublicReservation(
    string Token, ReservationStatus Status, DateTime StartsAtUtc, DateOnly LocalDate, TimeOnly LocalTime, string ServiceName,
    int PartySize, string CustomerName, string? Note, decimal AmountDue, decimal PreorderTotalInclVat,
    string? CheckoutUrl, PaymentStatus? PaymentStatus, DateTime FreeCancellationUntilUtc, bool CanCancel);

public record GuestDishDetail(int RecipeId, string DishName, int Quantity, decimal UnitPriceInclVat);

public record GuestDetail(int Position, string? FirstName, string? LastName, IReadOnlyList<GuestDishDetail> Dishes);

public record PaymentDetail(int Id, PaymentKind Kind, decimal Amount, PaymentStatus Status, decimal? RefundAmount);

public record ReservationDetail(
    int Id, DateTime StartsAtUtc, TimeOnly LocalTime, int PartySize, string CustomerName, string CustomerEmail, string CustomerPhone,
    string? Note, ReservationStatus Status, decimal AmountDue, decimal PreorderTotalInclVat, bool IsManual, int? OrderId,
    IReadOnlyList<GuestDetail> Guests, IReadOnlyList<PaymentDetail> Payments);

/// <summary>Un service du jour : capacité, couverts occupés (confirmés, installés, paiements en attente encore valides) et ses réservations.</summary>
public record ServiceDayView(string ServiceName, int Capacity, int BookedCovers, IReadOnlyList<ReservationDetail> Reservations);

/// <summary>
/// Réservations en ligne et du back : disponibilités par service, réservation avec acompte (petits groupes) ou prépaiement de la pré-commande
/// (grands groupes), annulation, installation en salle (qui crée la commande de cuisine), no-show, réservation manuelle.
/// Aucune surréservation : toute écriture qui prend des places prend d'abord le verrou du service (voir <see cref="ReservationCapacity"/>).
/// Le restaurant courant est celui du contexte filtré (la vitrine est scopée par son lien).
/// </summary>
public partial class ReservationService(
    IDbContextFactory<ChefOsDbContext> factory, TimeProvider clock, PaymentService payments,
    OrderService? orders = null, ILogger<ReservationService>? logger = null, EmailDispatchSignal? signal = null)
{
    private const string ClosedMessage = "Les réservations en ligne sont fermées";
    private const string ClosedDayMessage = "Le restaurant est fermé ce jour-là";
    private const string InvalidSlotMessage = "Ce créneau n'existe pas";
    private const string PastSlotMessage = "Ce créneau est déjà passé";
    private const string FullMessage = "Ce créneau vient d'être complété, choisissez-en un autre";
    private const string PartyMessage = "Le nombre de couverts est invalide";
    private const string NameMessage = "Le nom est obligatoire";
    private const string EmailMessage = "L'adresse e-mail est invalide";
    private const string PhoneMessage = "Le numéro de téléphone est invalide (6 caractères minimum)";
    private const string TooLongMessage = "Une des informations saisies est trop longue";
    private const string DishUnavailableMessage = "Un des plats choisis n'est pas disponible";
    private const string UnknownReservationMessage = "Réservation introuvable";
    private const string StatusChangedMessage = "La réservation a déjà changé de statut";
    private const string CannotCancelMessage = "Cette réservation ne peut plus être annulée";
    private const int OrderNumberRetries = 5;
    private const int MaxSyncedPaymentsPerListing = 10;
    private const int MaxDishQuantity = 50;
    private const int MaxDishLinesPerGuest = 10;

    private readonly OrderService _orders = orders ?? new OrderService(factory);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+\z")]
    private static partial Regex EmailPattern();

    // ── Disponibilités ───────────────────────────────────────────────────────

    /// <summary>Créneaux réservables du jour pour <paramref name="partySize"/> personnes ; vide si la vitrine n'est pas publiée ou si les réservations sont fermées.</summary>
    public async Task<IReadOnlyList<Slot>> GetSlotsAsync(DateOnly date, int partySize)
    {
        await TryExpireStaleAsync();

        await using var db = await factory.CreateDbContextAsync();
        var profile = await db.VitrineProfiles.AsNoTracking().FirstOrDefaultAsync();
        if (profile is null || !profile.IsPublished || !profile.ReservationsOpen)
            return [];

        var timeZone = await db.Restaurants.Where(r => r.Id == profile.RestaurantId).Select(r => r.TimeZoneId).FirstAsync();
        var schedules = await db.ServiceSchedules.AsNoTracking().Where(s => s.DayOfWeek == date.DayOfWeek).ToListAsync();
        var closures = await db.ClosureDays.AsNoTracking().Where(c => c.DateLocal == date).ToListAsync();

        var from = RestaurantClock.ToUtc(date, TimeOnly.MinValue, timeZone);
        var to = RestaurantClock.ToUtc(date.AddDays(1), TimeOnly.MinValue, timeZone);
        var reservations = (await db.Reservations.AsNoTracking()
                .Where(r => r.StartsAtUtc >= from && r.StartsAtUtc < to)
                .Select(r => new { r.StartsAtUtc, r.ServiceName, r.PartySize, r.Status, r.CreatedAt })
                .ToListAsync())
            .Select(r => (r.StartsAtUtc, r.ServiceName, r.PartySize, r.Status, r.CreatedAt))
            .ToList();

        return AvailabilityCalculator.SlotsFor(date, schedules, closures, reservations, profile, timeZone, Now, partySize);
    }

    /// <summary>Passe en <c>Expired</c> les réservations <c>PendingPayment</c> plus vieilles que <c>PendingPaymentMinutes</c> (mise à jour conditionnelle).</summary>
    public async Task ExpireStaleAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var minutes = await db.VitrineProfiles.Select(p => (int?)p.PendingPaymentMinutes).FirstOrDefaultAsync();
        if (minutes is null)
            return;

        var cutoff = Now.AddMinutes(-minutes.Value);
        await db.Reservations
            .Where(r => r.Status == ReservationStatus.PendingPayment && r.CreatedAt <= cutoff)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, ReservationStatus.Expired));
    }

    /// <summary>Expiration paresseuse « au mieux » : un échec ne doit pas empêcher d'afficher les créneaux ou la liste.</summary>
    private async Task TryExpireStaleAsync()
    {
        try
        {
            await ExpireStaleAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Expiration des réservations en attente impossible : {Reason}", ex.Message);
        }
    }

    // ── Réservation en ligne ─────────────────────────────────────────────────

    /// <summary>
    /// Enregistre une réservation. Les contrôles faits, une transaction prend le verrou du service, relit le restant et insère : jamais plus de couverts que la capacité.
    /// Montant 0 : <c>Confirmed</c>. Sinon <c>PendingPayment</c>, validée avant d'appeler la passerelle (hors transaction) ; si la passerelle échoue, la
    /// réservation passe en <c>Expired</c> (voir <see cref="PaymentService.StartAsync"/>). <paramref name="returnUrlBase"/> : le client revient sur « {returnUrlBase}/{jeton} ».
    /// </summary>
    public async Task<ReservationStart> CreateAsync(ReservationRequest request, string returnUrlBase, string webhookUrl)
    {
        await TryExpireStaleAsync();

        var now = Now;
        await using var db = await factory.CreateDbContextAsync();

        var profile = await db.VitrineProfiles.AsNoTracking().FirstOrDefaultAsync();
        if (profile is null || !profile.IsPublished || !profile.ReservationsOpen)
            throw new DomainValidationException(ClosedMessage, ValidationFields.Slot);

        if (request.PartySize < 1 || request.PartySize > profile.MaxPartySize)
            throw new DomainValidationException(request.PartySize < 1 ? PartyMessage : $"Le nombre de couverts est limité à {profile.MaxPartySize}", ValidationFields.Slot);

        var name = request.CustomerName?.Trim() ?? "";
        var mail = request.CustomerEmail?.Trim() ?? "";
        var phone = request.CustomerPhone?.Trim() ?? "";
        if (name.Length == 0)
            throw new DomainValidationException(NameMessage, ValidationFields.Contact);
        if (!EmailPattern().IsMatch(mail))
            throw new DomainValidationException(EmailMessage, ValidationFields.Contact);
        if (phone.Replace(" ", "").Length < 6)
            throw new DomainValidationException(PhoneMessage, ValidationFields.Contact);
        RequireLengths(name, mail, phone, request.Note);

        var restaurant = await db.Restaurants.AsNoTracking().FirstAsync(r => r.Id == profile.RestaurantId);
        var startsAtUtc = await ValidateSlotAsync(db, request.Date, request.Time, request.ServiceName, restaurant.TimeZoneId, now, profile, enforceLead: true);

        // Pré-commande : seulement au-delà du seuil de groupe ; en dessous, les convives saisis sont ignorés.
        var isGroup = request.PartySize > profile.GroupThreshold;
        var guests = new List<ReservationGuest>();
        var preorderTotal = 0m;
        if (isGroup)
            (guests, preorderTotal) = await BuildGuestsAsync(db, request);

        var due = ReservationPricing.Compute(request.PartySize, preorderTotal, profile);
        if (request.ExpectedAmount is { } expected && expected != due.Amount)
            throw new DomainValidationException($"Le montant a changé ({Money(expected)} → {Money(due.Amount)}) : vérifiez votre récapitulatif", ValidationFields.Amount);

        var token = NewToken();
        var reservation = new Reservation
        {
            PublicToken = token,
            StartsAtUtc = startsAtUtc,
            ServiceName = request.ServiceName,
            PartySize = request.PartySize,
            CustomerName = name,
            CustomerEmail = mail,
            CustomerPhone = phone,
            Note = NullIfBlank(request.Note),
            Status = due.Amount == 0m ? ReservationStatus.Confirmed : ReservationStatus.PendingPayment,
            CreatedAt = now,
            ConfirmedAt = due.Amount == 0m ? now : null,
            AmountDue = due.Amount,
            PreorderTotalInclVat = preorderTotal,
            FreeCancellationUntilUtc = startsAtUtc.AddHours(-profile.FreeCancellationHours),
            Guests = guests
        };

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await ReservationCapacity.LockAsync(db, profile.RestaurantId, request.ServiceName, request.Date, CancellationToken.None);
            var remaining = await ReservationCapacity.RemainingAsync(db, request.ServiceName, request.Date, restaurant.TimeZoneId, now,
                profile.PendingPaymentMinutes, 0, CancellationToken.None);
            if (remaining < request.PartySize)
                throw new DomainValidationException(FullMessage, ValidationFields.Slot);

            db.Reservations.Add(reservation);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        if (due.Kind is null)
        {
            await TrySendAsync(EmailKind.ReservationConfirmed, restaurant.Id, mail, $"Votre réservation chez {restaurant.Name} est confirmée",
                $"Bonjour {name}, votre table pour {request.PartySize} personne(s) le {request.Date:dd/MM/yyyy} à {request.Time:HH\\:mm} est confirmée.");
            return new ReservationStart(token, null, 0m, ReservationStatus.Confirmed);
        }

        // La passerelle est appelée après le commit, hors transaction : un appel réseau ne doit pas tenir le verrou du service.
        var started = await payments.StartAsync(reservation.Id, due.Amount, due.Kind.Value,
            new PaymentUrls($"{returnUrlBase.TrimEnd('/')}/{token}", webhookUrl));
        return new ReservationStart(token, started.CheckoutUrl, due.Amount, ReservationStatus.PendingPayment);
    }

    private async Task<DateTime> ValidateSlotAsync(ChefOsDbContext db, DateOnly date, TimeOnly time, string serviceName, string timeZone,
        DateTime now, VitrineProfile profile, bool enforceLead, bool checkClosure = true)
    {
        if (checkClosure && await db.ClosureDays.AnyAsync(c => c.DateLocal == date))
            throw new DomainValidationException(ClosedDayMessage, ValidationFields.Slot);

        var schedule = await db.ServiceSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.DayOfWeek == date.DayOfWeek && s.Name == serviceName);
        if (schedule is null || time < schedule.StartLocal || time >= schedule.EndLocal
            || (time.Hour * 60 + time.Minute - (schedule.StartLocal.Hour * 60 + schedule.StartLocal.Minute)) % Math.Max(schedule.SlotMinutes, 1) != 0)
            throw new DomainValidationException(InvalidSlotMessage, ValidationFields.Slot);

        var startsAtUtc = RestaurantClock.ToUtc(date, time, timeZone);
        if (enforceLead)
        {
            if (startsAtUtc <= now)
                throw new DomainValidationException(PastSlotMessage, ValidationFields.Slot);
            if (startsAtUtc < now.AddMinutes(profile.MinLeadMinutes))
                throw new DomainValidationException($"Ce créneau est trop proche : réservez au moins {profile.MinLeadMinutes} minutes à l'avance", ValidationFields.Slot);
            if (date > RestaurantClock.Today(now, timeZone).AddDays(profile.MaxDaysAhead))
                throw new DomainValidationException($"Ce créneau est trop éloigné : {profile.MaxDaysAhead} jours à l'avance au maximum", ValidationFields.Slot);
        }

        return startsAtUtc;
    }

    /// <summary>Un convive par couvert, chacun avec au moins un plat publié et vendu ; les prix et la TVA sont figés ici.</summary>
    private static async Task<(List<ReservationGuest> Guests, decimal Total)> BuildGuestsAsync(ChefOsDbContext db, ReservationRequest request)
    {
        var inputs = request.Guests ?? [];
        if (inputs.Count != request.PartySize)
            throw new DomainValidationException($"Indiquez les {request.PartySize} convives et leurs plats", ValidationFields.Guests);
        if (inputs.Any(g => g.Dishes is null || g.Dishes.Count == 0))
            throw new DomainValidationException("Chaque convive doit choisir au moins un plat", ValidationFields.Guests);
        if (inputs.Any(g => g.Dishes.Count > MaxDishLinesPerGuest))
            throw new DomainValidationException($"Un convive ne peut pas choisir plus de {MaxDishLinesPerGuest} plats", ValidationFields.Guests);
        if (inputs.SelectMany(g => g.Dishes).Any(d => d.Quantity < 1 || d.Quantity > MaxDishQuantity))
            throw new DomainValidationException("La quantité d'un plat est invalide", ValidationFields.Guests);
        if (inputs.Any(g => (g.FirstName?.Trim().Length ?? 0) > 100 || (g.LastName?.Trim().Length ?? 0) > 100))
            throw new DomainValidationException(TooLongMessage, ValidationFields.Guests);

        var ids = inputs.SelectMany(g => g.Dishes).Select(d => d.RecipeId).Distinct().ToList();
        var recipes = await db.Recipes.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.Kind == RecipeKind.Dish && !r.IsArchived && r.ShowOnVitrine
                && r.SellingPriceInclVat != null && r.VatRate != null)
            .ToDictionaryAsync(r => r.Id);
        if (ids.Any(id => !recipes.ContainsKey(id)))
            throw new DomainValidationException(DishUnavailableMessage, ValidationFields.Guests);

        var total = 0m;
        var guests = new List<ReservationGuest>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var guest = new ReservationGuest { Position = i, FirstName = NullIfBlank(inputs[i].FirstName), LastName = NullIfBlank(inputs[i].LastName) };
            foreach (var dish in inputs[i].Dishes)
            {
                var recipe = recipes[dish.RecipeId];
                guest.Dishes.Add(new ReservationGuestDish
                {
                    RecipeId = recipe.Id, Quantity = dish.Quantity,
                    UnitPriceInclVat = recipe.SellingPriceInclVat!.Value, VatRate = recipe.VatRate!.Value
                });
                total += dish.Quantity * recipe.SellingPriceInclVat.Value;
            }

            guests.Add(guest);
        }

        return (guests, total);
    }

    /// <summary>
    /// La réservation du lien public, ou null si le jeton est inconnu (ou d'un autre restaurant). Si un paiement est encore ouvert, son statut est relu chez la passerelle
    /// (retour du client avant le webhook, ou webhook perdu) ; une passerelle injoignable n'empêche pas d'afficher la réservation.
    /// </summary>
    public async Task<PublicReservation?> GetByTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64)
            return null;

        await using var db = await factory.CreateDbContextAsync();
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.PublicToken == token);
        if (reservation is null)
            return null;

        if (reservation.Status is ReservationStatus.PendingPayment or ReservationStatus.Expired)
        {
            var open = await db.Payments.AsNoTracking()
                .Where(p => p.ReservationId == reservation.Id && p.Status == PaymentStatus.Open && p.GatewayPaymentId != null)
                .Select(p => p.GatewayPaymentId!).ToListAsync();
            foreach (var gatewayId in open)
            {
                try
                {
                    await payments.SyncAsync(gatewayId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning("Lecture du statut du paiement impossible : {Reason}", ex.Message);
                }
            }

            if (open.Count > 0)
                reservation = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == reservation.Id);
        }

        var timeZone = await db.Restaurants.Where(r => r.Id == reservation.RestaurantId).Select(r => r.TimeZoneId).FirstAsync();
        var (date, time) = RestaurantClock.ToLocal(reservation.StartsAtUtc, timeZone);
        var latest = await db.Payments.AsNoTracking().Where(p => p.ReservationId == reservation.Id).OrderByDescending(p => p.Id).FirstOrDefaultAsync();

        return new PublicReservation(
            reservation.PublicToken, reservation.Status, Utc(reservation.StartsAtUtc), date, time, reservation.ServiceName,
            reservation.PartySize, reservation.CustomerName, reservation.Note, reservation.AmountDue, reservation.PreorderTotalInclVat,
            reservation.Status == ReservationStatus.PendingPayment && latest?.Status == PaymentStatus.Open ? latest.CheckoutUrl : null,
            latest?.Status, Utc(reservation.FreeCancellationUntilUtc),
            CanCustomerCancel(reservation.Status, reservation.StartsAtUtc, Now));
    }

    private static bool CanCustomerCancel(ReservationStatus status, DateTime startsAtUtc, DateTime now) =>
        status is ReservationStatus.PendingPayment or ReservationStatus.Confirmed && startsAtUtc > now;

    /// <summary>
    /// Annulation par le client. Dans le délai gratuit figé à la réservation (<see cref="Reservation.FreeCancellationUntilUtc"/>) : <c>Cancelled</c> et remboursement intégral automatique d'un paiement réglé (si la passerelle échoue, le paiement
    /// reste <c>RefundPending</c> et le back peut relancer). Hors délai : <c>Cancelled</c>, sans remboursement. Refusée si la réservation est installée, passée ou terminée.
    /// Annuler une réservation déjà annulée ne change rien.
    /// </summary>
    public async Task CancelByCustomerAsync(string token)
    {
        await using var db = await factory.CreateDbContextAsync();
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.PublicToken == token)
                          ?? throw new DomainValidationException(UnknownReservationMessage);
        if (reservation.Status == ReservationStatus.Cancelled)
            return;

        var now = Now;
        if (!CanCustomerCancel(reservation.Status, reservation.StartsAtUtc, now))
            throw new DomainValidationException(CannotCancelMessage);

        var free = now <= reservation.FreeCancellationUntilUtc; // la règle figée à la réservation, pas celle d'aujourd'hui

        await CancelAsync(db, reservation.Id, refund: free, now);
    }

    /// <summary>
    /// Passe la réservation en <c>Cancelled</c> et, avec <paramref name="refund"/>, les paiements réglés en <c>RefundPending</c> dans la même transaction ;
    /// la passerelle n'est appelée qu'après le commit. Un échec laisse le paiement <c>RefundPending</c>, que le back relance.
    /// </summary>
    private async Task CancelAsync(ChefOsDbContext db, int reservationId, bool refund, DateTime now)
    {
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var claimed = await db.Reservations
                .Where(r => r.Id == reservationId && (r.Status == ReservationStatus.PendingPayment || r.Status == ReservationStatus.Confirmed))
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, ReservationStatus.Cancelled).SetProperty(r => r.CancelledAt, now));
            if (claimed != 1)
                throw new DomainValidationException(CannotCancelMessage);

            if (refund)
            {
                await db.Payments
                    .Where(p => p.ReservationId == reservationId && p.Status == PaymentStatus.Paid)
                    .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, PaymentStatus.RefundPending).SetProperty(p => p.RefundAmount, p => p.Amount));
            }

            await transaction.CommitAsync();
        }

        if (!refund)
            return;

        var pending = await db.Payments.AsNoTracking()
            .Where(p => p.ReservationId == reservationId && p.Status == PaymentStatus.RefundPending)
            .Select(p => p.Id).ToListAsync();
        foreach (var paymentId in pending)
        {
            try
            {
                await payments.RefundAsync(paymentId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning("Remboursement du paiement {PaymentId} impossible, à relancer : {Reason}", paymentId, ex.Message);
            }
        }
    }

    // ── Back ─────────────────────────────────────────────────────────────────

    /// <summary>Les services du jour local avec leurs réservations (convives, plats, paiements), annulées et expirées comprises.</summary>
    public async Task<IReadOnlyList<ServiceDayView>> ListForDayAsync(DateOnly localDate)
    {
        await TryExpireStaleAsync();

        var now = Now;
        await using var db = await factory.CreateDbContextAsync();
        var profile = await db.VitrineProfiles.AsNoTracking().FirstOrDefaultAsync();
        if (profile is null)
            return [];

        var timeZone = await db.Restaurants.Where(r => r.Id == profile.RestaurantId).Select(r => r.TimeZoneId).FirstAsync();
        var from = RestaurantClock.ToUtc(localDate, TimeOnly.MinValue, timeZone);
        var to = RestaurantClock.ToUtc(localDate.AddDays(1), TimeOnly.MinValue, timeZone);

        await TrySyncOpenPaymentsAsync(db, from, to);

        var schedules = await db.ServiceSchedules.AsNoTracking().Where(s => s.DayOfWeek == localDate.DayOfWeek).ToListAsync();
        var reservations = await db.Reservations.AsNoTracking()
            .Include(r => r.Guests.OrderBy(g => g.Position)).ThenInclude(g => g.Dishes)
            .Where(r => r.StartsAtUtc >= from && r.StartsAtUtc < to)
            .OrderBy(r => r.StartsAtUtc).ThenBy(r => r.Id)
            .AsSplitQuery()
            .ToListAsync();

        var ids = reservations.Select(r => r.Id).ToList();
        var paymentRows = await db.Payments.AsNoTracking().Where(p => ids.Contains(p.ReservationId)).OrderBy(p => p.Id).ToListAsync();
        var recipeIds = reservations.SelectMany(r => r.Guests).SelectMany(g => g.Dishes).Select(d => d.RecipeId).Distinct().ToList();
        var names = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name);
        var pendingLimit = TimeSpan.FromMinutes(profile.PendingPaymentMinutes);

        var serviceNames = schedules.Select(s => s.Name).Concat(reservations.Select(r => r.ServiceName)).Distinct().ToList();
        return serviceNames.Select(service =>
        {
            var mine = reservations.Where(r => r.ServiceName == service).ToList();
            var booked = mine.Where(r => r.Status is ReservationStatus.Confirmed or ReservationStatus.Seated
                    || (r.Status == ReservationStatus.PendingPayment && now - r.CreatedAt < pendingLimit))
                .Sum(r => r.PartySize);
            var details = mine.Select(r => new ReservationDetail(
                r.Id, Utc(r.StartsAtUtc), RestaurantClock.ToLocal(r.StartsAtUtc, timeZone).Time, r.PartySize, r.CustomerName, r.CustomerEmail,
                r.CustomerPhone, r.Note, r.Status, r.AmountDue, r.PreorderTotalInclVat, r.IsManual, r.OrderId,
                r.Guests.Select(g => new GuestDetail(g.Position, g.FirstName, g.LastName,
                    g.Dishes.Select(d => new GuestDishDetail(d.RecipeId, names.GetValueOrDefault(d.RecipeId, "?"), d.Quantity, d.UnitPriceInclVat)).ToList())).ToList(),
                paymentRows.Where(p => p.ReservationId == r.Id).Select(p => new PaymentDetail(p.Id, p.Kind, p.Amount, p.Status, p.RefundAmount)).ToList())).ToList();
            return new ServiceDayView(service, schedules.FirstOrDefault(s => s.Name == service)?.Capacity ?? 0, booked, details);
        }).ToList();
    }

    /// <summary>
    /// Relit chez la passerelle, « au mieux », les paiements encore ouverts des réservations du jour qui attendent leur paiement ou ont expiré : le back reprend ainsi
    /// un paiement dont le webhook ne lui est jamais parvenu. Borné à <see cref="MaxSyncedPaymentsPerListing"/> par appel ; un échec est journalisé et n'empêche pas la liste.
    /// </summary>
    private async Task TrySyncOpenPaymentsAsync(ChefOsDbContext db, DateTime from, DateTime to)
    {
        try
        {
            var open = await db.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Open && p.GatewayPaymentId != null
                    && db.Reservations.Any(r => r.Id == p.ReservationId && r.StartsAtUtc >= from && r.StartsAtUtc < to
                        && (r.Status == ReservationStatus.PendingPayment || r.Status == ReservationStatus.Expired)))
                .OrderBy(p => p.Id)
                .Select(p => p.GatewayPaymentId!)
                .Take(MaxSyncedPaymentsPerListing)
                .ToListAsync();
            foreach (var gatewayId in open)
            {
                try
                {
                    await payments.SyncAsync(gatewayId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning("Lecture du statut du paiement {GatewayPaymentId} impossible : {Reason}", gatewayId, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Lecture des paiements ouverts du jour impossible : {Reason}", ex.Message);
        }
    }

    /// <summary>
    /// Réservation prise par le restaurant (téléphone, comptoir) : <c>Confirmed</c> tout de suite, sans paiement, <c>IsManual</c>. La capacité est vérifiée sous le même verrou ;
    /// la dépasser (ou réserver un jour de fermeture) exige <paramref name="force"/>, et le résultat porte alors un avertissement. Le délai minimal et le passé ne sont pas imposés.
    /// </summary>
    public async Task<ManualReservationResult> CreateManualAsync(ReservationRequest request, bool force)
    {
        var now = Now;
        await using var db = await factory.CreateDbContextAsync();
        var profile = await db.VitrineProfiles.AsNoTracking().FirstOrDefaultAsync()
                      ?? throw new DomainValidationException(InvalidSlotMessage);
        var restaurant = await db.Restaurants.AsNoTracking().FirstAsync(r => r.Id == profile.RestaurantId);

        if (request.PartySize < 1 || request.PartySize > 500)
            throw new DomainValidationException(PartyMessage);
        var name = request.CustomerName?.Trim() ?? "";
        var mail = request.CustomerEmail?.Trim() ?? "";
        var phone = request.CustomerPhone?.Trim() ?? "";
        if (name.Length == 0)
            throw new DomainValidationException(NameMessage);
        if (mail.Length > 0 && !EmailPattern().IsMatch(mail))
            throw new DomainValidationException(EmailMessage);
        RequireLengths(name, mail, phone, request.Note);

        var warnings = new List<string>();
        var closed = await db.ClosureDays.AnyAsync(c => c.DateLocal == request.Date);
        if (closed)
        {
            if (!force)
                throw new ConfirmationRequiredException(ClosedDayMessage, $"{ClosedDayMessage} (confirmez pour réserver quand même)");
            warnings.Add(ClosedDayMessage);
        }

        var startsAtUtc = await ValidateSlotAsync(db, request.Date, request.Time, request.ServiceName, restaurant.TimeZoneId, now, profile, enforceLead: false, checkClosure: false);

        var reservation = new Reservation
        {
            PublicToken = NewToken(), StartsAtUtc = startsAtUtc, ServiceName = request.ServiceName, PartySize = request.PartySize,
            CustomerName = name, CustomerEmail = mail, CustomerPhone = phone, Note = NullIfBlank(request.Note),
            Status = ReservationStatus.Confirmed, CreatedAt = now, ConfirmedAt = now, AmountDue = 0m, IsManual = true,
            FreeCancellationUntilUtc = startsAtUtc.AddHours(-profile.FreeCancellationHours)
        };

        await using var transaction = await db.Database.BeginTransactionAsync();
        await ReservationCapacity.LockAsync(db, profile.RestaurantId, request.ServiceName, request.Date, CancellationToken.None);
        var remaining = await ReservationCapacity.RemainingAsync(db, request.ServiceName, request.Date, restaurant.TimeZoneId, now,
            profile.PendingPaymentMinutes, 0, CancellationToken.None);
        if (remaining < request.PartySize)
        {
            var message = $"Capacité dépassée : {Math.Max(remaining, 0)} couvert(s) restant(s) pour {request.PartySize} demandé(s)";
            if (!force)
                throw new ConfirmationRequiredException(message, $"{message} (confirmez pour forcer)");
            warnings.Add(message);
        }

        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new ManualReservationResult(reservation.Id, warnings.Count == 0 ? null : string.Join(" · ", warnings));
    }

    /// <summary>
    /// Installe les convives : <c>Confirmed → Seated</c> (uniquement si <paramref name="expectedStatus"/> est <c>Confirmed</c> et que la réservation l'est encore).
    /// Une pré-commande crée la commande de cuisine (source <c>Vitrine</c>) à partir des lignes figées à la réservation, même si un plat a été archivé depuis ;
    /// chaque ligne porte « Prénom Nom » du convive. Commande et changement de statut forment une seule transaction : si la réservation a changé entre-temps,
    /// la commande n'est pas créée.
    /// </summary>
    public async Task SeatAsync(int reservationId, ReservationStatus expectedStatus)
    {
        if (expectedStatus != ReservationStatus.Confirmed)
            throw new DomainValidationException(StatusChangedMessage);

        for (var attempt = 0; attempt <= OrderNumberRetries; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();

            var reservation = await db.Reservations.AsNoTracking()
                .Include(r => r.Guests.OrderBy(g => g.Position)).ThenInclude(g => g.Dishes.OrderBy(d => d.Id))
                .AsSplitQuery()
                .FirstOrDefaultAsync(r => r.Id == reservationId)
                              ?? throw new DomainValidationException(UnknownReservationMessage);
            if (reservation.Status != ReservationStatus.Confirmed)
                throw new DomainValidationException(StatusChangedMessage);

            var lines = new List<FrozenOrderLine>();
            foreach (var guest in reservation.Guests)
            {
                var label = $"{guest.FirstName} {guest.LastName}".Trim();
                if (label.Length == 0)
                    label = $"Convive {guest.Position + 1}";
                lines.AddRange(guest.Dishes.Select(d => new FrozenOrderLine(d.RecipeId, d.Quantity, d.UnitPriceInclVat, d.VatRate, label)));
            }

            int? orderId = null;
            if (lines.Count > 0)
            {
                var note = $"Réservation {reservation.CustomerName} · {reservation.PartySize} couverts"
                           + (string.IsNullOrWhiteSpace(reservation.Note) ? "" : $" · {reservation.Note.Trim()}");
                var paid = await db.Payments.AsNoTracking()
                    .Where(p => p.ReservationId == reservationId && p.Status == PaymentStatus.Paid)
                    .Select(p => new { p.Kind, p.Amount }).ToListAsync();
                if (paid.Count > 0)
                {
                    var kinds = paid.Select(p => p.Kind == PaymentKind.Deposit ? "acompte" : "prépaiement").Distinct();
                    note += $" · Déjà réglé en ligne : {paid.Sum(p => p.Amount).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("fr-BE"))} € ({string.Join(" + ", kinds)})";
                }
                try
                {
                    orderId = await _orders.CreateFromFrozenLinesAsync(OrderSource.Vitrine, reservation.CustomerName, note, lines, db, Now);
                }
                catch (DbUpdateException ex) when (OrderService.IsOrderNumberConflict(ex))
                {
                    continue; // transaction annulée en quittant la portée ; on reprend avec un numéro recalculé
                }
            }

            var claimed = await db.Reservations
                .Where(r => r.Id == reservationId && r.Status == ReservationStatus.Confirmed)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, ReservationStatus.Seated).SetProperty(r => r.OrderId, orderId));
            if (claimed != 1)
                throw new DomainValidationException(StatusChangedMessage);

            await transaction.CommitAsync();
            return;
        }

        throw new DomainValidationException("Impossible d'attribuer un numéro de commande, réessayez");
    }

    /// <summary><c>Confirmed → NoShow</c>, si la réservation est encore à <paramref name="expectedStatus"/> (mise à jour conditionnelle).</summary>
    public async Task MarkNoShowAsync(int reservationId, ReservationStatus expectedStatus)
    {
        if (expectedStatus != ReservationStatus.Confirmed)
            throw new DomainValidationException(StatusChangedMessage);

        await using var db = await factory.CreateDbContextAsync();
        var claimed = await db.Reservations
            .Where(r => r.Id == reservationId && r.Status == ReservationStatus.Confirmed)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, ReservationStatus.NoShow));
        if (claimed != 1)
            throw new DomainValidationException(await db.Reservations.AnyAsync(r => r.Id == reservationId) ? StatusChangedMessage : UnknownReservationMessage);
    }

    /// <summary>
    /// Annulation par le restaurant d'une réservation en attente ou confirmée. Avec <paramref name="refund"/>, les paiements réglés sont remboursés intégralement
    /// (un échec de la passerelle laisse le paiement <c>RefundPending</c>, à relancer avec <see cref="PaymentService.RefundAsync"/>).
    /// </summary>
    public async Task CancelByRestaurantAsync(int reservationId, bool refund)
    {
        await using var db = await factory.CreateDbContextAsync();
        if (!await db.Reservations.AnyAsync(r => r.Id == reservationId))
            throw new DomainValidationException(UnknownReservationMessage);

        await CancelAsync(db, reservationId, refund, Now);
    }

    // ── Aides ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Provisoire : met en file un e-mail en texte simple, après le commit de l'événement (les vrais gabarits, écrits dans la transaction, viendront
    /// ensuite), puis réveille le dispatcher. Un échec est journalisé sans faire échouer la réservation.
    /// </summary>
    private async Task TrySendAsync(EmailKind kind, int restaurantId, string to, string subject, string body)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            EmailOutbox.Enqueue(db, new Notifications.EmailMessage(kind, to, subject, body, string.Empty, RestaurantId: restaurantId), Now);
            await db.SaveChangesAsync();
            signal?.Notify();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Mise en file de l'e-mail impossible : {Reason}", ex.Message);
        }
    }

    private static string Money(decimal amount) => amount.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("fr-BE")) + "\u00A0€";

    /// <summary>Plafonds des saisies publiques.</summary>
    private static void RequireLengths(string name, string mail, string phone, string? note)
    {
        if (name.Length > 120)
            throw new DomainValidationException("Le nom ne peut pas dépasser 120 caractères", ValidationFields.Contact);
        if (mail.Length > 200)
            throw new DomainValidationException("L'adresse e-mail ne peut pas dépasser 200 caractères", ValidationFields.Contact);
        if (phone.Length > 40)
            throw new DomainValidationException("Le numéro de téléphone ne peut pas dépasser 40 caractères", ValidationFields.Contact);
        if ((note?.Trim().Length ?? 0) > 1000)
            throw new DomainValidationException("La note ne peut pas dépasser 1000 caractères", ValidationFields.Contact);
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
