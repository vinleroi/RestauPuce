using ChefOS.Back.Components.Shared;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Reservations;

/// <summary>Une ligne de paiement d'une réservation : type, montant et statut en français.</summary>
public sealed record PaymentLine(int Id, string KindLabel, string AmountText, string StatusLabel, PaymentStatus Status);

/// <summary>Le prochain grand groupe attendu (tableau de bord).</summary>
public sealed record NextGroupView(int Id, string TimeText, string Name, int PartySize, string ServiceName);

/// <summary>Une réservation telle que la carte l'affiche : l'heure locale du restaurant, les actions permises par son statut, ses paiements et ses convives.</summary>
public sealed class ReservationCardView
{
    private const int CollapsedAboveGuests = 4;

    internal ReservationCardView(ReservationDetail detail, DateTime utcNow, int freeCancellationHours)
    {
        Detail = detail;
        Id = detail.Id;
        TimeText = detail.LocalTime.ToString("HH:mm", Ui.Fr);
        Name = detail.CustomerName;
        PartySize = detail.PartySize;
        Status = detail.Status;
        StatusLabel = ReservationDayView.StatusLabel(detail.Status);
        StartsAtUtc = detail.StartsAtUtc;

        Payments = detail.Payments
            .Select(p => new PaymentLine(p.Id, ReservationDayView.PaymentKindLabel(p.Kind), Ui.Money(p.Amount),
                ReservationDayView.PaymentStatusLabel(p.Status), p.Status))
            .ToList();
        PaymentSummary = Summarise(detail);

        CanSeat = detail.Status == ReservationStatus.Confirmed;
        CanNoShow = detail.Status == ReservationStatus.Confirmed;
        CanCancel = detail.Status is ReservationStatus.PendingPayment or ReservationStatus.Confirmed;

        RefundablePayments = detail.Status is ReservationStatus.Cancelled or ReservationStatus.Expired
            ? Payments.Where(p => p.Status is PaymentStatus.Paid or PaymentStatus.RefundPending).ToList()
            : [];

        CheckablePayments = detail.Status is ReservationStatus.PendingPayment or ReservationStatus.Expired
            ? Payments.Where(p => p.Status == PaymentStatus.Open).ToList()
            : [];

        var paid = detail.Payments.FirstOrDefault(p => p.Status == PaymentStatus.Paid);
        CanOfferRefundOnCancel = paid is not null;
        RefundOnCancelLabel = paid?.Kind == PaymentKind.Prepayment ? "Rembourser le prépaiement" : "Rembourser l'acompte";
        RefundOnCancelByDefault = CanOfferRefundOnCancel && utcNow <= detail.StartsAtUtc.AddHours(-freeCancellationHours);

        Guests = detail.Guests.OrderBy(g => g.Position).Select(GuestText).ToList();
        GuestsCollapsedByDefault = detail.Guests.Count > CollapsedAboveGuests;
    }

    public ReservationDetail Detail { get; }

    public int Id { get; }

    public string TimeText { get; }

    public string Name { get; }

    public int PartySize { get; }

    public ReservationStatus Status { get; }

    public string StatusLabel { get; }

    public DateTime StartsAtUtc { get; }

    public bool CanSeat { get; }

    public bool CanNoShow { get; }

    public bool CanCancel { get; }

    public IReadOnlyList<PaymentLine> Payments { get; }

    /// <summary>« Payé 20,00 € », « À payer 20,00 € », « Remboursé 20,00 € · À rembourser 10,00 € » ; null sans paiement.</summary>
    public string? PaymentSummary { get; }

    /// <summary>Paiements qu'un clic sur « Rembourser » reprend : réglés ou dont le remboursement a échoué, d'une réservation annulée ou expirée.</summary>
    public IReadOnlyList<PaymentLine> RefundablePayments { get; }

    /// <summary>Paiements encore ouverts d'une réservation en attente ou expirée : « Vérifier le paiement » les relit chez la passerelle (webhook jamais reçu).</summary>
    public IReadOnlyList<PaymentLine> CheckablePayments { get; }

    public bool CanOfferRefundOnCancel { get; }

    public string RefundOnCancelLabel { get; }

    /// <summary>Case cochée d'office : le client annule dans le délai gratuit.</summary>
    public bool RefundOnCancelByDefault { get; }

    /// <summary>« Marie Dupont : Carbonnade, Dame blanche ».</summary>
    public IReadOnlyList<string> Guests { get; }

    public bool GuestsCollapsedByDefault { get; }

    private static string GuestText(GuestDetail guest)
    {
        var name = $"{guest.FirstName} {guest.LastName}".Trim();
        if (name.Length == 0)
            name = $"Convive {guest.Position + 1}";

        var dishes = guest.Dishes.Count == 0
            ? "aucun plat"
            : string.Join(", ", guest.Dishes.Select(d => d.Quantity > 1 ? $"{d.Quantity} × {d.DishName}" : d.DishName));
        return $"{name} : {dishes}";
    }

    private static string? Summarise(ReservationDetail detail)
    {
        var parts = new List<string>();
        var paid = detail.Payments.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.Amount);
        var refunded = detail.Payments.Where(p => p.Status == PaymentStatus.Refunded).Sum(p => p.RefundAmount ?? p.Amount);
        var toRefund = detail.Payments.Where(p => p.Status == PaymentStatus.RefundPending).Sum(p => p.RefundAmount ?? p.Amount);

        if (detail.Status == ReservationStatus.PendingPayment && detail.AmountDue > 0m)
            parts.Add($"À payer {Ui.Money(detail.AmountDue)}");
        if (paid > 0m)
            parts.Add($"Payé {Ui.Money(paid)}");
        if (refunded > 0m)
            parts.Add($"Remboursé {Ui.Money(refunded)}");
        if (toRefund > 0m)
            parts.Add($"À rembourser {Ui.Money(toRefund)}");

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

/// <summary>Un service du jour : couverts réservés sur capacité, barre de remplissage et cartes.</summary>
public sealed class ServiceView
{
    internal ServiceView(ServiceDayView service, DateTime utcNow, int freeCancellationHours)
    {
        Name = service.ServiceName;
        Capacity = service.Capacity;
        BookedCovers = service.BookedCovers;
        CoversText = service.Capacity > 0 ? $"{service.BookedCovers} / {service.Capacity} couverts" : $"{service.BookedCovers} couverts";
        IsOverCapacity = service.Capacity > 0 && service.BookedCovers > service.Capacity;
        FillPercent = service.Capacity > 0
            ? Math.Min(100, (int)Math.Round(service.BookedCovers * 100m / service.Capacity, MidpointRounding.AwayFromZero))
            : service.BookedCovers > 0 ? 100 : 0;

        // Les réservations vivantes d'abord, dans l'ordre d'arrivée ; les terminées (no-show, annulées, expirées) ensuite.
        Cards = service.Reservations
            .OrderBy(r => r.Status is ReservationStatus.NoShow or ReservationStatus.Cancelled or ReservationStatus.Expired ? 1 : 0)
            .ThenBy(r => r.StartsAtUtc).ThenBy(r => r.Id)
            .Select(r => new ReservationCardView(r, utcNow, freeCancellationHours))
            .ToList();
        FirstStart = service.Reservations.Count == 0 ? (DateTime?)null : service.Reservations.Min(r => r.StartsAtUtc);
    }

    public string Name { get; }

    public int Capacity { get; }

    public int BookedCovers { get; }

    public string CoversText { get; }

    public int FillPercent { get; }

    public bool IsOverCapacity { get; }

    public IReadOnlyList<ReservationCardView> Cards { get; }

    internal DateTime? FirstStart { get; }
}

/// <summary>
/// Les réservations d'un jour pour l'écran du back, sans dépendance à l'interface : services dans l'ordre de leur première réservation (les vides à la fin),
/// cartes, libellés français et prochain grand groupe.
/// </summary>
public sealed class ReservationDayView
{
    // Un groupe en retard reste « à venir » un moment : le chef l'attend encore.
    private static readonly TimeSpan LateGrace = TimeSpan.FromMinutes(60);

    private readonly DateTime _utcNow;

    private ReservationDayView(IReadOnlyList<ServiceView> services, DateTime utcNow)
    {
        Services = services;
        _utcNow = utcNow;
    }

    public IReadOnlyList<ServiceView> Services { get; }

    public int TotalCovers => Services.Sum(s => s.BookedCovers);

    public static ReservationDayView From(IReadOnlyList<ServiceDayView> services, DateTime utcNow, int freeCancellationHours)
    {
        var views = services.Select(s => new ServiceView(s, utcNow, freeCancellationHours)).ToList();
        var ordered = views.Where(v => v.FirstStart is not null).OrderBy(v => v.FirstStart)
            .Concat(views.Where(v => v.FirstStart is null))
            .ToList();
        return new ReservationDayView(ordered, utcNow);
    }

    /// <summary>Le prochain groupe d'au moins <paramref name="minPartySize"/> couverts, confirmé et pas encore installé.</summary>
    public NextGroupView? NextGroup(int minPartySize) =>
        Services
            .SelectMany(s => s.Cards.Select(c => (Service: s, Card: c)))
            .Where(x => x.Card.Status == ReservationStatus.Confirmed && x.Card.PartySize >= minPartySize && x.Card.StartsAtUtc >= _utcNow - LateGrace)
            .OrderBy(x => x.Card.StartsAtUtc).ThenBy(x => x.Card.Id)
            .Select(x => new NextGroupView(x.Card.Id, x.Card.TimeText, x.Card.Name, x.Card.PartySize, x.Service.Name))
            .FirstOrDefault();

    public static string StatusLabel(ReservationStatus status) => status switch
    {
        ReservationStatus.PendingPayment => "En attente de paiement",
        ReservationStatus.Confirmed => "Confirmée",
        ReservationStatus.Seated => "Arrivée",
        ReservationStatus.NoShow => "No-show",
        ReservationStatus.Cancelled => "Annulée",
        ReservationStatus.Expired => "Expirée",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string PaymentKindLabel(PaymentKind kind) => kind switch
    {
        PaymentKind.Deposit => "Acompte",
        PaymentKind.Prepayment => "Prépaiement",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static string PaymentStatusLabel(PaymentStatus status) => status switch
    {
        PaymentStatus.Open => "En attente",
        PaymentStatus.Paid => "Payé",
        PaymentStatus.Failed => "Échec",
        PaymentStatus.Expired => "Expiré",
        PaymentStatus.Canceled => "Annulé",
        PaymentStatus.RefundPending => "À rembourser",
        PaymentStatus.Refunded => "Remboursé",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
