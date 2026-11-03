using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Units;

namespace ChefOS.Tests.Core;

public class IngredientMatcherTests
{
    private static Ingredient Ing(int id, string name, bool archived = false)
        => new() { Id = id, Name = name, BaseUnit = BaseUnit.Gram, IsArchived = archived };

    private static SupplierAlias Alias(string normalizedLabel, int ingredientId, int? supplierId)
        => new() { NormalizedLabel = normalizedLabel, IngredientId = ingredientId, SupplierId = supplierId };

    private static readonly SupplierAlias[] NoAliases = [];

    [Fact]
    public void Suggest_SupplierAliasWins()
    {
        var ingredients = new[] { Ing(1, "Beurre doux"), Ing(2, "Beurre de cuisine"), Ing(3, "Beurre clarifié") };
        var aliases = new[]
        {
            Alias("BEURRE DOUX 82 1KG", 3, supplierId: null),
            Alias("BEURRE DOUX 82 1KG", 2, supplierId: 7),
            Alias("BEURRE DOUX 82 1KG", 1, supplierId: 8),
        };

        var id = IngredientMatcher.Suggest("Beurre doux 82% 1kg", 7, aliases, ingredients);

        Assert.Equal(2, id);
    }

    [Fact]
    public void Suggest_GlobalAliasWhenNoSupplierAlias()
    {
        var ingredients = new[] { Ing(1, "Beurre doux"), Ing(3, "Beurre clarifié") };
        var aliases = new[]
        {
            Alias("BEURRE DOUX 82 1KG", 1, supplierId: 8),
            Alias("BEURRE DOUX 82 1KG", 3, supplierId: null),
        };

        var id = IngredientMatcher.Suggest("BEURRE DOUX 82% 1KG", 7, aliases, ingredients);

        Assert.Equal(3, id);
    }

    [Fact]
    public void Suggest_GlobalAliasWhenSupplierUnknown()
    {
        var ingredients = new[] { Ing(1, "Beurre doux"), Ing(3, "Beurre clarifié") };
        var aliases = new[]
        {
            Alias("BEURRE DOUX 82 1KG", 1, supplierId: 8),
            Alias("BEURRE DOUX 82 1KG", 3, supplierId: null),
        };

        var id = IngredientMatcher.Suggest("BEURRE DOUX 82% 1KG", null, aliases, ingredients);

        Assert.Equal(3, id);
    }

    [Fact]
    public void Suggest_ByNameSimilarity()
    {
        var ingredients = new[] { Ing(1, "Farine T55"), Ing(2, "Beurre doux"), Ing(3, "Crème fraîche") };

        var id = IngredientMatcher.Suggest("BEURRE DOUX 82% 1KG", 7, NoAliases, ingredients);

        Assert.Equal(2, id);
    }

    [Fact]
    public void Suggest_ByNameSimilarity_BestScoreWins()
    {
        // « CREME FRAICHE 40 » : 2/2 pour « Crème fraîche », 2/3 pour « Crème fraîche épaisse », 1/2 pour « Crème anglaise ».
        var ingredients = new[] { Ing(5, "Crème fraîche épaisse"), Ing(4, "Crème fraîche"), Ing(6, "Crème anglaise") };

        var id = IngredientMatcher.Suggest("CREME FRAICHE 40%", null, NoAliases, ingredients);

        Assert.Equal(4, id);
    }

    [Fact]
    public void Suggest_SimilarityTie_SmallestIdWins()
    {
        var ingredients = new[] { Ing(9, "Beurre doux"), Ing(7, "Beurre doux"), Ing(8, "Beurre doux") };

        var id = IngredientMatcher.Suggest("BEURRE DOUX 1KG", null, NoAliases, ingredients);

        Assert.Equal(7, id);
    }

    [Fact]
    public void Suggest_ExactlyAtThreshold_Matches()
    {
        // 1 mot commun sur 2 = 0.5.
        var ingredients = new[] { Ing(1, "Poulet fermier") };

        var id = IngredientMatcher.Suggest("POULET 1KG X", null, NoAliases, ingredients);

        Assert.Equal(1, id);
    }

    [Fact]
    public void Suggest_BelowThreshold_Null()
    {
        // 1 mot commun sur 3 = 0.33.
        var ingredients = new[] { Ing(1, "Sucre glace fin") };

        var id = IngredientMatcher.Suggest("SUCRE ROUX 1KG", null, NoAliases, ingredients);

        Assert.Null(id);
    }

    [Fact]
    public void Suggest_NoCommonWord_Null()
    {
        var ingredients = new[] { Ing(1, "Farine T55") };

        Assert.Null(IngredientMatcher.Suggest("Beurre doux", null, NoAliases, ingredients));
        Assert.Null(IngredientMatcher.Suggest("***", null, NoAliases, ingredients));
        Assert.Null(IngredientMatcher.Suggest("Beurre doux", null, NoAliases, []));
    }

    [Fact]
    public void Suggest_SkipsArchivedIngredientsAndAliasesToArchived()
    {
        var ingredients = new[]
        {
            Ing(1, "Beurre doux", archived: true),
            Ing(2, "Beurre doux 82", archived: false),
            Ing(3, "Beurre clarifié", archived: true),
        };
        var aliases = new[]
        {
            Alias("BEURRE DOUX", 1, supplierId: 7),     // vers un archivé : ignoré
            Alias("BEURRE DOUX", 99, supplierId: null), // vers un ingrédient inconnu : ignoré
        };

        var id = IngredientMatcher.Suggest("Beurre doux", 7, aliases, ingredients);

        Assert.Equal(2, id);
    }

    [Fact]
    public void Suggest_SkippedSupplierAlias_FallsBackToGlobalAlias()
    {
        var ingredients = new[] { Ing(1, "Beurre doux", archived: true), Ing(2, "Beurre clarifié") };
        var aliases = new[]
        {
            Alias("BEURRE DOUX", 1, supplierId: 7),
            Alias("BEURRE DOUX", 2, supplierId: null),
        };

        var id = IngredientMatcher.Suggest("Beurre doux", 7, aliases, ingredients);

        Assert.Equal(2, id);
    }

    [Fact]
    public void Suggest_OnlyArchivedMatches_Null()
    {
        var ingredients = new[] { Ing(1, "Beurre doux", archived: true) };

        Assert.Null(IngredientMatcher.Suggest("Beurre doux", null, NoAliases, ingredients));
    }

    [Theory]
    [InlineData("BEURRE DOUX 82% 1KG", "Beurre doux", 1.0)]
    [InlineData("Beurre doux", "BEURRE DOUX 82% 1KG", 1.0)]
    [InlineData("Sucre roux 1kg", "Sucre glace fin", 1.0 / 3.0)]
    [InlineData("Farine", "Beurre", 0.0)]
    [InlineData("", "Beurre", 0.0)]
    [InlineData("Beurre", "---", 0.0)]
    [InlineData("A B", "A", 0.0)]            // mots d'un seul caractère ignorés → aucun mot
    [InlineData("Beurre beurre doux", "Beurre", 1.0)]  // mots dédoublonnés
    public void Similarity(string a, string b, double expected)
        => Assert.Equal(expected, IngredientMatcher.Similarity(a, b), 10);
}
