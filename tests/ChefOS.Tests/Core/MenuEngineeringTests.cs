using ChefOS.Core.Sales;

namespace ChefOS.Tests.Core;

public class MenuEngineeringTests
{
    [Fact]
    public void MenuEngineering_FourQuadrants()
    {
        // n = 4 → seuil de popularité 0.70 × 1/4 = 0.175.
        // Marge moyenne pondérée = (40×10 + 40×2 + 10×10 + 10×2) ÷ 100 = 6.
        var result = MenuEngineering.Classify(
        [
            new MenuItem(1, 40m, 10m), // populaire (0.40), marge haute   → Étoile
            new MenuItem(2, 40m, 2m),  // populaire (0.40), marge basse   → Vache à lait
            new MenuItem(3, 10m, 10m), // peu vendu (0.10), marge haute   → Énigme
            new MenuItem(4, 10m, 2m),  // peu vendu (0.10), marge basse   → Poids mort
        ]);

        Assert.Equal(4, result.Count);
        Assert.Equal([1, 2, 3, 4], result.Select(c => c.RecipeId));
        Assert.Equal(MenuClass.Etoile, result[0].Class);
        Assert.Equal(MenuClass.VacheALait, result[1].Class);
        Assert.Equal(MenuClass.Enigme, result[2].Class);
        Assert.Equal(MenuClass.PoidsMort, result[3].Class);
        Assert.Equal(0.40m, result[0].PopularityShare);
        Assert.Equal(0.10m, result[3].PopularityShare);
        Assert.Equal(10m, result[0].UnitMargin);
        Assert.Equal(2m, result[1].UnitMargin);
    }

    [Fact]
    public void MenuEngineering_ThresholdsInclusive()
    {
        // n = 4, Σ qty = 1000 → seuil de popularité 0.175 ; marge moyenne pondérée =
        // (175×6 + 325×4 + 250×8 + 250×6.6) ÷ 1000 = 6000 ÷ 1000 = 6.
        // Le plat 1 tombe exactement sur les deux seuils (part 0.175, marge 6) : égalité = franchi.
        var result = MenuEngineering.Classify(
        [
            new MenuItem(1, 175m, 6m),
            new MenuItem(2, 325m, 4m),
            new MenuItem(3, 250m, 8m),
            new MenuItem(4, 250m, 6.6m),
        ]);

        Assert.Equal(0.175m, result[0].PopularityShare);
        Assert.Equal(MenuClass.Etoile, result[0].Class);
        Assert.Equal(MenuClass.VacheALait, result[1].Class);
        Assert.Equal(MenuClass.Etoile, result[2].Class);
        Assert.Equal(MenuClass.Etoile, result[3].Class);
    }

    [Fact]
    public void MenuEngineering_JustBelowPopularityThreshold_IsNotPopular()
    {
        // n = 2 → seuil 0.35. Le plat 1 pèse 0.34 : pas populaire, mais sa marge (= moyenne) suffit → Énigme.
        var result = MenuEngineering.Classify(
        [
            new MenuItem(1, 34m, 5m),
            new MenuItem(2, 66m, 5m),
        ]);

        Assert.Equal(MenuClass.Enigme, result[0].Class);
        Assert.Equal(MenuClass.Etoile, result[1].Class);
    }

    [Fact]
    public void MenuEngineering_IgnoresUnsold()
    {
        // Les plats à quantité 0 (ou négative) sont ignorés : absents du résultat et exclus de n.
        // n = 2 → seuil 0.35 : le plat 2 (part 0.30) n'est pas populaire ; avec n = 3 (0.233) il le serait.
        // Marge moyenne pondérée = (30×10 + 70×2) ÷ 100 = 4.4.
        var result = MenuEngineering.Classify(
        [
            new MenuItem(1, 0m, 100m),
            new MenuItem(2, 30m, 10m),
            new MenuItem(3, -5m, 100m),
            new MenuItem(4, 70m, 2m),
        ]);

        Assert.Equal([2, 4], result.Select(c => c.RecipeId));
        Assert.Equal(MenuClass.Enigme, result[0].Class);
        Assert.Equal(0.30m, result[0].PopularityShare);
        Assert.Equal(MenuClass.VacheALait, result[1].Class);
        Assert.Equal(0.70m, result[1].PopularityShare);
    }

    [Fact]
    public void MenuEngineering_EmptyOrAllUnsold_ReturnsEmpty()
    {
        Assert.Empty(MenuEngineering.Classify([]));
        Assert.Empty(MenuEngineering.Classify([new MenuItem(1, 0m, 5m), new MenuItem(2, 0m, 3m)]));
    }

    [Fact]
    public void MenuEngineering_SingleItem_IsPopularAndAtAverageMargin()
    {
        // n = 1 → seuil 0.70, part 1.0 ; la marge égale la moyenne → Étoile (même si la marge est négative).
        var result = Assert.Single(MenuEngineering.Classify([new MenuItem(5, 12m, -1.5m)]));

        Assert.Equal(MenuClass.Etoile, result.Class);
        Assert.Equal(1m, result.PopularityShare);
        Assert.Equal(-1.5m, result.UnitMargin);
    }

    [Fact]
    public void MenuEngineering_KeepsInputOrder()
    {
        var result = MenuEngineering.Classify(
        [
            new MenuItem(30, 10m, 1m),
            new MenuItem(10, 10m, 1m),
            new MenuItem(20, 10m, 1m),
        ]);

        Assert.Equal([30, 10, 20], result.Select(c => c.RecipeId));
    }
}
