using ChefOS.Core.Recipes;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Payments;
using ChefOS.Infrastructure.Wines;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Seeding;

/// <summary>
/// La vitrine publique de « Le Petit Bistro » et ses réservations, pour que le site public et l'écran des réservations soient vivants dès le premier lancement :
/// <list type="bullet">
/// <item><description>vitrine publiée (ambiance, couleur, ville, téléphone, sans image : le dégradé de secours sert de couverture) ;</description></item>
/// <item><description>services du mardi au samedi (Midi et Soir), fermé le dimanche et le lundi, un congé exceptionnel dans trois semaines ;</description></item>
/// <item><description>une phrase publique sur chacun des 12 plats ;</description></item>
/// <item><description>15 vins publiés tirés de la bibliothèque (chargée ici si elle ne l'est pas encore), avec prix et accords, plus un vin saisi à la main dont la fiche IA est « à relire » (non publié) ;</description></item>
/// <item><description>9 réservations sur les 7 prochains jours ouverts : 6 petites tables confirmées, un groupe de 8 confirmé avec convives nommés et plats pré-commandés,
/// une demande en attente de paiement déjà périmée (elle expirera à la première lecture) et une annulation remboursée.</description></item>
/// </list>
/// Tout est relatif à <c>now</c>, déterministe (aucun hasard, hormis le jeton public de chaque réservation) et écrit comme le font les services : prix des plats figés,
/// montants de <see cref="ReservationPricing"/>, paiements de la passerelle « Demo » avec leur ligne en base (le remboursement fonctionne depuis le back).
/// </summary>
internal static class DemoVitrine
{
    private const string TimeZoneId = "Europe/Brussels";
    private const string GatewayIdPrefix = "demo_5eed";
    private const string Midi = "Midi";
    private const string Soir = "Soir";

    private static readonly DayOfWeek[] OpenDays =
        [DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

    private static readonly Dictionary<string, string> DishDescriptions = new()
    {
        ["Carbonnade flamande"] = "Le bœuf mijoté des heures dans la bière brune d'abbaye, fondant sous une sauce sombre et légèrement acidulée, servi avec des frites maison.",
        ["Boulets liégeois"] = "Deux boulets de viande dorés nappés d'une sauce lapin au sirop de Liège, aigre-douce comme on l'aime en Wallonie.",
        ["Vol-au-vent"] = "Le classique des repas de famille : poulet fermier, champignons et petites boulettes sous une sauce crémeuse, dans son feuilleté croustillant.",
        ["Moules-frites"] = "Moules de Zélande cuites au céleri et au vin blanc, accompagnées de frites dorées deux fois, croustillantes et bien salées.",
        ["Américain préparé"] = "Bœuf haché cru assaisonné à la minute aux câpres, aux échalotes et à la moutarde, avec ses frites et sa salade croquante.",
        ["Croquettes de crevettes"] = "Deux croquettes à la crevette grise de la mer du Nord, croustillantes dehors, onctueuses dedans, relevées d'un filet de citron.",
        ["Lapin à la bière"] = "Un lapin tendre braisé à la bière d'abbaye, aux pruneaux et à la moutarde à l'ancienne, nappé d'une sauce brillante et profonde.",
        ["Waterzooi de poulet"] = "Le waterzooi gantois de poulet fermier et de légumes, mijoté dans un bouillon crémeux et parfumé, tout en douceur.",
        ["Cabillaud beurre blanc"] = "Un cabillaud nacré cuit à la perfection, un beurre blanc soyeux aux échalotes, de la purée maison et des haricots verts croquants.",
        ["Chicons au gratin"] = "Des chicons fondants enroulés dans le jambon, gratinés sous une béchamel au gruyère, sur une purée maison bien beurrée.",
        ["Salade liégeoise"] = "La tiède du pays de Liège : haricots verts, pommes de terre et lardons fumés, un œuf mollet et une vinaigrette à la moutarde.",
        ["Gaufre de Bruxelles"] = "Une gaufre légère et dorée, glace vanille, fraises fraîches et crème fouettée : la gourmandise qui clôt le repas."
    };

    private sealed record WineChoice(string Key, decimal Bottle, decimal? Glass, string[] Dishes, string? Note = null);

    private static readonly WineChoice[] Wines =
    [
        new("bourgogne-rouge", 38m, null, ["Carbonnade flamande", "Lapin à la bière"]),
        new("cotes-du-rhone-rouge", 32m, 6.50m, ["Boulets liégeois"]),
        new("saint-emilion", 52m, null, ["Carbonnade flamande"]),
        new("chinon", 36m, null, ["Chicons au gratin", "Américain préparé"], "Un rouge de Loire souple, parfait sur la viande crue."),
        new("morgon", 40m, null, ["Américain préparé", "Salade liégeoise"]),
        new("belgique-pinot-noir", 48m, null, ["Lapin à la bière"]),
        new("chablis", 44m, 8m, ["Cabillaud beurre blanc", "Moules-frites"]),
        new("muscadet-sevre-et-maine", 32m, 6m, ["Moules-frites", "Croquettes de crevettes"], "Le vin des moules, tout simplement."),
        new("sancerre-blanc", 46m, null, ["Waterzooi de poulet", "Cabillaud beurre blanc"]),
        new("alsace-riesling", 38m, null, ["Waterzooi de poulet", "Chicons au gratin"]),
        new("belgique-chardonnay-haspengouw", 42m, null, ["Vol-au-vent"]),
        new("cotes-de-provence-rose", 34m, 6.50m, ["Salade liégeoise"]),
        new("belgique-cremant-blanc-de-blancs", 52m, 9.50m, ["Croquettes de crevettes", "Gaufre de Bruxelles"]),
        new("champagne-brut", 85m, null, ["Croquettes de crevettes"]),
        new("sauternes", 48m, null, ["Gaufre de Bruxelles"])
    ];

    private static readonly (string Name, string Email, string Phone, int Party, int OpenDay, string Service, TimeOnly Time)[] Tables =
    [
        ("Claire Dumont", "claire.dumont@example.com", "0470 00 10 01", 2, 0, Midi, new TimeOnly(12, 30)),
        ("Marc Lejeune", "marc.lejeune@example.com", "0470 00 10 02", 4, 0, Soir, new TimeOnly(19, 30)),
        ("Aurélie Gillet", "aurelie.gillet@example.com", "0470 00 10 03", 3, 1, Soir, new TimeOnly(20, 0)),
        ("Pierre Delvaux", "pierre.delvaux@example.com", "0470 00 10 04", 2, 2, Midi, new TimeOnly(12, 0)),
        ("Sophie Renard", "sophie.renard@example.com", "0470 00 10 05", 2, 3, Soir, new TimeOnly(19, 0)),
        ("Nicolas Hansen", "nicolas.hansen@example.com", "0470 00 10 06", 4, 4, Soir, new TimeOnly(20, 30))
    ];

    private static readonly (string First, string Last)[] GroupGuests =
    [
        ("Hélène", "Marchal"), ("Julien", "Dubois"), ("Camille", "Lambert"), ("Nicolas", "Renard"),
        ("Sophie", "Declercq"), ("Thomas", "Lejeune"), ("Isabelle", "Fontaine"), ("Maxime", "Collard")
    ];

    private static readonly string[] Starters = ["Croquettes de crevettes", "Salade liégeoise"];
    private static readonly string[] Mains = ["Carbonnade flamande", "Boulets liégeois", "Moules-frites", "Waterzooi de poulet", "Lapin à la bière", "Vol-au-vent"];

    public static async Task AddAsync(ChefOsDbContext db, DateTime now, CancellationToken ct)
    {
        var today = RestaurantClock.Today(now, TimeZoneId);

        // Une tentative interrompue peut avoir laissé des paiements de passerelle (table globale, hors cascade du restaurant).
        await db.DemoGatewayPayments.Where(p => p.Id.StartsWith(GatewayIdPrefix)).ExecuteDeleteAsync(ct);

        var profile = AddProfile(db);
        AddSchedules(db, today);
        var dishes = await db.Recipes.Where(r => r.Kind == RecipeKind.Dish).ToDictionaryAsync(r => r.Name, ct);
        DescribeDishes(dishes);
        await db.SaveChangesAsync(ct);

        await AddWinesAsync(db, dishes, ct);
        await AddReservationsAsync(db, profile, dishes, now, today, ct);
    }

    private static VitrineProfile AddProfile(ChefOsDbContext db)
    {
        var profile = new VitrineProfile
        {
            IsPublished = true,
            ReservationsOpen = true,
            Tagline = "Cuisine belge de saison, bières d'abbaye, feu de bois",
            AmbianceText = "Au cœur de Liège, Le Petit Bistro vous accueille comme à la maison, entre briques rouges, banquettes de cuir et lumière douce de fin de journée. "
                           + "Ici, on cuisine à la belge : carbonnade mijotée à la bière d'abbaye, boulets sauce lapin, moules du jour, le tout préparé avec des produits de saison choisis chez nos producteurs. "
                           + "La carte des vins fait la part belle aux petits vignerons, et nos bières du pays se dégustent au comptoir comme à table. "
                           + "Venez en famille, entre amis ou pour un repas d'affaires : la bonne humeur est comprise.",
            AccentColor = "#7A3E1D",
            City = "Liège",
            Phone = "+32 4 000 00 00",
            PublicEmail = "contact@le-petit-bistro.example"
        };
        db.VitrineProfiles.Add(profile);
        return profile;
    }

    private static void AddSchedules(ChefOsDbContext db, DateOnly today)
    {
        foreach (var day in OpenDays)
        {
            db.ServiceSchedules.Add(new ServiceSchedule
                { DayOfWeek = day, Name = Midi, StartLocal = new TimeOnly(12, 0), EndLocal = new TimeOnly(14, 0), Capacity = 40, SlotMinutes = 30 });
            db.ServiceSchedules.Add(new ServiceSchedule
                { DayOfWeek = day, Name = Soir, StartLocal = new TimeOnly(19, 0), EndLocal = new TimeOnly(22, 0), Capacity = 50, SlotMinutes = 30 });
        }

        db.ClosureDays.Add(new ClosureDay { DateLocal = NextOpenDay(today.AddDays(21)), Reason = "Congé annuel" });
    }

    private static DateOnly NextOpenDay(DateOnly from)
    {
        while (!OpenDays.Contains(from.DayOfWeek))
            from = from.AddDays(1);
        return from;
    }

    private static void DescribeDishes(Dictionary<string, Recipe> dishes)
    {
        foreach (var (name, description) in DishDescriptions)
        {
            var dish = dishes[name];
            dish.PublicDescription = description;
            dish.ShowOnVitrine = true;
        }
    }

    // ── Carte des vins ───────────────────────────────────────────────────────

    private static async Task AddWinesAsync(ChefOsDbContext db, Dictionary<string, Recipe> dishes, CancellationToken ct)
    {
        // Le seed de la démo passe avant le chargement de la bibliothèque au démarrage : on la charge ici (même ressource, même clé d'unicité).
        var existingKeys = (await db.WineReferences.Select(w => w.Key).ToListAsync(ct)).ToHashSet();
        foreach (var reference in WineLibrarySeeder.LoadLibrary().Where(w => !existingKeys.Contains(w.Key)))
        {
            reference.Id = 0;
            db.WineReferences.Add(reference);
        }
        await db.SaveChangesAsync(ct);

        var keys = Wines.Select(w => w.Key).ToList();
        var references = await db.WineReferences.Where(w => keys.Contains(w.Key)).ToDictionaryAsync(w => w.Key, ct);
        var positions = new Dictionary<WineColor, int>();
        int NextPosition(WineColor color) => positions[color] = positions.GetValueOrDefault(color) + 1;

        var items = new List<(WineListItem Item, string[] Dishes)>();
        foreach (var choice in Wines)
        {
            var reference = references[choice.Key];
            items.Add((new WineListItem
            {
                WineReferenceId = reference.Id,
                Name = reference.Name, Appellation = reference.Appellation, Region = reference.Region, Country = reference.Country, Color = reference.Color,
                Grapes = reference.Grapes, Robe = reference.Robe, Nez = reference.Nez, Bouche = reference.Bouche, Garde = reference.Garde,
                ServiceTemperature = reference.ServiceTemperature, GeneralPairings = reference.Pairings,
                PersonalNote = choice.Note, PriceBottle = choice.Bottle, PriceGlass = choice.Glass,
                IsPublished = true, Position = NextPosition(reference.Color)
            }, choice.Dishes));
        }

        // Un vin saisi à la main, dont la fiche a été rédigée par l'IA : non publié, avec le badge « à relire ».
        items.Add((new WineListItem
        {
            Name = "Cuvée du patron", Producer = "Domaine des Trois Chênes", Vintage = 2021, Appellation = "Cahors", Region = "Sud-Ouest", Country = "France",
            Color = WineColor.Rouge, Grapes = "Malbec",
            Robe = "Rouge sombre aux reflets violets.",
            Nez = "Fruits noirs mûrs, violette et une pointe de réglisse.",
            Bouche = "Ample et charpentée, aux tanins serrés et à la longue finale épicée.",
            Garde = "À boire ou à garder 5 à 8 ans.", ServiceTemperature = "16 à 18 °C",
            GeneralPairings = "Viandes rouges grillées, gibier, fromages affinés.",
            PriceBottle = 36m, IsPublished = false, DescriptionIsAiDraft = true, Position = NextPosition(WineColor.Rouge)
        }, ["Lapin à la bière"]));

        db.WineListItems.AddRange(items.Select(i => i.Item));
        await db.SaveChangesAsync(ct);

        foreach (var (item, pairedDishes) in items)
            foreach (var dish in pairedDishes)
                db.WineListItemPairings.Add(new WineListItemPairing { WineListItemId = item.Id, RecipeId = dishes[dish].Id });
        await db.SaveChangesAsync(ct);
    }

    // ── Réservations ─────────────────────────────────────────────────────────

    private static async Task AddReservationsAsync(
        ChefOsDbContext db, VitrineProfile profile, Dictionary<string, Recipe> dishes, DateTime now, DateOnly today, CancellationToken ct)
    {
        var closure = await db.ClosureDays.Select(c => c.DateLocal).ToListAsync(ct);
        var days = Enumerable.Range(1, 7).Select(today.AddDays)
            .Where(d => OpenDays.Contains(d.DayOfWeek) && !closure.Contains(d))
            .ToList();

        var reservations = new List<Reservation>();
        var gateway = new List<(Reservation Reservation, PaymentStatus Status, PaymentKind Kind)>();

        for (var i = 0; i < Tables.Length; i++)
        {
            var t = Tables[i];
            var created = now.AddHours(-(6 + 7 * i));
            var due = ReservationPricing.Compute(t.Party, 0m, profile);
            var reservation = NewReservation(days[t.OpenDay % days.Count], t.Time, t.Service, t.Party, t.Name, t.Email, t.Phone,
                ReservationStatus.Confirmed, created, due.Amount);
            reservations.Add(reservation);
            gateway.Add((reservation, PaymentStatus.Paid, due.Kind!.Value));
        }

        // Le groupe de 8 : convives nommés, 1 à 3 plats chacun, prépaiement d'une part de la pré-commande.
        var group = NewReservation(days[2 % days.Count], new TimeOnly(19, 30), Soir, 8, "Hélène Marchal", "helene.marchal@example.com", "0470 00 20 01",
            ReservationStatus.Confirmed, now.AddHours(-30), 0m);
        group.Note = "Anniversaire de mariage : une table ensemble, si possible.";
        for (var g = 0; g < GroupGuests.Length; g++)
        {
            var guest = new ReservationGuest { Position = g + 1, FirstName = GroupGuests[g].First, LastName = GroupGuests[g].Last };
            var chosen = new List<string>();
            if (g % 2 == 0) chosen.Add(Starters[(g / 2) % Starters.Length]);
            chosen.Add(Mains[g % Mains.Length]);
            if (g % 3 == 0) chosen.Add("Gaufre de Bruxelles");
            foreach (var name in chosen)
            {
                var dish = dishes[name];
                guest.Dishes.Add(new ReservationGuestDish
                    { RecipeId = dish.Id, Quantity = 1, UnitPriceInclVat = dish.SellingPriceInclVat!.Value, VatRate = dish.VatRate!.Value });
            }
            group.Guests.Add(guest);
        }
        group.PreorderTotalInclVat = group.Guests.SelectMany(g => g.Dishes).Sum(d => d.UnitPriceInclVat * d.Quantity);
        var groupDue = ReservationPricing.Compute(group.PartySize, group.PreorderTotalInclVat, profile);
        group.AmountDue = groupDue.Amount;
        reservations.Add(group);
        gateway.Add((group, PaymentStatus.Paid, groupDue.Kind!.Value));

        // En attente depuis bien plus que le délai de paiement : l'expiration paresseuse la passera en « Expirée » à la première lecture.
        var stale = NewReservation(days[1 % days.Count], new TimeOnly(13, 0), Midi, 2, "Laurent Maes", "laurent.maes@example.com", "0470 00 30 01",
            ReservationStatus.PendingPayment, now.AddHours(-3), ReservationPricing.Compute(2, 0m, profile).Amount);
        reservations.Add(stale);
        gateway.Add((stale, PaymentStatus.Open, PaymentKind.Deposit));

        // Annulée, acompte remboursé.
        var cancelled = NewReservation(days[3 % days.Count], new TimeOnly(12, 30), Midi, 2, "Valérie Closset", "valerie.closset@example.com", "0470 00 40 01",
            ReservationStatus.Cancelled, now.AddHours(-50), ReservationPricing.Compute(2, 0m, profile).Amount);
        cancelled.CancelledAt = now.AddHours(-20);
        reservations.Add(cancelled);
        gateway.Add((cancelled, PaymentStatus.Refunded, PaymentKind.Deposit));

        db.Reservations.AddRange(reservations);
        await db.SaveChangesAsync(ct);

        var sequence = 0;
        foreach (var (reservation, status, kind) in gateway)
        {
            var id = $"{GatewayIdPrefix}{++sequence:D7}";
            var paid = status != PaymentStatus.Open;
            var label = kind == PaymentKind.Prepayment ? "Prépaiement" : "Acompte";
            db.DemoGatewayPayments.Add(new DemoGatewayPayment
            {
                Id = id, Amount = reservation.AmountDue, Description = $"{label} réservation {reservation.CustomerName}",
                RedirectUrl = string.Empty, CreatedAt = reservation.CreatedAt,
                Status = paid ? GatewayStatus.Paid : GatewayStatus.Open,
                RefundedAmount = status == PaymentStatus.Refunded ? reservation.AmountDue : 0m
            });
            db.Payments.Add(new Payment
            {
                ReservationId = reservation.Id, Kind = kind, Amount = reservation.AmountDue, Status = status, Gateway = "Demo", GatewayPaymentId = id,
                CreatedAt = reservation.CreatedAt,
                PaidAt = paid ? reservation.CreatedAt.AddMinutes(3) : null,
                RefundedAt = status == PaymentStatus.Refunded ? reservation.CancelledAt : null,
                RefundAmount = status == PaymentStatus.Refunded ? reservation.AmountDue : null
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private static Reservation NewReservation(
        DateOnly date, TimeOnly time, string service, int party, string name, string email, string phone,
        ReservationStatus status, DateTime createdAt, decimal amountDue) => new()
    {
        PublicToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        StartsAtUtc = RestaurantClock.ToUtc(date, time, TimeZoneId),
        FreeCancellationUntilUtc = RestaurantClock.ToUtc(date, time, TimeZoneId).AddHours(-new VitrineProfile().FreeCancellationHours),
        ServiceName = service, PartySize = party, CustomerName = name, CustomerEmail = email, CustomerPhone = phone,
        Status = status, CreatedAt = createdAt,
        ConfirmedAt = status is ReservationStatus.Confirmed or ReservationStatus.Cancelled ? createdAt.AddMinutes(3) : null,
        AmountDue = amountDue
    };
}
