using ChefOS.Back.Components.Pages.Recipes;
using ChefOS.Core.Common;
using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class RecipeDraftTests
{
    private const int Carrots = 1;      // 1,00 €/kg, rendement 100 %
    private const int Saffron = 2;      // sans prix
    private const int Egg = 3;          // à la pièce, 0,30 €, sans poids moyen
    private const int Thyme = 4;        // archivé, 20,00 €/kg
    private const int Milk = 5;         // 1,00 €/l, lait
    private const int Stock = 10;       // préparation : 2 l pour 1 kg de carottes (1,00 €)
    private const int OldStock = 11;    // préparation archivée
    private const int Sauce = 12;       // préparation qui contient le fond (10)
    private const int StoredDish = 20;

    private readonly RecipeBook book = BuildBook();

    // ── Valeurs par défaut ───────────────────────────────────────────────────

    [Fact]
    public void New_Dish_DefaultsTheVatToTheRestaurantRate()
    {
        Assert.Equal(0.12m, RecipeDraft.New(RecipeKind.Dish, 0.12m).VatRate);
        Assert.Equal(0.21m, RecipeDraft.New(RecipeKind.Dish, 0.21m).VatRate);
    }

    [Fact]
    public void New_Draft_HasNoIdAndNoLine()
    {
        var draft = RecipeDraft.New(RecipeKind.Dish, 0.12m);

        Assert.Null(draft.Id);
        Assert.Empty(draft.Lines);
    }

    // ── Quantité produite : unités d'affichage ↔ unités de base ──────────────

    [Theory]
    [InlineData(MeasureUnit.Kg, 2, BaseUnit.Gram, 2000)]
    [InlineData(MeasureUnit.Kg, 0.45, BaseUnit.Gram, 450)]
    [InlineData(MeasureUnit.L, 1.5, BaseUnit.Milliliter, 1500)]
    [InlineData(MeasureUnit.Piece, 12, BaseUnit.Piece, 12)]
    public void ToInput_Preparation_ConvertsTheOutputToTheBaseUnit(MeasureUnit unit, double quantity, BaseUnit expectedUnit, double expectedQuantity)
    {
        var draft = RecipeDraft.New(RecipeKind.Preparation, 0.12m);
        draft.Name = "Fond";
        draft.OutputUnit = unit;
        draft.OutputQuantity = (decimal)quantity;

        var input = draft.ToInput();

        Assert.Equal((decimal)expectedQuantity, input.OutputQuantity);
        Assert.Equal(expectedUnit, input.OutputUnit);
    }

    [Fact]
    public void ToInput_Preparation_WithoutOutput_SendsNoQuantity_SoTheServiceRefusesIt()
    {
        var draft = RecipeDraft.New(RecipeKind.Preparation, 0.12m);
        draft.Name = "Fond";
        draft.OutputQuantity = null;

        Assert.Null(draft.ToInput().OutputQuantity);
    }

    [Theory]
    [InlineData(BaseUnit.Gram, 450, MeasureUnit.Kg, 0.45)]
    [InlineData(BaseUnit.Milliliter, 2000, MeasureUnit.L, 2)]
    [InlineData(BaseUnit.Piece, 12, MeasureUnit.Piece, 12)]
    public void From_Preparation_ShowsTheOutputInTheDisplayUnit(BaseUnit unit, double stored, MeasureUnit expectedUnit, double expectedQuantity)
    {
        var recipe = new Recipe { Id = 50, Name = "P", Kind = RecipeKind.Preparation, OutputQuantity = (decimal)stored, OutputUnit = unit };

        var draft = RecipeDraft.From(recipe, book);

        Assert.Equal(expectedUnit, draft.OutputUnit);
        Assert.Equal((decimal)expectedQuantity, draft.OutputQuantity);
    }

    [Fact]
    public void FromThenToInput_RestoresTheStoredOutputExactly()
    {
        var stored = book.Recipes.Single(r => r.Id == Stock);

        var input = RecipeDraft.From(stored, book).ToInput();

        Assert.Equal(stored.OutputQuantity, input.OutputQuantity);
        Assert.Equal(stored.OutputUnit, input.OutputUnit);
    }

    // ── Envoi au service ─────────────────────────────────────────────────────

    [Fact]
    public void ToInput_Dish_CarriesPriceAndVat_AndNoOutput()
    {
        var draft = RecipeDraft.New(RecipeKind.Dish, 0.12m);
        draft.Name = "Carbonnade";
        draft.Category = "Plats";
        draft.PriceInclVat = 22.5m;
        draft.Instructions = "Mijoter.";

        var input = draft.ToInput();

        Assert.Null(input.Id);
        Assert.Equal(RecipeKind.Dish, input.Kind);
        Assert.Equal("Carbonnade", input.Name);
        Assert.Equal("Plats", input.Category);
        Assert.Equal(22.5m, input.SellingPriceInclVat);
        Assert.Equal(0.12m, input.VatRate);
        Assert.Null(input.OutputQuantity);
        Assert.Null(input.OutputUnit);
        Assert.Equal("Mijoter.", input.Instructions);
    }

    [Fact]
    public void ToInput_Dish_CarriesPublicDescriptionAndVitrineFlag()
    {
        var draft = RecipeDraft.New(RecipeKind.Dish, 0.12m);
        draft.Name = "Carbonnade";
        draft.PriceInclVat = 22.5m;
        draft.PublicDescription = "Mijotée à la bière brune";
        draft.ShowOnVitrine = true;

        var input = draft.ToInput();

        Assert.Equal("Mijotée à la bière brune", input.PublicDescription);
        Assert.True(input.ShowOnVitrine);
    }

    [Fact]
    public void ToInput_Preparation_CarriesNoPublicFields()
    {
        var draft = RecipeDraft.New(RecipeKind.Preparation, 0.12m);
        draft.Name = "Fond";
        draft.OutputQuantity = 2m;
        draft.PublicDescription = "Interne";
        draft.ShowOnVitrine = true;

        var input = draft.ToInput();

        Assert.Null(input.PublicDescription);
        Assert.False(input.ShowOnVitrine);
    }

    [Fact]
    public void From_StoredRecipe_RestoresPublicFields()
    {
        var recipe = book.Recipes.Single(r => r.Id == StoredDish);
        recipe.PublicDescription = "Maison";
        recipe.ShowOnVitrine = true;

        var draft = RecipeDraft.From(recipe, book);

        Assert.Equal("Maison", draft.PublicDescription);
        Assert.True(draft.ShowOnVitrine);
        Assert.True(draft.ToInput().ShowOnVitrine);
    }

    [Fact]
    public void ToInput_Preparation_CarriesNoPriceNorVat()
    {
        var draft = RecipeDraft.New(RecipeKind.Preparation, 0.12m);
        draft.Name = "Fond";
        draft.OutputQuantity = 2m;
        draft.OutputUnit = MeasureUnit.L;
        draft.PriceInclVat = 10m;

        var input = draft.ToInput();

        Assert.Null(input.SellingPriceInclVat);
        Assert.Null(input.VatRate);
    }

    [Fact]
    public void ToInput_MapsLinesFromBothSources()
    {
        var draft = RecipeDraft.New(RecipeKind.Dish, 0.12m);
        draft.Name = "Plat";
        draft.PriceInclVat = 20m;
        draft.Lines.Add(Line(Ingredient(Carrots), 250m, MeasureUnit.G));
        draft.Lines.Add(Line(Preparation(Stock), 1.5m, MeasureUnit.L));

        var lines = draft.ToInput().Lines;

        Assert.Equal(
            [new RecipeLineInput(Carrots, null, 250m, MeasureUnit.G), new RecipeLineInput(null, Stock, 1.5m, MeasureUnit.L)],
            lines);
    }

    [Fact]
    public void ToInput_ExistingRecipe_CarriesItsId()
    {
        var draft = RecipeDraft.From(book.Recipes.Single(r => r.Id == StoredDish), book);

        Assert.Equal(StoredDish, draft.ToInput().Id);
    }

    [Fact]
    public void ToInput_LineWithoutItem_IsBlocked_AndNamesTheRow()
    {
        var draft = DishDraft(Line(Ingredient(Carrots), 100m), Line(null, 50m));

        var error = Assert.Throws<DomainValidationException>(() => draft.ToInput());

        Assert.Contains("Ligne 2", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void ToInput_LineWithoutPositiveQuantity_IsBlocked_AndNamesTheRow(double? quantity)
    {
        var draft = DishDraft(Line(Ingredient(Carrots), quantity is { } q ? (decimal)q : null));

        var error = Assert.Throws<DomainValidationException>(() => draft.ToInput());

        Assert.Contains("Ligne 1", error.Message);
    }

    // ── Brouillon de chiffrage ───────────────────────────────────────────────

    [Fact]
    public void CostingDraft_SkipsIncompleteLines_AndRemembersTheVisibleRows()
    {
        var draft = DishDraft(
            Line(null, 10m),                       // 0 : pas d'élément
            Line(Ingredient(Carrots), 500m),       // 1
            Line(Ingredient(Milk), null),          // 2 : pas de quantité
            Line(Ingredient(Saffron), 1m),         // 3
            Line(Ingredient(Egg), 0m));            // 4 : quantité nulle

        var (recipe, rows) = draft.ToCostingDraft();

        Assert.Equal(2, recipe.Lines.Count);
        Assert.Equal([1, 3], rows);
        Assert.Equal(Carrots, recipe.Lines[0].IngredientId);
        Assert.Equal(Saffron, recipe.Lines[1].IngredientId);
    }

    [Fact]
    public void CostingDraft_UsesTheStoredIdOrZero_AndNeedsNoNavigation()
    {
        var created = DishDraft(Line(Ingredient(Carrots), 100m));
        var stored = RecipeDraft.From(book.Recipes.Single(r => r.Id == StoredDish), book);

        Assert.Equal(0, created.ToCostingDraft().Recipe.Id);
        Assert.Equal(StoredDish, stored.ToCostingDraft().Recipe.Id);
        Assert.All(created.ToCostingDraft().Recipe.Lines, l => Assert.Null(l.Ingredient));
    }

    [Fact]
    public void CostingDraft_Preparation_ComesWithItsOutputInBaseUnits()
    {
        var draft = RecipeDraft.New(RecipeKind.Preparation, 0.12m);
        draft.OutputQuantity = 2m;
        draft.OutputUnit = MeasureUnit.L;

        var recipe = draft.ToCostingDraft().Recipe;

        Assert.Equal(2000m, recipe.OutputQuantity);
        Assert.Equal(BaseUnit.Milliliter, recipe.OutputUnit);
    }

    // ── Chiffrage en direct ──────────────────────────────────────────────────

    [Fact]
    public void Evaluate_MapsLineCostsBackToTheVisibleRows()
    {
        var draft = DishDraft(
            Line(null, 10m),                       // 0 : ignorée
            Line(Ingredient(Carrots), 500m),       // 1 : 0,50 €
            Line(Ingredient(Milk), null),          // 2 : ignorée
            Line(Ingredient(Saffron), 1m));        // 3 : prix manquant

        var live = draft.Evaluate(book);

        Assert.Equal(4, live.Rows.Count);
        Assert.Null(live.Rows[0]);
        Assert.Equal(0.5m, live.Rows[1]!.Cost);
        Assert.Empty(live.Rows[1]!.Issues);
        Assert.Null(live.Rows[2]);
        Assert.Null(live.Rows[3]!.Cost);
        var issue = Assert.Single(live.Rows[3]!.Issues);
        Assert.Equal(CostIssueKind.MissingPrice, issue.Kind);
        Assert.Equal("Safran", issue.ItemName);
    }

    [Fact]
    public void Evaluate_IncompleteCost_ListsTheIssues()
    {
        var live = DishDraft(Line(Ingredient(Carrots), 500m), Line(Ingredient(Saffron), 1m)).Evaluate(book);

        Assert.False(live.Cost.IsComplete);
        Assert.Equal(0.5m, live.Cost.TotalCost);
        Assert.Contains(live.Cost.Issues, i => i.Kind == CostIssueKind.MissingPrice && i.ItemName == "Safran");
    }

    [Fact]
    public void Evaluate_PieceIngredientInGrams_WithoutAverageWeight_IsAnIncompatibleUnit()
    {
        var live = DishDraft(Line(Ingredient(Egg), 100m, MeasureUnit.G)).Evaluate(book);

        var issue = Assert.Single(live.Rows[0]!.Issues);
        Assert.Equal(CostIssueKind.IncompatibleUnit, issue.Kind);
        Assert.Null(live.Rows[0]!.Cost);
    }

    [Fact]
    public void Evaluate_ComputesFoodCostAndMarginFromThePriceAndVat()
    {
        var draft = DishDraft(Line(Ingredient(Carrots), 1000m));     // 1,00 €
        draft.PriceInclVat = 11.2m;                                  // 10,00 € HT à 12 %

        var live = draft.Evaluate(book);

        Assert.Equal(10m, live.Cost.PriceExclVat);
        Assert.Equal(0.1m, live.Cost.FoodCostPercent);
        Assert.Equal(9m, live.Cost.Margin);
    }

    [Fact]
    public void Evaluate_FollowsTheFormWithoutReloadingTheBook()
    {
        var draft = DishDraft(Line(Ingredient(Carrots), 500m));
        var before = draft.Evaluate(book).Cost.TotalCost;

        draft.Lines[0].Quantity = 1000m;
        var after = draft.Evaluate(book).Cost.TotalCost;

        Assert.Equal(0.5m, before);
        Assert.Equal(1m, after);
    }

    [Fact]
    public void Evaluate_DeducesAllergensFromIngredientsAndPreparations()
    {
        var draft = DishDraft(Line(Ingredient(Carrots), 100m), Line(Ingredient(Milk), 100m, MeasureUnit.Ml), Line(Preparation(Stock), 100m, MeasureUnit.Ml));

        Assert.Equal(EuAllergen.Milk | EuAllergen.Celery, draft.Evaluate(book).Allergens);
    }

    [Fact]
    public void Evaluate_AddingAPreparationThatContainsTheRecipe_ReportsALoopOnThatRow()
    {
        var stock = RecipeDraft.From(book.Recipes.Single(r => r.Id == Stock), book);
        stock.Lines.Add(Line(Preparation(Sauce), 100m, MeasureUnit.Ml));

        var live = stock.Evaluate(book);

        // Le problème remonte de la sauce vers la ligne parente : la boucle se referme sur le fond lui-même.
        var issue = Assert.Single(live.Rows[^1]!.Issues, i => i.Kind == CostIssueKind.Cycle);
        Assert.Equal("Fond de veau", issue.ItemName);
        Assert.False(live.Cost.IsComplete);
    }

    [Fact]
    public void Evaluate_PreparationUsedInADish_CostsItPerOutputUnit()
    {
        var live = DishDraft(Line(Preparation(Stock), 500m, MeasureUnit.Ml)).Evaluate(book);

        Assert.Equal(0.25m, live.Cost.TotalCost);          // 500 ml × 1,00 € / 2000 ml
    }

    [Fact]
    public void Evaluate_ExposesTheOutputInBaseUnits_ForThePerUnitCost()
    {
        var draft = RecipeDraft.New(RecipeKind.Preparation, 0.12m);
        draft.OutputQuantity = 2m;
        draft.OutputUnit = MeasureUnit.L;

        var live = draft.Evaluate(book);

        Assert.Equal(2000m, live.OutputQuantity);
        Assert.Equal(BaseUnit.Milliliter, live.OutputUnit);
    }

    // ── Éléments proposés ────────────────────────────────────────────────────

    [Fact]
    public void Candidates_ExcludeArchivedItems_AndTheRecipeItself()
    {
        var candidates = RecipeItem.Candidates(book, excludeRecipeId: Stock);

        Assert.DoesNotContain(candidates, c => c.Kind == RecipeItemKind.Ingredient && c.Id == Thyme);
        Assert.DoesNotContain(candidates, c => c.Kind == RecipeItemKind.Preparation && c.Id == OldStock);
        Assert.DoesNotContain(candidates, c => c.Kind == RecipeItemKind.Preparation && c.Id == Stock);
        Assert.Contains(candidates, c => c.Kind == RecipeItemKind.Ingredient && c.Id == Carrots);
        Assert.Contains(candidates, c => c.Kind == RecipeItemKind.Preparation && c.Id == Sauce);
    }

    [Fact]
    public void Candidates_NeverOfferDishes_AsSubRecipes()
    {
        var candidates = RecipeItem.Candidates(book, excludeRecipeId: null);

        Assert.DoesNotContain(candidates, c => c.Kind == RecipeItemKind.Preparation && c.Id == StoredDish);
    }

    [Fact]
    public void Candidates_AreListedByName_IngredientsThenPreparations()
    {
        var candidates = RecipeItem.Candidates(book, excludeRecipeId: null);

        var firstPreparation = candidates.ToList().FindIndex(c => c.Kind == RecipeItemKind.Preparation);
        Assert.All(candidates.Take(firstPreparation), c => Assert.Equal(RecipeItemKind.Ingredient, c.Kind));
        Assert.Equal(candidates.Take(firstPreparation).OrderBy(c => c.Name, StringComparer.InvariantCultureIgnoreCase).Select(c => c.Id),
            candidates.Take(firstPreparation).Select(c => c.Id));
    }

    [Fact]
    public void Label_PrefixesTheKind()
    {
        Assert.Equal("🥕 Carottes", Ingredient(Carrots).Label);
        Assert.Equal("🍲 Fond de veau", Preparation(Stock).Label);
    }

    [Fact]
    public void From_ExistingLineOnAnArchivedItem_StillShowsItsName()
    {
        var recipe = new Recipe
        {
            Id = 60, Name = "Plat", Kind = RecipeKind.Dish, SellingPriceInclVat = 20m, VatRate = 0.12m,
            Lines = [new RecipeLine { IngredientId = Thyme, Quantity = 10m, Unit = MeasureUnit.G }, new RecipeLine { SubRecipeId = OldStock, Quantity = 100m, Unit = MeasureUnit.Ml }]
        };

        var draft = RecipeDraft.From(recipe, book);

        Assert.Equal("Thym", draft.Lines[0].Item!.Name);
        Assert.True(draft.Lines[0].Item!.IsArchived);
        Assert.Contains("Thym", draft.Lines[0].Item!.Label);
        Assert.Equal("Ancien fond", draft.Lines[1].Item!.Name);
        Assert.Equal(RecipeItemKind.Preparation, draft.Lines[1].Item!.Kind);
        Assert.Equal(0.2m, draft.Evaluate(book).Rows[0]!.Cost);      // l'élément archivé reste chiffré
    }

    [Fact]
    public void From_LoadsTheStoredLinesInOrder()
    {
        var draft = RecipeDraft.From(book.Recipes.Single(r => r.Id == StoredDish), book);

        Assert.Equal("Plat enregistré", draft.Name);
        Assert.Equal(22.5m, draft.PriceInclVat);
        Assert.Equal(0.12m, draft.VatRate);
        Assert.Equal([RecipeItemKind.Ingredient, RecipeItemKind.Preparation], draft.Lines.Select(l => l.Item!.Kind));
        Assert.Equal([200m, 100m], draft.Lines.Select(l => l.Quantity!.Value));
        Assert.Equal([MeasureUnit.G, MeasureUnit.Ml], draft.Lines.Select(l => l.Unit));
    }

    // ── Unité par défaut d'une ligne ─────────────────────────────────────────

    [Theory]
    [InlineData(BaseUnit.Gram, MeasureUnit.G)]
    [InlineData(BaseUnit.Milliliter, MeasureUnit.Ml)]
    [InlineData(BaseUnit.Piece, MeasureUnit.Piece)]
    public void SetItem_OnAnEmptyLine_ChoosesTheUnitOfTheItem(BaseUnit baseUnit, MeasureUnit expected)
    {
        var line = new DraftLine();

        line.SetItem(new RecipeItem(RecipeItemKind.Ingredient, 99, "X", baseUnit));

        Assert.Equal(expected, line.Unit);
    }

    [Fact]
    public void SetItem_KeepsTheUnitWhenTheNewItemIsOfTheSameKind()
    {
        var line = new DraftLine();
        line.SetItem(Ingredient(Carrots));
        line.Unit = MeasureUnit.Kg;

        line.SetItem(new RecipeItem(RecipeItemKind.Ingredient, 98, "Autre", BaseUnit.Gram));

        Assert.Equal(MeasureUnit.Kg, line.Unit);
    }

    [Fact]
    public void SetItem_SwitchesTheUnitWhenTheNewItemIsOfAnotherKind()
    {
        var line = new DraftLine();
        line.SetItem(Ingredient(Carrots));
        line.Unit = MeasureUnit.Kg;

        line.SetItem(Ingredient(Milk));

        Assert.Equal(MeasureUnit.Ml, line.Unit);
    }

    // ── Aides ────────────────────────────────────────────────────────────────

    private RecipeDraft DishDraft(params DraftLine[] lines)
    {
        var draft = RecipeDraft.New(RecipeKind.Dish, 0.12m);
        draft.Name = "Plat";
        draft.PriceInclVat = 20m;
        draft.Lines.AddRange(lines);
        return draft;
    }

    private static DraftLine Line(RecipeItem? item, decimal? quantity, MeasureUnit unit = MeasureUnit.G)
    {
        var line = new DraftLine { Quantity = quantity };
        line.SetItem(item);
        line.Unit = unit;
        return line;
    }

    private RecipeItem Ingredient(int id)
    {
        var ingredient = book.Ingredients.Single(i => i.Id == id);
        return new RecipeItem(RecipeItemKind.Ingredient, id, ingredient.Name, ingredient.BaseUnit, ingredient.IsArchived);
    }

    private RecipeItem Preparation(int id)
    {
        var recipe = book.Recipes.Single(r => r.Id == id);
        return new RecipeItem(RecipeItemKind.Preparation, id, recipe.Name, recipe.OutputUnit ?? BaseUnit.Gram, recipe.IsArchived);
    }

    private static RecipeBook BuildBook()
    {
        var ingredients = new List<Ingredient>
        {
            new() { Id = Carrots, Name = "Carottes", BaseUnit = BaseUnit.Gram, CurrentPricePerBaseUnit = 0.001m, Allergens = EuAllergen.Celery },
            new() { Id = Saffron, Name = "Safran", BaseUnit = BaseUnit.Gram, CurrentPricePerBaseUnit = null },
            new() { Id = Egg, Name = "Œuf", BaseUnit = BaseUnit.Piece, CurrentPricePerBaseUnit = 0.30m },
            new() { Id = Thyme, Name = "Thym", BaseUnit = BaseUnit.Gram, CurrentPricePerBaseUnit = 0.02m, IsArchived = true },
            new() { Id = Milk, Name = "Lait", BaseUnit = BaseUnit.Milliliter, CurrentPricePerBaseUnit = 0.001m, Allergens = EuAllergen.Milk }
        };

        // Dans ce jeu d'essai, les carottes portent le céleri et le lait le lait : de quoi vérifier les allergènes déduits.
        var recipes = new List<Recipe>
        {
            new()
            {
                Id = Stock, Name = "Fond de veau", Kind = RecipeKind.Preparation, OutputQuantity = 2000m, OutputUnit = BaseUnit.Milliliter,
                Lines = [new RecipeLine { IngredientId = Carrots, Quantity = 1000m, Unit = MeasureUnit.G }]
            },
            new()
            {
                Id = OldStock, Name = "Ancien fond", Kind = RecipeKind.Preparation, OutputQuantity = 1000m, OutputUnit = BaseUnit.Milliliter, IsArchived = true,
                Lines = [new RecipeLine { IngredientId = Thyme, Quantity = 1000m, Unit = MeasureUnit.G }]   // 20,00 € pour 1 l
            },
            new()
            {
                Id = Sauce, Name = "Sauce", Kind = RecipeKind.Preparation, OutputQuantity = 1000m, OutputUnit = BaseUnit.Milliliter,
                Lines = [new RecipeLine { SubRecipeId = Stock, Quantity = 500m, Unit = MeasureUnit.Ml }]
            },
            new()
            {
                Id = StoredDish, Name = "Plat enregistré", Kind = RecipeKind.Dish, SellingPriceInclVat = 22.5m, VatRate = 0.12m,
                Lines =
                [
                    new RecipeLine { IngredientId = Carrots, Quantity = 200m, Unit = MeasureUnit.G },
                    new RecipeLine { SubRecipeId = Stock, Quantity = 100m, Unit = MeasureUnit.Ml }
                ]
            }
        };

        return new RecipeBook(recipes, ingredients);
    }
}
