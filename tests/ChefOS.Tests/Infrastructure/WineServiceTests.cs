using ChefOS.Core.Common;
using ChefOS.Core.Recipes;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using ChefOS.Infrastructure.Wines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class WineServiceTests(PostgresFixture postgres)
{
    private sealed class FakeDescriber(Func<WineDescribeRequest, WineDraft>? draft = null) : IWineDescriber
    {
        public List<WineDescribeRequest> Requests { get; } = [];

        public Task<WineDraft> DescribeAsync(WineDescribeRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(draft?.Invoke(request) ?? new WineDraft(
                "Chablis", "Bourgogne", "France", WineColor.Blanc, "Chardonnay", "Or pâle.", "Agrumes.", "Vive.", "3 à 6 ans.", "10 °C", "Poissons.", []));
        }
    }

    private static WineService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId, IWineDescriber? describer = null, TimeProvider? time = null) =>
        new(new TestContextFactory(options, restaurantId), describer ?? new FakeDescriber(), time);

    private async Task<(DbContextOptions<ChefOsDbContext> Options, int Restaurant, WineService Service)> SetUpAsync(string name = "Bistro", bool library = true)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, name);
        if (library)
            await new WineLibrarySeeder(new TestContextFactory(options, null), NullLogger<WineLibrarySeeder>.Instance).SeedAsync();
        return (options, restaurant, CreateService(options, restaurant));
    }

    private static async Task<int> ReferenceIdAsync(DbContextOptions<ChefOsDbContext> options, string key)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        return (await db.WineReferences.SingleAsync(w => w.Key == key)).Id;
    }

    private static async Task<int> AddDishAsync(DbContextOptions<ChefOsDbContext> options, int restaurant, string name, bool show = true, bool archived = false, RecipeKind kind = RecipeKind.Dish)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurant));
        var recipe = new Recipe { Name = name, Kind = kind, ShowOnVitrine = show, IsArchived = archived, SellingPriceInclVat = 20m };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    private static WineInput Input(
        string name = "Cuvée maison", WineColor color = WineColor.Rouge, int? id = null, decimal bottle = 30m, decimal? glass = null, int? vintage = 2020,
        bool published = false, bool draft = false, bool confirm = false, IReadOnlyList<int>? pairings = null, string robe = "Rubis.") =>
        new(id, name, "Domaine", vintage, null, "Bordeaux", "France", color, "Merlot", robe, "Fruits", "Souple", "5 ans", "16 °C", "Viandes", null,
            bottle, glass, published, draft, confirm, pairings ?? []);

    // ── Bibliothèque ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchLibrary_IgnoresAccentsAndCase_AndOrdersByColorThenName()
    {
        var (_, _, service) = await SetUpAsync();

        var results = await service.SearchLibraryAsync("  CREMANT ", null);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Contains("crémant", r.Name, StringComparison.OrdinalIgnoreCase));

        var all = await service.SearchLibraryAsync(null, null);
        Assert.Equal(50, all.Count);
        Assert.Equal(all.OrderBy(w => w.Color).ThenBy(w => w.Name, StringComparer.InvariantCultureIgnoreCase).Select(w => w.Id), all.Select(w => w.Id));
    }

    [Fact]
    public async Task SearchLibrary_FiltersByColor()
    {
        var (_, _, service) = await SetUpAsync();

        var whites = await service.SearchLibraryAsync("", WineColor.Blanc);

        Assert.NotEmpty(whites);
        Assert.All(whites, w => Assert.Equal(WineColor.Blanc, w.Color));
        Assert.Empty(await service.SearchLibraryAsync("zzzz-inconnu", null));
    }

    [Fact]
    public async Task AddFromLibrary_CopiesTheSheet_AsAnUnpublishedWine()
    {
        var (options, restaurant, service) = await SetUpAsync();
        var referenceId = await ReferenceIdAsync(options, "bourgogne-rouge");

        var wine = await service.AddFromLibraryAsync(referenceId, 2021, "  Domaine Test ", 42m, 9m);

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurant));
        var saved = await db.WineListItems.SingleAsync();
        var reference = await db.WineReferences.SingleAsync(r => r.Id == referenceId);
        Assert.Equal(wine.Id, saved.Id);
        Assert.Equal(referenceId, saved.WineReferenceId);
        Assert.Equal(reference.Name, saved.Name);
        Assert.Equal("Domaine Test", saved.Producer);
        Assert.Equal(2021, saved.Vintage);
        Assert.Equal(reference.Robe, saved.Robe);
        Assert.Equal(reference.Pairings, saved.GeneralPairings);
        Assert.Equal(reference.Color, saved.Color);
        Assert.Equal((42m, 9m), (saved.PriceBottle, saved.PriceGlass));
        Assert.False(saved.IsPublished);
        Assert.False(saved.DescriptionIsAiDraft);
        Assert.Equal(0, saved.Position);
    }

    [Fact]
    public async Task AddFromLibrary_SameWineSameVintage_IsRefused_ButAnotherVintageIsFine()
    {
        var (options, _, service) = await SetUpAsync();
        var referenceId = await ReferenceIdAsync(options, "bourgogne-rouge");
        await service.AddFromLibraryAsync(referenceId, 2021, null, 40m, null);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.AddFromLibraryAsync(referenceId, 2021, null, 40m, null));
        Assert.Equal("Ce vin est déjà sur votre carte (même millésime)", ex.Message);

        await service.AddFromLibraryAsync(referenceId, 2022, null, 40m, null);
        await service.AddFromLibraryAsync(referenceId, null, null, 40m, null);
        var again = await Assert.ThrowsAsync<DomainValidationException>(() => service.AddFromLibraryAsync(referenceId, null, null, 40m, null));
        Assert.Equal("Ce vin est déjà sur votre carte (même millésime)", again.Message);
    }

    [Fact]
    public async Task AddFromLibrary_AppendsToItsColourGroup()
    {
        var (options, _, service) = await SetUpAsync();
        var red1 = await ReferenceIdAsync(options, "bourgogne-rouge");
        var red2 = await ReferenceIdAsync(options, "medoc");

        var a = await service.AddFromLibraryAsync(red1, null, null, 40m, null);
        var b = await service.AddFromLibraryAsync(red2, null, null, 40m, null);

        Assert.Equal(WineColor.Rouge, a.Color);
        Assert.Equal([0, 1], [a.Position, b.Position]);
    }

    [Theory]
    [InlineData(1899, "30", "Le millésime doit être compris entre 1900 et 2027")]
    [InlineData(2028, "30", "Le millésime doit être compris entre 1900 et 2027")]
    [InlineData(2020, "0", "Le prix de la bouteille doit être supérieur à 0")]
    [InlineData(2020, "-1", "Le prix de la bouteille doit être supérieur à 0")]
    public async Task AddFromLibrary_RejectsInvalidValues(int vintage, string bottle, string message)
    {
        var (options, restaurant, _) = await SetUpAsync();
        var service = CreateService(options, restaurant, time: new FixedTime(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero)));
        var referenceId = await ReferenceIdAsync(options, "bourgogne-rouge");

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.AddFromLibraryAsync(referenceId, vintage, null, decimal.Parse(bottle, System.Globalization.CultureInfo.InvariantCulture), null));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public async Task AddFromLibrary_UnknownReference_Throws()
    {
        var (_, _, service) = await SetUpAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.AddFromLibraryAsync(999999, null, null, 30m, null));

        Assert.Equal("Fiche de la bibliothèque introuvable", ex.Message);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ── Enregistrement ───────────────────────────────────────────────────────

    [Fact]
    public async Task Save_CreatesAManualWine_WithPairings()
    {
        var (options, restaurant, service) = await SetUpAsync();
        var steak = await AddDishAsync(options, restaurant, "Entrecôte");
        var fish = await AddDishAsync(options, restaurant, "Sole");

        var wine = await service.SaveAsync(Input(pairings: [steak, fish, steak]));

        var detail = await service.GetAsync(wine.Id);
        Assert.NotNull(detail);
        Assert.Equal("Cuvée maison", detail.Wine.Name);
        Assert.Null(detail.Wine.WineReferenceId);
        Assert.Equal([steak, fish], detail.PairingRecipeIds.Order());
    }

    [Fact]
    public async Task Save_Update_ReplacesThePairings_AndKeepsTheLibraryLink()
    {
        var (options, restaurant, service) = await SetUpAsync();
        var steak = await AddDishAsync(options, restaurant, "Entrecôte");
        var fish = await AddDishAsync(options, restaurant, "Sole");
        var referenceId = await ReferenceIdAsync(options, "bourgogne-rouge");
        var wine = await service.AddFromLibraryAsync(referenceId, 2021, null, 40m, null);
        await service.SaveAsync(Input(id: wine.Id, pairings: [steak]));

        await service.SaveAsync(Input(name: "Renommé", id: wine.Id, pairings: [fish]));

        var detail = await service.GetAsync(wine.Id);
        Assert.Equal("Renommé", detail!.Wine.Name);
        Assert.Equal(referenceId, detail.Wine.WineReferenceId);
        Assert.Equal([fish], detail.PairingRecipeIds);
    }

    [Fact]
    public async Task Save_PairingToAnotherRestaurantsDishOrAPreparation_IsRefused()
    {
        var (options, _, service) = await SetUpAsync();
        var otherRestaurant = await TestData.CreateRestaurantAsync(options, "Autre");
        var foreign = await AddDishAsync(options, otherRestaurant, "Plat étranger");

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Input(pairings: [foreign])));

        Assert.Equal("Un accord pointe vers un plat introuvable", ex.Message);
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task Save_PairingToAPreparation_IsRefused()
    {
        var (options, restaurant, service) = await SetUpAsync();
        var preparation = await AddDishAsync(options, restaurant, "Fond", kind: RecipeKind.Preparation);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Input(pairings: [preparation])));

        Assert.Equal("Un accord pointe vers un plat introuvable", ex.Message);
    }

    [Theory]
    [InlineData("", 2020, 30, null, "Le nom du vin est obligatoire")]
    [InlineData("Vin", 1899, 30, null, "Le millésime doit être compris entre 1900 et")]
    [InlineData("Vin", 2200, 30, null, "Le millésime doit être compris entre 1900 et")]
    [InlineData("Vin", 2020, 0, null, "Le prix de la bouteille doit être supérieur à 0")]
    [InlineData("Vin", 2020, 30, -0.5, "Le prix du verre ne peut pas être négatif")]
    public async Task Save_RejectsInvalidValues(string name, int vintage, double bottle, double? glass, string message)
    {
        var (_, _, service) = await SetUpAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(Input(name: name, vintage: vintage, bottle: (decimal)bottle, glass: (decimal?)glass)));

        Assert.StartsWith(message, ex.Message);
    }

    [Fact]
    public async Task Save_AcceptsBoundaryValues()
    {
        var (_, _, service) = await SetUpAsync();
        var nextYear = DateTime.UtcNow.Year + 1;

        await service.SaveAsync(Input(name: "A", vintage: 1900, bottle: 0.01m, glass: 0m));
        await service.SaveAsync(Input(name: "B", vintage: nextYear));
        await service.SaveAsync(Input(name: "C", vintage: null));

        Assert.Equal(3, (await service.ListAsync()).Count);
    }

    [Fact]
    public async Task Save_UnknownOrForeignWine_Throws()
    {
        var (options, _, service) = await SetUpAsync();
        var otherRestaurant = await TestData.CreateRestaurantAsync(options, "Autre");
        var foreign = await CreateService(options, otherRestaurant).SaveAsync(Input());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Input(id: foreign.Id)));

        Assert.Equal("Vin introuvable", ex.Message);
        Assert.Null(await service.GetAsync(foreign.Id));
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task Save_ChangingTheColour_MovesTheWineToTheEndOfItsNewGroup_AndClosesTheGap()
    {
        var (_, _, service) = await SetUpAsync();
        var r1 = await service.SaveAsync(Input("R1"));
        await service.SaveAsync(Input("R2"));
        await service.SaveAsync(Input("R3"));
        await service.SaveAsync(Input("W1", WineColor.Blanc));

        await service.SaveAsync(Input("R1", WineColor.Blanc, id: r1.Id));

        var list = await service.ListAsync();
        Assert.Equal(["R2", "R3", "W1", "R1"], list.Select(w => w.Name));
        Assert.Equal([0, 1], list.Where(w => w.Color == WineColor.Rouge).Select(w => w.Position));
        Assert.Equal([0, 1], list.Where(w => w.Color == WineColor.Blanc).Select(w => w.Position));
    }

    // ── Brouillon de l'assistant ─────────────────────────────────────────────

    [Fact]
    public async Task Publish_AiDraftWithoutConfirmation_IsRefused()
    {
        var (_, _, service) = await SetUpAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Input(published: true, draft: true)));

        Assert.Equal("Relisez et validez la fiche rédigée par l'assistant avant de la publier", ex.Message);
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task Publish_AiDraftWithConfirmation_Publishes_AndClearsTheFlag()
    {
        var (_, _, service) = await SetUpAsync();

        var wine = await service.SaveAsync(Input(published: true, draft: true, confirm: true));

        Assert.True(wine.IsPublished);
        Assert.False(wine.DescriptionIsAiDraft);
    }

    [Fact]
    public async Task Save_AiDraftNotPublished_StaysADraft_EvenAfterManualEdits()
    {
        var (_, _, service) = await SetUpAsync();
        var wine = await service.SaveAsync(Input(draft: true));
        Assert.True(wine.DescriptionIsAiDraft);

        // Le formulaire renvoie le drapeau tel qu'il est, avec un texte modifié à la main : il reste un brouillon.
        await service.SaveAsync(Input(id: wine.Id, draft: true, robe: "Retouchée à la main."));
        // Même un formulaire qui oublie le drapeau ne l'efface pas.
        await service.SaveAsync(Input(id: wine.Id, draft: false, robe: "Encore retouchée."));

        var saved = (await service.GetAsync(wine.Id))!.Wine;
        Assert.True(saved.DescriptionIsAiDraft);
        Assert.Equal("Encore retouchée.", saved.Robe);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(Input(id: wine.Id, published: true)));
        Assert.Equal("Relisez et validez la fiche rédigée par l'assistant avant de la publier", ex.Message);
    }

    [Fact]
    public async Task Save_Confirmation_ClearsTheDraftFlag_ThenPublishingIsFree()
    {
        var (_, _, service) = await SetUpAsync();
        var wine = await service.SaveAsync(Input(draft: true));

        await service.SaveAsync(Input(id: wine.Id, draft: true, confirm: true));

        Assert.False((await service.GetAsync(wine.Id))!.Wine.DescriptionIsAiDraft);
        await service.SetPublishedAsync(wine.Id, true);
        Assert.True((await service.GetAsync(wine.Id))!.Wine.IsPublished);
    }

    [Fact]
    public async Task SetPublished_AiDraft_IsRefused_ButUnpublishingIsAlwaysAllowed()
    {
        var (_, _, service) = await SetUpAsync();
        var draft = await service.SaveAsync(Input("Brouillon", draft: true));
        var plain = await service.SaveAsync(Input("Relu", published: true));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SetPublishedAsync(draft.Id, true));
        Assert.Equal("Relisez et validez la fiche rédigée par l'assistant avant de la publier", ex.Message);

        await service.SetPublishedAsync(plain.Id, false);
        Assert.False((await service.GetAsync(plain.Id))!.Wine.IsPublished);
        await service.SetPublishedAsync(plain.Id, true);
        Assert.True((await service.GetAsync(plain.Id))!.Wine.IsPublished);
    }

    [Fact]
    public async Task DraftDescription_PassesThePublishedDishNames_AndMapsSuggestionsToRecipeIds()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var sole = await AddDishAsync(options, restaurant, "Sole meunière");
        var entrecote = await AddDishAsync(options, restaurant, "Entrecôte");
        await AddDishAsync(options, restaurant, "Caché", show: false);
        await AddDishAsync(options, restaurant, "Archivé", archived: true);
        await AddDishAsync(options, restaurant, "Fond", kind: RecipeKind.Preparation);
        var describer = new FakeDescriber(_ => new WineDraft(
            "Chablis", "Bourgogne", "France", WineColor.Blanc, "Chardonnay", "Robe.", "Nez.", "Bouche.", "Garde.", "10 °C", "Accords.",
            ["entrecote", "SOLE MEUNIÈRE", "Plat inventé", "Entrecôte"]));
        var service = CreateService(options, restaurant, describer);

        var draft = await service.DraftDescriptionAsync("  Chablis ", 2022, "Domaine X", null);

        var request = Assert.Single(describer.Requests);
        Assert.Equal("Chablis", request.Name);
        Assert.Equal(2022, request.Vintage);
        Assert.Equal("Domaine X", request.Producer);
        Assert.Null(request.Appellation);
        Assert.Equal(["Entrecôte", "Sole meunière"], request.DishNames);
        Assert.Equal([entrecote, sole], draft.SuggestedPairingRecipeIds);
        Assert.Equal(["Entrecôte", "Sole meunière"], draft.SuggestedPairingDishNames);
        Assert.Equal(WineColor.Blanc, draft.Color);
        Assert.Equal("Robe.", draft.Robe);
    }

    [Fact]
    public async Task DraftDescription_DoesNotSaveAnything_AndRejectsBlankNames()
    {
        var (_, _, service) = await SetUpAsync();

        await service.DraftDescriptionAsync("Chablis", null, null, null);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.DraftDescriptionAsync("  ", null, null, null));

        Assert.Equal("Le nom du vin est obligatoire pour rédiger la fiche", ex.Message);
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task DraftDescription_AssistantFailure_ReachesTheCallerAsIs()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant, new FailingDescriber());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DraftDescriptionAsync("Chablis", null, null, null));

        Assert.Equal("Quota Anthropic atteint : réessayez dans un instant", ex.Message);
    }

    private sealed class FailingDescriber : IWineDescriber
    {
        public Task<WineDraft> DescribeAsync(WineDescribeRequest request, CancellationToken ct = default) =>
            Task.FromException<WineDraft>(new InvalidOperationException("Quota Anthropic atteint : réessayez dans un instant"));
    }

    // ── Ordre, suppression ───────────────────────────────────────────────────

    [Fact]
    public async Task Move_ReordersWithinTheColourGroupOnly()
    {
        var (_, _, service) = await SetUpAsync();
        await service.SaveAsync(Input("A"));
        await service.SaveAsync(Input("B"));
        var c = await service.SaveAsync(Input("C"));
        var white = await service.SaveAsync(Input("Blanc", WineColor.Blanc));

        await service.MoveAsync(c.Id, 0);

        var list = await service.ListAsync();
        Assert.Equal(["C", "A", "B"], list.Where(w => w.Color == WineColor.Rouge).Select(w => w.Name));
        Assert.Equal([0, 1, 2], list.Where(w => w.Color == WineColor.Rouge).Select(w => w.Position));
        Assert.Equal(0, list.Single(w => w.Id == white.Id).Position);
    }

    [Fact]
    public async Task Move_InvalidPositionOrUnknownWine_Throws()
    {
        var (_, _, service) = await SetUpAsync();
        var a = await service.SaveAsync(Input("A"));

        Assert.Equal("Position invalide", (await Assert.ThrowsAsync<DomainValidationException>(() => service.MoveAsync(a.Id, 1))).Message);
        Assert.Equal("Position invalide", (await Assert.ThrowsAsync<DomainValidationException>(() => service.MoveAsync(a.Id, -1))).Message);
        Assert.Equal("Vin introuvable", (await Assert.ThrowsAsync<DomainValidationException>(() => service.MoveAsync(999999, 0))).Message);
    }

    [Fact]
    public async Task Delete_RemovesThePairings_AndClosesThePositions()
    {
        var (options, restaurant, service) = await SetUpAsync();
        var steak = await AddDishAsync(options, restaurant, "Entrecôte");
        var a = await service.SaveAsync(Input("A", pairings: [steak]));
        await service.SaveAsync(Input("B"));
        await service.SaveAsync(Input("C"));

        await service.DeleteAsync(a.Id);

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurant));
        Assert.Empty(await db.WineListItemPairings.ToListAsync());
        Assert.Equal([0, 1], (await service.ListAsync()).Select(w => w.Position));
        Assert.Equal("Vin introuvable", (await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(a.Id))).Message);
    }

    [Fact]
    public async Task List_IsOrderedByColourThenPosition_AndIsolatedPerRestaurant()
    {
        var (options, _, service) = await SetUpAsync();
        var other = CreateService(options, await TestData.CreateRestaurantAsync(options, "Autre"));
        await service.SaveAsync(Input("Doux", WineColor.Doux));
        await service.SaveAsync(Input("Rouge", WineColor.Rouge));
        await service.SaveAsync(Input("Blanc", WineColor.Blanc));
        await other.SaveAsync(Input("Chez l'autre"));

        Assert.Equal(["Rouge", "Blanc", "Doux"], (await service.ListAsync()).Select(w => w.Name));
        Assert.Equal(["Chez l'autre"], (await other.ListAsync()).Select(w => w.Name));
    }

    [Fact]
    public async Task ListDishes_PublishedDishesByName_PlusRequestedOnes()
    {
        var (options, restaurant, service) = await SetUpAsync();
        var b = await AddDishAsync(options, restaurant, "bar");
        var a = await AddDishAsync(options, restaurant, "Agneau");
        var hidden = await AddDishAsync(options, restaurant, "Caché", show: false);
        await AddDishAsync(options, restaurant, "Archivé", archived: true);

        Assert.Equal([a, b], (await service.ListDishesAsync()).Select(d => d.Id));
        Assert.Equal([a, b, hidden], (await service.ListDishesAsync([hidden])).Select(d => d.Id));
    }

    // ── Lecture publique ─────────────────────────────────────────────────────

    [Fact]
    public async Task Public_ListsOnlyPublishedWines_ByColourThenPosition_FilteredByColourAndRegion()
    {
        var (_, _, service) = await SetUpAsync();
        await service.SaveAsync(Input("Rouge 1", published: true));
        await service.SaveAsync(Input("Rouge caché"));
        await service.SaveAsync(Input("Blanc 1", WineColor.Blanc, published: true));
        await service.SaveAsync(Input("Rouge 2", published: true));

        Assert.Equal(["Rouge 1", "Rouge 2", "Blanc 1"], (await service.ListPublishedAsync(null, null)).Select(w => w.Name));
        Assert.Equal(["Blanc 1"], (await service.ListPublishedAsync(WineColor.Blanc, null)).Select(w => w.Name));
        Assert.Equal(3, (await service.ListPublishedAsync(null, " BORDEAUX ")).Count);
        Assert.Empty(await service.ListPublishedAsync(null, "Alsace"));
    }

    [Fact]
    public async Task Public_Detail_OnlyForPublishedWines_WithPublishedDishPairings()
    {
        var (options, restaurant, service) = await SetUpAsync();
        var shown = await AddDishAsync(options, restaurant, "Entrecôte");
        var hidden = await AddDishAsync(options, restaurant, "Caché", show: false);
        var wine = await service.SaveAsync(Input(published: true, pairings: [shown, hidden]));
        var draft = await service.SaveAsync(Input("Brouillon"));

        var detail = await service.GetPublishedAsync(wine.Id);

        Assert.NotNull(detail);
        Assert.Equal("Cuvée maison", detail.Name);
        Assert.Equal([new PublicWinePairing(shown, "Entrecôte")], detail.Pairings);
        Assert.Null(await service.GetPublishedAsync(draft.Id));
        Assert.Null(await service.GetPublishedAsync(999999));
        var other = CreateService(options, await TestData.CreateRestaurantAsync(options, "Autre"));
        Assert.Null(await other.GetPublishedAsync(wine.Id));
    }
}
