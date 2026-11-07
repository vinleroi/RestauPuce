using System.Globalization;
using ChefOS.Core.Common;
using ChefOS.Core.Recipes;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Wines;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Carte des vins du restaurant courant : recherche dans la bibliothèque partagée, copie d'une fiche, vins manuels, accords avec les plats,
/// ordre par couleur, fiche rédigée par l'assistant (<see cref="IWineDescriber"/>) et lecture publique des vins publiés.
/// Une fiche rédigée par l'assistant est un brouillon (<see cref="WineListItem.DescriptionIsAiDraft"/>) : seule la confirmation explicite du chef
/// l'efface, et la publication l'exige. Les écritures sont tout ou rien.
/// </summary>
public class WineService(IDbContextFactory<ChefOsDbContext> factory, IWineDescriber describer, TimeProvider? timeProvider = null)
{
    private const int MaxLibraryResults = 50;
    private const int MaxDishNames = 80;

    internal const string ReviewRequiredMessage = "Relisez et validez la fiche rédigée par l'assistant avant de la publier";
    private const string DuplicateMessage = "Ce vin est déjà sur votre carte (même millésime)";
    private const string UnknownWineMessage = "Vin introuvable";
    private const string UnknownReferenceMessage = "Fiche de la bibliothèque introuvable";
    private const string InvalidPositionMessage = "Position invalide";
    private const string UnknownDishMessage = "Un accord pointe vers un plat introuvable";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // ── Bibliothèque ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fiches de la bibliothèque partagée dont le nom, l'appellation, la région, le pays ou les cépages contiennent <paramref name="text"/>
    /// (sans tenir compte des accents ni de la casse), de la couleur demandée, par couleur puis par nom ; 50 au plus.
    /// La bibliothèque est petite (quelques dizaines de fiches) : la comparaison se fait en mémoire.
    /// </summary>
    public async Task<IReadOnlyList<WineReference>> SearchLibraryAsync(string? text, WineColor? color)
    {
        await using var db = await factory.CreateDbContextAsync();

        var query = db.WineReferences.AsNoTracking();
        if (color is { } wanted)
            query = query.Where(w => w.Color == wanted);

        var references = await query.ToListAsync();

        return references
            .Where(w => Matches(text, w.Name, w.Appellation, w.Region, w.Country, w.Grapes))
            .OrderBy(w => w.Color)
            .ThenBy(w => w.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(w => w.Id)
            .Take(MaxLibraryResults)
            .ToList();
    }

    /// <summary>Copie la fiche de la bibliothèque sur la carte, en brouillon (non publiée), en dernière position de sa couleur.</summary>
    /// <exception cref="DomainValidationException">Fiche inconnue, prix ou millésime invalide, ou ce vin (même fiche et même millésime) est déjà sur la carte.</exception>
    public async Task<WineListItem> AddFromLibraryAsync(int referenceId, int? vintage, string? producer, decimal priceBottle, decimal? priceGlass)
    {
        ValidateVintage(vintage);
        ValidatePrices(priceBottle, priceGlass);
        ValidateLength(producer, 200, "Le producteur");

        await using var db = await factory.CreateDbContextAsync();

        var reference = await db.WineReferences.AsNoTracking().FirstOrDefaultAsync(w => w.Id == referenceId)
            ?? throw new DomainValidationException(UnknownReferenceMessage);

        if (await db.WineListItems.AnyAsync(w => w.WineReferenceId == referenceId && w.Vintage == vintage))
            throw new DomainValidationException(DuplicateMessage);

        var wine = new WineListItem
        {
            WineReferenceId = reference.Id,
            Name = reference.Name,
            Producer = NullIfBlank(producer),
            Vintage = vintage,
            Appellation = reference.Appellation,
            Region = reference.Region,
            Country = reference.Country,
            Color = reference.Color,
            Grapes = reference.Grapes,
            Robe = reference.Robe,
            Nez = reference.Nez,
            Bouche = reference.Bouche,
            Garde = reference.Garde,
            ServiceTemperature = reference.ServiceTemperature,
            GeneralPairings = reference.Pairings,
            PriceBottle = priceBottle,
            PriceGlass = priceGlass,
            Position = await NextPositionAsync(db, reference.Color)
        };
        db.WineListItems.Add(wine);
        await db.SaveChangesAsync();

        return wine;
    }

    // ── Carte ────────────────────────────────────────────────────────────────

    /// <summary>Les vins de la carte, publiés ou non, par couleur puis par position.</summary>
    public async Task<IReadOnlyList<WineListItem>> ListAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        var wines = await db.WineListItems.AsNoTracking().ToListAsync();

        return wines.OrderBy(w => w.Color).ThenBy(w => w.Position).ThenBy(w => w.Id).ToList();
    }

    /// <summary>Un vin de la carte et ses accords ; null s'il n'existe pas (ou appartient à un autre restaurant).</summary>
    public async Task<WineDetail?> GetAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        if (await db.WineListItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id) is not { } wine)
            return null;

        var recipeIds = await db.WineListItemPairings.AsNoTracking()
            .Where(p => p.WineListItemId == id)
            .Select(p => p.RecipeId)
            .OrderBy(r => r)
            .ToListAsync();

        return new WineDetail(wine, recipeIds);
    }

    /// <summary>
    /// Les plats publiés (plats de la carte de la vitrine, non archivés) par nom, plus ceux de <paramref name="alsoIncluding"/> (accords déjà posés
    /// sur un plat qui n'est plus publié) : les choix proposés pour un accord.
    /// </summary>
    public async Task<IReadOnlyList<WineDish>> ListDishesAsync(IEnumerable<int>? alsoIncluding = null)
    {
        await using var db = await factory.CreateDbContextAsync();

        var extra = alsoIncluding?.ToList() ?? [];
        var dishes = await db.Recipes.AsNoTracking()
            .Where(r => r.Kind == RecipeKind.Dish && ((r.ShowOnVitrine && !r.IsArchived) || extra.Contains(r.Id)))
            .Select(r => new WineDish(r.Id, r.Name))
            .ToListAsync();

        return dishes.OrderBy(d => d.Name, StringComparer.InvariantCultureIgnoreCase).ThenBy(d => d.Id).ToList();
    }

    /// <summary>Crée (<see cref="WineInput.Id"/> null) ou met à jour un vin, ses accords compris.</summary>
    /// <exception cref="DomainValidationException">Saisie invalide, vin inconnu, ou publication d'une fiche de l'assistant non relue.</exception>
    public async Task<WineListItem> SaveAsync(WineInput input)
    {
        Validate(input);
        var recipeIds = input.PairingRecipeIds.Distinct().ToList();

        await using var db = await factory.CreateDbContextAsync();

        WineListItem wine;
        var oldColor = (WineColor?)null;
        if (input.Id is { } id)
        {
            wine = await db.WineListItems.FirstOrDefaultAsync(w => w.Id == id)
                ?? throw new DomainValidationException(UnknownWineMessage);
            oldColor = wine.Color;
        }
        else
        {
            wine = new WineListItem();
            db.WineListItems.Add(wine);
        }

        // Une fiche de l'assistant reste un brouillon tant que le chef ne l'a pas confirmée : modifier un texte n'y change rien.
        var isDraft = input.DescriptionIsAiDraft || wine.DescriptionIsAiDraft;
        if (input.IsPublished && isDraft && !input.ConfirmAiDraftReviewed)
            throw new DomainValidationException(ReviewRequiredMessage);

        if (recipeIds.Count > 0 && await db.Recipes.CountAsync(r => recipeIds.Contains(r.Id) && r.Kind == RecipeKind.Dish) != recipeIds.Count)
            throw new DomainValidationException(UnknownDishMessage);

        wine.Name = input.Name.Trim();
        wine.Producer = NullIfBlank(input.Producer);
        wine.Vintage = input.Vintage;
        wine.Appellation = NullIfBlank(input.Appellation);
        wine.Region = input.Region.Trim();
        wine.Country = input.Country.Trim();
        wine.Color = input.Color;
        wine.Grapes = input.Grapes.Trim();
        wine.Robe = input.Robe.Trim();
        wine.Nez = input.Nez.Trim();
        wine.Bouche = input.Bouche.Trim();
        wine.Garde = input.Garde.Trim();
        wine.ServiceTemperature = input.ServiceTemperature.Trim();
        wine.GeneralPairings = input.GeneralPairings.Trim();
        wine.PersonalNote = NullIfBlank(input.PersonalNote);
        wine.PriceBottle = input.PriceBottle;
        wine.PriceGlass = input.PriceGlass;
        wine.IsPublished = input.IsPublished;
        wine.DescriptionIsAiDraft = isDraft && !input.ConfirmAiDraftReviewed;

        if (oldColor != input.Color)
            wine.Position = await NextPositionAsync(db, input.Color);

        await db.SaveChangesAsync();

        await SyncPairingsAsync(db, wine.Id, recipeIds);
        if (oldColor is { } previous && previous != input.Color)
            await CompactAsync(db, previous);

        return wine;
    }

    /// <summary>Publie ou retire un vin. Une fiche de l'assistant non relue ne se publie pas : elle passe par <see cref="SaveAsync"/> avec la confirmation.</summary>
    public async Task SetPublishedAsync(int id, bool published)
    {
        await using var db = await factory.CreateDbContextAsync();

        var wine = await db.WineListItems.FirstOrDefaultAsync(w => w.Id == id)
            ?? throw new DomainValidationException(UnknownWineMessage);
        if (published && wine.DescriptionIsAiDraft)
            throw new DomainValidationException(ReviewRequiredMessage);

        wine.IsPublished = published;
        await db.SaveChangesAsync();
    }

    /// <summary>Retire le vin de la carte, ses accords avec (suppression en cascade), et referme les positions de sa couleur.</summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        var wine = await db.WineListItems.FirstOrDefaultAsync(w => w.Id == id)
            ?? throw new DomainValidationException(UnknownWineMessage);

        db.WineListItems.Remove(wine);
        await db.SaveChangesAsync();
        await CompactAsync(db, wine.Color);
    }

    /// <summary>Place le vin à <paramref name="newPosition"/> (à partir de 0) parmi les vins de sa couleur ; les positions de la couleur sont renumérotées sans trou.</summary>
    public async Task MoveAsync(int id, int newPosition)
    {
        await using var db = await factory.CreateDbContextAsync();

        var wine = await db.WineListItems.FirstOrDefaultAsync(w => w.Id == id)
            ?? throw new DomainValidationException(UnknownWineMessage);

        var group = await db.WineListItems.Where(w => w.Color == wine.Color).OrderBy(w => w.Position).ThenBy(w => w.Id).ToListAsync();
        if (newPosition < 0 || newPosition >= group.Count)
            throw new DomainValidationException(InvalidPositionMessage);

        var current = group.First(w => w.Id == id);
        group.Remove(current);
        group.Insert(newPosition, current);
        for (var position = 0; position < group.Count; position++)
            group[position].Position = position;

        await db.SaveChangesAsync();
    }

    // ── Fiche rédigée par l'assistant ────────────────────────────────────────

    /// <summary>
    /// Fait rédiger la fiche du vin par l'assistant. Les accords sont proposés parmi les plats publiés du restaurant : les noms inconnus sont écartés
    /// et les autres sont traduits en identifiants de recettes (<see cref="WineDraft.SuggestedPairingRecipeIds"/>).
    /// </summary>
    /// <exception cref="DomainValidationException">Nom vide, millésime invalide.</exception>
    /// <exception cref="InvalidOperationException">L'assistant a échoué ; le message est en français.</exception>
    public async Task<WineDraft> DraftDescriptionAsync(string name, int? vintage, string? producer, string? appellation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainValidationException("Le nom du vin est obligatoire pour rédiger la fiche");
        ValidateLength(name, 200, "Le nom");
        ValidateLength(producer, 200, "Le producteur");
        ValidateLength(appellation, 200, "L'appellation");
        ValidateVintage(vintage);

        var dishes = (await ListDishesAsync()).Take(MaxDishNames).ToList();

        var draft = await describer.DescribeAsync(
            new WineDescribeRequest(name.Trim(), vintage, NullIfBlank(producer), NullIfBlank(appellation), dishes.Select(d => d.Name).ToList()), ct);

        var known = new List<WineDish>();
        foreach (var suggested in draft.SuggestedPairingDishNames)
        {
            var dish = dishes.FirstOrDefault(d => Normalize(d.Name) == Normalize(suggested));
            if (dish is not null && known.All(k => k.Id != dish.Id))
                known.Add(dish);
        }

        return draft with { SuggestedPairingDishNames = known.Select(d => d.Name).ToList() } with
        {
            SuggestedPairingRecipeIds = known.Select(d => d.Id).ToList()
        };
    }

    // ── Lecture publique ─────────────────────────────────────────────────────

    /// <summary>Les vins publiés du restaurant courant, par couleur puis position, éventuellement d'une couleur ou d'une région (sans tenir compte des accents ni de la casse).</summary>
    public async Task<IReadOnlyList<PublicWine>> ListPublishedAsync(WineColor? color, string? region)
    {
        await using var db = await factory.CreateDbContextAsync();

        var query = db.WineListItems.AsNoTracking().Where(w => w.IsPublished);
        if (color is { } wanted)
            query = query.Where(w => w.Color == wanted);

        var wines = await query.ToListAsync();

        return wines
            .Where(w => string.IsNullOrWhiteSpace(region) || Normalize(w.Region) == Normalize(region))
            .OrderBy(w => w.Color).ThenBy(w => w.Position).ThenBy(w => w.Id)
            .Select(w => new PublicWine(w.Id, w.Name, w.Vintage, w.Color, w.Region, w.PriceBottle, w.PriceGlass))
            .ToList();
    }

    /// <summary>La fiche d'un vin publié, avec ses accords sur les plats publiés ; null si le vin n'existe pas ou n'est pas publié.</summary>
    public async Task<PublicWineDetail?> GetPublishedAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        if (await db.WineListItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id && w.IsPublished) is not { } wine)
            return null;

        var pairings = await (
                from p in db.WineListItemPairings.AsNoTracking()
                join r in db.Recipes.AsNoTracking() on p.RecipeId equals r.Id
                where p.WineListItemId == id && r.Kind == RecipeKind.Dish && r.ShowOnVitrine && !r.IsArchived
                select new PublicWinePairing(r.Id, r.Name))
            .ToListAsync();

        return new PublicWineDetail(
            wine.Id, wine.Name, wine.Producer, wine.Vintage, wine.Appellation, wine.Region, wine.Country, wine.Color, wine.Grapes,
            wine.Robe, wine.Nez, wine.Bouche, wine.Garde, wine.ServiceTemperature, wine.GeneralPairings, wine.PersonalNote,
            wine.PriceBottle, wine.PriceGlass,
            pairings.OrderBy(p => p.DishName, StringComparer.InvariantCultureIgnoreCase).ThenBy(p => p.RecipeId).ToList());
    }

    // ── Validation et aides ──────────────────────────────────────────────────

    private void Validate(WineInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new DomainValidationException("Le nom du vin est obligatoire");
        if (!Enum.IsDefined(input.Color))
            throw new DomainValidationException("La couleur du vin est invalide");

        ValidateLength(input.Name, 200, "Le nom");
        ValidateLength(input.Producer, 200, "Le producteur");
        ValidateLength(input.Appellation, 200, "L'appellation");
        ValidateLength(input.Region, 100, "La région");
        ValidateLength(input.Country, 100, "Le pays");
        ValidateLength(input.Grapes, 300, "Les cépages");
        ValidateLength(input.ServiceTemperature, 100, "La température de service");
        ValidateVintage(input.Vintage);
        ValidatePrices(input.PriceBottle, input.PriceGlass);
    }

    private void ValidateVintage(int? vintage)
    {
        var max = _time.GetUtcNow().Year + 1;
        if (vintage is { } year && (year < 1900 || year > max))
            throw new DomainValidationException($"Le millésime doit être compris entre 1900 et {max.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void ValidatePrices(decimal priceBottle, decimal? priceGlass)
    {
        if (priceBottle <= 0m)
            throw new DomainValidationException("Le prix de la bouteille doit être supérieur à 0");
        if (priceGlass is < 0m)
            throw new DomainValidationException("Le prix du verre ne peut pas être négatif");
    }

    private static void ValidateLength(string? value, int max, string label)
    {
        if (value is not null && value.Trim().Length > max)
            throw new DomainValidationException($"{label} ne peut pas dépasser {max} caractères");
    }

    private static async Task<int> NextPositionAsync(ChefOsDbContext db, WineColor color)
    {
        var last = await db.WineListItems.Where(w => w.Color == color).MaxAsync(w => (int?)w.Position);
        return last is { } position ? position + 1 : 0;
    }

    private static async Task CompactAsync(ChefOsDbContext db, WineColor color)
    {
        var group = await db.WineListItems.Where(w => w.Color == color).OrderBy(w => w.Position).ThenBy(w => w.Id).ToListAsync();
        for (var position = 0; position < group.Count; position++)
            group[position].Position = position;

        await db.SaveChangesAsync();
    }

    private static async Task SyncPairingsAsync(ChefOsDbContext db, int wineId, List<int> recipeIds)
    {
        var current = await db.WineListItemPairings.Where(p => p.WineListItemId == wineId).ToListAsync();

        db.WineListItemPairings.RemoveRange(current.Where(p => !recipeIds.Contains(p.RecipeId)));
        foreach (var recipeId in recipeIds.Where(r => current.All(p => p.RecipeId != r)))
            db.WineListItemPairings.Add(new WineListItemPairing { WineListItemId = wineId, RecipeId = recipeId });

        await db.SaveChangesAsync();
    }

    private static bool Matches(string? term, params string?[] fields) =>
        string.IsNullOrWhiteSpace(term) || fields.Any(f => f is not null && Normalize(f).Contains(Normalize(term), StringComparison.Ordinal));

    /// <summary>Minuscules sans accents, espaces rognés : la forme de comparaison des recherches.</summary>
    private static string Normalize(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
