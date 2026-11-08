using System.Text.RegularExpressions;
using ChefOS.Core.Common;
using ChefOS.Core.Costing;
using ChefOS.Core.Recipes;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Vitrine publique du restaurant courant : profil et règles de réservation, images, services récurrents, jours de fermeture,
/// et lecture publique (<see cref="GetPublicAsync"/>, utilisée par le site public dont le contexte est déjà scopé au restaurant par son lien).
/// Les écritures sont tout ou rien. Le restaurant courant n'est pas connu du service : il se déduit du profil, créé au besoin
/// (le contexte filtré par restaurant l'estampille).
/// </summary>
public partial class VitrineService(IDbContextFactory<ChefOsDbContext> factory, TimeProvider? clock = null)
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxGalleryImages = 6;
    private const int MaxImages = MaxGalleryImages + 1;
    private const int PreviewWines = 3;
    private const string OtherCategory = "Autres";

    private const string UnsupportedImageMessage = "Format non pris en charge (JPEG, PNG ou WebP)";
    private const string TooLargeImageMessage = "Image trop volumineuse (5 Mo maximum)";
    private const string EmptyImageMessage = "Image vide";
    private const string TooManyImagesMessage = "7 images au maximum (1 couverture et 6 photos de galerie)";
    private const string UnknownImageMessage = "Image introuvable";
    private const string InvalidPositionMessage = "Position invalide";
    private const string UnknownClosureMessage = "Fermeture introuvable";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private static readonly HashSet<string> AcceptedImageTypes = new(StringComparer.Ordinal) { "image/jpeg", "image/png", "image/webp" };

    private static readonly string[] DayNames =
        ["dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi"];

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z")]
    private static partial Regex AccentColorPattern();

    // ── Profil ───────────────────────────────────────────────────────────────

    /// <summary>Profil du restaurant courant, créé avec les valeurs par défaut s'il n'existe pas (un seul par restaurant).</summary>
    public async Task<VitrineProfile> GetOrCreateProfileAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        return await RequireProfileAsync(db, track: false);
    }

    /// <summary>Profil du restaurant courant sans le créer : null tant que le chef n'a rien réglé.</summary>
    public async Task<VitrineProfile?> FindProfileAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.VitrineProfiles.AsNoTracking().FirstOrDefaultAsync();
    }

    /// <summary>Enregistre les réglages validés ; le fuseau horaire est porté par le restaurant.</summary>
    public async Task SaveProfileAsync(VitrineProfileInput input)
    {
        Validate(input);

        await using var db = await factory.CreateDbContextAsync();

        var profile = await RequireProfileAsync(db, track: true);
        var restaurant = await db.Restaurants.FirstAsync(r => r.Id == profile.RestaurantId);

        profile.IsPublished = input.IsPublished;
        profile.Tagline = NullIfBlank(input.Tagline);
        profile.AmbianceText = NullIfBlank(input.AmbianceText);
        profile.AccentColor = NullIfBlank(input.AccentColor);
        profile.City = NullIfBlank(input.City);
        profile.Phone = NullIfBlank(input.Phone);
        profile.PublicEmail = NullIfBlank(input.PublicEmail);
        profile.ReservationsOpen = input.ReservationsOpen;
        profile.GroupThreshold = input.GroupThreshold;
        profile.DepositPerPerson = Math.Round(input.DepositPerPerson, 2, MidpointRounding.AwayFromZero);
        profile.PrepaymentPercent = Math.Round(input.PrepaymentPercent, 4, MidpointRounding.AwayFromZero);
        profile.FreeCancellationHours = input.FreeCancellationHours;
        profile.MinLeadMinutes = input.MinLeadMinutes;
        profile.MaxDaysAhead = input.MaxDaysAhead;
        profile.MaxPartySize = input.MaxPartySize;
        profile.PendingPaymentMinutes = input.PendingPaymentMinutes;
        restaurant.TimeZoneId = input.TimeZoneId.Trim();

        await db.SaveChangesAsync();
    }

    private static void Validate(VitrineProfileInput input)
    {
        if (NullIfBlank(input.AccentColor) is { } color && !AccentColorPattern().IsMatch(color))
            throw new DomainValidationException("La couleur d'accent doit être au format #RRGGBB");
        if (NullIfBlank(input.Tagline) is { Length: > 200 })
            throw new DomainValidationException("L'accroche ne peut pas dépasser 200 caractères");
        if (input.PrepaymentPercent is < 0m or > 1m)
            throw new DomainValidationException("Le pourcentage de prépaiement doit être compris entre 0 et 100 %");
        if (input.DepositPerPerson < 0m)
            throw new DomainValidationException("L'acompte ne peut pas être négatif");
        if (input.GroupThreshold < 1)
            throw new DomainValidationException("Le seuil de groupe doit être d'au moins 1 personne");
        if (input.MaxPartySize < input.GroupThreshold || input.MaxPartySize > 100)
            throw new DomainValidationException("La taille maximale d'un groupe doit être comprise entre le seuil de groupe et 100");
        if (input.FreeCancellationHours < 0)
            throw new DomainValidationException("Le délai d'annulation gratuite ne peut pas être négatif");
        if (input.MinLeadMinutes < 0)
            throw new DomainValidationException("Le délai minimal avant réservation ne peut pas être négatif");
        if (input.MaxDaysAhead < 0)
            throw new DomainValidationException("Le nombre de jours de réservation à l'avance ne peut pas être négatif");
        if (input.PendingPaymentMinutes is < 10 or > 120)
            throw new DomainValidationException("Le délai de paiement doit être compris entre 10 et 120 minutes");

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(input.TimeZoneId?.Trim() ?? "");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new DomainValidationException("Fuseau horaire inconnu");
        }
    }

    /// <summary>Le profil du restaurant courant ; créé s'il manque. Une création concurrente (index unique) se résout en relisant le profil.</summary>
    private static async Task<VitrineProfile> RequireProfileAsync(ChefOsDbContext db, bool track)
    {
        var query = track ? db.VitrineProfiles : db.VitrineProfiles.AsNoTracking();
        if (await query.FirstOrDefaultAsync() is { } existing)
            return existing;

        var created = new VitrineProfile();
        db.VitrineProfiles.Add(created);
        try
        {
            await db.SaveChangesAsync();
            return created;
        }
        catch (DbUpdateException)
        {
            db.Entry(created).State = EntityState.Detached;
            return await query.FirstAsync();
        }
    }

    // ── Images ───────────────────────────────────────────────────────────────

    /// <summary>Images du restaurant (sans les octets), la couverture d'abord puis la galerie dans l'ordre.</summary>
    public async Task<IReadOnlyList<VitrineImage>> GetImagesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.VitrineImages.AsNoTracking()
            .OrderByDescending(i => i.IsCover).ThenBy(i => i.Position).ThenBy(i => i.Id)
            .Select(i => new VitrineImage
            {
                Id = i.Id, RestaurantId = i.RestaurantId, FileName = i.FileName, ContentType = i.ContentType,
                Position = i.Position, IsCover = i.IsCover
            })
            .ToListAsync();
    }

    /// <summary>
    /// Ajoute une image et renvoie son identifiant : 7 au plus (1 couverture et 6 de galerie). Une nouvelle couverture remplace
    /// l'ancienne (supprimée) ; une image de galerie prend la dernière position.
    /// </summary>
    public async Task<int> AddImageAsync(byte[] data, string fileName, string contentType, bool isCover)
    {
        var type = contentType.Trim().ToLowerInvariant();
        if (!AcceptedImageTypes.Contains(type))
            throw new DomainValidationException(UnsupportedImageMessage);
        if (data.Length == 0)
            throw new DomainValidationException(EmptyImageMessage);
        if (data.Length > MaxImageBytes)
            throw new DomainValidationException(TooLargeImageMessage);

        await using var db = await factory.CreateDbContextAsync();

        var profile = await RequireProfileAsync(db, track: true);
        var existing = await db.VitrineImages.Select(i => new { i.Id, i.IsCover }).ToListAsync();
        var oldCover = existing.FirstOrDefault(i => i.IsCover);
        var galleryCount = existing.Count(i => !i.IsCover);

        if (isCover ? existing.Count - (oldCover is null ? 0 : 1) + 1 > MaxImages : galleryCount >= MaxGalleryImages)
            throw new DomainValidationException(TooManyImagesMessage);

        var image = new VitrineImage
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? "image" : fileName.Trim(),
            ContentType = type,
            Data = data,
            IsCover = isCover,
            Position = isCover ? 0 : galleryCount
        };

        // L'identifiant de la nouvelle image n'existe qu'après le premier enregistrement : une transaction garde le tout indivisible.
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.VitrineImages.Add(image);
        await db.SaveChangesAsync();

        if (isCover)
        {
            profile.CoverImageId = image.Id;
            await db.SaveChangesAsync();
            if (oldCover is not null)
                await db.VitrineImages.Where(i => i.Id == oldCover.Id).ExecuteDeleteAsync();
        }

        await transaction.CommitAsync();
        return image.Id;
    }

    /// <summary>Supprime une image ; les positions de la galerie restent consécutives (0, 1, 2…). Une couverture supprimée laisse le profil sans couverture.</summary>
    public async Task RemoveImageAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        var image = await db.VitrineImages.FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new DomainValidationException(UnknownImageMessage);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var wasCover = image.IsCover;
        db.VitrineImages.Remove(image);
        await db.SaveChangesAsync();

        if (!wasCover)
            await CompactGalleryAsync(db);

        await transaction.CommitAsync();
    }

    /// <summary>Déplace une image de la galerie à <paramref name="newPosition"/> (0 = première) ; les autres se décalent.</summary>
    public async Task MoveImageAsync(int id, int newPosition)
    {
        await using var db = await factory.CreateDbContextAsync();

        var gallery = await db.VitrineImages.Where(i => !i.IsCover).OrderBy(i => i.Position).ThenBy(i => i.Id).ToListAsync();
        var image = gallery.FirstOrDefault(i => i.Id == id)
            ?? throw new DomainValidationException(UnknownImageMessage);
        if (newPosition < 0 || newPosition >= gallery.Count)
            throw new DomainValidationException(InvalidPositionMessage);

        gallery.Remove(image);
        gallery.Insert(newPosition, image);
        for (var position = 0; position < gallery.Count; position++)
            gallery[position].Position = position;

        await db.SaveChangesAsync();
    }

    private static async Task CompactGalleryAsync(ChefOsDbContext db)
    {
        var gallery = await db.VitrineImages.Where(i => !i.IsCover).OrderBy(i => i.Position).ThenBy(i => i.Id).ToListAsync();
        for (var position = 0; position < gallery.Count; position++)
            gallery[position].Position = position;

        await db.SaveChangesAsync();
    }

    /// <summary>Une image du restaurant courant (octets compris), publiée ou non : pour l'aperçu du back.</summary>
    public async Task<VitrineImage?> GetImageAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.VitrineImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
    }

    /// <summary>Une image du restaurant courant, seulement si sa vitrine est publiée ; null sinon.</summary>
    public async Task<VitrineImage?> GetPublicImageAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        if (!await db.VitrineProfiles.AnyAsync(p => p.IsPublished))
            return null;

        return await db.VitrineImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
    }

    // ── Services ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ServiceSchedule>> GetSchedulesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        var all = await db.ServiceSchedules.AsNoTracking().ToListAsync();
        return all.OrderBy(s => DayOrder(s.DayOfWeek)).ThenBy(s => s.StartLocal).ThenBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Remplace tous les services du restaurant, tout ou rien : un seul service invalide laisse les services actuels intacts. Les places réservées sont rattachées
    /// au service par son nom : renommer ou supprimer un service (jour + nom) qui a des réservations à venir (confirmées, installées, ou en attente de paiement encore
    /// valides) est refusé, car ses couverts seraient revendus. Changer la capacité ou les horaires d'un service qui garde son nom reste permis.
    /// </summary>
    public async Task SaveSchedulesAsync(IReadOnlyList<ServiceScheduleInput> schedules)
    {
        var normalized = schedules.Select(s => s with { Name = RequireServiceName(s.Name) }).ToList();
        foreach (var s in normalized)
        {
            if (s.EndLocal <= s.StartLocal)
                throw new DomainValidationException($"Le service {s.Name} doit se terminer après son début");
            if (s.Capacity is < 1 or > 500)
                throw new DomainValidationException($"La capacité du service {s.Name} doit être comprise entre 1 et 500");
            if (s.SlotMinutes is not (15 or 30 or 60))
                throw new DomainValidationException($"Le pas des créneaux du service {s.Name} doit être de 15, 30 ou 60 minutes");
        }

        foreach (var day in normalized.GroupBy(s => s.DayOfWeek))
        {
            var ordered = day.OrderBy(s => s.StartLocal).ThenBy(s => s.EndLocal).ToList();

            if (ordered.GroupBy(s => s.Name, StringComparer.InvariantCultureIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
                throw new DomainValidationException($"Deux services du {DayName(day.Key)} portent le même nom : {duplicate.First().Name}");

            // [début, fin[ : deux services qui se touchent (fin = début) ne se chevauchent pas.
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].StartLocal < ordered[i - 1].EndLocal)
                    throw new DomainValidationException(
                        $"Les services {ordered[i - 1].Name} et {ordered[i].Name} du {DayName(day.Key)} se chevauchent");
            }
        }

        await using var db = await factory.CreateDbContextAsync();
        await RequireProfileAsync(db, track: false);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await RefuseDisappearingServicesWithBookingsAsync(db, normalized);
        await db.ServiceSchedules.ExecuteDeleteAsync();
        db.ServiceSchedules.AddRange(normalized.Select(s => new ServiceSchedule
        {
            DayOfWeek = s.DayOfWeek, Name = s.Name, StartLocal = s.StartLocal, EndLocal = s.EndLocal,
            Capacity = s.Capacity, SlotMinutes = s.SlotMinutes
        }));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task RefuseDisappearingServicesWithBookingsAsync(ChefOsDbContext db, List<ServiceScheduleInput> next)
    {
        var kept = next.Select(s => (s.DayOfWeek, s.Name)).ToHashSet();
        var disappearing = (await db.ServiceSchedules.AsNoTracking().Select(s => new { s.DayOfWeek, s.Name }).ToListAsync())
            .Where(s => !kept.Contains((s.DayOfWeek, s.Name)))
            .ToList();
        if (disappearing.Count == 0)
            return;

        var now = _clock.GetUtcNow().UtcDateTime;
        var pendingMinutes = await db.VitrineProfiles.Select(p => (int?)p.PendingPaymentMinutes).FirstOrDefaultAsync() ?? new VitrineProfile().PendingPaymentMinutes;
        var cutoff = now.AddMinutes(-pendingMinutes);
        var timeZone = await db.Restaurants.Select(r => r.TimeZoneId).FirstAsync();
        var names = disappearing.Select(s => s.Name).Distinct().ToList();

        var upcoming = await db.Reservations.AsNoTracking()
            .Where(r => r.StartsAtUtc > now && names.Contains(r.ServiceName)
                && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.Seated
                    || (r.Status == ReservationStatus.PendingPayment && r.CreatedAt > cutoff)))
            .Select(r => new { r.ServiceName, r.StartsAtUtc })
            .ToListAsync();

        foreach (var service in disappearing)
        {
            if (upcoming.Any(r => r.ServiceName == service.Name && RestaurantClock.ToLocal(r.StartsAtUtc, timeZone).Date.DayOfWeek == service.DayOfWeek))
                throw new DomainValidationException(
                    $"Le service {service.Name} du {DayName(service.DayOfWeek)} a des réservations à venir : modifiez-le après ces dates ou gardez son nom");
        }
    }

    private static string RequireServiceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainValidationException("Le nom du service est obligatoire");
        if (name.Trim().Length > 100)
            throw new DomainValidationException("Le nom du service ne peut pas dépasser 100 caractères");

        return name.Trim();
    }

    private static int DayOrder(DayOfWeek day) => ((int)day + 6) % 7; // lundi d'abord

    private static string DayName(DayOfWeek day) => DayNames[(int)day];

    // ── Fermetures ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ClosureDay>> GetClosuresAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.ClosureDays.AsNoTracking().OrderBy(c => c.DateLocal).ToListAsync();
    }

    /// <summary>Ajoute un jour de fermeture (date locale du restaurant) ; un jour ne peut être fermé qu'une fois.</summary>
    public async Task<int> AddClosureAsync(DateOnly date, string? reason)
    {
        var trimmed = NullIfBlank(reason);
        if (trimmed is { Length: > 200 })
            throw new DomainValidationException("Le motif ne peut pas dépasser 200 caractères");

        await using var db = await factory.CreateDbContextAsync();

        if (await db.ClosureDays.AnyAsync(c => c.DateLocal == date))
            throw new DomainValidationException("Ce jour est déjà fermé");

        var closure = new ClosureDay { DateLocal = date, Reason = trimmed };
        db.ClosureDays.Add(closure);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Index unique (restaurant, date) : un ajout concurrent du même jour.
            throw new DomainValidationException("Ce jour est déjà fermé");
        }

        return closure.Id;
    }

    public async Task RemoveClosureAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        var closure = await db.ClosureDays.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new DomainValidationException(UnknownClosureMessage);
        db.ClosureDays.Remove(closure);

        await db.SaveChangesAsync();
    }

    // ── Lecture publique ─────────────────────────────────────────────────────

    /// <summary>
    /// Contenu public de la vitrine du restaurant courant, ou null si le profil n'existe pas ou n'est pas publié. La carte regroupe les plats
    /// affichés (non archivés, avec prix de vente) par catégorie (ordre alphabétique, « Autres » en dernier), puis par nom ; les allergènes
    /// viennent d'un seul <see cref="RecipeBook"/>. Les vins publiés sont limités à 3, dans l'ordre de la carte des vins.
    /// </summary>
    public async Task<PublicVitrine?> GetPublicAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        var profile = await db.VitrineProfiles.AsNoTracking().FirstOrDefaultAsync();
        if (profile is not { IsPublished: true })
            return null;

        var restaurant = await db.Restaurants.AsNoTracking().FirstAsync(r => r.Id == profile.RestaurantId);

        var galleryIds = await db.VitrineImages.AsNoTracking()
            .Where(i => !i.IsCover)
            .OrderBy(i => i.Position).ThenBy(i => i.Id)
            .Select(i => i.Id)
            .ToListAsync();

        var recipes = await db.Recipes.AsNoTracking().Include(r => r.Lines.OrderBy(l => l.Id)).ToListAsync();
        var ingredients = await db.Ingredients.AsNoTracking().ToListAsync();
        var book = new RecipeBook(recipes, ingredients);

        var menu = recipes
            .Where(r => r.Kind == RecipeKind.Dish && r.ShowOnVitrine && !r.IsArchived && r.SellingPriceInclVat is not null)
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? OtherCategory : r.Category.Trim())
            .OrderBy(g => g.Key == OtherCategory ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.InvariantCultureIgnoreCase)
            .Select(g => new PublicMenuCategory(
                g.Key,
                g.OrderBy(r => r.Name, StringComparer.InvariantCultureIgnoreCase).ThenBy(r => r.Id)
                    .Select(r => new PublicDish(r.Id, r.Name, r.PublicDescription, r.SellingPriceInclVat!.Value, book.Allergens(r)))
                    .ToList()))
            .ToList();

        var wines = await db.WineListItems.AsNoTracking()
            .Where(w => w.IsPublished)
            .OrderBy(w => w.Position).ThenBy(w => w.Id)
            .Take(PreviewWines)
            .Select(w => new PublicWine(w.Id, w.Name, w.Vintage, w.Color, w.Region, w.PriceBottle, w.PriceGlass))
            .ToListAsync();

        return new PublicVitrine(
            restaurant.Name, restaurant.Address, profile.City, profile.Phone, profile.PublicEmail, profile.Tagline,
            profile.AmbianceText, profile.AccentColor, profile.CoverImageId, galleryIds, menu, wines);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
