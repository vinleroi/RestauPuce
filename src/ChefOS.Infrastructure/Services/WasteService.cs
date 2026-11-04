using ChefOS.Core.Common;
using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Pertes déclarées par le chef (un ingrédient ou une recette jetés), valorisées au prix en vigueur à leur date : une perte garde le coût
/// qu'elle avait le jour où elle a eu lieu, quelle que soit l'évolution des prix ensuite.
/// Les clés étrangères de la base sont à colonne unique : la vérification qu'un ingrédient ou une recette appartient bien au restaurant
/// courant se fait ici, via le contexte filtré par restaurant (qui estampille aussi les ajouts).
/// Le jour d'une perte est un jour du calendrier UTC.
/// </summary>
public class WasteService(IDbContextFactory<ChefOsDbContext> factory)
{
    private const string TargetMessage = "Choisissez un ingrédient ou une recette";
    private const string QuantityMessage = "La quantité doit être supérieure à 0";
    private const string QuantityTooHighMessage = "Quantité trop élevée";
    private const string FutureMessage = "La date ne peut pas être dans le futur";
    private const string ReasonMessage = "Motif invalide";
    private const string IncompatibleUnitMessage = "Unité incompatible";
    private const string UnknownIngredientMessage = "Ingrédient introuvable";
    private const string UnknownRecipeMessage = "Recette introuvable";
    private const string UnknownWasteMessage = "Perte introuvable";

    /// <summary>Plafond d'une saisie, dans l'unité saisie : au-delà, c'est une faute de frappe.</summary>
    private const decimal MaxQuantity = 100_000m;

    // Échelle de la colonne numeric : arrondir avant de valider évite qu'une quantité positive soit stockée comme 0.
    private const int QuantityScale = 4;

    /// <summary>
    /// Déclare une perte et renvoie son identifiant. La date est ramenée au jour UTC ; la quantité est arrondie à 4 décimales (échelle de la colonne).
    /// Ni ingrédient ni recette archivés ne sont écartés : un produit retiré de la carte peut encore périmer au frigo.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// Pas exactement une cible, quantité nulle, négative ou supérieure à 100 000, date postérieure à demain (UTC, même règle que pour les tickets),
    /// motif inconnu, cible inconnue (ou d'un autre restaurant), ou unité incompatible avec la cible : pour un ingrédient, elle doit se convertir dans
    /// son unité de base ; pour un plat, c'est <c>Piece</c> (des portions) ; pour une préparation, elle doit se convertir dans son unité de production.
    /// </exception>
    public async Task<int> AddAsync(WasteInput input)
    {
        if ((input.IngredientId is null) == (input.RecipeId is null))
            throw new DomainValidationException(TargetMessage);

        var quantity = Math.Round(input.Quantity, QuantityScale, MidpointRounding.AwayFromZero);
        if (quantity <= 0m)
            throw new DomainValidationException(QuantityMessage);
        if (quantity > MaxQuantity)
            throw new DomainValidationException(QuantityTooHighMessage);

        var date = UtcDays.Of(input.Date);
        if (UtcDays.IsInTheFuture(date))
            throw new DomainValidationException(FutureMessage);

        if (!Enum.IsDefined(input.Reason))
            throw new DomainValidationException(ReasonMessage);

        await using var db = await factory.CreateDbContextAsync();

        if (input.IngredientId is { } ingredientId)
        {
            var ingredient = await db.Ingredients.AsNoTracking().FirstOrDefaultAsync(i => i.Id == ingredientId)
                             ?? throw new DomainValidationException(UnknownIngredientMessage);
            if (!UnitConverter.ToBase(quantity, input.Unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams).Success)
                throw new DomainValidationException(IncompatibleUnitMessage);
        }
        else
        {
            var recipeId = input.RecipeId!.Value;
            var recipe = await db.Recipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId)
                         ?? throw new DomainValidationException(UnknownRecipeMessage);
            if (!IsUnitCompatible(recipe, quantity, input.Unit))
                throw new DomainValidationException(IncompatibleUnitMessage);
        }

        var entry = new WasteEntry
        {
            Date = date,
            IngredientId = input.IngredientId,
            RecipeId = input.RecipeId,
            Quantity = quantity,
            Unit = input.Unit,
            Reason = input.Reason,
            Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        db.WasteEntries.Add(entry);
        await db.SaveChangesAsync();

        return entry.Id;
    }

    /// <summary>Supprime une perte du restaurant courant.</summary>
    /// <exception cref="DomainValidationException">La perte n'existe pas dans le restaurant courant.</exception>
    public async Task DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        // Une seule instruction, filtrée par restaurant : rien n'est supprimé si la perte est celle d'un autre restaurant ou a déjà disparu.
        var deleted = await db.WasteEntries.Where(e => e.Id == id).ExecuteDeleteAsync();
        if (deleted != 1)
            throw new DomainValidationException(UnknownWasteMessage);
    }

    /// <summary>
    /// Les pertes des jours UTC <paramref name="fromDay"/> à <paramref name="toDay"/> inclus (l'heure éventuelle est ignorée), de la plus récente
    /// à la plus ancienne ; à jour égal, la dernière saisie d'abord. Des bornes inversées donnent une liste vide.
    /// <para>
    /// Le coût (non arrondi) est calculé aux prix en vigueur à la date de la perte (voir <see cref="PriceAsOf"/> : le prix courant tient lieu de prix
    /// à un ingrédient sans historique antérieur). Pour un ingrédient, c'est le prix × la quantité convertie en unité de base, sans rendement : on
    /// jette le produit tel qu'acheté. Pour un plat, le coût de revient d'une portion × le nombre de portions. Pour une préparation, son coût total
    /// ÷ sa quantité produite, par unité de production, × la quantité convertie dans cette unité. Une recette est chiffrée sur les prix du jour de la perte.
    /// </para>
    /// <para>
    /// <see cref="WasteItem.Cost"/> est null quand il est inconnu : ingrédient sans aucun prix, unité qui ne se convertit plus (ingrédient ou
    /// préparation modifié depuis), préparation sans quantité produite valide. <see cref="WasteItem.CostIncomplete"/> est vrai quand le coût de la
    /// recette ne porte que sur les lignes chiffrées : le coût est alors partiel.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<WasteItem>> ListAsync(DateTime fromDay, DateTime toDay)
    {
        var start = UtcDays.Of(fromDay);
        var endExclusive = UtcDays.Of(toDay).AddDays(1);

        await using var db = await factory.CreateDbContextAsync();

        var entries = await db.WasteEntries.AsNoTracking()
            .Where(e => e.Date >= start && e.Date < endExclusive)
            .OrderByDescending(e => e.Date)
            .ThenByDescending(e => e.Id)
            .ToListAsync();
        if (entries.Count == 0)
            return [];

        // Un livre de recettes a besoin de tous les ingrédients ; sans perte de recette, ceux des pertes suffisent.
        var needsRecipes = entries.Any(e => e.RecipeId is not null);
        var wastedIngredientIds = entries.Where(e => e.IngredientId is not null).Select(e => e.IngredientId!.Value).Distinct().ToList();

        var ingredientQuery = db.Ingredients.AsNoTracking();
        var priceQuery = db.IngredientPrices.AsNoTracking().Where(p => p.Date < endExclusive);   // un prix postérieur à la dernière perte ne sert à rien
        if (!needsRecipes)
        {
            ingredientQuery = ingredientQuery.Where(i => wastedIngredientIds.Contains(i.Id));
            priceQuery = priceQuery.Where(p => wastedIngredientIds.Contains(p.IngredientId));
        }

        var ingredients = await ingredientQuery.ToListAsync();
        var history = (await priceQuery.ToListAsync()).ToLookup(p => p.IngredientId);
        var recipes = needsRecipes
            ? await db.Recipes.AsNoTracking().Include(r => r.Lines.OrderBy(l => l.Id)).ToListAsync()
            : [];

        var costing = new WasteCosting(recipes, ingredients, history);

        var items = new List<WasteItem>(entries.Count);
        foreach (var entry in entries)
        {
            // Sinon, la cible a été supprimée entre les lectures (la perte part avec elle par cascade) : rien à afficher.
            if (costing.Value(entry) is { } valuation)
                items.Add(ToItem(entry, DateTime.SpecifyKind(entry.Date, DateTimeKind.Utc), valuation));
        }

        return items;
    }

    /// <summary>Un plat se perd en portions (<c>Piece</c>) ; une préparation, dans une unité qui se convertit dans son unité de production.</summary>
    private static bool IsUnitCompatible(Recipe recipe, decimal quantity, MeasureUnit unit)
    {
        if (recipe.Kind == RecipeKind.Dish)
            return unit == MeasureUnit.Piece;

        return recipe.OutputUnit is { } outputUnit
               && UnitConverter.ToBase(quantity, unit, outputUnit, averagePieceWeightGrams: null).Success;
    }

    private static WasteItem ToItem(WasteEntry entry, DateTime day, WasteValuation valuation) =>
        new(entry.Id, day, valuation.ItemName, valuation.IsRecipe, entry.Quantity, entry.Unit, entry.Reason, entry.Note, valuation.Cost, valuation.CostIncomplete);
}
