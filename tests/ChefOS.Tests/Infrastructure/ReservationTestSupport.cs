using ChefOS.Core.Recipes;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Notifications;
using ChefOS.Infrastructure.Payments;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Passerelle de test : délègue à la passerelle de démonstration, avec des pannes à la demande et un compteur de remboursements.</summary>
public sealed class FaultyGateway(DemoPaymentGateway inner) : IPaymentGateway
{
    public bool FailCreate { get; set; }
    public bool FailRefund { get; set; }
    /// <summary>Le remboursement réussit chez la passerelle, puis l'appel échoue (réponse perdue, enregistrement impossible).</summary>
    public bool FailAfterRefund { get; set; }
    /// <summary>Si défini, le remboursement attend cette tâche (pour tester deux appels simultanés).</summary>
    public Task? RefundBlock { get; set; }
    public int RefundCalls;
    public List<string> RefundKeys { get; } = [];
    public string Name => inner.Name;

    public Task<GatewayPayment> CreateAsync(decimal amount, string description, string redirectUrl, string webhookUrl,
        IDictionary<string, string> metadata, CancellationToken ct) =>
        FailCreate ? throw new InvalidOperationException("Mollie ne répond pas") : inner.CreateAsync(amount, description, redirectUrl, webhookUrl, metadata, ct);

    public Task<GatewayStatus> GetStatusAsync(string id, CancellationToken ct) => inner.GetStatusAsync(id, ct);

    public Task<decimal> GetRefundedAmountAsync(string id, CancellationToken ct) => inner.GetRefundedAmountAsync(id, ct);

    public async Task RefundAsync(string id, decimal amount, string idempotencyKey, CancellationToken ct)
    {
        Interlocked.Increment(ref RefundCalls);
        lock (RefundKeys)
            RefundKeys.Add(idempotencyKey);
        if (RefundBlock is { } block)
            await block;
        if (FailRefund)
            throw new InvalidOperationException("Remboursement refusé");
        await inner.RefundAsync(id, amount, idempotencyKey, ct);
        if (FailAfterRefund)
            throw new InvalidOperationException("Réponse perdue");
    }
}

public sealed class RecordingEmailSender : IEmailSender
{
    public List<(string To, string Subject)> Sent { get; } = [];

    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        lock (Sent)
            Sent.Add((to, subject));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Restaurant de test : vitrine publiée, réservations ouvertes, service « Dîner » le vendredi 2026-11-06 (18:00–22:00, pas de 30 min),
/// horloge figée au lundi 2026-11-02 09:00 UTC. Un créneau à 19:00 locale est à 18:00 UTC (hiver, UTC+1).
/// </summary>
public sealed class ReservationHarness
{
    public const string ReturnBase = "https://vitrine.test/reservation";
    public const string Webhook = "https://vitrine.test/webhook";
    public static readonly DateOnly Friday = new(2026, 11, 6);
    public static readonly TimeOnly Seven = new(19, 0);

    public required DbContextOptions<ChefOsDbContext> Options { get; init; }
    public required int RestaurantId { get; init; }
    public required FakeTimeProvider Clock { get; init; }
    public required DemoPaymentGateway Demo { get; init; }
    public required FaultyGateway Gateway { get; init; }
    public required RecordingEmailSender Email { get; init; }
    public required PaymentService Payments { get; init; }
    public required ReservationService Reservations { get; init; }

    public static async Task<ReservationHarness> CreateAsync(PostgresFixture postgres, int capacity = 20, Action<VitrineProfile>? profile = null)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurantId = await TestData.CreateRestaurantAsync(options, "Bistro");

        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId)))
        {
            var p = new VitrineProfile { IsPublished = true, ReservationsOpen = true };
            profile?.Invoke(p);
            db.VitrineProfiles.Add(p);
            db.ServiceSchedules.Add(new ServiceSchedule
            {
                DayOfWeek = DayOfWeek.Friday, Name = "Dîner", StartLocal = new TimeOnly(18, 0), EndLocal = new TimeOnly(22, 0),
                Capacity = capacity, SlotMinutes = 30
            });
            await db.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        var demo = new DemoPaymentGateway("https://vitrine.test", new TestContextFactory(options, null), clock);
        var gateway = new FaultyGateway(demo);
        var email = new RecordingEmailSender();
        var (payments, reservations) = Services(options, restaurantId, clock, gateway, email);
        return new ReservationHarness
        {
            Options = options, RestaurantId = restaurantId, Clock = clock, Demo = demo, Gateway = gateway, Email = email,
            Payments = payments, Reservations = reservations
        };
    }

    public static (PaymentService, ReservationService) Services(DbContextOptions<ChefOsDbContext> options, int restaurantId,
        TimeProvider clock, IPaymentGateway gateway, IEmailSender email)
    {
        var factory = new TestContextFactory(options, restaurantId);
        var payments = new PaymentService(factory, gateway, clock, NullLogger<PaymentService>.Instance, email);
        return (payments, new ReservationService(factory, clock, payments, email));
    }

    public ChefOsDbContext Open(int? restaurantId = null) => new(Options, new FixedTenantContext(restaurantId ?? RestaurantId));

    public async Task<int> AddDishAsync(string name, decimal price = 20m, decimal vat = 0.12m, bool show = true, bool archived = false)
    {
        await using var db = Open();
        var recipe = new Recipe { Name = name, Kind = RecipeKind.Dish, SellingPriceInclVat = price, VatRate = vat, ShowOnVitrine = show, IsArchived = archived };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    public static ReservationRequest Request(int partySize = 2, DateOnly? date = null, TimeOnly? time = null, IReadOnlyList<ReservationGuestInput>? guests = null,
        string name = "Dupont", string email = "dupont@example.com", string phone = "0470 12 34 56", string? note = null) =>
        new(date ?? Friday, time ?? Seven, "Dîner", partySize, name, email, phone, note, guests ?? []);

    public static ReservationGuestInput Guest(string? first, string? last, params int[] recipeIds) =>
        new(first, last, recipeIds.Select(id => new ReservationDishInput(id, 1)).ToList());

    public Task<ReservationStart> CreateAsync(ReservationRequest request) => Reservations.CreateAsync(request, ReturnBase, Webhook);

    public async Task<Reservation> LoadAsync(string token)
    {
        await using var db = Open();
        return await db.Reservations.AsNoTracking().Include(r => r.Guests).ThenInclude(g => g.Dishes).SingleAsync(r => r.PublicToken == token);
    }

    public async Task<List<Payment>> PaymentsOfAsync(int reservationId)
    {
        await using var db = Open();
        return await db.Payments.AsNoTracking().Where(p => p.ReservationId == reservationId).OrderBy(p => p.Id).ToListAsync();
    }

    /// <summary>Un paiement ouvert passe à « réglé » chez la passerelle ; le webhook est simulé par <see cref="PaymentService.SyncAsync"/>.</summary>
    public async Task PayAsync(string token)
    {
        var reservation = await LoadAsync(token);
        var payment = (await PaymentsOfAsync(reservation.Id)).Single();
        await Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid);
        await Payments.SyncAsync(payment.GatewayPaymentId!);
    }
}
