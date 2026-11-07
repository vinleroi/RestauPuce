using ChefOS.Core.Common;
using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Recipes;

/// <summary>Une ligne du formulaire. Tant qu'elle n'a ni élément ni quantité positive, elle n'est pas chiffrée et ne peut pas être enregistrée.</summary>
public sealed class DraftLine
{
    public RecipeItem? Item { get; private set; }

    /// <summary>Null : champ vide.</summary>
    public decimal? Quantity { get; set; }

    public MeasureUnit Unit { get; set; } = MeasureUnit.G;

    public bool IsComplete => Item is not null && Quantity is > 0m;

    /// <summary>
    /// Choisit l'élément de la ligne. L'unité suit l'élément quand celui-ci change de nature (g → ml → pièce) ;
    /// entre deux éléments de même nature, l'unité déjà choisie par le chef est conservée.
    /// </summary>
    public void SetItem(RecipeItem? item)
    {
        if (item is not null && Item?.BaseUnit != item.BaseUnit)
            Unit = DefaultUnit(item.BaseUnit);

        Item = item;
    }

    internal static MeasureUnit DefaultUnit(BaseUnit baseUnit) => baseUnit switch
    {
        BaseUnit.Gram => MeasureUnit.G,
        BaseUnit.Milliliter => MeasureUnit.Ml,
        BaseUnit.Piece => MeasureUnit.Piece,
        _ => throw new ArgumentOutOfRangeException(nameof(baseUnit), baseUnit, null)
    };
}

/// <summary>Coût d'une ligne visible du formulaire : null si la ligne n'a pas pu être chiffrée, avec les problèmes qui s'y rattachent.</summary>
public sealed record LineEvaluation(decimal? Cost, IReadOnlyList<CostIssue> Issues);

/// <summary>
/// Résultat du chiffrage en direct. <see cref="Rows"/> a une entrée par ligne visible du formulaire (null pour une ligne incomplète,
/// ignorée du chiffrage) : les indices de <c>CostResult.Lines</c> sont ramenés aux lignes affichées.
/// <see cref="Cost"/> garde les indices du brouillon de chiffrage : n'y lire que les totaux et la liste des problèmes.
/// </summary>
public sealed record DraftEvaluation(
    CostResult Cost,
    EuAllergen Allergens,
    IReadOnlyList<LineEvaluation?> Rows,
    decimal? OutputQuantity,
    BaseUnit? OutputUnit);

/// <summary>
/// État du formulaire de recette, sans dépendance à l'interface : se transforme en <see cref="Recipe"/> pour le chiffrage en direct
/// (<see cref="ToCostingDraft"/>) et en <see cref="RecipeInput"/> pour l'enregistrement (<see cref="ToInput"/>).
/// La quantité produite d'une préparation est saisie en unité d'affichage (kg, l ou pièces) et enregistrée en unité de base (g, ml, pièce).
/// </summary>
public sealed class RecipeDraft
{
    /// <summary>Taux de TVA proposés pour un plat (fractions).</summary>
    public static IReadOnlyList<decimal> VatRates { get; } = [0.06m, 0.12m, 0.21m];

    /// <summary>Unités de la quantité produite par une préparation.</summary>
    public static IReadOnlyList<MeasureUnit> OutputUnits { get; } = [MeasureUnit.Kg, MeasureUnit.L, MeasureUnit.Piece];

    private RecipeDraft(int? id, RecipeKind kind)
    {
        Id = id;
        Kind = kind;
    }

    /// <summary>Null : recette pas encore enregistrée.</summary>
    public int? Id { get; }

    public RecipeKind Kind { get; }

    public string Name { get; set; } = "";

    public string? Category { get; set; }

    public decimal? PriceInclVat { get; set; }

    /// <summary>Fraction (0,12 = 12 %).</summary>
    public decimal? VatRate { get; set; }

    /// <summary>Quantité produite par une préparation, dans <see cref="OutputUnit"/>.</summary>
    public decimal? OutputQuantity { get; set; }

    public MeasureUnit OutputUnit { get; set; } = MeasureUnit.Kg;

    public string? Instructions { get; set; }

    /// <summary>Texte montré sur la vitrine publique (500 caractères au plus) ; plats seulement.</summary>
    public string? PublicDescription { get; set; }

    /// <summary>Le plat figure sur la carte de la vitrine publique ; plats seulement.</summary>
    public bool ShowOnVitrine { get; set; }

    public List<DraftLine> Lines { get; } = [];

    public BaseUnit OutputBaseUnit => OutputUnit switch
    {
        MeasureUnit.Kg => BaseUnit.Gram,
        MeasureUnit.L => BaseUnit.Milliliter,
        MeasureUnit.Piece => BaseUnit.Piece,
        _ => throw new InvalidOperationException($"L'unité {OutputUnit} n'est pas une unité de production.")
    };

    public decimal? OutputQuantityInBase =>
        OutputQuantity is { } quantity ? UnitConverter.ToBase(quantity, OutputUnit, OutputBaseUnit, averagePieceWeightGrams: null).Value : null;

    /// <summary>Formulaire vide ; le taux de TVA d'un plat est pré-rempli avec celui du restaurant.</summary>
    public static RecipeDraft New(RecipeKind kind, decimal defaultVatRate) =>
        new(null, kind) { VatRate = kind == RecipeKind.Dish ? defaultVatRate : null };

    /// <summary>
    /// Formulaire d'une recette enregistrée. Les éléments des lignes sont retrouvés dans <paramref name="book"/>, qui contient aussi
    /// les éléments archivés : une ligne sur un élément archivé garde son nom.
    /// </summary>
    public static RecipeDraft From(Recipe recipe, RecipeBook book)
    {
        var draft = new RecipeDraft(recipe.Id, recipe.Kind)
        {
            Name = recipe.Name,
            Category = recipe.Category,
            PriceInclVat = recipe.SellingPriceInclVat,
            VatRate = recipe.VatRate,
            Instructions = recipe.Instructions,
            PublicDescription = recipe.PublicDescription,
            ShowOnVitrine = recipe.ShowOnVitrine
        };

        if (recipe.OutputQuantity is { } quantity && recipe.OutputUnit is { } unit)
            (draft.OutputQuantity, draft.OutputUnit) = ToDisplayUnit(quantity, unit);

        var ingredients = book.Ingredients.ToDictionary(i => i.Id);
        var preparations = book.Recipes.ToDictionary(r => r.Id);
        foreach (var line in recipe.Lines)
        {
            var draftLine = new DraftLine();
            draftLine.SetItem(Resolve(line, ingredients, preparations));
            draftLine.Quantity = line.Quantity;
            draftLine.Unit = line.Unit;
            draft.Lines.Add(draftLine);
        }

        return draft;
    }

    /// <summary>
    /// <see cref="Recipe"/> brouillon pour <c>RecipeBook.Cost</c> : <c>Id</c> de la recette (0 si elle est nouvelle), lignes complètes seulement,
    /// résolues par identifiant. <c>Rows[k]</c> est l'indice, dans le formulaire, de la ligne <c>k</c> du brouillon.
    /// </summary>
    public (Recipe Recipe, IReadOnlyList<int> Rows) ToCostingDraft()
    {
        var lines = new List<RecipeLine>(Lines.Count);
        var rows = new List<int>(Lines.Count);
        for (var row = 0; row < Lines.Count; row++)
        {
            if (!Lines[row].IsComplete)
                continue;

            lines.Add(ToEntity(Lines[row]));
            rows.Add(row);
        }

        var isDish = Kind == RecipeKind.Dish;
        var recipe = new Recipe
        {
            Id = Id ?? 0,
            Name = Name,
            Kind = Kind,
            Category = Category,
            SellingPriceInclVat = isDish ? PriceInclVat : null,
            VatRate = isDish ? VatRate : null,
            OutputQuantity = isDish ? null : OutputQuantityInBase,
            OutputUnit = isDish ? null : OutputBaseUnit,
            Lines = lines
        };

        return (recipe, rows);
    }

    /// <summary>Chiffre le formulaire tel qu'il est, sans accès à la base : les prix et les recettes viennent de <paramref name="book"/>.</summary>
    public DraftEvaluation Evaluate(RecipeBook book)
    {
        var (recipe, rowOfLine) = ToCostingDraft();
        var cost = book.Cost(recipe);

        var rows = new LineEvaluation?[Lines.Count];
        foreach (var line in cost.Lines)
        {
            var issues = cost.Issues.Where(i => i.LineIndex == line.LineIndex).ToList();
            rows[rowOfLine[line.LineIndex]] = new LineEvaluation(line.Cost, issues);
        }

        return new DraftEvaluation(cost, book.Allergens(recipe), rows, recipe.OutputQuantity, recipe.OutputUnit);
    }

    /// <summary>
    /// Saisie du service. Une ligne sans élément ou sans quantité positive est refusée (avec son numéro) plutôt que d'être
    /// enregistrée à moitié : ces lignes ne comptent pas dans le chiffrage en direct, le chef doit les compléter ou les supprimer.
    /// </summary>
    /// <exception cref="DomainValidationException">Une ligne est incomplète.</exception>
    public RecipeInput ToInput()
    {
        var lines = new List<RecipeLineInput>(Lines.Count);
        for (var row = 0; row < Lines.Count; row++)
        {
            var line = Lines[row];
            if (line.Item is not { } item)
                throw new DomainValidationException($"Ligne {row + 1} : choisissez un ingrédient ou une préparation");
            if (line.Quantity is not { } quantity || quantity <= 0m)
                throw new DomainValidationException($"Ligne {row + 1} : la quantité doit être supérieure à 0");

            lines.Add(item.Kind == RecipeItemKind.Ingredient
                ? new RecipeLineInput(item.Id, null, quantity, line.Unit)
                : new RecipeLineInput(null, item.Id, quantity, line.Unit));
        }

        var isDish = Kind == RecipeKind.Dish;
        return new RecipeInput(
            Id,
            Name,
            Kind,
            Category,
            isDish ? PriceInclVat : null,
            isDish ? VatRate : null,
            isDish ? null : OutputQuantityInBase,
            isDish ? null : OutputBaseUnit,
            Instructions,
            lines,
            isDish ? PublicDescription : null,
            isDish && ShowOnVitrine);
    }

    private static RecipeLine ToEntity(DraftLine line) => new()
    {
        IngredientId = line.Item!.Kind == RecipeItemKind.Ingredient ? line.Item.Id : null,
        SubRecipeId = line.Item.Kind == RecipeItemKind.Preparation ? line.Item.Id : null,
        Quantity = line.Quantity!.Value,
        Unit = line.Unit
    };

    private static RecipeItem? Resolve(RecipeLine line, Dictionary<int, Ingredient> ingredients, Dictionary<int, Recipe> preparations)
    {
        if (line.IngredientId is { } ingredientId)
            return ingredients.TryGetValue(ingredientId, out var ingredient)
                ? new RecipeItem(RecipeItemKind.Ingredient, ingredient.Id, ingredient.Name, ingredient.BaseUnit, ingredient.IsArchived)
                : Unknown(RecipeItemKind.Ingredient, ingredientId);

        if (line.SubRecipeId is { } subRecipeId)
            return preparations.TryGetValue(subRecipeId, out var preparation)
                ? RecipeItem.ToItem(preparation)
                : Unknown(RecipeItemKind.Preparation, subRecipeId);

        return null;
    }

    private static RecipeItem Unknown(RecipeItemKind kind, int id) => new(kind, id, "Élément introuvable", BaseUnit.Gram);

    private static (decimal Quantity, MeasureUnit Unit) ToDisplayUnit(decimal quantity, BaseUnit unit) => unit switch
    {
        BaseUnit.Gram => (quantity / 1000m, MeasureUnit.Kg),
        BaseUnit.Milliliter => (quantity / 1000m, MeasureUnit.L),
        BaseUnit.Piece => (quantity, MeasureUnit.Piece),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
    };
}
