using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Payments;
using ChefOS.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChefOS.Infrastructure.Services;

public record PaymentUrls(string ReturnUrl, string WebhookUrl);

public record PaymentStart(int PaymentId, string CheckoutUrl);

/// <summary>
/// Paiements des réservations : création chez la passerelle, synchronisation (webhook et retour du client) et remboursement.
/// Chaque changement de statut est une mise à jour conditionnelle (« uniquement si le statut est encore celui qu'on a lu ») : un webhook et un retour
/// simultanés ne peuvent ni confirmer deux fois ni rembourser deux fois. Statuts d'un paiement : <c>Open → Paid → RefundPending → Refunded</c>,
/// ou <c>Open → Failed | Expired | Canceled</c>.
/// </summary>
public class PaymentService(IDbContextFactory<ChefOsDbContext> factory, IPaymentGateway gateway, TimeProvider clock, ILogger<PaymentService> logger,
    PublicSiteOptions? site = null, EmailDispatchSignal? signal = null)
{
    private readonly PublicSiteOptions _site = site ?? new PublicSiteOptions();

    private const string InitFailedMessage = "Le paiement n'a pas pu être initialisé, réessayez";
    private const string UnknownReservationMessage = "Réservation introuvable";
    private const string UnknownPaymentMessage = "Paiement introuvable";
    private const string NotRefundableMessage = "Ce paiement ne peut pas être remboursé";
    internal const string PaidAfterExpiryNote = "Paiement reçu après expiration : remboursé";
    internal const string PaidAfterCancellationNote = "Paiement reçu après annulation : remboursé";

    private string WrongGatewayMessage(string used) =>
        $"Ce paiement a été fait via {used} : configurez Payments:Gateway={used} sur cette application";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Crée le paiement chez la passerelle pour une réservation <c>PendingPayment</c> déjà enregistrée. Appelé hors de toute transaction de base de données.
    /// Si la passerelle échoue, la réservation passe en <c>Expired</c> (la place est libérée), un paiement <c>Failed</c> garde la trace, et l'erreur est traduite.
    /// </summary>
    public async Task<PaymentStart> StartAsync(int reservationId, decimal amount, PaymentKind kind, PaymentUrls urls, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId, ct)
                          ?? throw new DomainValidationException(UnknownReservationMessage);

        var metadata = new Dictionary<string, string>
        {
            ["reservationId"] = reservation.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["restaurantId"] = reservation.RestaurantId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        GatewayPayment created;
        try
        {
            created = await gateway.CreateAsync(amount, $"Réservation {reservation.CustomerName} · {reservation.PartySize} couverts",
                urls.ReturnUrl, urls.WebhookUrl, metadata, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Échec de création du paiement ({Gateway}) pour la réservation {ReservationId} : {Reason}", gateway.Name, reservationId, ex.Message);

            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            await db.Reservations
                .Where(r => r.Id == reservationId && r.Status == ReservationStatus.PendingPayment)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, ReservationStatus.Expired), CancellationToken.None);
            db.Payments.Add(new Payment
            {
                RestaurantId = reservation.RestaurantId, ReservationId = reservationId, Kind = kind, Amount = amount,
                Status = PaymentStatus.Failed, Gateway = gateway.Name, CreatedAt = Now
            });
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);

            throw new DomainValidationException(InitFailedMessage, ValidationFields.Payment);
        }

        var payment = new Payment
        {
            RestaurantId = reservation.RestaurantId, ReservationId = reservationId, Kind = kind, Amount = amount,
            Status = PaymentStatus.Open, Gateway = gateway.Name, GatewayPaymentId = created.Id, CheckoutUrl = created.CheckoutUrl, CreatedAt = Now
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        return new PaymentStart(payment.Id, created.CheckoutUrl);
    }

    /// <summary>
    /// Relit le statut chez la passerelle et applique le résultat ; appelé par le webhook et au retour du client, de façon idempotente
    /// (un paiement déjà traité est renvoyé tel quel). Retourne le statut du paiement, ou null s'il est inconnu.
    /// <list type="bullet">
    /// <item>Réglé et réservation <c>PendingPayment</c> encore valide : <c>Confirmed</c>.</item>
    /// <item>Réglé après expiration : sous le verrou du service, <c>Confirmed</c> si la place est encore libre ; sinon remboursement automatique
    /// (<c>RefundPending</c> puis <c>Refunded</c>), réservation laissée <c>Expired</c> avec une note. Réglé après une annulation : remboursé.</item>
    /// <item>Échoué, expiré ou annulé : la réservation <c>PendingPayment</c> passe en <c>Expired</c> et libère sa place.</item>
    /// </list>
    /// Une confirmation, ou un remboursement automatique faute de place, met l'e-mail du client en file dans la même transaction : une seule fois,
    /// puisque seul l'appel qui fait passer le paiement de <c>Open</c> à <c>Paid</c> y arrive.
    /// </summary>
    public async Task<PaymentStatus?> SyncAsync(string gatewayPaymentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.GatewayPaymentId == gatewayPaymentId, ct);
        if (payment is null)
            return null;
        if (payment.Status != PaymentStatus.Open)
            return payment.Status;
        if (payment.Gateway != gateway.Name)
            throw new DomainValidationException(WrongGatewayMessage(payment.Gateway));

        var status = await gateway.GetStatusAsync(gatewayPaymentId, ct);
        return status switch
        {
            GatewayStatus.Open => PaymentStatus.Open,
            GatewayStatus.Paid => await ApplyPaidAsync(db, payment, ct),
            GatewayStatus.Failed => await ApplyUnpaidAsync(db, payment, PaymentStatus.Failed, ct),
            GatewayStatus.Expired => await ApplyUnpaidAsync(db, payment, PaymentStatus.Expired, ct),
            _ => await ApplyUnpaidAsync(db, payment, PaymentStatus.Canceled, ct)
        };
    }

    /// <summary>
    /// Relit chez la passerelle un paiement connu par son identifiant local (action « Vérifier le paiement » du back) ; même effet que <see cref="SyncAsync"/>.
    /// </summary>
    /// <exception cref="DomainValidationException">Paiement inconnu, sans identifiant chez la passerelle, ou fait via une autre passerelle.</exception>
    public async Task<PaymentStatus> SyncByIdAsync(int paymentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var gatewayId = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => p.GatewayPaymentId).FirstOrDefaultAsync(ct);
        if (gatewayId is null)
        {
            if (!await db.Payments.AnyAsync(p => p.Id == paymentId, ct))
                throw new DomainValidationException(UnknownPaymentMessage);
            return await CurrentStatusAsync(db, paymentId, ct);
        }

        return await SyncAsync(gatewayId, ct) ?? throw new DomainValidationException(UnknownPaymentMessage);
    }

    private async Task<PaymentStatus> ApplyUnpaidAsync(ChefOsDbContext db, Payment payment, PaymentStatus target, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Payments
            .Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Open)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, target), ct);
        if (claimed == 1)
        {
            await db.Reservations
                .Where(r => r.Id == payment.ReservationId && r.Status == ReservationStatus.PendingPayment)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, ReservationStatus.Expired), ct);
        }

        await transaction.CommitAsync(ct);
        return await CurrentStatusAsync(db, payment.Id, ct);
    }

    private async Task<PaymentStatus> ApplyPaidAsync(ChefOsDbContext db, Payment payment, CancellationToken ct)
    {
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == payment.ReservationId, ct);
        if (reservation is null)
        {
            await db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Open)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, PaymentStatus.Paid).SetProperty(p => p.PaidAt, Now), ct);
            return await CurrentStatusAsync(db, payment.Id, ct);
        }

        var timeZone = await db.Restaurants.Where(r => r.Id == reservation.RestaurantId).Select(r => r.TimeZoneId).FirstAsync(ct);
        var pendingMinutes = await db.VitrineProfiles.Select(p => (int?)p.PendingPaymentMinutes).FirstOrDefaultAsync(ct) ?? 30;
        var localDate = RestaurantClock.ToLocal(reservation.StartsAtUtc, timeZone).Date;

        var refund = false;
        var confirmed = false;
        var enqueued = false;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Le verrou du service sérialise cette confirmation avec toute autre prise de places du même service et du même jour.
            await ReservationCapacity.LockAsync(db, reservation.RestaurantId, reservation.ServiceName, localDate, ct);

            var now = Now;
            var claimed = await db.Payments
                .Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Open)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, PaymentStatus.Paid).SetProperty(p => p.PaidAt, now), ct);
            if (claimed != 1)
                return await CurrentStatusAsync(db, payment.Id, ct);

            // Verrou de ligne de la réservation (mise à jour sans effet) : l'expiration paresseuse et l'annulation du client, qui ne prennent pas le verrou du
            // service, attendent la fin de cette transaction, et la lecture qui suit voit leur résultat. Le statut lu ne peut plus changer avant le commit.
            await db.Reservations.Where(r => r.Id == reservation.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Note, r => r.Note), ct);
            var current = await db.Reservations.Where(r => r.Id == reservation.Id)
                .Select(r => new { r.Status, r.CreatedAt }).FirstAsync(ct);
            var fresh = current.Status == ReservationStatus.PendingPayment && now - current.CreatedAt < TimeSpan.FromMinutes(pendingMinutes);

            string? note = null;
            if (fresh)
            {
                confirmed = await ConfirmAsync(db, reservation.Id, ReservationStatus.PendingPayment, now, ct) == 1;
                note = PaidAfterExpiryNote; // n'est utilisée que si la confirmation n'a touché aucune ligne
            }
            else if (current.Status is ReservationStatus.PendingPayment or ReservationStatus.Expired)
            {
                var remaining = await ReservationCapacity.RemainingAsync(db, reservation.ServiceName, localDate, timeZone, now, pendingMinutes, reservation.Id, ct);
                if (remaining >= reservation.PartySize)
                    confirmed = await ConfirmAsync(db, reservation.Id, current.Status, now, ct) == 1;
                note = PaidAfterExpiryNote;
            }
            else if (current.Status == ReservationStatus.Cancelled)
            {
                // Annulée entre-temps : la place a été rendue, elle n'est pas reprise silencieusement.
                note = PaidAfterCancellationNote;
            }
            else
            {
                note = string.Empty; // déjà confirmée ou installée : le paiement est enregistré, rien à faire
                logger.LogWarning("Paiement {PaymentId} réglé pour la réservation {ReservationId} déjà {Status} : aucun changement d'état",
                    payment.Id, reservation.Id, current.Status);
            }

            // Ni confirmée ni simplement enregistrée : le paiement ne doit jamais rester réglé sans place, on le rembourse.
            refund = !confirmed && note is { Length: > 0 };
            if (refund)
            {
                await db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Paid)
                    .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, PaymentStatus.RefundPending).SetProperty(p => p.RefundAmount, payment.Amount), ct);
                var from = current.Status;
                var keep = from == ReservationStatus.Cancelled ? ReservationStatus.Cancelled : ReservationStatus.Expired;
                await db.Reservations.Where(r => r.Id == reservation.Id && r.Status == from)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(r => r.Status, keep)
                        .SetProperty(r => r.Note, r => r.Note == null ? note : r.Note + "\n" + note), ct);
            }

            // L'e-mail est écrit avec l'étape qui confirme l'événement : confirmation, ou paiement tardif remboursé (place reprise entre-temps).
            // Un paiement reçu après une annulation n'en envoie pas : le client a déjà reçu l'accusé de son annulation.
            Func<ReservationEmailContext, EmailMessage>? email = confirmed ? ReservationEmails.Confirmed
                : refund && current.Status != ReservationStatus.Cancelled ? ReservationEmails.LatePaymentRefunded
                : null;
            if (email is not null)
            {
                var context = await ReservationMail.LoadAsync(db, reservation.Id, _site.BaseUrl, now, payment.Amount, confirmed ? 0m : payment.Amount, ct);
                enqueued = ReservationMail.Enqueue(db, context, email, now);
                if (enqueued)
                    await db.SaveChangesAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }

        if (enqueued)
            signal?.Notify();

        if (refund)
        {
            try
            {
                await RefundAsync(payment.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Le paiement reste RefundPending : le back peut relancer le remboursement.
                logger.LogWarning("Remboursement automatique du paiement {PaymentId} impossible, à relancer : {Reason}", payment.Id, ex.Message);
            }
        }

        return await CurrentStatusAsync(db, payment.Id, ct);
    }

    private static Task<int> ConfirmAsync(ChefOsDbContext db, int reservationId, ReservationStatus from, DateTime now, CancellationToken ct) =>
        db.Reservations.Where(r => r.Id == reservationId && r.Status == from)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, ReservationStatus.Confirmed).SetProperty(r => r.ConfirmedAt, now), ct);

    private static Task<PaymentStatus> CurrentStatusAsync(ChefOsDbContext db, int paymentId, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => p.Status).FirstAsync(ct);

    /// <summary>
    /// Rembourse intégralement un paiement réglé, une seule fois. <c>Paid → RefundPending</c> est une mise à jour conditionnelle (l'appel qui la gagne continue,
    /// les autres s'effacent). L'appel à la passerelle se fait ensuite sous un verrou consultatif de transaction propre au paiement : une relance simultanée d'un
    /// paiement déjà <c>RefundPending</c> s'efface si un autre appel est en cours. La clé d'idempotence <c>refund-{identifiant chez la passerelle}</c> rend un rejeu inoffensif chez la passerelle, et
    /// avant de rembourser on relit le montant déjà remboursé et on ne demande que le reste : si la passerelle a réussi mais que l'enregistrement a échoué, la relance rattrape (<c>Refunded</c>)
    /// au lieu d'échouer indéfiniment. Si la passerelle échoue, le paiement reste <c>RefundPending</c> et l'erreur remonte.
    /// </summary>
    /// <exception cref="DomainValidationException">Paiement inconnu ou non remboursable (jamais réglé).</exception>
    public async Task RefundAsync(int paymentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, ct)
                      ?? throw new DomainValidationException(UnknownPaymentMessage);

        if (payment.Status == PaymentStatus.Refunded)
            return;
        if (payment.Status is (PaymentStatus.Paid or PaymentStatus.RefundPending) && payment.Gateway != gateway.Name)
            throw new DomainValidationException(WrongGatewayMessage(payment.Gateway));
        if (payment.Status == PaymentStatus.Paid)
        {
            var claimed = await db.Payments
                .Where(p => p.Id == paymentId && p.Status == PaymentStatus.Paid)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, PaymentStatus.RefundPending).SetProperty(p => p.RefundAmount, payment.Amount), ct);
            if (claimed != 1)
                return; // un autre appel a pris le remboursement
        }
        else if (payment.Status != PaymentStatus.RefundPending || payment.GatewayPaymentId is null)
        {
            throw new DomainValidationException(NotRefundableMessage);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockKey = RefundLockBase + paymentId;
        var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({lockKey}) AS \"Value\"").SingleAsync(ct);
        if (!acquired)
            return; // un autre appel rembourse en ce moment

        var gatewayId = payment.GatewayPaymentId!;
        var remaining = payment.Amount - await gateway.GetRefundedAmountAsync(gatewayId, ct);
        if (remaining > 0m)
            await gateway.RefundAsync(gatewayId, remaining, $"refund-{gatewayId}", ct);

        var refundedAt = Now;
        await db.Payments
            .Where(p => p.Id == paymentId && p.Status == PaymentStatus.RefundPending)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, PaymentStatus.Refunded).SetProperty(p => p.RefundedAt, refundedAt), ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Espace de clés des verrous de remboursement (verrou à un seul bigint, distinct de celui des services à deux entiers).</summary>
    private const long RefundLockBase = 1L << 40;
}
