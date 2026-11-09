using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class VitrineServiceTests(PostgresFixture postgres)
{
    private static readonly byte[] Bytes = [1, 2, 3, 4];

    private static readonly DateTimeOffset Today = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static VitrineService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Today));

    private static VitrineProfileInput ValidInput(Func<VitrineProfileInput, VitrineProfileInput>? change = null)
    {
        var input = new VitrineProfileInput(
            true, "Cuisine de saison", "Ambiance chaleureuse", "#C0392B", "Bruxelles", "+32 2 123 45 67", "contact@bistro.test",
            "Europe/Brussels", true, 6, 12m, 0.25m, 48, 180, 90, 30, 20);
        return change is null ? input : change(input);
    }

    private static ServiceScheduleInput Schedule(DayOfWeek day, string name, int startHour, int endHour, int capacity = 40, int slot = 30) =>
        new(day, name, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0), capacity, slot);

    private async Task<(DbContextOptions<ChefOsDbContext> Options, int Restaurant, VitrineService Service)> SetUpAsync(string name = "Bistro")
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, name);
        return (options, restaurant, CreateService(options, restaurant));
    }

    // ── Profil ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Profile_DefaultsCreatedOnce()
    {
        var (options, restaurant, service) = await SetUpAsync();

        var first = await service.GetOrCreateProfileAsync();
        var second = await service.GetOrCreateProfileAsync();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(restaurant, first.RestaurantId);
        Assert.False(first.IsPublished);
        Assert.Equal(5, first.GroupThreshold);
        Assert.Equal(10m, first.DepositPerPerson);
        Assert.Equal(0.30m, first.PrepaymentPercent);
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurant));
        Assert.Equal(1, await db.VitrineProfiles.CountAsync());
    }

    [Fact]
    public async Task SaveProfile_PersistsFieldsAndTimeZone()
    {
        var (options, restaurant, service) = await SetUpAsync();

        await service.SaveProfileAsync(ValidInput(i => i with { Tagline = "  Belle table  ", Phone = " ", TimeZoneId = "Europe/Paris" }));

        var profile = await service.GetOrCreateProfileAsync();
        Assert.True(profile.IsPublished);
        Assert.Equal("Belle table", profile.Tagline);
        Assert.Null(profile.Phone);
        Assert.Equal("#C0392B", profile.AccentColor);
        Assert.Equal(6, profile.GroupThreshold);
        Assert.Equal(0.25m, profile.PrepaymentPercent);
        Assert.Equal(20, profile.PendingPaymentMinutes);
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        Assert.Equal("Europe/Paris", (await db.Restaurants.SingleAsync(r => r.Id == restaurant)).TimeZoneId);
    }

    [Theory]
    [InlineData("accent", "rouge", "La couleur d'accent doit être au format #RRGGBB")]
    [InlineData("accent", "#12345", "La couleur d'accent doit être au format #RRGGBB")]
    [InlineData("percent", "1.5", "Le pourcentage de prépaiement doit être compris entre 0 et 100 %")]
    [InlineData("percent", "-0.1", "Le pourcentage de prépaiement doit être compris entre 0 et 100 %")]
    [InlineData("deposit", "-1", "L'acompte ne peut pas être négatif")]
    [InlineData("threshold", "0", "Le seuil de groupe doit être d'au moins 1 personne")]
    [InlineData("max", "101", "La taille maximale d'un groupe doit être comprise entre le seuil de groupe et 100")]
    [InlineData("max", "5", "La taille maximale d'un groupe doit être comprise entre le seuil de groupe et 100")]
    [InlineData("cancel", "-1", "Le délai d'annulation gratuite ne peut pas être négatif")]
    [InlineData("lead", "-1", "Le délai minimal avant réservation ne peut pas être négatif")]
    [InlineData("ahead", "-1", "Le nombre de jours de réservation à l'avance ne peut pas être négatif")]
    [InlineData("pending", "-1", "Le délai de paiement doit être compris entre 10 et 120 minutes")]
    [InlineData("pending", "9", "Le délai de paiement doit être compris entre 10 et 120 minutes")]
    [InlineData("pending", "121", "Le délai de paiement doit être compris entre 10 et 120 minutes")]
    [InlineData("tz", "Mars/Olympus", "Fuseau horaire inconnu")]
    public async Task SaveProfile_Validates(string field, string value, string message)
    {
        var (_, _, service) = await SetUpAsync();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var input = field switch
        {
            "accent" => ValidInput(i => i with { AccentColor = value }),
            "percent" => ValidInput(i => i with { PrepaymentPercent = decimal.Parse(value, ci) }),
            "deposit" => ValidInput(i => i with { DepositPerPerson = decimal.Parse(value, ci) }),
            "threshold" => ValidInput(i => i with { GroupThreshold = int.Parse(value, ci) }),
            "max" => ValidInput(i => i with { MaxPartySize = int.Parse(value, ci) }),
            "cancel" => ValidInput(i => i with { FreeCancellationHours = int.Parse(value, ci) }),
            "lead" => ValidInput(i => i with { MinLeadMinutes = int.Parse(value, ci) }),
            "ahead" => ValidInput(i => i with { MaxDaysAhead = int.Parse(value, ci) }),
            "pending" => ValidInput(i => i with { PendingPaymentMinutes = int.Parse(value, ci) }),
            _ => ValidInput(i => i with { TimeZoneId = value })
        };

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveProfileAsync(input));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public async Task SaveProfile_PendingPaymentMinutes_BoundariesAreAccepted()
    {
        var (_, _, service) = await SetUpAsync();

        await service.SaveProfileAsync(ValidInput(i => i with { PendingPaymentMinutes = 10 }));
        await service.SaveProfileAsync(ValidInput(i => i with { PendingPaymentMinutes = 120 }));

        Assert.Equal(120, (await service.GetOrCreateProfileAsync()).PendingPaymentMinutes);
    }

    [Fact]
    public async Task SaveProfile_BoundariesAreAccepted_AndInvalidSaveChangesNothing()
    {
        var (_, _, service) = await SetUpAsync();
        await service.SaveProfileAsync(ValidInput(i => i with { GroupThreshold = 1, MaxPartySize = 1, PrepaymentPercent = 0m, AccentColor = null }));
        await service.SaveProfileAsync(ValidInput(i => i with { GroupThreshold = 5, MaxPartySize = 100, PrepaymentPercent = 1m, DepositPerPerson = 0m }));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveProfileAsync(ValidInput(i => i with { Tagline = "Nouveau", MaxPartySize = 101 })));

        var profile = await service.GetOrCreateProfileAsync();

        Assert.Equal("Cuisine de saison", profile.Tagline);
        Assert.Equal(100, profile.MaxPartySize);
    }

    // ── Images ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Images_TypeSizeAndLimit()
    {
        var (_, _, service) = await SetUpAsync();

        var type = await Assert.ThrowsAsync<DomainValidationException>(() => service.AddImageAsync(Bytes, "a.gif", "image/gif", false));
        Assert.Equal("Format non pris en charge (JPEG, PNG ou WebP)", type.Message);
        var size = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.AddImageAsync(new byte[5 * 1024 * 1024 + 1], "a.jpg", "image/jpeg", false));
        Assert.Equal("Image trop volumineuse (5 Mo maximum)", size.Message);
        var empty = await Assert.ThrowsAsync<DomainValidationException>(() => service.AddImageAsync([], "a.jpg", "image/jpeg", false));
        Assert.Equal("Image vide", empty.Message);

        await service.AddImageAsync(new byte[5 * 1024 * 1024], "limit.jpg", "IMAGE/JPEG", true);
        for (var i = 0; i < 6; i++)
            await service.AddImageAsync(Bytes, $"g{i}.png", "image/png", false);

        var tooMany = await Assert.ThrowsAsync<DomainValidationException>(() => service.AddImageAsync(Bytes, "g7.png", "image/png", false));
        Assert.Equal("7 images au maximum (1 couverture et 6 photos de galerie)", tooMany.Message);
        Assert.Equal(7, (await service.GetImagesAsync()).Count);

        // Remplacer la couverture reste possible à 7 images.
        await service.AddImageAsync(Bytes, "new-cover.webp", "image/webp", true);
        Assert.Equal(7, (await service.GetImagesAsync()).Count);
    }

    [Fact]
    public async Task Images_NewCoverReplacesTheOldOne_AndUpdatesTheProfile()
    {
        var (_, _, service) = await SetUpAsync();
        var first = await service.AddImageAsync(Bytes, "c1.jpg", "image/jpeg", true);
        var second = await service.AddImageAsync(Bytes, "c2.jpg", "image/jpeg", true);

        var images = await service.GetImagesAsync();
        var image = Assert.Single(images);
        Assert.Equal(second, image.Id);
        Assert.True(image.IsCover);
        Assert.NotEqual(first, second);
        Assert.Equal(second, (await service.GetOrCreateProfileAsync()).CoverImageId);
    }

    [Fact]
    public async Task Images_RemoveAndMove_KeepPositionsCompact()
    {
        var (_, _, service) = await SetUpAsync();
        var cover = await service.AddImageAsync(Bytes, "c.jpg", "image/jpeg", true);
        var a = await service.AddImageAsync(Bytes, "a.jpg", "image/jpeg", false);
        var b = await service.AddImageAsync(Bytes, "b.jpg", "image/jpeg", false);
        var c = await service.AddImageAsync(Bytes, "c.jpg", "image/jpeg", false);

        Assert.Equal([a, b, c], Gallery(await service.GetImagesAsync()));
        Assert.Equal([0, 1, 2], (await service.GetImagesAsync()).Where(i => !i.IsCover).Select(i => i.Position));

        await service.MoveImageAsync(c, 0);
        Assert.Equal([c, a, b], Gallery(await service.GetImagesAsync()));

        await service.RemoveImageAsync(a);
        var afterRemove = await service.GetImagesAsync();
        Assert.Equal([c, b], Gallery(afterRemove));
        Assert.Equal([0, 1], afterRemove.Where(i => !i.IsCover).Select(i => i.Position));

        await service.RemoveImageAsync(cover);
        Assert.Null((await service.GetOrCreateProfileAsync()).CoverImageId);

        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => service.RemoveImageAsync(cover));
        Assert.Equal("Image introuvable", unknown.Message);
        var outOfRange = await Assert.ThrowsAsync<DomainValidationException>(() => service.MoveImageAsync(c, 2));
        Assert.Equal("Position invalide", outOfRange.Message);
        var coverMove = await Assert.ThrowsAsync<DomainValidationException>(() => service.MoveImageAsync(b, -1));
        Assert.Equal("Position invalide", coverMove.Message);
    }

    private static int[] Gallery(IReadOnlyList<VitrineImage> images) => images.Where(i => !i.IsCover).OrderBy(i => i.Position).Select(i => i.Id).ToArray();

    // ── Services ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schedules_ReplaceAllOrNothing_AndRejectOverlap()
    {
        var (_, _, service) = await SetUpAsync();
        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Monday, "Midi", 12, 14), Schedule(DayOfWeek.Monday, "Soir", 18, 22)]);
        Assert.Equal(["Midi", "Soir"], (await service.GetSchedulesAsync()).Select(s => s.Name));

        // Remplacement complet.
        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Dîner", 19, 23, 50, 15)]);
        var only = Assert.Single(await service.GetSchedulesAsync());
        Assert.Equal((DayOfWeek.Friday, "Dîner", 50, 15), (only.DayOfWeek, only.Name, only.Capacity, only.SlotMinutes));

        // Chevauchement : tout ou rien.
        var overlap = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveSchedulesAsync(
            [Schedule(DayOfWeek.Monday, "Midi", 12, 14), Schedule(DayOfWeek.Monday, "Soir", 13, 22)]));
        Assert.Equal("Les services Midi et Soir du lundi se chevauchent", overlap.Message);
        Assert.Equal("Dîner", Assert.Single(await service.GetSchedulesAsync()).Name);

        // Bornes qui se touchent : acceptées ; autres jours : pas de comparaison.
        await service.SaveSchedulesAsync(
            [Schedule(DayOfWeek.Monday, "Midi", 12, 14), Schedule(DayOfWeek.Monday, "Soir", 14, 22), Schedule(DayOfWeek.Tuesday, "Midi", 12, 14)]);
        Assert.Equal(3, (await service.GetSchedulesAsync()).Count);
    }

    [Theory]
    [InlineData(14, 12, 40, 30, "Le service Midi doit se terminer après son début")]
    [InlineData(12, 12, 40, 30, "Le service Midi doit se terminer après son début")]
    [InlineData(12, 14, 0, 30, "La capacité du service Midi doit être comprise entre 1 et 500")]
    [InlineData(12, 14, 501, 30, "La capacité du service Midi doit être comprise entre 1 et 500")]
    [InlineData(12, 14, 40, 20, "Le pas des créneaux du service Midi doit être de 15, 30 ou 60 minutes")]
    public async Task Schedules_ValidateEachService(int start, int end, int capacity, int slot, string message)
    {
        var (_, _, service) = await SetUpAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveSchedulesAsync([Schedule(DayOfWeek.Monday, "Midi", start, end, capacity, slot)]));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public async Task Schedules_BlankOrDuplicateName_Throws()
    {
        var (_, _, service) = await SetUpAsync();

        var blank = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveSchedulesAsync([Schedule(DayOfWeek.Monday, " ", 12, 14)]));
        Assert.Equal("Le nom du service est obligatoire", blank.Message);
        var duplicate = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveSchedulesAsync(
            [Schedule(DayOfWeek.Monday, "Midi", 12, 13), Schedule(DayOfWeek.Monday, "midi", 15, 16)]));
        Assert.Equal("Deux services du lundi portent le même nom : Midi", duplicate.Message);
    }

    // ── Fermetures ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Closures_UniquePerDate()
    {
        var (_, _, service) = await SetUpAsync();
        var date = new DateOnly(2026, 12, 25);

        var id = await service.AddClosureAsync(date, "  Noël ");
        var duplicate = await Assert.ThrowsAsync<DomainValidationException>(() => service.AddClosureAsync(date, null));
        await service.AddClosureAsync(new DateOnly(2026, 1, 1), " ");

        Assert.Equal("Ce jour est déjà fermé", duplicate.Message);
        var closures = await service.GetClosuresAsync();
        Assert.Equal([new DateOnly(2026, 1, 1), date], closures.Select(c => c.DateLocal));
        Assert.Null(closures[0].Reason);
        Assert.Equal("Noël", closures[1].Reason);

        await service.RemoveClosureAsync(id);
        Assert.Single(await service.GetClosuresAsync());
        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => service.RemoveClosureAsync(id));
        Assert.Equal("Fermeture introuvable", unknown.Message);
    }

    // ── Lecture publique ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetPublic_OnlyPublishedDishesAndWines_GroupedWithAllergens()
    {
        var (options, restaurant, service) = await SetUpAsync("Le Bistro");
        await service.SaveProfileAsync(ValidInput());
        var cover = await service.AddImageAsync(Bytes, "c.jpg", "image/jpeg", true);
        var g1 = await service.AddImageAsync(Bytes, "g1.jpg", "image/jpeg", false);
        var g2 = await service.AddImageAsync(Bytes, "g2.jpg", "image/jpeg", false);

        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(restaurant)))
        {
            var milk = new Ingredient { Name = "Lait", Category = "Crèmerie", Allergens = EuAllergen.Milk, BaseUnit = BaseUnit.Milliliter, YieldPercent = 100m };
            var egg = new Ingredient { Name = "Œuf", Category = "Crèmerie", Allergens = EuAllergen.Eggs, BaseUnit = BaseUnit.Piece, YieldPercent = 100m };
            var sauce = new Recipe
            {
                Name = "Sauce", Kind = RecipeKind.Preparation, OutputQuantity = 1000m, OutputUnit = BaseUnit.Milliliter,
                Lines = [new RecipeLine { Ingredient = egg, Quantity = 2m, Unit = MeasureUnit.Piece }]
            };
            db.Recipes.AddRange(
                sauce,
                Dish("Tarte", "Desserts", 9m, show: true, lines: [new RecipeLine { Ingredient = milk, Quantity = 100m, Unit = MeasureUnit.Ml }], description: "Maison"),
                Dish("Crème", "Desserts", 7m, show: true, lines: [new RecipeLine { SubRecipe = sauce, Quantity = 100m, Unit = MeasureUnit.Ml }]),
                Dish("Steak", "Plats", 24m, show: true),
                Dish("Pain", null, 3m, show: true),
                Dish("Caché", "Plats", 10m, show: false),
                Dish("Archivé", "Plats", 10m, show: true, archived: true),
                Dish("Sans prix", "Plats", null, show: true));
            db.WineListItems.AddRange(
                Wine("Pomerol", 2018, 3, true, 60m, null),
                Wine("Chablis", 2022, 1, true, 45m, 8m),
                Wine("Brouillon", 2020, 0, false, 30m, null),
                Wine("Rosé", null, 2, true, 28m, 6m),
                Wine("Cava", null, 4, true, 25m, 5m),
                Wine("Hors aperçu", null, 5, true, 99m, null));
            await db.SaveChangesAsync();
        }

        var result = await service.GetPublicAsync();

        Assert.NotNull(result);
        Assert.Equal("Le Bistro", result.RestaurantName);
        Assert.Equal("Cuisine de saison", result.Tagline);
        Assert.Equal("#C0392B", result.AccentColor);
        Assert.Equal(cover, result.CoverImageId);
        Assert.Equal([g1, g2], result.GalleryImageIds);
        Assert.Equal(["Desserts", "Plats", "Autres"], result.Menu.Select(c => c.Name));
        Assert.Equal(["Crème", "Tarte"], result.Menu[0].Dishes.Select(d => d.Name));
        Assert.Equal(EuAllergen.Eggs, result.Menu[0].Dishes[0].Allergens);
        var tarte = result.Menu[0].Dishes[1];
        Assert.Equal((EuAllergen.Milk, 9m, "Maison"), (tarte.Allergens, tarte.PriceInclVat, tarte.Description));
        Assert.Equal(["Steak"], result.Menu[1].Dishes.Select(d => d.Name));
        Assert.Equal(["Pain"], result.Menu[2].Dishes.Select(d => d.Name));
        Assert.Equal(["Chablis", "Rosé", "Pomerol"], result.Wines.Select(w => w.Name));
        Assert.Equal((2022, 45m, 8m), (result.Wines[0].Vintage, result.Wines[0].PriceBottle, result.Wines[0].PriceGlass));
    }

    [Fact]
    public async Task GetPublic_Unpublished_Null()
    {
        var (_, _, service) = await SetUpAsync();

        Assert.Null(await service.GetPublicAsync());
        await service.SaveProfileAsync(ValidInput(i => i with { IsPublished = false }));
        Assert.Null(await service.GetPublicAsync());
        await service.SaveProfileAsync(ValidInput());
        Assert.NotNull(await service.GetPublicAsync());
    }

    [Fact]
    public async Task GetPublicImage_RequiresAPublishedVitrine()
    {
        var (_, _, service) = await SetUpAsync();
        var id = await service.AddImageAsync(Bytes, "c.jpg", "image/jpeg", true);

        Assert.Null(await service.GetPublicImageAsync(id));
        Assert.NotNull(await service.GetImageAsync(id));
        await service.SaveProfileAsync(ValidInput());
        var image = await service.GetPublicImageAsync(id);
        Assert.NotNull(image);
        Assert.Equal(Bytes, image.Data);
        Assert.Null(await service.GetPublicImageAsync(id + 1000));
    }

    [Fact]
    public async Task OtherRestaurant_Isolated()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var serviceA = CreateService(options, a);
        var serviceB = CreateService(options, b);
        await serviceA.SaveProfileAsync(ValidInput());
        var image = await serviceA.AddImageAsync(Bytes, "a.jpg", "image/jpeg", true);
        await serviceA.SaveSchedulesAsync([Schedule(DayOfWeek.Monday, "Midi", 12, 14)]);
        var closure = await serviceA.AddClosureAsync(new DateOnly(2026, 12, 25), null);

        Assert.False((await serviceB.GetOrCreateProfileAsync()).IsPublished);
        Assert.Null(await serviceB.GetPublicAsync());
        Assert.Empty(await serviceB.GetImagesAsync());
        Assert.Null(await serviceB.GetImageAsync(image));
        Assert.Null(await serviceB.GetPublicImageAsync(image));
        Assert.Empty(await serviceB.GetSchedulesAsync());
        Assert.Empty(await serviceB.GetClosuresAsync());
        await Assert.ThrowsAsync<DomainValidationException>(() => serviceB.RemoveImageAsync(image));
        await Assert.ThrowsAsync<DomainValidationException>(() => serviceB.RemoveClosureAsync(closure));
        Assert.NotNull(await serviceA.GetPublicAsync());
    }

    private static Recipe Dish(string name, string? category, decimal? price, bool show, bool archived = false, List<RecipeLine>? lines = null, string? description = null) => new()
    {
        Name = name, Kind = RecipeKind.Dish, Category = category, SellingPriceInclVat = price, VatRate = 0.12m,
        ShowOnVitrine = show, IsArchived = archived, PublicDescription = description, Lines = lines ?? []
    };

    private static WineListItem Wine(string name, int? vintage, int position, bool published, decimal bottle, decimal? glass) => new()
    {
        Name = name, Vintage = vintage, Position = position, IsPublished = published, PriceBottle = bottle, PriceGlass = glass,
        Region = "Bordeaux", Country = "France", Color = WineColor.Rouge
    };

    // ── Services et réservations à venir ─────────────────────────────────────

    private static async Task AddReservationAsync(DbContextOptions<ChefOsDbContext> options, int restaurant, string service, DateTime startsAtUtc,
        ReservationStatus status = ReservationStatus.Confirmed, DateTime? createdAt = null)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurant));
        db.Reservations.Add(new Reservation
        {
            PublicToken = Guid.NewGuid().ToString("N"), StartsAtUtc = startsAtUtc, ServiceName = service, PartySize = 2, CustomerName = "Dupont",
            CustomerEmail = "d@example.com", CustomerPhone = "0470 12 34 56", Status = status, CreatedAt = createdAt ?? Today.UtcDateTime,
            FreeCancellationUntilUtc = startsAtUtc.AddHours(-24)
        });
        await db.SaveChangesAsync();
    }

    private static readonly DateTime NextFridayDinner = new(2026, 11, 6, 18, 0, 0, DateTimeKind.Utc); // vendredi 19:00 à Bruxelles

    [Fact]
    public async Task SaveSchedules_RenamingAServiceWithUpcomingBookings_Refused()
    {
        var (options, restaurant, service) = await SetUpAsync();
        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Soir", 19, 22), Schedule(DayOfWeek.Saturday, "Soir", 19, 22)]);
        await AddReservationAsync(options, restaurant, "Soir", NextFridayDinner);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Dîner", 19, 22), Schedule(DayOfWeek.Saturday, "Soir", 19, 22)]));

        Assert.Equal("Le service Soir du vendredi a des réservations à venir : modifiez-le après ces dates ou gardez son nom", ex.Message);
        Assert.Equal(["Soir", "Soir"], (await service.GetSchedulesAsync()).Select(s => s.Name));
    }

    [Fact]
    public async Task SaveSchedules_RemovingAServiceWithUpcomingBookings_Refused()
    {
        var (options, restaurant, service) = await SetUpAsync();
        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Soir", 19, 22)]);
        await AddReservationAsync(options, restaurant, "Soir", NextFridayDinner, ReservationStatus.Seated);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveSchedulesAsync([Schedule(DayOfWeek.Saturday, "Soir", 19, 22)]));

        Assert.Contains("a des réservations à venir", ex.Message);
        Assert.Single(await service.GetSchedulesAsync());
    }

    [Fact]
    public async Task SaveSchedules_PendingPaymentStillValid_BlocksRename_ButAStaleOneDoesNot()
    {
        var (options, restaurant, service) = await SetUpAsync();
        await service.SaveProfileAsync(ValidInput(i => i with { PendingPaymentMinutes = 30 }));
        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Soir", 19, 22)]);
        await AddReservationAsync(options, restaurant, "Soir", NextFridayDinner, ReservationStatus.PendingPayment, Today.UtcDateTime.AddMinutes(-31));

        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Dîner", 19, 22)]); // périmée : elle ne tient plus de places

        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Soir", 19, 22)]);
        await AddReservationAsync(options, restaurant, "Soir", NextFridayDinner, ReservationStatus.PendingPayment, Today.UtcDateTime.AddMinutes(-5));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Dîner", 19, 22)]));
    }

    [Fact]
    public async Task SaveSchedules_RenameAllowed_WhenNothingUpcomingHoldsSeats()
    {
        var (options, restaurant, service) = await SetUpAsync();
        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Soir", 19, 22)]);
        await AddReservationAsync(options, restaurant, "Soir", NextFridayDinner.AddDays(-14)); // passée
        await AddReservationAsync(options, restaurant, "Soir", NextFridayDinner, ReservationStatus.Cancelled);
        await AddReservationAsync(options, restaurant, "Soir", NextFridayDinner, ReservationStatus.Expired);
        await AddReservationAsync(options, restaurant, "Midi", NextFridayDinner); // un autre service

        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Dîner", 19, 22)]);

        Assert.Equal("Dîner", (await service.GetSchedulesAsync()).Single().Name);
    }

    [Fact]
    public async Task SaveSchedules_CapacityAndTimeChangesAllowed_OnAKeptName()
    {
        var (options, restaurant, service) = await SetUpAsync();
        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Soir", 19, 22, capacity: 40)]);
        await AddReservationAsync(options, restaurant, "Soir", NextFridayDinner);

        await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Soir", 18, 23, capacity: 60, slot: 15)]);

        var saved = Assert.Single(await service.GetSchedulesAsync());
        Assert.Equal(60, saved.Capacity);
        Assert.Equal(new TimeOnly(18, 0), saved.StartLocal);
    }

    [Fact]
    public async Task SaveSchedules_UsesTheCurrentRestaurantsTimeZone_WhenAnotherRestaurantSharesTheDatabase()
    {
        var options = await postgres.CreateDatabaseAsync();
        var auckland = await TestData.CreateRestaurantAsync(options, "Auckland");
        var brussels = await TestData.CreateRestaurantAsync(options, "Bruxelles");
        var aucklandService = CreateService(options, auckland);
        var brusselsService = CreateService(options, brussels);
        await aucklandService.SaveProfileAsync(ValidInput(i => i with { TimeZoneId = "Pacific/Auckland" }));
        await brusselsService.SaveProfileAsync(ValidInput(i => i with { TimeZoneId = "Europe/Brussels" }));

        // Le même instant tombe un vendredi à 22:30 à Bruxelles et un samedi à 10:30 à Auckland.
        var lateFriday = new DateTime(2026, 11, 6, 21, 30, 0, DateTimeKind.Utc);
        foreach (var (service, restaurant) in new[] { (aucklandService, auckland), (brusselsService, brussels) })
        {
            await service.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Soir", 19, 23)]);
            await AddReservationAsync(options, restaurant, "Soir", lateFriday);
        }

        // Bruxelles : la réservation est un vendredi, le service du vendredi ne peut pas changer de nom.
        await Assert.ThrowsAsync<DomainValidationException>(() => brusselsService.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Dîner", 19, 23)]));
        // Auckland : la même réservation est un samedi, le service du vendredi peut changer de nom.
        await aucklandService.SaveSchedulesAsync([Schedule(DayOfWeek.Friday, "Dîner", 19, 23)]);

        Assert.Equal("Soir", (await brusselsService.GetSchedulesAsync()).Single().Name);
        Assert.Equal("Dîner", (await aucklandService.GetSchedulesAsync()).Single().Name);
    }
}
