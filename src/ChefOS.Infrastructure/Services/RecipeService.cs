using ChefOS.Core.Common;
using ChefOS.Core.Costing;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Plats et préparations du restaurant courant : enregistrement validé (références, boucles), archivage, suppression gardée
/// et résumés chiffrés via <see cref="RecipeBook"/>.
/// Les clés étrangères de la base sont à colonne unique : la vérification qu'un ingrédient ou une préparation référencé(e)
/// appartient bien au restaurant courant se fait ici, via le contexte filtré par restaurant.
/// </summary>
public class RecipeService(IDbContextFactory<ChefOsDbContext> factory)
{
    private const string UnknownRecipeMessage = "Recette introuvable";
    private const string UnknownIngredientMessage = "Ingrédient introuvable";
    private const string UnknownPreparationMessage = "Préparation introuvable";

    // Échelles des colonnes numeric : arrondir avant de valider évite qu'une quantité positive soit stockée comme 0.
    private const int QuantityScale = 4;
    private const int PriceScale = 2;
    private const int VatRateScale = 4;

    /// <summary>Toutes les recettes (archivées comprises) avec leurs lignes, et tous les ingrédients : de quoi chiffrer n'importe quelle recette.</summary>
    public async Task<RecipeBook> LoadBookAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        var recipes = await db.Recipes.AsNoTracking().Include(r => r.Lines.OrderBy(l => l.Id)).ToListAsync();
        var ingredients = await db.Ingredients.AsNoTracking().ToListAsync();

        return new RecipeBook(recipes, ingredients);
    }

    /// <summary>Recettes d'un type, triées par nom ; les archivées sont masquées par défaut.</summary>
    public async Task<IReadOnlyList<RecipeSummary>> ListAsync(RecipeKind kind, bool includeArchived = false)
    {
        var book = await LoadBookAsync();

        return book.Recipes
            .Where(r => r.Kind == kind && (includeArchived || !r.IsArchived))
            .OrderBy(r => r.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(r => r.Id)
            .Select(r => new RecipeSummary(
                r.Id, r.Name, r.Kind, r.Category, r.IsArchived, book.Cost(r.Id), book.Allergens(r)))
            .ToList();
    }

    /// <summary>La recette et ses lignes (dans l'ordre d'enregistrement), ou null si elle n'existe pas dans le restaurant courant.</summary>
    public async Task<Recipe?> GetAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.Recipes.AsNoTracking()
            .Include(r => r.Lines.OrderBy(l => l.Id))
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    /// <summary>
    /// Crée (<c>Id</c> null) ou met à jour une recette ; à la mise à jour, l'ensemble des lignes est remplacé par celui de
    /// <paramref name="input"/>. Refuse une recette invalide, une référence à un ingrédient ou une préparation inconnu(e)
    /// du restaurant courant, ou une boucle de préparations. Un plat ne peut pas servir de sous-recette : une préparation
    /// déjà utilisée par d'autres recettes ne peut donc pas être transformée en plat.
    /// </summary>
    public async Task<Recipe> SaveAsync(RecipeInput input)
    {
        var name = RequireName(input.Name);
        var sellingPrice = Round(input.SellingPriceInclVat, PriceScale);
        var vatRate = Round(input.VatRate, VatRateScale);
        var outputQuantity = Round(input.OutputQuantity, QuantityScale);
        if (input.Kind == RecipeKind.Dish)
            RequireDishPricing(sellingPrice, vatRate);
        else
            RequirePreparationOutput(outputQuantity, input.OutputUnit);
        var lines = NormalizeLines(input.Lines);

        await using var db = await factory.CreateDbContextAsync();

        var recipe = input.Id is { } id
            ? await RequireRecipeAsync(db, id, includeLines: true)
            : new Recipe();
        if (input.Id is { } existingId && recipe.Kind == RecipeKind.Preparation && input.Kind == RecipeKind.Dish)
            await RequirePreparationNotUsedAsync(db, existingId);
        await RequireReferencesAsync(db, lines);

        var draft = new Recipe { Id = input.Id ?? 0, Name = name, Kind = input.Kind, Lines = ToEntities(lines) };
        if ((await LoadBookAsync()).FindCycle(draft) is { } cycle)
            throw new DomainValidationException(cycle.Message);

        recipe.Name = name;
        recipe.Kind = input.Kind;
        recipe.Category = NullIfBlank(input.Category);
        recipe.SellingPriceInclVat = sellingPrice;
        recipe.VatRate = vatRate;
        recipe.OutputQuantity = outputQuantity;
        recipe.OutputUnit = input.OutputUnit;
        recipe.Instructions = NullIfBlank(input.Instructions);

        // Les anciennes lignes sont supprimées par orphelinage (clé étrangère obligatoire) ; les nouvelles sont insérées dans le même SaveChanges.
        recipe.Lines.Clear();
        recipe.Lines.AddRange(ToEntities(lines));

        if (input.Id is null)
            db.Recipes.Add(recipe);

        await db.SaveChangesAsync();
        return recipe;
    }

    public async Task SetArchivedAsync(int id, bool archived)
    {
        await using var db = await factory.CreateDbContextAsync();

        var recipe = await RequireRecipeAsync(db, id);
        recipe.IsArchived = archived;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Supprime la recette et ses lignes, sauf si elle sert de sous-préparation : le résultat liste alors les recettes concernées.
    /// </summary>
    public async Task<DeleteResult> DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        var recipe = await RequireRecipeAsync(db, id);

        var usedInRecipes = await RecipeNamesUsingAsync(db, id);
        if (usedInRecipes.Count > 0)
            return new DeleteResult(false, usedInRecipes);

        // Les lignes de la recette suivent par cascade en base.
        db.Recipes.Remove(recipe);
        await db.SaveChangesAsync();

        return new DeleteResult(true, []);
    }

    /// <summary>Noms des recettes qui utilisent <paramref name="recipeId"/> comme sous-préparation, triés par nom.</summary>
    private static async Task<List<string>> RecipeNamesUsingAsync(ChefOsDbContext db, int recipeId) =>
        await db.Recipes
            .Where(r => r.Lines.Any(l => l.SubRecipeId == recipeId))
            .OrderBy(r => r.Name)
            .Select(r => r.Name)
            .ToListAsync();

    private static async Task RequirePreparationNotUsedAsync(ChefOsDbContext db, int recipeId)
    {
        var usedInRecipes = await RecipeNamesUsingAsync(db, recipeId);
        if (usedInRecipes.Count > 0)
            throw new DomainValidationException(
                "Cette préparation est utilisée dans d'autres recettes : " + string.Join(", ", usedInRecipes));
    }

    private static async Task<Recipe> RequireRecipeAsync(ChefOsDbContext db, int id, bool includeLines = false)
    {
        var query = db.Recipes.AsQueryable();
        if (includeLines)
            query = query.Include(r => r.Lines);

        return await query.FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new DomainValidationException(UnknownRecipeMessage);
    }

    /// <summary>Chaque ingrédient référencé existe dans le restaurant courant, et chaque sous-recette y est une préparation.</summary>
    private static async Task RequireReferencesAsync(ChefOsDbContext db, IReadOnlyList<RecipeLineInput> lines)
    {
        var ingredientIds = lines.Where(l => l.IngredientId is not null).Select(l => l.IngredientId!.Value).Distinct().ToList();
        if (ingredientIds.Count > 0 && await db.Ingredients.CountAsync(i => ingredientIds.Contains(i.Id)) != ingredientIds.Count)
            throw new DomainValidationException(UnknownIngredientMessage);

        var subRecipeIds = lines.Where(l => l.SubRecipeId is not null).Select(l => l.SubRecipeId!.Value).Distinct().ToList();
        if (subRecipeIds.Count > 0
            && await db.Recipes.CountAsync(r => r.Kind == RecipeKind.Preparation && subRecipeIds.Contains(r.Id)) != subRecipeIds.Count)
            throw new DomainValidationException(UnknownPreparationMessage);
    }

    private static List<RecipeLine> ToEntities(IEnumerable<RecipeLineInput> lines) =>
        lines.Select(l => new RecipeLine
        {
            IngredientId = l.IngredientId,
            SubRecipeId = l.SubRecipeId,
            Quantity = l.Quantity,
            Unit = l.Unit
        }).ToList();

    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainValidationException("Le nom est obligatoire");

        return name.Trim();
    }

    private static void RequireDishPricing(decimal? sellingPrice, decimal? vatRate)
    {
        if (sellingPrice is not >= 0m || vatRate is not (>= 0m and <= 1m))
            throw new DomainValidationException("Indiquez le prix de vente et la TVA du plat");
    }

    private static void RequirePreparationOutput(decimal? outputQuantity, BaseUnit? outputUnit)
    {
        if (outputQuantity is not > 0m || outputUnit is null)
            throw new DomainValidationException("Indiquez la quantité produite par la préparation");
    }

    /// <summary>Chaque ligne a exactement une source et une quantité positive ; les quantités sont arrondies à l'échelle de la colonne.</summary>
    private static List<RecipeLineInput> NormalizeLines(IReadOnlyList<RecipeLineInput> lines)
    {
        var normalized = new List<RecipeLineInput>(lines.Count);
        foreach (var line in lines)
        {
            if ((line.IngredientId is null) == (line.SubRecipeId is null))
                throw new DomainValidationException("Chaque ligne doit contenir un ingrédient ou une préparation");

            var quantity = Math.Round(line.Quantity, QuantityScale, MidpointRounding.AwayFromZero);
            if (quantity <= 0m)
                throw new DomainValidationException("La quantité doit être supérieure à 0");

            normalized.Add(line with { Quantity = quantity });
        }

        return normalized;
    }

    private static decimal? Round(decimal? value, int scale) =>
        value is { } number ? Math.Round(number, scale, MidpointRounding.AwayFromZero) : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
