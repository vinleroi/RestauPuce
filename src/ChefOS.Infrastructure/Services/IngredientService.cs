using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Ingrédients du restaurant courant : prix avec historique, rendement, archivage et suppression gardée.
/// Les clés étrangères de la base sont à colonne unique : la vérification qu'un fournisseur ou un ingrédient appartient
/// bien au restaurant courant se fait ici, via le contexte filtré par restaurant.
/// </summary>
public class IngredientService(IDbContextFactory<ChefOsDbContext> factory)
{
    private const string DefaultCategory = "Divers";
    private const string UnknownIngredientMessage = "Ingrédient introuvable";
    private const string UnknownSupplierMessage = "Fournisseur introuvable";

    // Échelle de la colonne numeric(18,6) : arrondir avant de comparer évite un faux « nouveau prix » à chaque enregistrement.
    private const int PriceScale = 6;

    public async Task<IReadOnlyList<Ingredient>> ListAsync(bool includeArchived = false)
    {
        await using var db = await factory.CreateDbContextAsync();

        var query = db.Ingredients.AsNoTracking().Include(i => i.Supplier).AsQueryable();
        if (!includeArchived)
            query = query.Where(i => !i.IsArchived);

        return await query.OrderBy(i => i.Category).ThenBy(i => i.Name).ToListAsync();
    }

    public async Task<Ingredient?> GetAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.Ingredients.AsNoTracking().Include(i => i.Supplier).FirstOrDefaultAsync(i => i.Id == id);
    }

    /// <summary>
    /// Crée (<c>Id</c> null) ou met à jour un ingrédient. Un prix renseigné et différent du prix courant est consigné
    /// dans l'historique (source Manual) ; un prix null laisse le prix courant tel quel.
    /// Le prix n'a de sens qu'avec son unité de base : changer l'unité d'un ingrédient existant est refusé s'il est utilisé
    /// dans des recettes ; sinon le prix dans la nouvelle unité est obligatoire et il remplace tout l'historique
    /// (exprimé dans l'ancienne unité) par une seule entrée Manual.
    /// L'ingrédient retourné n'a pas sa propriété <c>Supplier</c> chargée.
    /// </summary>
    public async Task<Ingredient> SaveAsync(IngredientInput input)
    {
        var name = RequireName(input.Name);
        RequireYield(input.YieldPercent);
        var price = input.PricePerBaseUnit is { } entered ? NormalizePrice(entered) : (decimal?)null;
        if (input.AveragePieceWeightGrams is <= 0m)
            throw new DomainValidationException("Le poids moyen d'une pièce doit être supérieur à 0");

        await using var db = await factory.CreateDbContextAsync();

        var ingredient = input.Id is { } id
            ? await RequireIngredientAsync(db, id)
            : new Ingredient();
        await RequireSupplierAsync(db, input.SupplierId);

        // Contrôlé avant toute modification : un refus ne laisse rien derrière lui.
        var unitChanged = input.Id is not null && ingredient.BaseUnit != input.BaseUnit;
        if (unitChanged)
            await RequireUnitChangeAllowedAsync(db, ingredient.Id, price);

        ingredient.Name = name;
        ingredient.Category = string.IsNullOrWhiteSpace(input.Category) ? DefaultCategory : input.Category.Trim();
        ingredient.Allergens = input.Allergens;
        ingredient.BaseUnit = input.BaseUnit;
        ingredient.YieldPercent = input.YieldPercent;
        ingredient.AveragePieceWeightGrams = input.AveragePieceWeightGrams;
        ingredient.SupplierId = input.SupplierId;

        // Après un changement d'unité, le prix saisi est toujours consigné, même s'il a la même valeur numérique que l'ancien.
        var priceChanged = unitChanged || (price is not null && price != ingredient.CurrentPricePerBaseUnit);
        if (priceChanged)
            ingredient.CurrentPricePerBaseUnit = price;

        if (input.Id is null)
            db.Ingredients.Add(ingredient);

        // L'identifiant d'un nouvel ingrédient n'existe qu'après le premier enregistrement : une transaction
        // garantit qu'on n'obtient jamais un ingrédient avec un prix courant mais sans entrée d'historique.
        await using var transaction = await db.Database.BeginTransactionAsync();

        // L'ancien historique est exprimé dans l'ancienne unité : il disparaît avec elle, dans la même transaction.
        if (unitChanged)
            await db.IngredientPrices.Where(p => p.IngredientId == ingredient.Id).ExecuteDeleteAsync();

        await db.SaveChangesAsync();

        if (priceChanged)
        {
            db.IngredientPrices.Add(NewPriceEntry(
                ingredient.Id, price!.Value, PriceSource.Manual, ingredient.SupplierId, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        await transaction.CommitAsync();
        return ingredient;
    }

    /// <summary>
    /// Ajoute toujours une entrée d'historique (utilisé aussi par le scan de tickets). Le prix courant ne devient
    /// <paramref name="pricePerBaseUnit"/> que si le jour UTC de <paramref name="dateUtc"/> est égal ou postérieur à celui de la plus récente
    /// entrée déjà présente (ou s'il n'y a aucun historique) : un ticket en retard n'enrichit que l'historique. La comparaison se fait au jour
    /// et non à l'heure : une entrée manuelle saisie à 15 h ne rend pas « en retard » le ticket du même jour, daté de minuit.
    /// </summary>
    public async Task SetPriceAsync(
        int ingredientId, decimal pricePerBaseUnit, PriceSource source, int? supplierId, DateTime dateUtc)
    {
        await using var db = await factory.CreateDbContextAsync();

        await AddPriceEntryAsync(db, ingredientId, pricePerBaseUnit, source, supplierId, dateUtc);

        // Un seul SaveChanges : le prix courant et son entrée d'historique sont enregistrés ensemble ou pas du tout.
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Écrit un prix dans le contexte de l'appelant, sans l'enregistrer : l'appelant décide du <c>SaveChanges</c> et de la transaction
    /// (le ticket appliqué écrit ses prix, ses alias et son statut d'un seul bloc). Même règles que <see cref="SetPriceAsync"/> ;
    /// renvoie vrai quand le prix courant de l'ingrédient a été remplacé, faux quand l'entrée n'enrichit que l'historique.
    /// </summary>
    internal static async Task<bool> AddPriceEntryAsync(
        ChefOsDbContext db, int ingredientId, decimal pricePerBaseUnit, PriceSource source, int? supplierId, DateTime dateUtc)
    {
        var price = NormalizePrice(pricePerBaseUnit);
        if (dateUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("La date du prix doit être exprimée en UTC.", nameof(dateUtc));

        var ingredient = await RequireIngredientAsync(db, ingredientId);
        await RequireSupplierAsync(db, supplierId);

        // Lu avant d'ajouter l'entrée : on compare à l'historique existant, pas à la nouvelle ligne.
        var latestDate = await db.IngredientPrices
            .Where(p => p.IngredientId == ingredientId)
            .MaxAsync(p => (DateTime?)p.Date);
        var replacesCurrent = latestDate is null || dateUtc.Date >= latestDate.Value.Date;
        if (replacesCurrent)
            ingredient.CurrentPricePerBaseUnit = price;

        db.IngredientPrices.Add(NewPriceEntry(ingredient.Id, price, source, supplierId, dateUtc));
        return replacesCurrent;
    }

    public async Task<IReadOnlyList<IngredientPrice>> GetPriceHistoryAsync(int ingredientId)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.IngredientPrices
            .AsNoTracking()
            .Where(p => p.IngredientId == ingredientId)
            .OrderBy(p => p.Date)
            .ThenBy(p => p.Id)
            .ToListAsync();
    }

    /// <summary>Noms des recettes (archivées comprises) qui utilisent l'ingrédient, triés par nom.</summary>
    public async Task<IReadOnlyList<string>> GetUsageAsync(int ingredientId)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await RecipeNamesUsingAsync(db, ingredientId);
    }

    public async Task SetArchivedAsync(int id, bool archived)
    {
        await using var db = await factory.CreateDbContextAsync();

        var ingredient = await RequireIngredientAsync(db, id);
        ingredient.IsArchived = archived;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Supprime l'ingrédient et son historique de prix, sauf s'il est utilisé : le résultat liste alors les recettes concernées.
    /// </summary>
    public async Task<DeleteResult> DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        var ingredient = await RequireIngredientAsync(db, id);

        var usedInRecipes = await RecipeNamesUsingAsync(db, id);
        if (usedInRecipes.Count > 0)
            return new DeleteResult(false, usedInRecipes);

        // L'historique de prix suit par cascade en base.
        db.Ingredients.Remove(ingredient);
        await db.SaveChangesAsync();

        return new DeleteResult(true, []);
    }

    // Un prix par unité de base ne vaut que pour son unité : passer de la pièce au gramme sans le ressaisir multiplierait
    // ou diviserait le coût de toutes les recettes par 1000.
    private static async Task RequireUnitChangeAllowedAsync(ChefOsDbContext db, int ingredientId, decimal? newPrice)
    {
        var recipes = await RecipeNamesUsingAsync(db, ingredientId);
        if (recipes.Count > 0)
            throw new DomainValidationException(
                "Impossible de changer l'unité : ingrédient utilisé dans " + string.Join(", ", recipes));

        if (newPrice is null)
            throw new DomainValidationException("Indiquez le prix dans la nouvelle unité");
    }

    private static async Task<List<string>> RecipeNamesUsingAsync(ChefOsDbContext db, int ingredientId) =>
        await db.Recipes
            .Where(r => r.Lines.Any(l => l.IngredientId == ingredientId))
            .OrderBy(r => r.Name)
            .Select(r => r.Name)
            .ToListAsync();

    private static async Task<Ingredient> RequireIngredientAsync(ChefOsDbContext db, int id) =>
        await db.Ingredients.FirstOrDefaultAsync(i => i.Id == id)
        ?? throw new DomainValidationException(UnknownIngredientMessage);

    private static async Task RequireSupplierAsync(ChefOsDbContext db, int? supplierId)
    {
        if (supplierId is { } id && !await db.Suppliers.AnyAsync(s => s.Id == id))
            throw new DomainValidationException(UnknownSupplierMessage);
    }

    private static IngredientPrice NewPriceEntry(
        int ingredientId, decimal price, PriceSource source, int? supplierId, DateTime dateUtc) => new()
    {
        IngredientId = ingredientId,
        Date = dateUtc,
        PricePerBaseUnit = price,
        SupplierId = supplierId,
        Source = source
    };

    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainValidationException("Le nom est obligatoire");

        return name.Trim();
    }

    // RecipeBook divise par YieldPercent / 100 : une valeur nulle ou hors plage fausserait tous les coûts de revient.
    private static void RequireYield(decimal yieldPercent)
    {
        if (yieldPercent is < 1m or > 100m)
            throw new DomainValidationException("Le rendement doit être compris entre 1 et 100 %");
    }

    /// <summary>Prix arrondi à l'échelle de la colonne (6 décimales) : la valeur enregistrée, donc celle qu'on annonce.</summary>
    internal static decimal NormalizePrice(decimal price)
    {
        if (price < 0m)
            throw new DomainValidationException("Le prix ne peut pas être négatif");

        return Math.Round(price, PriceScale, MidpointRounding.AwayFromZero);
    }
}
