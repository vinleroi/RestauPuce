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

        var suggestion = IngredientMatcher.Suggest("Beurre doux 82% 1kg", 7, aliases, ingredients);

        Assert.Equal(new IngredientSuggestion(2, MatchSource.SupplierAlias), suggestion);
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

        var suggestion = IngredientMatcher.Suggest("BEURRE DOUX 82% 1KG", 7, aliases, ingredients);

        Assert.Equal(new IngredientSuggestion(3, MatchSource.GlobalAlias), suggestion);
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

        var suggestion = IngredientMatcher.Suggest("BEURRE DOUX 82% 1KG", null, aliases, ingredients);

        Assert.Equal(new IngredientSuggestion(3, MatchSource.GlobalAlias), suggestion);
    }

    [Fact]
    public void Suggest_ReportsSource()
    {
        var ingredients = new[] { Ing(1, "Beurre doux"), Ing(2, "Beurre clarifié"), Ing(3, "Farine") };
        var aliases = new[]
        {
            Alias("BEURRE CLARIFIE 1KG", 2, supplierId: null),
            Alias("FARINE T55 5KG", 3, supplierId: 7),
        };

        var supplierAlias = IngredientMatcher.Suggest("Farine T55 5kg", 7, aliases, ingredients);
        var globalAlias = IngredientMatcher.Suggest("Beurre clarifié 1kg", 7, aliases, ingredients);
        var nameMatch = IngredientMatcher.Suggest("Beurre doux 82% 1kg", 7, aliases, ingredients);

        Assert.Equal(new IngredientSuggestion(3, MatchSource.SupplierAlias), supplierAlias);
        Assert.Equal(new IngredientSuggestion(2, MatchSource.GlobalAlias), globalAlias);
        Assert.Equal(new IngredientSuggestion(1, MatchSource.NameMatch), nameMatch);
    }

    [Fact]
    public void Suggest_ByNameSimilarity()
    {
        var ingredients = new[] { Ing(1, "Farine T55"), Ing(2, "Beurre doux"), Ing(3, "Crème fraîche") };

        var suggestion = IngredientMatcher.Suggest("BEURRE DOUX 82% 1KG", 7, NoAliases, ingredients);

        Assert.Equal(new IngredientSuggestion(2, MatchSource.NameMatch), suggestion);
    }

    [Fact]
    public void Suggest_ByNameSimilarity_BestScoreWins()
    {
        // « POULET ROTI ENTIER 2KG » : 1/2 pour « Poulet fermier » et « Poulet bio », 2/2 pour « Poulet rôti ».
        // Le meilleur score a le plus grand id : un « plus petit id éligible » se tromperait.
        var ingredients = new[] { Ing(1, "Poulet fermier"), Ing(2, "Poulet bio"), Ing(3, "Poulet rôti") };

        var suggestion = IngredientMatcher.Suggest("POULET ROTI ENTIER 2KG", null, NoAliases, ingredients);

        Assert.Equal(new IngredientSuggestion(3, MatchSource.NameMatch), suggestion);
    }

    [Fact]
    public void Suggest_PrefersMoreSpecificIngredient()
    {
        // « CREME FRAICHE 40% 1L » : « Crème » (1 mot sur 1) et « Crème fraîche » (2 sur 2) font 1.0 ; plus de mots communs l'emporte.
        var ingredients = new[] { Ing(1, "Crème"), Ing(2, "Crème fraîche") };

        var suggestion = IngredientMatcher.Suggest("CREME FRAICHE 40% 1L", null, NoAliases, ingredients);

        Assert.Equal(new IngredientSuggestion(2, MatchSource.NameMatch), suggestion);
    }

    [Fact]
    public void Suggest_SameScoreAndCommonWords_FewerIngredientWordsWins()
    {
        // Les deux ingrédients font 2/2 avec 2 mots communs ; le nom le plus court (le plus précis) l'emporte, même avec un id plus grand.
        var ingredients = new[] { Ing(5, "Crème fraîche épaisse"), Ing(9, "Crème fraîche") };

        var suggestion = IngredientMatcher.Suggest("CREME FRAICHE 40%", null, NoAliases, ingredients);

        Assert.Equal(new IngredientSuggestion(9, MatchSource.NameMatch), suggestion);
    }

    [Fact]
    public void Suggest_SimilarityTie_SmallestIdWins()
    {
        var ingredients = new[] { Ing(9, "Beurre doux"), Ing(7, "Beurre doux"), Ing(8, "Beurre doux") };

        var suggestion = IngredientMatcher.Suggest("BEURRE DOUX 1KG", null, NoAliases, ingredients);

        Assert.Equal(new IngredientSuggestion(7, MatchSource.NameMatch), suggestion);
    }

    [Fact]
    public void Suggest_IgnoresStopWordsAndSizes()
    {
        // « DE », « 2KG » ne comptent pas : sans eux, rien ne relie ces deux libellés.
        var unrelated = new[] { Ing(1, "Cuisse de poulet 2kg") };
        Assert.Null(IngredientMatcher.Suggest("FILET DE BOEUF 2KG", null, NoAliases, unrelated));

        // Une taille seule ne relie pas non plus deux libellés (1 mot sur 2 sinon).
        var sizeOnly = new[] { Ing(1, "Farine 1kg") };
        Assert.Null(IngredientMatcher.Suggest("POULET 1KG", null, NoAliases, sizeOnly));

        // « Filet de bœuf » (2/2) l'emporte sur « Filet de poulet » (1/2, seul « FILET » est commun), même avec un id plus grand.
        var ingredients = new[] { Ing(1, "Filet de poulet"), Ing(2, "Filet de bœuf") };
        var suggestion = IngredientMatcher.Suggest("FILET DE BOEUF 2KG", null, NoAliases, ingredients);
        Assert.Equal(new IngredientSuggestion(2, MatchSource.NameMatch), suggestion);
    }

    [Fact]
    public void Suggest_ExactlyAtThreshold_Matches()
    {
        // 1 mot commun sur 2 = 0.5.
        var ingredients = new[] { Ing(1, "Poulet fermier") };

        var suggestion = IngredientMatcher.Suggest("POULET ROTI 1KG", null, NoAliases, ingredients);

        Assert.Equal(new IngredientSuggestion(1, MatchSource.NameMatch), suggestion);
    }

    [Fact]
    public void Suggest_BelowThreshold_Null()
    {
        // 1 mot commun sur 3 = 0.33.
        var ingredients = new[] { Ing(1, "Sucre glace fin") };

        Assert.Null(IngredientMatcher.Suggest("SUCRE ROUX BRUN 1KG", null, NoAliases, ingredients));
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

        var suggestion = IngredientMatcher.Suggest("Beurre doux", 7, aliases, ingredients);

        Assert.Equal(new IngredientSuggestion(2, MatchSource.NameMatch), suggestion);
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

        var suggestion = IngredientMatcher.Suggest("Beurre doux", 7, aliases, ingredients);

        Assert.Equal(new IngredientSuggestion(2, MatchSource.GlobalAlias), suggestion);
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
    [InlineData("Sucre roux brun", "Sucre glace fin", 1.0 / 3.0)]
    [InlineData("Sucre roux 1kg", "Sucre glace fin", 0.5)]   // « 1kg » ignoré : 1 mot commun sur les 2 mots du libellé
    [InlineData("Filet de boeuf", "Filet de poulet", 0.5)]   // « de » ignoré : 1 sur 2
    [InlineData("Farine", "Beurre", 0.0)]
    [InlineData("", "Beurre", 0.0)]
    [InlineData("Beurre", "---", 0.0)]
    [InlineData("A B", "A", 0.0)]                            // mots d'un seul caractère ignorés → aucun mot
    [InlineData("de la", "de la", 0.0)]                      // que des mots vides → aucun mot
    [InlineData("1kg 40%", "1kg 40%", 0.0)]                  // que des tailles / nombres → aucun mot
    [InlineData("Beurre beurre doux", "Beurre", 1.0)]        // mots dédoublonnés
    public void Similarity(string a, string b, double expected)
        => Assert.Equal(expected, IngredientMatcher.Similarity(a, b), 10);

    [Theory]
    [InlineData("à")]
    [InlineData("au")]
    [InlineData("aux")]
    [InlineData("de")]
    [InlineData("des")]
    [InlineData("du")]
    [InlineData("en")]
    [InlineData("et")]
    [InlineData("la")]
    [InlineData("le")]
    [InlineData("les")]
    public void Similarity_StopWordsDoNotCount(string stopWord)
        => Assert.Equal(0.0, IngredientMatcher.Similarity($"Poulet {stopWord}", $"Sel {stopWord}"));
}
